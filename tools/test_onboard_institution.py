#!/usr/bin/env python3
"""Unit and contract tests for LaPluma Institution Onboarding CLI (INT-13)."""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

from onboard_institution import (
    InstitutionOnboardingManager,
    OnboardingValidationError,
    main,
)


class TestInstitutionOnboarding(unittest.TestCase):
    """Test suite for institution onboarding validation, planning, and SQL generation."""

    def setUp(self) -> None:
        self.manager = InstitutionOnboardingManager()
        self.valid_request = {
            "tenantId": "clinic-sf-01",
            "displayName": "San Francisco Immigrant Legal Clinic",
            "contactEmail": "admin@sfclinic.org",
            "author": "engineer_alice@lapluma.io",
            "reviewer": "engineer_bob@lapluma.io",
            "assignedCollections": [
                {
                    "namespace": "official",
                    "collectionId": "family-reunification-i130",
                    "revision": 1,
                },
                {
                    "namespace": "official",
                    "collectionId": "adjustment-of-status-i485",
                    "revision": 1,
                },
            ],
        }

    def test_valid_request_passes(self) -> None:
        errors = self.manager.validate_request(self.valid_request)
        self.assertEqual(errors, [], f"Expected no errors but got: {errors}")

    def test_self_approval_prohibited(self) -> None:
        req = dict(self.valid_request)
        req["author"] = "engineer_alice@lapluma.io"
        req["reviewer"] = "engineer_alice@lapluma.io"

        errors = self.manager.validate_request(req)
        self.assertTrue(
            any("self-approval prohibited" in e for e in errors),
            f"Expected self-approval error, got {errors}",
        )

    def test_self_approval_case_insensitive(self) -> None:
        req = dict(self.valid_request)
        req["author"] = "Engineer_Alice@LaPluma.io"
        req["reviewer"] = "engineer_alice@lapluma.io"

        errors = self.manager.validate_request(req)
        self.assertTrue(
            any("self-approval prohibited" in e for e in errors),
            f"Expected self-approval error with case differences, got {errors}",
        )

    def test_missing_required_fields(self) -> None:
        for field in ("tenantId", "displayName", "contactEmail", "author", "reviewer"):
            req = dict(self.valid_request)
            del req[field]
            errors = self.manager.validate_request(req)
            self.assertTrue(
                any(f"{field} is required" in e or "identifier/email is required" in e for e in errors),
                f"Expected missing field error for '{field}', got {errors}",
            )

    def test_invalid_tenant_id(self) -> None:
        invalid_ids = ["A", "INVALID_UPPERCASE", "with spaces", "special!", "a", "x" * 65]
        for bad_id in invalid_ids:
            req = dict(self.valid_request)
            req["tenantId"] = bad_id
            errors = self.manager.validate_request(req)
            self.assertTrue(
                any("tenantId" in e and "invalid" in e for e in errors),
                f"Expected invalid tenantId error for '{bad_id}', got {errors}",
            )

    def test_invalid_contact_email(self) -> None:
        invalid_emails = ["not-an-email", "@missinguser.com", "user@", "spaces in@email.com"]
        for bad_email in invalid_emails:
            req = dict(self.valid_request)
            req["contactEmail"] = bad_email
            errors = self.manager.validate_request(req)
            self.assertTrue(
                any("contactEmail" in e for e in errors),
                f"Expected contactEmail error for '{bad_email}', got {errors}",
            )

    def test_unknown_collection_rejected(self) -> None:
        req = dict(self.valid_request)
        req["assignedCollections"] = [
            {
                "namespace": "official",
                "collectionId": "unknown_collection_xyz",
                "revision": 1,
            }
        ]
        errors = self.manager.validate_request(req)
        self.assertTrue(
            any("is not registered or known" in e for e in errors),
            f"Expected unknown collection error, got {errors}",
        )

    def test_empty_collections_rejected(self) -> None:
        req = dict(self.valid_request)
        req["assignedCollections"] = []
        errors = self.manager.validate_request(req)
        self.assertTrue(
            any("non-empty list" in e for e in errors),
            f"Expected non-empty list error, got {errors}",
        )

    def test_generate_sql_success(self) -> None:
        up_sql, down_sql = self.manager.generate_sql(self.valid_request)

        # UP SQL checks
        self.assertIn("BEGIN;", up_sql)
        self.assertIn("COMMIT;", up_sql)
        self.assertIn("INSERT INTO library.institution_tenant", up_sql)
        self.assertIn("'clinic-sf-01'", up_sql)
        self.assertIn("'San Francisco Immigrant Legal Clinic'", up_sql)
        self.assertIn("INSERT INTO library.tenant_collection_assignment", up_sql)
        self.assertIn("'family-reunification-i130'", up_sql)
        self.assertIn("'adjustment-of-status-i485'", up_sql)
        self.assertIn("ON CONFLICT (tenant_id) DO UPDATE", up_sql)

        # DOWN SQL checks
        self.assertIn("BEGIN;", down_sql)
        self.assertIn("COMMIT;", down_sql)
        self.assertIn("DELETE FROM library.tenant_collection_assignment WHERE tenant_id = 'clinic-sf-01';", down_sql)
        self.assertIn("UPDATE library.institution_tenant", down_sql)
        self.assertIn("SET is_active = FALSE", down_sql)
        self.assertIn("WHERE tenant_id = 'clinic-sf-01';", down_sql)

    def test_generate_sql_escapes_quotes(self) -> None:
        req = dict(self.valid_request)
        req["displayName"] = "O'Connor & Saint-Claire's Legal Aid"
        up_sql, down_sql = self.manager.generate_sql(req)

        self.assertIn("O''Connor & Saint-Claire''s Legal Aid", up_sql)

    def test_generate_sql_invalid_request_raises(self) -> None:
        req = dict(self.valid_request)
        req["reviewer"] = req["author"]  # self-approval violation

        with self.assertRaises(OnboardingValidationError) as ctx:
            self.manager.generate_sql(req)
        self.assertIn("self-approval prohibited", str(ctx.exception))

    def test_cli_validate_command(self) -> None:
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as tf:
            json.dump(self.valid_request, tf)
            tf_path = Path(tf.name)

        try:
            exit_code = main(["validate", str(tf_path)])
            self.assertEqual(exit_code, 0)
        finally:
            tf_path.unlink(missing_ok=True)

    def test_cli_plan_command(self) -> None:
        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as tf:
            json.dump(self.valid_request, tf)
            tf_path = Path(tf.name)

        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as out_tf:
            out_path = Path(out_tf.name)

        try:
            exit_code = main(["plan", str(tf_path), "-o", str(out_path)])
            self.assertEqual(exit_code, 0)
            plan_data = json.loads(out_path.read_text(encoding="utf-8"))
            self.assertEqual(plan_data["tenantId"], "clinic-sf-01")
            self.assertEqual(plan_data["status"], "APPROVED_FOR_PROVISIONING")
            self.assertTrue(plan_data["governance"]["independentReviewVerified"])
        finally:
            tf_path.unlink(missing_ok=True)
            out_path.unlink(missing_ok=True)

    def test_cli_generate_sql_command(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            req_path = Path(td) / "request.json"
            req_path.write_text(json.dumps(self.valid_request), encoding="utf-8")

            exit_code = main(["generate-sql", str(req_path), "-o", td])
            self.assertEqual(exit_code, 0)

            up_file = Path(td) / "clinic-sf-01_onboard.up.sql"
            down_file = Path(td) / "clinic-sf-01_onboard.down.sql"
            self.assertTrue(up_file.is_file())
            self.assertTrue(down_file.is_file())
            self.assertIn("INSERT INTO library.institution_tenant", up_file.read_text(encoding="utf-8"))
            self.assertIn("DELETE FROM library.tenant_collection_assignment", down_file.read_text(encoding="utf-8"))


if __name__ == "__main__":
    unittest.main()
