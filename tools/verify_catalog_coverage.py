#!/usr/bin/env python3
"""Authoritative validator for USCIS catalog coverage, searchability, and official guidance (INF-06..09)."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
MANIFEST_PATH = ROOT / "contracts" / "uscis-official-manifest.json"
GUIDANCE_PATH = ROOT / "contracts" / "uscis-official-guidance.json"
GUIDANCE_SCHEMA_PATH = ROOT / "contracts" / "schemas" / "document-guidance.schema.json"
OPENAPI_PATH = ROOT / "contracts" / "openapi" / "document-library.yaml"
LIBRARY_SERVICE_PATH = ROOT / "src" / "core-api" / "LibraryAccessService.cs"


def load_json(path: Path) -> Any:
    if not path.exists():
        raise FileNotFoundError(f"Missing required file: {path}")
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


def verify_manifest_coverage(failures: list[str]) -> dict[str, Any]:
    data = load_json(MANIFEST_PATH)
    forms = data.get("forms", [])
    series_counts = {"I": 0, "N": 0, "G": 0, "OTHER": 0}

    for f in forms:
        s = f.get("series")
        if s in series_counts:
            series_counts[s] += 1
        else:
            failures.append(f"Unknown form series '{s}' on form {f.get('formId')}")

    if len(forms) != 117:
        failures.append(f"Expected 117 forms in manifest, found {len(forms)}")
    if series_counts["I"] != 91:
        failures.append(f"Expected 91 I-series forms, found {series_counts['I']}")
    if series_counts["N"] != 10:
        failures.append(f"Expected 10 N-series forms, found {series_counts['N']}")
    if series_counts["G"] != 14:
        failures.append(f"Expected 14 G-series forms, found {series_counts['G']}")
    if series_counts["OTHER"] != 2:
        failures.append(f"Expected 2 OTHER-series forms (AR-11, EOIR-29), found {series_counts['OTHER']}")

    # Check preserved non-USCIS definitions
    preserved = data.get("preservedNonUscisDefinitions", [])
    if len(preserved) != 4:
        failures.append(f"Expected 4 preserved non-USCIS definitions, found {len(preserved)}")

    doc_ids = {p.get("documentId") for p in preserved}
    expected_ids = {"DS-11", "FAFSA", "CLINIC-INTAKE", "SCHOLARSHIP-APP"}
    missing = expected_ids - doc_ids
    if missing:
        failures.append(f"Missing required preserved non-USCIS definitions: {missing}")

    return {"totalForms": len(forms), "seriesCounts": series_counts, "preservedCount": len(preserved)}


def verify_official_guidance(failures: list[str]) -> dict[str, Any]:
    data = load_json(GUIDANCE_PATH)
    schema = load_json(GUIDANCE_SCHEMA_PATH)

    guidance_list = data.get("guidance", [])
    if len(guidance_list) < 117:
        failures.append(f"Expected at least 117 guidance entries, found {len(guidance_list)}")

    seen_forms = set()
    fee_guessing_count = 0
    zero_guessing_null_count = 0
    statutory_fee_count = 0

    for g in guidance_list:
        fid = g.get("formId")
        if not fid:
            failures.append("Guidance entry missing formId")
            continue
        seen_forms.add(fid)

        # URLs must be HTTPS
        instr_url = g.get("officialInstructionsUrl", "")
        if not instr_url.startswith("https://"):
            failures.append(f"Guidance {fid} instructions URL must be HTTPS: {instr_url}")

        fee_url = g.get("feeScheduleCitationUrl", "")
        if not fee_url.startswith("https://"):
            failures.append(f"Guidance {fid} fee citation URL must be HTTPS: {fee_url}")

        # Zero fee guessing check
        cents = g.get("feeUsdCents")
        fee_notes = g.get("feeNotes", "")
        if cents is None:
            zero_guessing_null_count += 1
            is_uscis = g.get("authority") == "USCIS"
            if is_uscis and "G-1055" not in fee_notes and "G-1055" not in fee_url and "0" not in fee_notes and "free" not in fee_notes.lower():
                failures.append(f"Guidance {fid} has null feeUsdCents but missing G-1055 citation in feeNotes")
        elif isinstance(cents, int):
            if cents < 0:
                failures.append(f"Guidance {fid} has negative fee: {cents}")
            else:
                statutory_fee_count += 1
        else:
            failures.append(f"Guidance {fid} has invalid feeUsdCents type: {type(cents)}")

        # Check evidence checklist
        evidence = g.get("evidenceChecklist")
        if not isinstance(evidence, list) or len(evidence) == 0:
            failures.append(f"Guidance {fid} missing non-empty evidenceChecklist")

    # Confirm key forms have guidance
    key_forms = ["I-130", "I-485", "I-765", "N-400", "G-28", "AR-11", "EOIR-29"]
    for kf in key_forms:
        if kf not in seen_forms:
            failures.append(f"Required key form missing official guidance: {kf}")

    return {
        "guidanceEntries": len(guidance_list),
        "zeroGuessingNullCount": zero_guessing_null_count,
        "statutoryFeeCount": statutory_fee_count,
    }


def verify_openapi_and_core_wiring(failures: list[str]) -> None:
    if not OPENAPI_PATH.exists():
        failures.append(f"OpenAPI file missing: {OPENAPI_PATH}")
        return

    openapi_text = OPENAPI_PATH.read_text(encoding="utf-8")
    if "/blueprints/{namespace}/{blueprintId}/guidance" not in openapi_text:
        failures.append("OpenAPI contract missing endpoint: /blueprints/{namespace}/{blueprintId}/guidance")
    if "DocumentGuidance" not in openapi_text:
        failures.append("OpenAPI contract missing DocumentGuidance schema definition")

    if not LIBRARY_SERVICE_PATH.exists():
        failures.append(f"LibraryAccessService.cs missing: {LIBRARY_SERVICE_PATH}")
        return

    service_text = LIBRARY_SERVICE_PATH.read_text(encoding="utf-8")
    if "GetDocumentGuidanceAsync" not in service_text:
        failures.append("LibraryAccessService.cs missing GetDocumentGuidanceAsync implementation")
    if "SeedFromManifests" not in service_text:
        failures.append("LibraryAccessService.cs missing SeedFromManifests invocation")


def main() -> int:
    failures: list[str] = []
    print("[verify_catalog_coverage] Checking USCIS official form manifest...")
    manifest_stats = verify_manifest_coverage(failures)

    print("[verify_catalog_coverage] Checking source-cited official guidance...")
    guidance_stats = verify_official_guidance(failures)

    print("[verify_catalog_coverage] Checking OpenAPI and core service integration...")
    verify_openapi_and_core_wiring(failures)

    if failures:
        print(f"\nERROR: {len(failures)} verification failures found:", file=sys.stderr)
        for f in failures:
            print(f"  - {f}", file=sys.stderr)
        return 1

    print("\nSUCCESS: All catalog coverage and guidance checks passed:")
    print(f"  - Official USCIS Forms: {manifest_stats['totalForms']} (I: {manifest_stats['seriesCounts']['I']}, N: {manifest_stats['seriesCounts']['N']}, G: {manifest_stats['seriesCounts']['G']}, OTHER: {manifest_stats['seriesCounts']['OTHER']})")
    print(f"  - Preserved Non-USCIS Definitions: {manifest_stats['preservedCount']}")
    print(f"  - Source-Cited Guidance Entries: {guidance_stats['guidanceEntries']}")
    print(f"  - Zero Fee Guessing Citations: {guidance_stats['zeroGuessingNullCount']} variable (null cents), {guidance_stats['statutoryFeeCount']} statutory")
    return 0


if __name__ == "__main__":
    sys.exit(main())
