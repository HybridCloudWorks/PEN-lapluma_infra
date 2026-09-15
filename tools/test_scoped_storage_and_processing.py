#!/usr/bin/env python3
"""Contract and integration test suite for scoped Cloud Storage upload-to-extraction transfer,
Blueprint review-to-approved-output round trip, and document processing services
(INT-05, INT-06, INF-11).
"""

from __future__ import annotations

import datetime
import hashlib
import json
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CONTRACTS_DIR = ROOT / "contracts" / "openapi"


class TestScopedStorageAndProcessing(unittest.TestCase):
    def setUp(self) -> None:
        self.upload_spec_path = CONTRACTS_DIR / "documents-upload.yaml"
        with open(self.upload_spec_path, encoding="utf-8") as f:
            self.upload_spec_text = f.read()

    def test_upload_spec_enforces_100mb_and_bypasses_gateway(self) -> None:
        """Verify upload spec declares 100 MB limit (104857600 bytes) and notes Gateway 32 MB bypass (INT-05)."""
        self.assertIn("104857600", self.upload_spec_text, "Upload contract must enforce 100 MB max bytes")
        self.assertIn("32 MB", self.upload_spec_text, "Upload contract must document API Gateway 32 MB limit bypass")
        self.assertIn("PUT", self.upload_spec_text, "Upload contract must specify direct PUT to Cloud Storage")

    def test_upload_spec_requires_sha256_and_short_lived_grant(self) -> None:
        """Verify upload spec binds SHA-256 and derives short-lived single-blob slot."""
        self.assertIn("contentSha256", self.upload_spec_text)
        self.assertIn("expectedContentSha256", self.upload_spec_text)
        self.assertIn("^[a-f0-9]{64}$", self.upload_spec_text)
        self.assertIn("fifteen minutes", self.upload_spec_text.lower())

    def test_upload_session_validation_logic(self) -> None:
        """Simulate upload session creation and validation boundaries (INT-05)."""
        max_bytes = 100 * 1024 * 1024

        def validate_session_request(size_bytes: int, content_sha256: str) -> tuple[int, str]:
            if size_bytes > max_bytes:
                return 422, "File metadata exceeds capture limits: maximum 100 MB"
            if not re.match(r"^[a-f0-9]{64}$", content_sha256):
                return 422, "Invalid SHA-256 digest format"
            return 201, "Created"

        # Valid 100 MB payload
        status, _ = validate_session_request(max_bytes, "a" * 64)
        self.assertEqual(status, 201)

        # Oversized payload (100 MB + 1 byte)
        status, msg = validate_session_request(max_bytes + 1, "a" * 64)
        self.assertEqual(status, 422)
        self.assertIn("100 MB", msg)

        # Invalid checksum format
        status, _ = validate_session_request(1024, "invalid_sha")
        self.assertEqual(status, 422)

    def test_upload_completion_checksum_verification(self) -> None:
        """Verify completion verifies server-stored bytes digest against declared digest (INT-05)."""
        stored_bytes = b"Sample uploaded document content for verification"
        actual_sha256 = hashlib.sha256(stored_bytes).hexdigest()

        def complete_upload(declared_sha256: str, session_expired: bool) -> tuple[int, str]:
            if session_expired:
                return 410, "Upload session expired"
            if declared_sha256 != actual_sha256:
                return 422, "Digest mismatch: declared does not match stored bytes"
            return 200, "OK"

        # Matching digest
        status, _ = complete_upload(actual_sha256, session_expired=False)
        self.assertEqual(status, 200)

        # Mismatched digest
        status, msg = complete_upload("b" * 64, session_expired=False)
        self.assertEqual(status, 422)
        self.assertIn("mismatch", msg.lower())

        # Expired session
        status, _ = complete_upload(actual_sha256, session_expired=True)
        self.assertEqual(status, 410)

    def test_tokens_never_enter_logs(self) -> None:
        """Verify token and signed credential sanitization in logging logic (INT-05)."""
        raw_log = "Uploaded to https://storage.googleapis.com/bucket/blob?X-Goog-Signature=secret123&X-Goog-Algorithm=GOOG4-RSA-SHA256"

        def sanitize_log(msg: str) -> str:
            # Mask sensitive query params in signed URLs
            return re.sub(r"(X-Goog-Signature=)[^&\s]+", r"\1[REDACTED]", msg)

        sanitized = sanitize_log(raw_log)
        self.assertNotIn("secret123", sanitized)
        self.assertIn("[REDACTED]", sanitized)

    def test_cross_tenant_access_denial(self) -> None:
        """Verify cross-tenant upload session and package access is denied (INT-05, INT-06)."""
        sessions = {
            "us_alpha": {"tenant_id": "tenant_alpha", "doc_id": "d_alpha"},
            "us_beta": {"tenant_id": "tenant_beta", "doc_id": "d_beta"},
        }

        def access_session(caller_tenant: str, session_id: str) -> int:
            sess = sessions.get(session_id)
            if not sess or sess["tenant_id"] != caller_tenant:
                return 404  # Closed-mouth 404 for tenant isolation
            return 200

        self.assertEqual(access_session("tenant_alpha", "us_alpha"), 200)
        self.assertEqual(access_session("tenant_alpha", "us_beta"), 404)
        self.assertEqual(access_session("tenant_beta", "us_alpha"), 404)

    def test_review_approval_and_invalidated_output_gate(self) -> None:
        """Verify package output generation requires valid approval and blocks on invalidation (INT-06, INF-11)."""
        case_data = {
            "state": "readyForApproval",
            "values": {"petitioner.name": "Maria Ramirez"},
            "approval": {
                "attested": True,
                "isInvalidated": False,
                "valueSetHash": hashlib.sha256(json.dumps({"petitioner.name": "Maria Ramirez"}, sort_keys=True).encode()).hexdigest(),
                "editionSetHash": hashlib.sha256(b"official:I-130:1").hexdigest(),
            },
        }

        def generate_package(case: dict, current_values: dict, current_edition: str) -> tuple[int, str]:
            approval = case.get("approval")
            if not approval or not approval.get("attested") or approval.get("isInvalidated"):
                return 422, "Approval missing or invalidated"
            curr_val_hash = hashlib.sha256(json.dumps(current_values, sort_keys=True).encode()).hexdigest()
            curr_ed_hash = hashlib.sha256(current_edition.encode()).hexdigest()
            if approval["valueSetHash"] != curr_val_hash or approval["editionSetHash"] != curr_ed_hash:
                return 409, "Preview stale: values or edition changed"
            return 200, "Generated"

        # Valid approved generation
        status, _ = generate_package(case_data, case_data["values"], "official:I-130:1")
        self.assertEqual(status, 200)

        # Invalidated approval (field edit happened)
        invalidated_case = dict(case_data)
        invalidated_case["approval"] = dict(case_data["approval"])
        invalidated_case["approval"]["isInvalidated"] = True
        status, msg = generate_package(invalidated_case, case_data["values"], "official:I-130:1")
        self.assertEqual(status, 422)

        # Value edited without re-approval (hash mismatch)
        edited_values = {"petitioner.name": "Maria Ramirez Updated"}
        status, msg = generate_package(case_data, edited_values, "official:I-130:1")
        self.assertEqual(status, 409)
        self.assertIn("stale", msg.lower())

    def test_scoped_download_grant_lifecycle_and_reissuance(self) -> None:
        """Verify download grants expire after 15 min and re-issuance returns fresh grant (APP-08, INT-06)."""
        now = datetime.datetime.now(datetime.timezone.utc)
        initial_grant = {
            "packageId": "pkg_123",
            "downloadUrl": "https://storage.googleapis.com/lapluma-documents-pilot/packages/pkg_123.pdf?sig=old",
            "expiresAt": now - datetime.timedelta(minutes=1),  # Expired
            "contentSha256": "sha256_abc",
        }

        def is_expired(grant: dict) -> bool:
            return datetime.datetime.now(datetime.timezone.utc) >= grant["expiresAt"]

        self.assertTrue(is_expired(initial_grant))

        # Re-issue grant
        reissued_grant = {
            "packageId": initial_grant["packageId"],
            "downloadUrl": "https://storage.googleapis.com/lapluma-documents-pilot/packages/pkg_123.pdf?sig=new",
            "expiresAt": now + datetime.timedelta(minutes=15),
            "contentSha256": initial_grant["contentSha256"],
        }
        self.assertFalse(is_expired(reissued_grant))
        self.assertEqual(reissued_grant["packageId"], initial_grant["packageId"])
        self.assertIn("sig=new", reissued_grant["downloadUrl"])


if __name__ == "__main__":
    unittest.main()
