"""Health surface for the isolated Python 3.12 processing worker.

Queue and Document Intelligence adapters are intentionally absent from this Sprint 2 skeleton.
Adding them requires managed-identity endpoints and private-network infrastructure approval.
"""

from __future__ import annotations

import json
import os
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from contracts import CONTRACT_VERSION


SERVICE_NAME = "document-processing"


class HealthHandler(BaseHTTPRequestHandler):
    def do_GET(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
        if self.path not in {"/health", "/ready"}:
            self.send_error(404)
            return

        status = "ready" if self.path == "/ready" else "ok"
        payload = json.dumps(
            {"status": status, "service": SERVICE_NAME, "version": CONTRACT_VERSION},
            separators=(",", ":"),
        ).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, format: str, *args: object) -> None:
        # Do not emit paths, query strings, document IDs, or free text from health traffic.
        return


def main() -> None:
    port = int(os.environ.get("PORT", "8080"))
    server = ThreadingHTTPServer(("0.0.0.0", port), HealthHandler)
    server.serve_forever()


if __name__ == "__main__":
    main()
