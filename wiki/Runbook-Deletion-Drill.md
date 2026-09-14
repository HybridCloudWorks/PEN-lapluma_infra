# Runbook — deletion drill

> Operational Runbook for GCP Data Deletion and Retention Verification under ADR-019.
> Its pass criteria are the ratified retention numbers on the
> [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates) page. See
> [Operational Runbooks](Operational-Runbooks).

## Why this is a drill

The pilot issues a deletion receipt upon participant or institution request. A receipt is a legal and regulatory claim, and this drill is the operational verification that proves the claim is accurate. The failure being guarded against is not a refusal to delete — it is deletion that reaches five stores out of six and reports success, because nobody verified the remaining storage tiers.

Run in `staging`, against a synthetic participant and case created specifically for the drill and used for nothing else.

## The two stores everyone forgets

Stated first, because a checklist read top to bottom loses attention exactly where these sit:

1. **Cloud Storage (GCS) object versions and soft-deleted objects.** Deleting a GCS object with versioning enabled creates a noncurrent version. Furthermore, GCP Cloud Storage buckets enforce soft delete (7-day retention window: 604,800 seconds). Noncurrent object versions are purged via bucket lifecycle rules at 7 days (`days_since_noncurrent_time = 7`). Both the soft-delete retention window (7 days) and version purge window (7 days) are strictly shorter than the ratified 30-day account erasure SLA. A deletion sweep that removes current objects without accounting for versions or soft-delete retention fails audit verification.
2. **Scoped Signed URLs and Download Grants.** Short-lived V4 signed URLs (15-minute TTL) issued before erasure remain cryptographically valid at the storage edge until expiration. Because Google Cloud Storage does not support instantaneous single-URL revocation without rotating root service account keys, the architecture strictly limits grant TTL to 15 minutes and revokes the associated database authorization record in PostgreSQL immediately. No claim of instantaneous signed-URL destruction is permitted.

## Scope

Erasure must reach every one of these tiers, and the drill fails if any single tier is unverified:

| Store | Technology | Verification |
|-------|------------|--------------|
| Authoritative Cases & Sections | Cloud SQL PostgreSQL (`workflow` schema) | No row keyed to participant/case in `case_workspace`, `case_section_value`, `case_evidence_link`, `case_approval`, `outbox_event` |
| Working & Output Objects | Cloud Storage (`lp-documents-{env}`) | No live object matching case ID or participant ID |
| Noncurrent Versions & Soft-Delete | Cloud Storage (`lp-documents-{env}`) | Noncurrent versions purged by 7-day lifecycle; soft-delete window expires within 7 days |
| Upload Quarantine & Staging | Cloud Storage (`lp-quarantine-{env}`) | No pending or completed upload session objects surviving past 24-hour auto-expire lifecycle |
| Ephemeral Processing Scratch | Cloud Storage (`lp-scratch-{env}`) & Cloud Run `/tmp` | 1-day auto-delete lifecycle on scratch bucket; Cloud Run worker instances process in memory or purge `/tmp` on exit |
| Event Streams & Dead-Letters | Google Cloud Pub/Sub (`lp-workflow-events-dlq`) | No unacknowledged dead-letter messages referencing the participant |
| Pseudonymized Audit Trail | Cloud SQL PostgreSQL (`workflow.case_history`) | Records **remain**, content-free and pseudonymized; zero plaintext PII (no names, unhashed emails, or SSNs) |
| Local Client Data | Mobile / Desktop App (`ApertureApp`) | `deleteAllLocalData()` wipes `PendingCaptureQueue`, `ExportScratch`, user defaults, and in-memory caches |
| Database Backups & PITR | Cloud SQL Automated Backups | Point-in-time recovery logs and automated backups expire within the 30-day backup retention window |

The audit row is the only one that inverts. Audit records survive erasure by design — they are the evidence that erasure happened — which is why the schema requires them to be content-free and pseudonymized. A drill that finds the audit container empty after erasure has found a failure, not a success.

## Steps

### 1. Seed

Create a synthetic participant and drive it through the full workflow:
- A case in `workflow.case_workspace` with canonical section values in `workflow.case_section_value`.
- At least one uploaded evidence document in `lp-quarantine` promoted to `lp-documents`.
- A watermarked draft preview in `lp-scratch`.
- A completed review decision and step-up approval in `workflow.case_approval`.
- A generated AcroForm package in `lp-documents` and an issued signed download grant.
- At least one dead-lettered Pub/Sub message deliberately generated in `lp-workflow-events-dlq`.

Record every generated identifier (`case_id`, `person_id`, `document_id`, `package_id`, `message_id`).

### 2. Record the pre-state

For each row in the scope table, record what exists. A drill that only checks the post-state cannot distinguish "erasure removed it" from "it was never written", and the second is the more common reason a naive check passes.

### 3. Execute Erasure

Invoke the authoritative erasure path:
1. Trigger client data erasure via `AppSession.deleteAllLocalData()`.
2. Invoke backend participant erasure via the administrative endpoint or migration procedure.
3. Record the wall-clock time from invocation to completion, and compare it to the ratified 30-day SLA for active data.

### 4. Verify

Work the scope table systematically using native GCP tools:

```bash
# 1. Cloud Storage: verify current objects, noncurrent versions, and soft-deleted items
gcloud storage objects list \
  --bucket="lp-documents-${ENVIRONMENT}-${PROJECT_ID}" \
  --filter="name:${CASE_ID}" \
  --raw \
  --include-deleted

# 2. Cloud SQL PostgreSQL: verify cascading deletion across workflow tables
psql "host=${DB_HOST} dbname=lapluma user=lapluma_app_workflow sslmode=verify-full" -c \
  "SELECT 'case_workspace', count(*) FROM workflow.case_workspace WHERE case_id = '${CASE_ID}'
   UNION ALL
   SELECT 'case_section_value', count(*) FROM workflow.case_section_value WHERE case_id = '${CASE_ID}'
   UNION ALL
   SELECT 'case_evidence_link', count(*) FROM workflow.case_evidence_link WHERE case_id = '${CASE_ID}'
   UNION ALL
   SELECT 'case_approval', count(*) FROM workflow.case_approval WHERE case_id = '${CASE_ID}';"

# 3. Pub/Sub: inspect dead-letter queue for surviving messages
gcloud pubsub subscriptions pull \
  "lp-workflow-events-dlq-sub" \
  --project="${PROJECT_ID}" \
  --auto-ack=false \
  --limit=10 \
  --format="json"

# 4. Audit Trail: verify pseudonymized history survives without PII
psql "host=${DB_HOST} dbname=lapluma user=lapluma_app_workflow sslmode=verify-full" -c \
  "SELECT event_type, pseudonymized_subject, created_at \
   FROM workflow.case_history \
   WHERE case_id = '${CASE_ID}';"
```

### 5. Verify the receipt

The deletion receipt must accurately reflect what was executed:
- Confirm that the receipt details the removal of case files, section values, and generated forms.
- Confirm the receipt explicitly discloses that pseudonymized audit events are retained for compliance.
- Confirm the participant notice aligns with the receipt.

### 6. Backups and Recovery Guard

Confirm that backups containing the participant's data expire within the 30-day window. Verify that restoring an earlier database backup cannot resurrect deleted tenant permissions or unassigned collection grants (`restore cannot resurrect authorization`).

## Cadence

Executed before `pilot` launch, quarterly thereafter, and after any modification to storage bucket lifecycle configurations or database cascade schemas.
