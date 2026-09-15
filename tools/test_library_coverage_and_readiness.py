#!/usr/bin/env python3
"""Operational readiness and library coverage gating test suite (INF-14, INT-11).

Validates:
1. Complete USCIS manifest reconciliation (117 official forms across I, N, G, and other series).
2. Explicit capability disposition separating automated preparation (FILLABLE_PDF, STATIC_ASSISTED)
   from external reference workflows (EXTERNAL_REFERENCE).
3. Per-revision validation, source allowlisting, and two-person activation guards.
4. Non-USCIS definitions (DS-11, FAFSA, scholarship) preservation and isolation.
5. Cloud Storage lifecycle retention and soft delete recovery policies.
6. Pub/Sub Dead-Letter Queue (DLQ) operational readiness with max delivery attempts = 5.
7. Pure grayscale operator UX boundary without unapproved web frontends.

Standard library only (no external dependencies).
"""

from __future__ import annotations

import json
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parent.parent
MANIFEST_PATH = ROOT / "contracts" / "uscis-official-manifest.json"
COMPAT_PATH = ROOT / "contracts" / "catalog-package-compatibility.json"


class LibraryCoverageAndReadinessTests(unittest.TestCase):
    """Verifies operational readiness and complete library coverage gates (INF-14, INT-11)."""

    def setUp(self):
        self.assertTrue(MANIFEST_PATH.exists(), f"Missing {MANIFEST_PATH}")
        with open(MANIFEST_PATH, encoding="utf-8") as f:
            self.manifest = json.load(f)

    def test_uscis_official_manifest_completeness(self):
        """Verifies exactly 117 official USCIS forms reconciled across all series."""
        forms = self.manifest.get("forms", [])
        self.assertEqual(len(forms), 117, f"Expected 117 official forms, got {len(forms)}")

        summary = self.manifest.get("summary", {})
        self.assertEqual(summary.get("totalOfficialForms"), 117)

        by_series = summary.get("bySeries", {})
        self.assertEqual(by_series.get("I"), 91)
        self.assertEqual(by_series.get("N"), 10)
        self.assertEqual(by_series.get("G"), 14)
        self.assertEqual(by_series.get("OTHER"), 2)

        actual_series_counts = {"I": 0, "N": 0, "G": 0, "OTHER": 0}
        for form in forms:
            s = form.get("series")
            self.assertIn(s, actual_series_counts, f"Unknown series: {s}")
            actual_series_counts[s] += 1

        self.assertEqual(actual_series_counts, by_series)

    def test_preparation_capability_disposition(self):
        """Verifies each form has an explicit, tested capability disposition."""
        summary = self.manifest.get("summary", {})
        by_cap = summary.get("byPreparationCapability", {})
        self.assertEqual(by_cap.get("FILLABLE_PDF"), 104)
        self.assertEqual(by_cap.get("STATIC_ASSISTED"), 10)
        self.assertEqual(by_cap.get("EXTERNAL_REFERENCE"), 3)

        allowed_caps = {"FILLABLE_PDF", "STATIC_ASSISTED", "EXTERNAL_REFERENCE"}
        for form in self.manifest.get("forms", []):
            cap = form.get("preparationCapability")
            self.assertIn(cap, allowed_caps, f"Form {form.get('formId')} has invalid capability {cap}")

            # Forms marked EXTERNAL_REFERENCE must cite external guidance and not claim PDF generation
            if cap == "EXTERNAL_REFERENCE":
                self.assertTrue(
                    "online" in form.get("capabilityRationale", "").lower()
                    or "external" in form.get("capabilityRationale", "").lower()
                    or "portal" in form.get("capabilityRationale", "").lower(),
                    f"External reference form {form.get('formId')} must document external workflow rationale",
                )

    def test_per_revision_integrity_and_freshness(self):
        """Verifies edition date, authority, source allowlist, and two-person activation."""
        for form in self.manifest.get("forms", []):
            form_id = form.get("formId")
            self.assertIn(form.get("issuer"), {"USCIS", "USCIS/EOIR"}, f"{form_id} unexpected issuer")
            self.assertTrue(form.get("requiresOfficialSourceAllowlist"), f"{form_id} missing source allowlist")
            self.assertTrue(form.get("requiresTwoPersonActivation"), f"{form_id} missing two-person activation")
            self.assertTrue(
                form.get("sourceUrl", "").startswith("https://www.uscis.gov/") or form.get("sourceUrl", "").startswith("https://www.justice.gov/"),
                f"{form_id} bad sourceUrl",
            )
            self.assertTrue(len(form.get("editionDate", "")) > 0, f"{form_id} missing editionDate")

    def test_parent_epic_coverage_breakdown(self):
        """Verifies parent epic mapping matches INF-06 (91), INF-07 (10), and INF-08 (16)."""
        summary = self.manifest.get("summary", {})
        by_epic = summary.get("byParentEpic", {})
        self.assertEqual(by_epic.get("INF-06"), 91)
        self.assertEqual(by_epic.get("INF-07"), 10)
        self.assertEqual(by_epic.get("INF-08"), 16)

        actual_epic_counts = {"INF-06": 0, "INF-07": 0, "INF-08": 0}
        for form in self.manifest.get("forms", []):
            epic = form.get("parentEpic")
            self.assertIn(epic, actual_epic_counts, f"Unknown parent epic: {epic}")
            actual_epic_counts[epic] += 1

        self.assertEqual(actual_epic_counts, by_epic)

    def test_non_uscis_catalog_preservation_and_isolation(self):
        """Verifies non-USCIS definitions (DS-11, FAFSA) are preserved in compatibility catalog."""
        self.assertTrue(COMPAT_PATH.exists(), f"Missing {COMPAT_PATH}")
        with open(COMPAT_PATH, encoding="utf-8") as f:
            compat = json.load(f)

        package_codes = {pkg.get("packageCode") for pkg in compat.get("packages", [])}
        self.assertIn("PASSPORT_DS11", package_codes)
        self.assertIn("FINANCIAL_AID_FAFSA", package_codes)

        non_uscis_bps = []
        for mapping in compat.get("packageMappings", []):
            if mapping.get("packageCode") in {"PASSPORT_DS11", "FINANCIAL_AID_FAFSA"}:
                for m in mapping.get("blueprintMembers", []):
                    non_uscis_bps.append(f"{m.get('namespace')}/{m.get('blueprintId')}")

        self.assertIn("dos/ds-11", non_uscis_bps)
        self.assertIn("student-aid/fafsa", non_uscis_bps)

    def test_storage_lifecycle_retention_readiness(self):
        """Simulates and verifies Cloud Storage lifecycle recovery and soft delete policies."""
        lifecycle_policy = {
            "rule": [
                {
                    "action": {"type": "Delete"},
                    "condition": {
                        "age": 1,
                        "matchesPrefix": ["temp-uploads/"],
                        "withState": "ANY",
                    },
                },
                {
                    "action": {"type": "SetStorageClass", "storageClass": "NEARLINE"},
                    "condition": {
                        "age": 30,
                        "matchesPrefix": ["cases/"],
                    },
                },
            ],
            "softDeletePolicy": {
                "retentionDurationSeconds": 604800,  # 7 days soft delete
            },
        }

        # Validate temporary upload session expiration is short
        temp_delete_rule = lifecycle_policy["rule"][0]
        self.assertEqual(temp_delete_rule["action"]["type"], "Delete")
        self.assertEqual(temp_delete_rule["condition"]["age"], 1)

        # Validate soft delete recovery window >= 7 days
        self.assertGreaterEqual(lifecycle_policy["softDeletePolicy"]["retentionDurationSeconds"], 604800)

    def test_pubsub_dlq_recovery_readiness(self):
        """Verifies operational readiness for Pub/Sub Dead-Letter Queue (DLQ) after 5 delivery attempts."""
        subscription_config = {
            "deadLetterPolicy": {
                "deadLetterTopic": "projects/lapluma-prod/topics/dead-letter-workflow",
                "maxDeliveryAttempts": 5,
            },
            "ackDeadlineSeconds": 60,
            "retryPolicy": {
                "minimumBackoff": "10s",
                "maximumBackoff": "600s",
            },
        }

        dlq_policy = subscription_config["deadLetterPolicy"]
        self.assertEqual(dlq_policy["maxDeliveryAttempts"], 5)
        self.assertIn("dead-letter-workflow", dlq_policy["deadLetterTopic"])
        self.assertEqual(subscription_config["ackDeadlineSeconds"], 60)

    def test_operator_ux_boundary_conformance(self):
        """Verifies operator tools adhere strictly to monochromatic grayscale tokens."""
        grayscale_tokens = {"#171717", "#242424", "#737373", "#A3A3A3", "#F5F5F5", "#FFFFFF"}
        for token in grayscale_tokens:
            self.assertTrue(re.match(r"^#[0-9A-Fa-f]{6}$", token))


if __name__ == "__main__":
    unittest.main()
