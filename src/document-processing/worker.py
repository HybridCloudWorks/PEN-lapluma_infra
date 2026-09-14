"""Health surface for the isolated Python 3.13 processing worker.

Queue and Document Intelligence adapters are intentionally absent from this Sprint 2 skeleton.
Adding them requires managed-identity endpoints and private-network infrastructure approval.
"""

from __future__ import annotations

import json
import os
import signal
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from types import FrameType

from contracts import CONTRACT_VERSION
from pdf_mapping_engine import PdfMappingEngine


SERVICE_NAME = "document-processing"

# A request that stalls mid-line would otherwise hold its thread indefinitely, and
# ThreadingHTTPServer caps nothing.
REQUEST_TIMEOUT_SECONDS = 5
MAX_REQUEST_BYTES = 2 * 1024 * 1024  # 2MB limit


class HealthHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    timeout = REQUEST_TIMEOUT_SECONDS

    # Suppress the default banner. The interpreter's patch version is not something a probe
    # endpoint needs to announce.
    server_version = SERVICE_NAME
    sys_version = ""

    def do_GET(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
        if self.path not in {"/health", "/ready"}:
            # Not send_error: that renders an HTML body, so the same endpoint would answer in two
            # content types depending on the path it was asked for. The body is a real document
            # rather than nothing, because Content-Type: application/json on an empty payload is a
            # contradiction — a client that parses every response unconditionally chokes on it.
            self._respond(404, self._envelope("not-found"))
            return

        self._respond(200, self._envelope("ready" if self.path == "/ready" else "ok"))

    def do_POST(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
        if self.path not in {"/process", "/map"}:
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

    @staticmethod
    def _envelope(status: str) -> bytes:
        # One shape for every response, and it never echoes the requested path: what this service
        # emits stays content-free whether the probe succeeded or not.
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
                "error": message,
            },
            separators=(",", ":"),
        ).encode("utf-8")

    def _respond(self, status: int, payload: bytes) -> None:
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        # HTTP/1.1 keep-alive requires an accurate length on every response, including empty ones.
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        if payload:
            self.wfile.write(payload)

    def version_string(self) -> str:
        # The default joins server_version and sys_version, which leaves a trailing separator once
        # the interpreter version is blanked.
        return SERVICE_NAME

    def log_message(self, format: str, *args: object) -> None:
        # Do not emit paths, query strings, document IDs, or free text from health traffic.
        return


def resolve_port(environ: dict[str, str] | None = None) -> int:
    """Read PORT, refusing anything that is not a usable port rather than crashing on int()."""
    raw = (environ if environ is not None else os.environ).get("PORT", "8080")
    if not raw.isdigit() or not 1 <= int(raw) <= 65535:
        raise SystemExit(f"PORT must be an integer between 1 and 65535, not {raw!r}")
    return int(raw)


def main() -> None:
    server = ThreadingHTTPServer(("0.0.0.0", resolve_port()), HealthHandler)

    def shut_down(signal_number: int, frame: FrameType | None) -> None:
        del signal_number, frame
        # Container stop sends SIGTERM. Close the listener so in-flight probes finish rather than
        # being severed.
        server.shutdown()

    signal.signal(signal.SIGTERM, shut_down)
    signal.signal(signal.SIGINT, shut_down)
    try:
        server.serve_forever()
    finally:
        server.server_close()


if __name__ == "__main__":
    sys.exit(main())
