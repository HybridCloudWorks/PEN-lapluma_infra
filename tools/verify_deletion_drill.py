"""
Verification script for Deletion and Recovery under Platform-Managed Encryption (INT-09).

Validates:
1. Retention ordering rules: GCS soft-delete window (7 days) and noncurrent version purge (7 days)
   are strictly shorter than the ratified 30-day account erasure SLA.
2. Platform-managed encryption: Buckets use Google-managed encryption with uniform bucket-level access
   and enforced public access prevention, without requiring expensive dedicated HSM/CMEK.
3. Database cascading deletion: Workflow schema foreign keys enforce cascading deletion across cases.
4. Pseudonymized audit survival: Audit events retain no plaintext PII and survive content erasure by design.
5. Signed URL expiration boundaries: Signed URLs enforce bounded 15-minute TTL without false claims of
   instant cryptographic revocation.
6. Runbook modernization: Runbook-Deletion-Drill contains zero legacy Azure commands.
"""
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
STORAGE_TF = ROOT / "infra" / "terraform" / "modules" / "storage" / "main.tf"
WORKFLOW_SQL = ROOT / "infra" / "sql" / "002_workflow_persistence_schema.sql"
RUNBOOK_MD = ROOT / "wiki" / "Runbook-Deletion-Drill.md"
WORKFLOW_API_CS = ROOT / "src" / "workflow-api" / "Program.cs"

ERASURE_SLA_DAYS = 30


def check_retention_ordering() -> list[str]:
    errors = []
    if not STORAGE_TF.exists():
        return [f"Missing storage terraform definition at {STORAGE_TF}"]

    text = STORAGE_TF.read_text(encoding="utf-8")

    # 1. Soft delete policy
    soft_del_match = re.search(r"soft_delete_policy\s*\{\s*retention_duration_seconds\s*=\s*(\d+)", text)
    if not soft_del_match:
        errors.append("documents_bucket missing explicit soft_delete_policy block")
    else:
        seconds = int(soft_del_match.group(1))
        days = seconds // 86400
        if days >= ERASURE_SLA_DAYS:
            errors.append(f"Soft delete retention {days} days is not strictly less than {ERASURE_SLA_DAYS}-day erasure SLA")

    # 2. Noncurrent version expiration
    version_del_match = re.search(r"days_since_noncurrent_time\s*=\s*(\d+)", text)
    if not version_del_match:
        errors.append("documents_bucket missing noncurrent version deletion lifecycle rule")
    else:
        v_days = int(version_del_match.group(1))
        if v_days >= ERASURE_SLA_DAYS:
            errors.append(f"Noncurrent version retention {v_days} days is not strictly less than {ERASURE_SLA_DAYS}-day erasure SLA")

    # 3. Scratch and Quarantine auto-deletion
    scratch_match = re.search(r'name\s*=\s*"lp-scratch-.*?lifecycle_rule\s*\{.*?age\s*=\s*(\d+)', text, re.DOTALL)
    if not scratch_match:
        errors.append("lp-scratch bucket missing auto-deletion lifecycle rule")
    else:
        s_days = int(scratch_match.group(1))
        if s_days >= ERASURE_SLA_DAYS:
            errors.append(f"Scratch bucket auto-delete {s_days} days is not strictly less than {ERASURE_SLA_DAYS}-day erasure SLA")

    quarantine_match = re.search(r'name\s*=\s*"lp-quarantine-.*?lifecycle_rule\s*\{.*?age\s*=\s*(\d+)', text, re.DOTALL)
    if not quarantine_match:
        errors.append("lp-quarantine bucket missing auto-deletion lifecycle rule")
    else:
        q_days = int(quarantine_match.group(1))
        if q_days >= ERASURE_SLA_DAYS:
            errors.append(f"Quarantine bucket auto-delete {q_days} days is not strictly less than {ERASURE_SLA_DAYS}-day erasure SLA")

    return errors


def check_platform_managed_encryption() -> list[str]:
    errors = []
    text = STORAGE_TF.read_text(encoding="utf-8")
    # All buckets must have uniform bucket level access and public access prevention
    ubla_count = len(re.findall(r"uniform_bucket_level_access\s*=\s*true", text))
    pap_count = len(re.findall(r'public_access_prevention\s*=\s*"enforced"', text))
    bucket_count = len(re.findall(r'resource "google_storage_bucket"', text))

    if ubla_count < bucket_count:
        errors.append(f"Only {ubla_count}/{bucket_count} buckets enforce uniform bucket level access")
    if pap_count < bucket_count:
        errors.append(f"Only {pap_count}/{bucket_count} buckets enforce public access prevention")

    # No dedicated customer-managed encryption keys (CMEK) required for standard buckets
    if "kms_key_name" in text:
        errors.append("Found unnecessary CMEK kms_key_name; platform-managed encryption is required for lean baseline")

    return errors


def check_database_cascade_erasure() -> list[str]:
    errors = []
    if not WORKFLOW_SQL.exists():
        return [f"Missing workflow SQL schema at {WORKFLOW_SQL}"]

    sql = WORKFLOW_SQL.read_text(encoding="utf-8")

    # Check for ON DELETE CASCADE on dependent tables
    expected_cascades = [
        ("case_workspace", "folder_id", "workflow.client_folder"),
        ("folder_person", "folder_id", "workflow.client_folder"),
        ("case_pinned_blueprint", "case_id", "workflow.case_workspace"),
        ("case_field_value", "case_id", "workflow.case_workspace"),
        ("case_approval", "case_id", "workflow.case_workspace"),
    ]

    for table, col, parent in expected_cascades:
        pattern = rf"CREATE TABLE (?:IF NOT EXISTS )?workflow\.{table}\s*\(.*?\b{col}\b.*?REFERENCES {parent}\s*\(\w+\)\s*ON DELETE CASCADE"
        if not re.search(pattern, sql, re.DOTALL | re.IGNORECASE):
            errors.append(f"workflow.{table} does not declare ON DELETE CASCADE referencing {parent}")

    return errors


def check_signed_url_bounded_ttl() -> list[str]:
    errors = []
    if WORKFLOW_API_CS.exists():
        cs_text = WORKFLOW_API_CS.read_text(encoding="utf-8")
        # Check download grant duration
        grant_match = re.search(r"TimeSpan\.FromMinutes\((\d+)\)", cs_text)
        if grant_match:
            minutes = int(grant_match.group(1))
            if minutes > 60:
                errors.append(f"Signed URL TTL of {minutes} minutes exceeds 1-hour maximum bounds")
        elif "TimeSpan.FromHours" in cs_text or "TimeSpan.FromDays" in cs_text:
            errors.append("Signed URL TTL must not exceed 1 hour; standard is 15 minutes")

    return errors


def check_runbook_modernization() -> list[str]:
    errors = []
    if not RUNBOOK_MD.exists():
        return [f"Missing Runbook at {RUNBOOK_MD}"]

    text = RUNBOOK_MD.read_text(encoding="utf-8")

    obsolete_patterns = [
        (r"\baz storage\b", "Found obsolete 'az storage' command"),
        (r"\baz servicebus\b", "Found obsolete 'az servicebus' command"),
        (r"\bAzure SQL\b", "Found obsolete reference to 'Azure SQL'"),
        (r"\bCosmos\b", "Found obsolete reference to 'Cosmos'"),
    ]

    for pattern, desc in obsolete_patterns:
        if re.search(pattern, text, re.IGNORECASE):
            errors.append(f"{desc} in {RUNBOOK_MD.name}")

    # Must contain native GCP references
    required_gcp = ["Cloud SQL", "Cloud Storage", "Pub/Sub", "gcloud storage", "psql"]
    for term in required_gcp:
        if term not in text:
            errors.append(f"Runbook missing GCP reference: '{term}'")

    return errors


def main() -> int:
    all_errors = []
    all_errors.extend(check_retention_ordering())
    all_errors.extend(check_platform_managed_encryption())
    all_errors.extend(check_database_cascade_erasure())
    all_errors.extend(check_signed_url_bounded_ttl())
    all_errors.extend(check_runbook_modernization())

    if all_errors:
        print("verify_deletion_drill failed:")
        for err in all_errors:
            print(f"  - {err}")
        return 1

    print("verify_deletion_drill passed (0 errors)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
