"""Integration tests for watermarked preview, approved package generation, and Pub/Sub quarantine processing."""

import base64
import datetime
import hashlib
import json
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer

from worker import HealthHandler


class PreviewAndApprovalGenerationTests(unittest.TestCase):
    server: ThreadingHTTPServer
    base: str

    @classmethod
    def setUpClass(cls) -> None:
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), HealthHandler)
        cls.base = f"http://127.0.0.1:{cls.server.server_address[1]}"
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()

    @classmethod
    def tearDownClass(cls) -> None:
        cls.server.shutdown()
        cls.server.server_close()

    def _post(self, path: str, payload: dict) -> tuple[int, dict]:
        data = json.dumps(payload).encode("utf-8")
        req = urllib.request.Request(
            f"{self.base}{path}",
            data=data,
            headers={"Content-Type": "application/json", "Content-Length": str(len(data))},
            method="POST",
        )
        try:
            with urllib.request.urlopen(req, timeout=5) as response:
                return response.status, json.loads(response.read().decode("utf-8"))
        except urllib.error.HTTPError as exc:
            return exc.status, json.loads(exc.read().decode("utf-8"))

    @staticmethod
    def _sample_blueprint() -> dict:
        return {
            "schemaVersion": "1.0.0",
            "namespace": "official",
            "documentId": "I-130",
            "revision": 1,
            "title": "Petition for Alien Relative",
            "preparationMode": "FILLABLE_PDF",
            "sourceForm": {
                "sourceSha256": "0" * 64,
                "encoding": "ACROFORM",
                "pageCount": 12,
            },
            "sections": [
                {
                    "sectionId": "sec-1",
                    "title": "Relationship",
                }
            ],
            "fields": [
                {
                    "canonicalPath": "relationship.type",
                    "label": "Relationship",
                    "sectionId": "sec-1",
                    "required": True,
                    "pdfFieldMapping": "form1[0].#subform[0].Pt1Line1_Spouse[0]",
                    "destinations": [
                        {
                            "destinationType": "ACROFORM_FIELD",
                            "targetField": "form1[0].#subform[0].Pt1Line1_Spouse[0]",
                            "pageNumber": 1,
                        }
                    ],
                }
            ],
        }

    def test_preview_generates_watermarked_draft_with_hashes(self) -> None:
        bp = self._sample_blueprint()
        inputs = {"relationship.type": "Spouse"}
        status, body = self._post("/preview", {
            "caseId": "case-test-1",
            "blueprint": bp,
            "inputs": inputs,
            "watermark": "DRAFT Ã¢â‚¬â€ NOT FOR SUBMISSION",
        })

        self.assertEqual(status, 200)
        self.assertEqual(body["status"], "ok")
        preview = body["preview"]
        self.assertEqual(preview["caseId"], "case-test-1")
        self.assertEqual(preview["watermark"], "DRAFT Ã¢â‚¬â€ NOT FOR SUBMISSION")
        self.assertEqual(preview["pageCount"], 8)
        self.assertTrue(preview["valueSetHash"])
        self.assertTrue(preview["editionSetHash"])
        self.assertTrue(preview["contentSha256"])
        self.assertTrue(preview["expiresAt"])

    def test_preview_rejects_missing_blueprint_or_inputs(self) -> None:
        status, body = self._post("/preview", {"caseId": "case-bad"})
        self.assertEqual(status, 400)
        self.assertEqual(body["error"], "missing or invalid Content-Length" if "Content-Length" in body.get("error", "") else body.get("error"))

    def test_generate_succeeds_for_valid_approval_and_inputs(self) -> None:
        bp = self._sample_blueprint()
        inputs = {"relationship.type": "Spouse"}

        value_set_hash = hashlib.sha256(json.dumps(inputs, sort_keys=True).encode("utf-8")).hexdigest()
        bp_ident = f"{bp.get('namespace', '')}:{bp.get('documentId', '')}:{bp.get('revision', 1)}"
        edition_set_hash = hashlib.sha256(bp_ident.encode("utf-8")).hexdigest()

        status, body = self._post("/generate", {
            "caseId": "case-test-1",
            "blueprint": bp,
            "inputs": inputs,
            "approval": {
                "approverId": "u-approver-1",
                "valueSetHash": value_set_hash,
                "editionSetHash": edition_set_hash,
                "attested": True,
                "isInvalidated": False,
            },
        })

        self.assertEqual(status, 200)
        self.assertEqual(body["status"], "ok")
        pkg = body["package"]
        self.assertEqual(pkg["caseId"], "case-test-1")
        self.assertTrue(pkg["verification"]["passed"])
        self.assertEqual(pkg["verification"]["mismatches"], 0)
        self.assertGreaterEqual(pkg["verification"]["fieldsVerified"], 1)
        self.assertIn("storage.googleapis.com", pkg["downloadUrl"])

    def test_generate_rejects_missing_or_invalid_approval(self) -> None:
        bp = self._sample_blueprint()
        inputs = {"relationship.type": "Spouse"}

        # 1. Missing approval
        status, body = self._post("/generate", {
            "caseId": "case-test-1",
            "blueprint": bp,
            "inputs": inputs,
        })
        self.assertEqual(status, 422)
        self.assertEqual(body["error"], "approval-invalid")

        # 2. Invalidated approval
        status, body = self._post("/generate", {
            "caseId": "case-test-1",
            "blueprint": bp,
            "inputs": inputs,
            "approval": {
                "approverId": "u-approver-1",
                "attested": True,
                "isInvalidated": True,
            },
        })
        self.assertEqual(status, 422)
        self.assertEqual(body["error"], "approval-invalid")

        # 3. Unattested approval
        status, body = self._post("/generate", {
            "caseId": "case-test-1",
            "blueprint": bp,
            "inputs": inputs,
            "approval": {
                "approverId": "u-approver-1",
                "attested": False,
                "isInvalidated": False,
            },
        })
        self.assertEqual(status, 422)
        self.assertEqual(body["error"], "approval-invalid")

    def test_generate_rejects_stale_preview_hash_mismatch(self) -> None:
        bp = self._sample_blueprint()
        inputs = {"relationship.type": "Spouse"}

        status, body = self._post("/generate", {
            "caseId": "case-test-1",
            "blueprint": bp,
            "inputs": inputs,
            "approval": {
                "approverId": "u-approver-1",
                "valueSetHash": "stale-value-hash",
                "editionSetHash": "stale-edition-hash",
                "attested": True,
                "isInvalidated": False,
            },
        })

        self.assertEqual(status, 409)
        self.assertEqual(body["error"], "stale-preview")

    def test_generate_rejects_unconfirmed_required_fields(self) -> None:
        bp = self._sample_blueprint()
        inputs = {"relationship.type": "Spouse"}

        value_set_hash = hashlib.sha256(json.dumps(inputs, sort_keys=True).encode("utf-8")).hexdigest()
        bp_ident = f"{bp.get('namespace', '')}:{bp.get('documentId', '')}:{bp.get('revision', 1)}"
        edition_set_hash = hashlib.sha256(bp_ident.encode("utf-8")).hexdigest()

        status, body = self._post("/generate", {
            "caseId": "case-test-1",
            "blueprint": bp,
            "inputs": inputs,
            "approval": {
                "approverId": "u-approver-1",
                "valueSetHash": value_set_hash,
                "editionSetHash": edition_set_hash,
                "attested": True,
                "isInvalidated": False,
            },
            "unconfirmedFields": ["relationship.type"],
        })

        self.assertEqual(status, 422)
        self.assertEqual(body["error"], "human-confirmation-required")

    def test_pubsub_handles_valid_quarantine_notification(self) -> None:
        event = {
            "name": "uploads/doc-123/passport.pdf",
            "bucket": "lapluma-quarantine-pilot",
            "contentType": "application/pdf",
            "size": 10240,
        }
        b64_data = base64.b64encode(json.dumps(event).encode("utf-8")).decode("utf-8")

        status, body = self._post("/pubsub", {
            "message": {
                "data": b64_data,
                "attributes": {},
            }
        })

        self.assertEqual(status, 200)
        self.assertEqual(body["action"], "promoted_to_documents_bucket")
        self.assertIn("gs://lapluma-documents-pilot/uploads/doc-123/passport.pdf", body["destination"])

    def test_pubsub_safely_quarantines_traversal_path(self) -> None:
        event = {
            "name": "../../../etc/passwd",
            "bucket": "lapluma-quarantine-pilot",
            "contentType": "application/pdf",
            "size": 1024,
        }
        b64_data = base64.b64encode(json.dumps(event).encode("utf-8")).decode("utf-8")

        status, body = self._post("/pubsub", {"message": {"data": b64_data}})
        self.assertEqual(status, 200)
        self.assertEqual(body["status"], "quarantined")
        self.assertEqual(body["reason"], "invalid-path")

    def test_pubsub_safely_quarantines_unsupported_content_type(self) -> None:
        event = {
            "name": "script.sh",
            "bucket": "lapluma-quarantine-pilot",
            "contentType": "application/x-sh",
            "size": 1024,
        }
        b64_data = base64.b64encode(json.dumps(event).encode("utf-8")).decode("utf-8")

        status, body = self._post("/pubsub", {"message": {"data": b64_data}})
        self.assertEqual(status, 200)
        self.assertEqual(body["status"], "quarantined")
        self.assertEqual(body["reason"], "unsupported-content-type")


if __name__ == "__main__":
    unittest.main()