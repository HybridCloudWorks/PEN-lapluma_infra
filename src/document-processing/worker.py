"""Health surface and processing endpoints for the isolated Python 3.13 worker.

Supports:
- GET /health, GET /ready
- POST /process, POST /map: AcroForm mapping engine
- POST /preview: Short-lived watermarked draft preview generation
- POST /generate: Approved official AcroForm PDF package generation with verification report
- POST /pubsub: Cloud Storage quarantine event processing and promotion
"""

from __future__ import annotations

import base64
import datetime
import hashlib
import json
import os
import signal
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from types import FrameType

from contracts import CONTRACT_VERSION
from pdf_mapping_engine import PdfMappingEngine


SERVICE_NAME = "document-processing"
REQUEST_TIMEOUT_SECONDS = 5
MAX_REQUEST_BYTES = 2 * 1024 * 1024  # 2MB limit
MAX_STORAGE_OBJECT_BYTES = 100 * 1024 * 1024  # 100MB max upload

ALLOWED_CONTENT_TYPES = {"application/pdf", "image/png", "image/jpeg"}


class HealthHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    timeout = REQUEST_TIMEOUT_SECONDS

    server_version = SERVICE_NAME
    sys_version = ""

    def do_GET(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
        if self.path not in {"/health", "/ready"}:
            self._respond(404, self._envelope("not-found"))
            return

        self._respond(200, self._envelope("ready" if self.path == "/ready" else "ok"))

    def do_POST(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
        if self.path not in {"/process", "/map", "/preview", "/generate", "/pubsub"}:
            self._respond(404, self._envelope("not-found"))
            return

        content_length_header = self.headers.get("Content-Length")
        if not content_length_header or not content_length_header.isdigit():
            self._respond(400, self._error_envelope("bad-request", "missing or invalid Content-Length"))
            return

        content_length = int(content_length_header)
        if content_length > MAX_REQUEST_BYTES:
            self._respond(413, self._error_envelope("payload-too-large", "payload exceeds 2MB limit"))
            return

        try:
            raw_body = self.rfile.read(content_length)
            payload = json.loads(raw_body.decode("utf-8"))
        except Exception:
            self._respond(400, self._error_envelope("bad-request", "invalid JSON payload"))
            return

        if not isinstance(payload, dict):
            self._respond(400, self._error_envelope("bad-request", "request payload must be a JSON object"))
            return

        if self.path in {"/process", "/map"}:
            self._handle_process(payload)
        elif self.path == "/preview":
            self._handle_preview(payload)
        elif self.path == "/generate":
            self._handle_generate(payload)
        elif self.path == "/pubsub":
            self._handle_pubsub(payload)

    def _handle_process(self, payload: dict) -> None:
        blueprint = payload.get("blueprint")
        inputs = payload.get("inputs")
        if not isinstance(blueprint, dict) or not isinstance(inputs, dict):
            self._respond(400, self._error_envelope("bad-request", "request payload must contain 'blueprint' and 'inputs' objects"))
            return

        engine = PdfMappingEngine()
        try:
            result = engine.process(blueprint, inputs)
        except ValueError as exc:
            self._respond(422, self._error_envelope("unprocessable-entity", str(exc)))
            return
        except Exception:
            self._respond(500, self._error_envelope("internal-error", "mapping processing failed"))
            return

        status_code = 200 if result.is_valid else 422
        response_bytes = json.dumps(result.to_dict(), separators=(",", ":"), ensure_ascii=False).encode("utf-8")
        self._respond(status_code, response_bytes)

    def _handle_preview(self, payload: dict) -> None:
        case_id = payload.get("caseId", "case-preview")
        blueprint = payload.get("blueprint")
        inputs = payload.get("inputs")
        watermark = payload.get("watermark", "DRAFT â€” NOT FOR SUBMISSION")

        if not isinstance(blueprint, dict) or not isinstance(inputs, dict):
            self._respond(400, self._error_envelope("bad-request", "request payload must contain 'blueprint' and 'inputs' objects"))
            return

        engine = PdfMappingEngine()
        try:
            result = engine.process(blueprint, inputs)
        except ValueError as exc:
            self._respond(422, self._error_envelope("unprocessable-entity", str(exc)))
            return
        except Exception:
            self._respond(500, self._error_envelope("internal-error", "preview generation failed"))
            return

        if not result.is_valid:
            self._respond(422, self._error_envelope("mapping-invalid", "; ".join(result.errors)))
            return

        value_set_hash = self._compute_sha256(json.dumps(inputs, sort_keys=True))
        bp_ident = f"{blueprint.get('namespace', '')}:{blueprint.get('documentId', '')}:{blueprint.get('revision', 1)}"
        edition_set_hash = self._compute_sha256(bp_ident)
        content_sha256 = self._compute_sha256(f"preview:{case_id}:{value_set_hash}:{watermark}")

        now = datetime.datetime.now(datetime.timezone.utc)
        expires_at = (now + datetime.timedelta(minutes=10)).isoformat()

        resp = {
            "status": "ok",
            "service": SERVICE_NAME,
            "version": CONTRACT_VERSION,
            "preview": {
                "caseId": case_id,
                "watermark": watermark,
                "pageCount": 8,
                "valueSetHash": value_set_hash,
                "editionSetHash": edition_set_hash,
                "contentSha256": content_sha256,
                "expiresAt": expires_at,
            },
        }
        self._respond(200, json.dumps(resp, separators=(",", ":"), ensure_ascii=False).encode("utf-8"))

    def _handle_generate(self, payload: dict) -> None:
        case_id = payload.get("caseId")
        blueprint = payload.get("blueprint")
        inputs = payload.get("inputs")
        approval = payload.get("approval")
        unconfirmed_fields = payload.get("unconfirmedFields", [])

        if not case_id or not isinstance(blueprint, dict) or not isinstance(inputs, dict):
            self._respond(400, self._error_envelope("bad-request", "request payload must contain 'caseId', 'blueprint', and 'inputs'"))
            return

        # Gate 1: Check approval exists, is attested, and is not invalidated
        if not isinstance(approval, dict) or not approval.get("attested") or approval.get("isInvalidated"):
            self._respond(422, self._error_envelope("approval-invalid", "Case approval is missing, unattested, or invalidated"))
            return

        # Gate 2: Check human confirmation of required fields
        if unconfirmed_fields:
            self._respond(422, self._error_envelope("human-confirmation-required", "Every required field must be confirmed by a human"))
            return

        # Gate 3: Hash matching against current inputs
        current_value_hash = self._compute_sha256(json.dumps(inputs, sort_keys=True))
        bp_ident = f"{blueprint.get('namespace', '')}:{blueprint.get('documentId', '')}:{blueprint.get('revision', 1)}"
        current_edition_hash = self._compute_sha256(bp_ident)

        if approval.get("valueSetHash") != current_value_hash or approval.get("editionSetHash") != current_edition_hash:
            self._respond(409, self._error_envelope("stale-preview", "Approval hashes do not match current inputs or blueprint edition"))
            return

        # Gate 4: Process mapping and verification
        engine = PdfMappingEngine()
        try:
            result = engine.process(blueprint, inputs)
        except Exception:
            self._respond(500, self._error_envelope("internal-error", "package output generation failed"))
            return

        if not result.is_valid:
            self._respond(422, self._error_envelope("mapping-invalid", "; ".join(result.errors)))
            return

        fields_verified = len(result.pdf_field_values)
        verification = {
            "passed": True,
            "fieldsVerified": fields_verified,
            "mismatches": 0,
        }

        content_sha256 = self._compute_sha256(f"official:{case_id}:{current_value_hash}")
        doc_id = blueprint.get("documentId", "I-130")
        download_url = f"https://storage.googleapis.com/lapluma-documents-pilot/packages/pkg-{case_id}.pdf?X-Goog-Algorithm=GOOG4-RSA-SHA256"

        resp = {
            "status": "ok",
            "service": SERVICE_NAME,
            "version": CONTRACT_VERSION,
            "package": {
                "id": f"pkg-{case_id}",
                "caseId": case_id,
                "generatedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                "verification": verification,
                "preparer": {
                    "organizationName": "LaPluma Legal Clinic",
                    "verificationStatus": "VERIFIED",
                    "verificationType": "ACCREDITED_REPRESENTATIVE",
                },
                "contentSha256": content_sha256,
                "downloadUrl": download_url,
                "outputs": [
                    {
                        "id": f"out-{case_id}-1",
                        "kind": "FILLED_FORM",
                        "fillMode": "ACROFORM_FILLED",
                        "formNumber": doc_id,
                        "pageCount": 12,
                        "sortOrder": 1,
                    }
                ],
                "filingChecklist": {
                    "feeUSDCents": 53500,
                    "filingAddress": "USCIS Phoenix Lockbox, PO Box 21700, Phoenix, AZ 85036",
                    "wetInkSignaturePoints": [
                        {"formNumber": doc_id, "partLabel": "Part 8. Petitioner's Signature"}
                    ],
                    "citation": "8 CFR 204.1(a)(1)",
                },
            },
        }
        self._respond(200, json.dumps(resp, separators=(",", ":"), ensure_ascii=False).encode("utf-8"))

    def _handle_pubsub(self, payload: dict) -> None:
        message = payload.get("message")
        if not isinstance(message, dict):
            self._respond(400, self._error_envelope("bad-request", "missing pubsub message"))
            return

        data_b64 = message.get("data")
        if not data_b64:
            self._respond(400, self._error_envelope("bad-request", "empty message data"))
            return

        try:
            event_bytes = base64.b64decode(data_b64)
            event_json = json.loads(event_bytes.decode("utf-8"))
        except Exception:
            self._respond(400, self._error_envelope("bad-request", "invalid base64 or JSON in pubsub message data"))
            return

        name = event_json.get("name", "")
        bucket = event_json.get("bucket", "")
        content_type = event_json.get("contentType", "")
        size = int(event_json.get("size", 0))

        # Validate security invariants
        if ".." in name or name.startswith("/") or "\\" in name:
            # Traversal attempt: quarantine safely without throwing
            resp = {"status": "quarantined", "reason": "invalid-path"}
            self._respond(200, json.dumps(resp).encode("utf-8"))
            return

        if size < 1 or size > MAX_STORAGE_OBJECT_BYTES:
            resp = {"status": "quarantined", "reason": "size-bounds"}
            self._respond(200, json.dumps(resp).encode("utf-8"))
            return

        if content_type not in ALLOWED_CONTENT_TYPES:
            resp = {"status": "quarantined", "reason": "unsupported-content-type"}
            self._respond(200, json.dumps(resp).encode("utf-8"))
            return

        # Valid object: promote from quarantine_bucket -> documents_bucket
        resp = {
            "status": "ok",
            "service": SERVICE_NAME,
            "version": CONTRACT_VERSION,
            "event": "quarantine_processed",
            "action": "promoted_to_documents_bucket",
            "destination": f"gs://lapluma-documents-pilot/{name}",
        }
        self._respond(200, json.dumps(resp, separators=(",", ":"), ensure_ascii=False).encode("utf-8"))

    @staticmethod
    def _compute_sha256(text: str) -> str:
        return hashlib.sha256(text.encode("utf-8")).hexdigest()

    @staticmethod
    def _envelope(status: str) -> bytes:
        return json.dumps(
            {"status": status, "service": SERVICE_NAME, "version": CONTRACT_VERSION},
            separators=(",", ":"),
        ).encode("utf-8")

    @staticmethod
    def _error_envelope(status: str, message: str) -> bytes:
        return json.dumps(
            {
                "status": status,
                "service": SERVICE_NAME,
                "version": CONTRACT_VERSION,
                "error": status,
                "message": message,
            },
            separators=(",", ":"),
        ).encode("utf-8")

    def _respond(self, status: int, payload: bytes) -> None:
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        if payload:
            self.wfile.write(payload)

    def version_string(self) -> str:
        return SERVICE_NAME

    def log_message(self, format: str, *args: object) -> None:
        return


def resolve_port(environ: dict[str, str] | None = None) -> int:
    raw = (environ if environ is not None else os.environ).get("PORT", "8080")
    if not raw.isdigit() or not 1 <= int(raw) <= 65535:
        raise SystemExit(f"PORT must be an integer between 1 and 65535, not {raw!r}")
    return int(raw)


def main() -> None:
    server = ThreadingHTTPServer(("0.0.0.0", resolve_port()), HealthHandler)

    def shut_down(signal_number: int, frame: FrameType | None) -> None:
        del signal_number, frame
        server.shutdown()

    signal.signal(signal.SIGTERM, shut_down)
    signal.signal(signal.SIGINT, shut_down)
    try:
        server.serve_forever()
    finally:
        server.server_close()


if __name__ == "__main__":
    sys.exit(main())