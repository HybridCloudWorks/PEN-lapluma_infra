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

    def test_rejection_of_executable_code_in_blueprint(self) -> None:
        """Verify that script injection or eval patterns in any field are strictly rejected."""
        malicious_patterns = [
            "<script>alert('xss')</script>",
            "javascript:void(0)",
            "eval('dangerous()')",
            "exec('something')",
            "__proto__.polluted = true",
        ]
        for pattern in malicious_patterns:
            bad_data = copy.deepcopy(self.sample_blueprint)
            bad_data["title"] = f"Form Title {pattern}"
            failures = self.validator.validate_data(bad_data)
            self.assertTrue(
                any("Executable code or injection pattern rejected" in f for f in failures),
                f"Expected injection failure for pattern {pattern!r}, got: {failures}",
            )

    def test_rejection_of_unsafe_source_urls(self) -> None:
        """Verify that HTTP, loopback, private IP, and cloud metadata source URLs are rejected."""
        unsafe_urls = [
            "http://example.com/test.pdf",
            "https://localhost/test.pdf",
            "https://127.0.0.1/test.pdf",
            "https://169.254.169.254/latest/meta-data",
            "https://metadata.google.internal/computeMetadata/v1",
            "https://10.0.0.1/internal.pdf",
        ]
        for url in unsafe_urls:
            bad_data = copy.deepcopy(self.sample_blueprint)
            bad_data["source"]["url"] = url
            failures = self.validator.validate_data(bad_data)
            self.assertTrue(
                any("source.url" in f for f in failures),
                f"Expected source URL failure for {url!r}, got: {failures}",
            )

    def test_rejection_of_arbitrary_expressions_in_validation_rules(self) -> None:
        """Verify that unapproved or arbitrary expression grammars are rejected."""
        bad_data = copy.deepcopy(self.sample_blueprint)
        bad_data["validationRules"].append({
            "ruleId": "arbitrary_eval_rule",
            "type": "regex",
            "expression": "person.age.is_even()",  # missing required 'path:pattern' format
            "errorMessage": "Arbitrary function call"
        })
        failures = self.validator.validate_data(bad_data)
        self.assertTrue(
            any("regex expression must be 'canonicalPath:pattern'" in f for f in failures)
        )

    def test_rejection_of_reserved_official_namespace_by_institution_private(self) -> None:
        """Verify that institution private blueprints cannot overwrite or claim official agency namespaces."""
        bad_data = copy.deepcopy(self.sample_blueprint)
        bad_data["namespace"] = "uscis"
        bad_data["accessScope"] = "INSTITUTION_PRIVATE"
        failures = self.validator.validate_data(bad_data)
        self.assertTrue(
            any("cannot redefine reserved official agency namespace" in f for f in failures)
        )

    def test_declarative_sections_and_conditional_required(self) -> None:
        """Verify sections, overflow strategies, and conditional_required rules."""
        bp = copy.deepcopy(self.sample_blueprint)
        bp["sections"] = [
            {
                "sectionId": "sec_main",
                "title": "Main Section",
                "overflowStrategy": "ATTACHMENT_ADDENDUM"
            },
            {
                "sectionId": "sec_spouse",
                "title": "Spouse Section",
                "condition": {
                    "field": "person.is_married",
                    "operator": "equals",
                    "value": True
                }
            }
        ]
        bp["fields"].append({
            "canonicalPath": "person.is_married",
            "sectionId": "sec_main",
            "type": "boolean",
            "label": "Is Married",
            "required": True,
            "attributedRole": "APPLICANT"
        })
        bp["fields"].append({
            "canonicalPath": "spouse.name",
            "sectionId": "sec_spouse",
            "type": "string",
            "label": "Spouse Name",
            "required": False,
            "attributedRole": "APPLICANT"
        })
        bp["validationRules"].append({
            "ruleId": "spouse_name_when_married",
            "type": "conditional_required",
            "expression": "when:person.is_married==True:then_required:spouse.name",
            "errorMessage": "Spouse name is required when married"
        })
        bp["syntheticFixtures"] = [
            {
                "fixtureName": "married_with_spouse_name",
                "expectedValid": True,
                "inputValues": {
                    "person.first_name": "Alice",
                    "person.age": 30,
                    "person.is_married": True,
                    "spouse.name": "Bob"
                }
            },
            {
                "fixtureName": "married_missing_spouse_name",
                "expectedValid": False,
                "inputValues": {
                    "person.first_name": "Alice",
                    "person.age": 30,
                    "person.is_married": True
                }
            },
            {
                "fixtureName": "unmarried_valid",
                "expectedValid": True,
                "inputValues": {
                    "person.first_name": "Alice",
                    "person.age": 30,
                    "person.is_married": False
                }
            }
        ]
        failures = self.validator.validate_data(bp)
        self.assertEqual(failures, [])

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

    def test_cli_review_rejects_self_approval(self) -> None:
        parser = build_parser()
        sample_path = ROOT / "blueprints/official/uscis/i-130/blueprint.json"
        args = parser.parse_args([
            "review", str(sample_path),
            "--author", "engineer_alice",
            "--reviewer", "engineer_alice"
        ])
        res = args.func(args)
        self.assertEqual(res, 1, "Self-approval must be rejected")

    def test_cli_review_success(self) -> None:
        parser = build_parser()
        sample_path = ROOT / "blueprints/official/uscis/i-130/blueprint.json"
        with tempfile.TemporaryDirectory() as tmp_dir:
            out_file = Path(tmp_dir) / "review.json"
            args = parser.parse_args([
                "review", str(sample_path),
                "--author", "engineer_alice",
                "--reviewer", "staff_bob",
                "-o", str(out_file)
            ])
            res = args.func(args)
            self.assertEqual(res, 0)
            data = json.loads(out_file.read_text(encoding="utf-8"))
            self.assertEqual(data["action"], "REVIEW_APPROVED")
            self.assertEqual(data["author"], "engineer_alice")
            self.assertEqual(data["reviewer"], "staff_bob")

    def test_cli_publish_rejects_self_approval(self) -> None:
        parser = build_parser()
        sample_path = ROOT / "blueprints/official/uscis/i-130/blueprint.json"
        args = parser.parse_args([
            "publish", str(sample_path),
            "--publisher", "lead_charlie",
            "--author", "engineer_alice",
            "--reviewer", "engineer_alice"
        ])
        res = args.func(args)
        self.assertEqual(res, 1, "Self-approved publication must be rejected")

    def test_cli_publish_success(self) -> None:
        parser = build_parser()
        sample_path = ROOT / "blueprints/official/uscis/i-130/blueprint.json"
        with tempfile.TemporaryDirectory() as tmp_dir:
            out_file = Path(tmp_dir) / "publication.json"
            args = parser.parse_args([
                "publish", str(sample_path),
                "--publisher", "lead_charlie",
                "--author", "engineer_alice",
                "--reviewer", "staff_bob",
                "-o", str(out_file)
            ])
            res = args.func(args)
            self.assertEqual(res, 0)
            data = json.loads(out_file.read_text(encoding="utf-8"))
            self.assertEqual(data["publicationState"], "PUBLISHED")
            self.assertRegex(data["manifestSha256"], r"^[a-f0-9]{64}$")
            self.assertEqual(data["reviewer"], "staff_bob")

    def test_cli_check_drift_detection(self) -> None:
        parser = build_parser()
        sample_path = ROOT / "blueprints/official/uscis/i-130/blueprint.json"
        # Match
        args_match = parser.parse_args([
            "check-drift", str(sample_path),
            "--observed-sha256", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        ])
        self.assertEqual(args_match.func(args_match), 0)

        # Drift mismatch
        args_mismatch = parser.parse_args([
            "check-drift", str(sample_path),
            "--observed-sha256", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        ])
        self.assertEqual(args_mismatch.func(args_mismatch), 2, "Source drift must return exit code 2 (quarantine)")

    def test_cli_rollback_manifest(self) -> None:
        parser = build_parser()
        sample_path = ROOT / "blueprints/official/uscis/i-130/blueprint.json"
        with tempfile.TemporaryDirectory() as tmp_dir:
            out_file = Path(tmp_dir) / "rollback.json"
            args = parser.parse_args([
                "rollback", str(sample_path),
                "--target-revision", "1",
                "--operator", "lead_charlie",
                "--reason", "Regressed field mapping on r2",
                "-o", str(out_file)
            ])
            res = args.func(args)
            self.assertEqual(res, 0)
            data = json.loads(out_file.read_text(encoding="utf-8"))
            self.assertEqual(data["action"], "ROLLED_BACK")
            self.assertEqual(data["targetRevision"], 1)


if __name__ == "__main__":
    unittest.main()
