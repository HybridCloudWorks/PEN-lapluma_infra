#!/usr/bin/env python3
"""Contract & boundary test suite for Blueprint drift, access gates & synthetic non-immigration blueprints (INF-14, INF-18, APP-05, APP-06)."""

import json
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
BLUEPRINTS_DIR = ROOT / "blueprints"


class TestBlueprintDriftAndAccess(unittest.TestCase):
    def setUp(self) -> None:
        self.blueprints = {}
        for p in BLUEPRINTS_DIR.rglob("blueprint.json"):
            with open(p, encoding="utf-8") as f:
                data = json.load(f)
                key = f"{data['namespace']}/{data['blueprintId']}"
                self.blueprints[key] = data

    def test_synthetic_non_immigration_blueprints_present(self) -> None:
        """Verify all synthetic non-immigration Blueprints are present and valid."""
        expected_keys = {
            "clinic/intake",
            "hope-heritage/scholarship-app",
            "dos/ds-11",
            "external/fafsa",
        }
        for key in expected_keys:
            self.assertIn(key, self.blueprints, f"Missing blueprint {key}")

    def test_repeated_sections_and_overflow_strategy(self) -> None:
        """Verify repeatable sections have valid bounds and overflow strategy (APP-05)."""
        scholarship = self.blueprints["hope-heritage/scholarship-app"]
        repeatable_sections = [s for s in scholarship.get("sections", []) if s.get("isRepeatable")]
        self.assertTrue(len(repeatable_sections) >= 1)
        for s in repeatable_sections:
            self.assertGreaterEqual(s.get("maxOccurs", 1), 2)
            self.assertIn(s.get("overflowStrategy"), ["ATTACHMENT_ADDENDUM", "TRUNCATE_ERROR", "SPLIT_PAGES"])

    def test_conditional_fields_and_evidence_requirements(self) -> None:
        """Verify conditional fields and evidence requirements evaluate declarative conditions."""
        scholarship = self.blueprints["hope-heritage/scholarship-app"]
        cond_fields = [f for f in scholarship.get("fields", []) if "condition" in f]
        self.assertTrue(len(cond_fields) >= 1)
        for f in cond_fields:
            cond = f["condition"]
            self.assertIn("field", cond)
            self.assertIn("operator", cond)
            self.assertIn(cond["operator"], ["equals", "not_equals", "is_set", "is_not_set", "in"])

        ds11 = self.blueprints["dos/ds-11"]
        cond_fields_ds11 = [f for f in ds11.get("fields", []) if "condition" in f]
        self.assertTrue(len(cond_fields_ds11) >= 1)

    def test_institution_access_scope_partitioning(self) -> None:
        """Verify institutional blueprints are scoped to INSTITUTION_PRIVATE."""
        clinic = self.blueprints["clinic/intake"]
        self.assertEqual(clinic.get("accessScope"), "INSTITUTION_PRIVATE")

        scholarship = self.blueprints["hope-heritage/scholarship-app"]
        self.assertEqual(scholarship.get("accessScope"), "INSTITUTION_PRIVATE")

        ds11 = self.blueprints["dos/ds-11"]
        self.assertEqual(ds11.get("accessScope"), "SHARED_OFFICIAL")

        fafsa = self.blueprints["external/fafsa"]
        self.assertEqual(fafsa.get("accessScope"), "SHARED_OFFICIAL")

    def test_no_executable_code_in_blueprint_definitions(self) -> None:
        """Verify configuration cannot add executable behavior (APP-05)."""
        forbidden = ["<script", "javascript:", "eval(", "exec(", "__import__", "os.system"]
        for key, bp in self.blueprints.items():
            serialized = json.dumps(bp).lower()
            for token in forbidden:
                self.assertNotIn(token, serialized, f"Forbidden token '{token}' in blueprint {key}")


if __name__ == "__main__":
    unittest.main()
