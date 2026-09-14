#!/usr/bin/env python3
"""Contract and unit tests for the LaPluma Blueprint CLI and schema validator (INF-17)."""

from __future__ import annotations

import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

from blueprint_cli import (
    DEFAULT_SCHEMA_PATH,
    ROOT,
    BlueprintValidator,
    build_parser,
    cmd_init,
    cmd_package,
    cmd_validate,
)


class TestBlueprintCli(unittest.TestCase):
    """Test suite for blueprint schema, validation logic, and CLI commands."""

    def setUp(self) -> None:
        self.validator = BlueprintValidator(DEFAULT_SCHEMA_PATH)
        self.sample_blueprint = {
            "$schema": "https://json-schema.org/draft/2020-12/schema",
            "namespace": "test_ns",
            "blueprintId": "test_form",
            "revision": 1,
            "title": "Test Form",
            "issuer": "Test Agency",
            "officialEditionDate": "2026-01-01",
            "preparationMode": "FILLABLE_PDF",
            "artifactType": "OFFICIAL_PDF",
            "accessScope": "SHARED_OFFICIAL",
            "source": {
                "url": "https://example.gov/test.pdf",
                "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
                "lastVerified": "2026-09-13T00:00:00Z"
            },
            "fields": [
                {
                    "canonicalPath": "person.first_name",
                    "type": "string",
                    "label": "First Name",
                    "required": True,
                    "attributedRole": "APPLICANT"
                },
                {
                    "canonicalPath": "person.age",
                    "type": "number",
                    "label": "Age",
                    "required": True,
                    "attributedRole": "APPLICANT"
                }
            ],
            "validationRules": [
                {
                    "ruleId": "age_range",
                    "type": "range",
                    "expression": "person.age:18:120",
                    "errorMessage": "Age must be at least 18"
                }
            ],
            "evidenceRequirements": [
                {
                    "code": "ID_CARD",
                    "title": "Identity Card",
                    "attributedRole": "APPLICANT",
                    "isConditional": False,
                    "acceptedMimeTypes": ["application/pdf"]
                }
            ],
            "syntheticFixtures": [
                {
                    "fixtureName": "valid_adult",
                    "expectedValid": True,
                    "inputValues": {
                        "person.first_name": "Alice",
                        "person.age": 25
                    }
                },
                {
                    "fixtureName": "invalid_underage",
                    "expectedValid": False,
                    "inputValues": {
                        "person.first_name": "Bob",
                        "person.age": 16
                    }
                }
            ]
        }

    def test_schema_file_exists_and_valid_json(self) -> None:
        self.assertTrue(DEFAULT_SCHEMA_PATH.is_file(), "Schema file must exist")
        data = json.loads(DEFAULT_SCHEMA_PATH.read_text(encoding="utf-8"))
        self.assertEqual(data.get("title"), "DocumentBlueprint")
        self.assertIn("namespace", data.get("required", []))

    def test_sample_blueprints_all_pass_validation(self) -> None:
        blueprints_dir = ROOT / "blueprints"
        blueprint_files = list(blueprints_dir.glob("**/blueprint.json"))
        self.assertGreaterEqual(len(blueprint_files), 3, "Expected at least 3 sample blueprints")

        for bp_file in blueprint_files:
            failures = self.validator.validate_file(bp_file)
            self.assertEqual(failures, [], f"Sample blueprint {bp_file} failed validation: {failures}")

    def test_validate_base_sample(self) -> None:
        failures = self.validator.validate_data(self.sample_blueprint)
        self.assertEqual(failures, [])

    def test_catches_missing_required_property(self) -> None:
        for required_prop in ("namespace", "blueprintId", "revision", "title", "issuer", "preparationMode", "fields"):
            bad_data = copy.deepcopy(self.sample_blueprint)
            bad_data.pop(required_prop)
            failures = self.validator.validate_data(bad_data)
            self.assertTrue(any(required_prop in f for f in failures), f"Should flag missing {required_prop}")

    def test_catches_invalid_identifiers(self) -> None:
        bad_data = copy.deepcopy(self.sample_blueprint)
        bad_data["namespace"] = "INVALID NAMESPACE WITH SPACES"
        failures = self.validator.validate_data(bad_data)
        self.assertTrue(any("namespace" in f for f in failures))

        bad_data = copy.deepcopy(self.sample_blueprint)
        bad_data["blueprintId"] = "x"  # Too short (min 2)
        failures = self.validator.validate_data(bad_data)
        self.assertTrue(any("blueprintId" in f for f in failures))

    def test_catches_invalid_mode_or_scope(self) -> None:
        bad_data = copy.deepcopy(self.sample_blueprint)
        bad_data["preparationMode"] = "INVALID_MODE"
        failures = self.validator.validate_data(bad_data)
        self.assertTrue(any("preparationMode" in f for f in failures))

        bad_data = copy.deepcopy(self.sample_blueprint)
        bad_data["accessScope"] = "PUBLIC_GLOBAL"
        failures = self.validator.validate_data(bad_data)
        self.assertTrue(any("accessScope" in f for f in failures))

    def test_catches_bad_sha256(self) -> None:
        bad_data = copy.deepcopy(self.sample_blueprint)
        bad_data["source"]["sha256"] = "not-a-sha256"
        failures = self.validator.validate_data(bad_data)
        self.assertTrue(any("sha256" in f for f in failures))

    def test_catches_bad_canonical_path(self) -> None:
        bad_data = copy.deepcopy(self.sample_blueprint)
        bad_data["fields"][0]["canonicalPath"] = "bad/path/with/slashes"
        failures = self.validator.validate_data(bad_data)
        self.assertTrue(any("canonicalPath" in f for f in failures))

    def test_catches_duplicate_canonical_path(self) -> None:
        bad_data = copy.deepcopy(self.sample_blueprint)
        bad_data["fields"].append({
            "canonicalPath": "person.first_name",  # Duplicate
            "type": "string",
            "label": "Duplicate First Name",
            "required": False
        })
        failures = self.validator.validate_data(bad_data)
        self.assertTrue(any("Duplicate canonicalPath" in f for f in failures))

    def test_synthetic_fixture_expectation_mismatch(self) -> None:
        bad_data = copy.deepcopy(self.sample_blueprint)
        # Change expectedValid to False for a completely valid fixture
        bad_data["syntheticFixtures"][0]["expectedValid"] = False
        failures = self.validator.validate_data(bad_data)
        self.assertTrue(any("expectation mismatch" in f for f in failures))

    def test_cli_init_command(self) -> None:
        parser = build_parser()
        with tempfile.TemporaryDirectory() as tmp_dir:
            out_file = Path(tmp_dir) / "test_init_blueprint.json"
            args = parser.parse_args([
                "init",
                "--namespace", "custom_dept",
                "--id", "custom_doc",
                "--title", "Custom Document Title",
                "--issuer", "State Department",
                "-o", str(out_file)
            ])
            res = cmd_init(args)
            self.assertEqual(res, 0)
            self.assertTrue(out_file.is_file())

            # Validate the newly scaffolded file
            val_failures = self.validator.validate_file(out_file)
            self.assertEqual(val_failures, [])

    def test_cli_validate_command(self) -> None:
        parser = build_parser()
        # Validate sample blueprints dir
        args = parser.parse_args(["validate", str(ROOT / "blueprints")])
        res = cmd_validate(args)
        self.assertEqual(res, 0)

    def test_cli_package_command(self) -> None:
        parser = build_parser()
        sample_path = ROOT / "blueprints/official/uscis/i-130/blueprint.json"
        with tempfile.TemporaryDirectory() as tmp_dir:
            out_pkg = Path(tmp_dir) / "bundle.json"
            args = parser.parse_args(["package", str(sample_path), "-o", str(out_pkg)])
            res = cmd_package(args)
            self.assertEqual(res, 0)
            self.assertTrue(out_pkg.is_file())

            pkg_data = json.loads(out_pkg.read_text(encoding="utf-8"))
            self.assertEqual(pkg_data.get("manifestVersion"), "1.0.0")
            self.assertEqual(pkg_data.get("namespace"), "uscis")
            self.assertEqual(pkg_data.get("blueprintId"), "i-130")
            self.assertRegex(pkg_data.get("sha256", ""), r"^[a-f0-9]{64}$")


if __name__ == "__main__":
    unittest.main()
