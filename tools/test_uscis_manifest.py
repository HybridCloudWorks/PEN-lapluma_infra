"""Unit tests for USCIS official form manifest and validator (INF-01)."""

import json
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
ROOT_DIR = TOOLS_DIR.parent

for p in (str(TOOLS_DIR), str(ROOT_DIR)):
    if p not in sys.path:
        sys.path.insert(0, p)

try:
    from tools.uscis_manifest_validator import (
        DEFAULT_MANIFEST_PATH,
        DEFAULT_SCHEMA_PATH,
        ManifestValidationError,
        load_json,
        validate_manifest_integrity,
        validate_schema,
    )
except ImportError:
    from uscis_manifest_validator import (
        DEFAULT_MANIFEST_PATH,
        DEFAULT_SCHEMA_PATH,
        ManifestValidationError,
        load_json,
        validate_manifest_integrity,
        validate_schema,
    )



class TestUSCISManifest(unittest.TestCase):
    def setUp(self):
        self.manifest = load_json(DEFAULT_MANIFEST_PATH)
        self.schema = load_json(DEFAULT_SCHEMA_PATH)

    def test_official_manifest_passes_schema_validation(self):
        errors = validate_schema(self.manifest, self.schema)
        self.assertEqual(errors, [], f"Schema validation produced errors: {errors}")

    def test_official_manifest_integrity_and_summaries(self):
        stats = validate_manifest_integrity(self.manifest)
        self.assertEqual(stats["totalOfficialForms"], 117)
        self.assertEqual(stats["bySeries"]["I"], 91)
        self.assertEqual(stats["bySeries"]["N"], 10)
        self.assertEqual(stats["bySeries"]["G"], 14)
        self.assertEqual(stats["bySeries"]["OTHER"], 2)
        self.assertEqual(stats["preservedNonUscisCount"], 4)

    def test_no_duplicate_form_ids(self):
        forms = self.manifest.get("forms", [])
        seen = set()
        for f in forms:
            fid = f["formId"]
            self.assertNotIn(fid, seen, f"Duplicate formId: {fid}")
            seen.add(fid)

    def test_all_forms_have_https_source_urls(self):
        forms = self.manifest.get("forms", [])
        for f in forms:
            url = f["sourceUrl"]
            self.assertTrue(url.startswith("https://"), f"Form {f['formId']} has non-https url: {url}")

    def test_parent_epic_mapping_by_series(self):
        forms = self.manifest.get("forms", [])
        for f in forms:
            series = f["series"]
            epic = f["parentEpic"]
            if series == "I":
                self.assertEqual(epic, "INF-06", f"Form {f['formId']} expected INF-06")
            elif series == "N":
                self.assertEqual(epic, "INF-07", f"Form {f['formId']} expected INF-07")
            elif series in ("G", "OTHER"):
                self.assertEqual(epic, "INF-08", f"Form {f['formId']} expected INF-08")

    def test_preserved_non_uscis_definitions(self):
        preserved = self.manifest.get("preservedNonUscisDefinitions", [])
        doc_ids = {p["documentId"] for p in preserved}
        self.assertIn("DS-11", doc_ids)
        self.assertIn("FAFSA", doc_ids)
        self.assertIn("CLINIC-INTAKE", doc_ids)
        self.assertIn("SCHOLARSHIP-APP", doc_ids)

        for item in preserved:
            self.assertTrue(len(item["title"]) > 0)
            self.assertTrue(len(item["issuer"]) > 0)
            self.assertTrue(len(item["namespace"]) > 0)
            self.assertIn(item["preparationMode"], ("FILLABLE_PDF", "STATIC_ASSISTED", "EXTERNAL_REFERENCE"))

    def test_schema_validator_catches_errors(self):
        # Missing required field
        corrupted = dict(self.manifest)
        corrupted["forms"] = [dict(f) for f in self.manifest["forms"][:2]]
        del corrupted["forms"][0]["title"]
        errors = validate_schema(corrupted, self.schema)
        self.assertTrue(any("missing required field: 'title'" in e for e in errors))

        # Duplicate ID
        dup_manifest = dict(self.manifest)
        dup_manifest["forms"] = [
            dict(self.manifest["forms"][0]),
            dict(self.manifest["forms"][0]),
        ]
        errors = validate_schema(dup_manifest, self.schema)
        self.assertTrue(any("Duplicate formId detected" in e for e in errors))

        # Invalid series
        invalid_series = dict(self.manifest)
        f_bad = dict(self.manifest["forms"][0])
        f_bad["series"] = "XYZ"
        invalid_series["forms"] = [f_bad]
        errors = validate_schema(invalid_series, self.schema)
        self.assertTrue(any("invalid series: 'XYZ'" in e for e in errors))

    def test_integrity_validator_catches_mismatches(self):
        bad_summary = dict(self.manifest)
        bad_summary["summary"] = dict(self.manifest["summary"])
        bad_summary["summary"]["totalOfficialForms"] = 999
        with self.assertRaises(ManifestValidationError):
            validate_manifest_integrity(bad_summary)


if __name__ == "__main__":
    unittest.main()
