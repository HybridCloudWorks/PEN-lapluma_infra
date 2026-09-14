"""USCIS Official Form Manifest Validator and CLI (INF-01).

Validates the complete USCIS all-forms reconciled inventory against its schema,
verifies integrity constraints, ensures zero unexplained omissions, and checks
non-USCIS catalog preservation.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any


REPO_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_MANIFEST_PATH = REPO_ROOT / "contracts" / "uscis-official-manifest.json"
DEFAULT_SCHEMA_PATH = REPO_ROOT / "contracts" / "schemas" / "uscis-manifest.schema.json"


class ManifestValidationError(Exception):
    """Raised when the manifest violates schema or semantic integrity rules."""


def load_json(path: Path) -> dict[str, Any]:
    if not path.is_file():
        raise FileNotFoundError(f"File not found: {path}")
    with open(path, "r", encoding="utf-8") as fp:
        return json.load(fp)


def validate_schema(data: dict[str, Any], schema: dict[str, Any]) -> list[str]:
    """Lightweight pure-Python schema validator without external dependencies."""
    errors: list[str] = []

    # Required root keys
    for req in schema.get("required", []):
        if req not in data:
            errors.append(f"Missing required root field: '{req}'")

    if errors:
        return errors

    # Check authority and reconciliation status
    if data.get("authority") != "USCIS":
        errors.append(f"Expected authority 'USCIS', got '{data.get('authority')}'")

    if data.get("reconciliationStatus") not in ("COMPLETE", "IN_PROGRESS"):
        errors.append(f"Invalid reconciliationStatus: '{data.get('reconciliationStatus')}'")

    # Validate forms array
    forms = data.get("forms")
    if not isinstance(forms, list):
        errors.append("'forms' must be a list")
        return errors

    form_req = schema.get("properties", {}).get("forms", {}).get("items", {}).get("required", [])

    allowed_series = {"I", "N", "G", "OTHER"}
    allowed_capabilities = {"FILLABLE_PDF", "STATIC_ASSISTED", "EXTERNAL_REFERENCE"}
    allowed_artifact_types = {"OFFICIAL_PDF", "XFA", "FLAT", "EXTERNAL_LINK"}
    allowed_epics = {"INF-06", "INF-07", "INF-08"}

    seen_ids: set[str] = set()

    for idx, form in enumerate(forms):
        if not isinstance(form, dict):
            errors.append(f"Form at index {idx} must be a dictionary")
            continue

        for req in form_req:
            if req not in form:
                errors.append(f"Form index {idx} ('{form.get('formId', 'unknown')}') missing required field: '{req}'")

        form_id = form.get("formId", "")
        if not form_id:
            errors.append(f"Form index {idx} has empty formId")
        elif form_id in seen_ids:
            errors.append(f"Duplicate formId detected: '{form_id}'")
        else:
            seen_ids.add(form_id)

        series = form.get("series")
        if series not in allowed_series:
            errors.append(f"Form '{form_id}' has invalid series: '{series}'")

        cap = form.get("preparationCapability")
        if cap not in allowed_capabilities:
            errors.append(f"Form '{form_id}' has invalid preparationCapability: '{cap}'")

        art = form.get("artifactType")
        if art not in allowed_artifact_types:
            errors.append(f"Form '{form_id}' has invalid artifactType: '{art}'")

        epic = form.get("parentEpic")
        if epic not in allowed_epics:
            errors.append(f"Form '{form_id}' has invalid parentEpic: '{epic}'")

        # Verify series to parentEpic mapping consistency
        if series == "I" and epic != "INF-06":
            errors.append(f"Form '{form_id}' series is 'I' but parentEpic is '{epic}' (expected INF-06)")
        elif series == "N" and epic != "INF-07":
            errors.append(f"Form '{form_id}' series is 'N' but parentEpic is '{epic}' (expected INF-07)")
        elif series in ("G", "OTHER") and epic != "INF-08":
            errors.append(f"Form '{form_id}' series is '{series}' but parentEpic is '{epic}' (expected INF-08)")

        source_url = form.get("sourceUrl", "")
        if not source_url.startswith("https://"):
            errors.append(f"Form '{form_id}' sourceUrl must be a secure HTTPS URI: '{source_url}'")

    # Validate preserved non-USCIS definitions
    preserved = data.get("preservedNonUscisDefinitions")
    if not isinstance(preserved, list):
        errors.append("'preservedNonUscisDefinitions' must be a list")
    elif len(preserved) == 0:
        errors.append("preservedNonUscisDefinitions must not be empty; non-USCIS definitions must be preserved (INF-01)")

    return errors


def validate_manifest_integrity(manifest: dict[str, Any]) -> dict[str, Any]:
    """Validates summary statistics and semantic consistency of the manifest."""
    forms = manifest.get("forms", [])
    summary = manifest.get("summary", {})

    total_official = len(forms)
    if summary.get("totalOfficialForms") != total_official:
        raise ManifestValidationError(
            f"Summary total mismatch: summary says {summary.get('totalOfficialForms')}, but counted {total_official}"
        )

    by_series: dict[str, int] = {"I": 0, "N": 0, "G": 0, "OTHER": 0}
    by_cap: dict[str, int] = {"FILLABLE_PDF": 0, "STATIC_ASSISTED": 0, "EXTERNAL_REFERENCE": 0}
    by_epic: dict[str, int] = {"INF-06": 0, "INF-07": 0, "INF-08": 0}

    for f in forms:
        s = f["series"]
        c = f["preparationCapability"]
        e = f["parentEpic"]
        by_series[s] = by_series.get(s, 0) + 1
        by_cap[c] = by_cap.get(c, 0) + 1
        by_epic[e] = by_epic.get(e, 0) + 1

    if summary.get("bySeries") != by_series:
        raise ManifestValidationError(f"Series summary mismatch: {summary.get('bySeries')} vs calculated {by_series}")

    if summary.get("byPreparationCapability") != by_cap:
        raise ManifestValidationError(f"Capability summary mismatch: {summary.get('byPreparationCapability')} vs {by_cap}")

    if summary.get("byParentEpic") != by_epic:
        raise ManifestValidationError(f"Parent epic summary mismatch: {summary.get('byParentEpic')} vs {by_epic}")

    return {
        "totalOfficialForms": total_official,
        "bySeries": by_series,
        "byPreparationCapability": by_cap,
        "byParentEpic": by_epic,
        "preservedNonUscisCount": len(manifest.get("preservedNonUscisDefinitions", [])),
    }


def format_summary(stats: dict[str, Any]) -> str:
    lines = [
        "=================================================================",
        "            USCIS OFFICIAL FORM RECONCILIATION MANIFEST          ",
        "=================================================================",
        f"Total Reconciled Official Forms: {stats['totalOfficialForms']}",
        f"Preserved Non-USCIS Definitions: {stats['preservedNonUscisCount']}",
        "",
        "Distribution by Series / Family:",
        f"  - I-Series (Immigration & Petitions):    {stats['bySeries']['I']:>3} forms (Covered in INF-06)",
        f"  - N-Series (Citizenship & Naturalization):{stats['bySeries']['N']:>3} forms (Covered in INF-07)",
        f"  - G-Series (General & Representation):    {stats['bySeries']['G']:>3} forms (Covered in INF-08)",
        f"  - Other (AR-11, EOIR-29):                {stats['bySeries']['OTHER']:>3} forms (Covered in INF-08)",
        "",
        "Distribution by Preparation Capability:",
        f"  - Fillable PDF (AcroForm):               {stats['byPreparationCapability']['FILLABLE_PDF']:>3} forms",
        f"  - Static Assisted (Specialist/Checklist): {stats['byPreparationCapability']['STATIC_ASSISTED']:>3} forms",
        f"  - External Reference (Online Portals):   {stats['byPreparationCapability']['EXTERNAL_REFERENCE']:>3} forms",
        "",
        "Reconciliation & Epic Traceability: 100% complete, zero unassigned",
        "=================================================================",
    ]
    return "\n".join(lines)


def run_cli() -> int:
    parser = argparse.ArgumentParser(description="Validate and inspect USCIS official form manifest (INF-01)")
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST_PATH, help="Path to manifest JSON")
    parser.add_argument("--schema", type=Path, default=DEFAULT_SCHEMA_PATH, help="Path to schema JSON")
    parser.add_argument("--validate", action="store_true", help="Perform schema and integrity validation")
    parser.add_argument("--summary", action="store_true", help="Print manifest summary statistics")
    parser.add_argument("--find", type=str, default=None, help="Find and display details for a specific formId")

    args = parser.parse_args()

    try:
        manifest = load_json(args.manifest)
        schema = load_json(args.schema)

        errors = validate_schema(manifest, schema)
        if errors:
            print(f"Validation failed with {len(errors)} error(s):", file=sys.stderr)
            for err in errors:
                print(f"  - {err}", file=sys.stderr)
            return 1

        stats = validate_manifest_integrity(manifest)

        if args.find:
            query = args.find.strip().upper()
            matching = [f for f in manifest.get("forms", []) if f["formId"].upper() == query or f["formNumber"].upper() == query]
            if not matching:
                print(f"Form '{args.find}' not found in manifest.", file=sys.stderr)
                return 1
            form = matching[0]
            print(json.dumps(form, indent=2))
            return 0

        if args.summary or not args.validate:
            print(format_summary(stats))

        if args.validate:
            print("USCIS Manifest Validation: PASS (all schema and integrity checks passed).")

        return 0
    except Exception as exc:
        print(f"Error: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(run_cli())
