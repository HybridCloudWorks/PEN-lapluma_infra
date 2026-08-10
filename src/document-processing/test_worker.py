"""Tests for the health surface, exercised over a real socket."""

import json
import threading
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer

from worker import HealthHandler, resolve_port


class PortResolutionTests(unittest.TestCase):
    def test_the_default_is_used_when_unset(self) -> None:
        self.assertEqual(resolve_port({}), 8080)

    def test_a_valid_port_is_read(self) -> None:
        self.assertEqual(resolve_port({"PORT": "9001"}), 9001)

    def test_a_non_numeric_port_exits_rather_than_raising_a_traceback(self) -> None:
        # int(os.environ["PORT"]) previously died with an unhandled ValueError at startup.
        with self.assertRaises(SystemExit):
            resolve_port({"PORT": "not-a-port"})

    def test_a_port_outside_the_valid_range_exits(self) -> None:
        for value in ("0", "70000"):
            with self.subTest(value=value), self.assertRaises(SystemExit):
                resolve_port({"PORT": value})


class HealthSurfaceTests(unittest.TestCase):
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

    def test_health_and_ready_report_their_status(self) -> None:
        for path, expected in (("/health", "ok"), ("/ready", "ready")):
            with self.subTest(path=path):
                with urllib.request.urlopen(f"{self.base}{path}", timeout=5) as response:
                    self.assertEqual(response.status, 200)
                    body = json.loads(response.read())
                self.assertEqual(body["status"], expected)
                self.assertEqual(body["service"], "document-processing")

    def test_an_unknown_path_answers_json_rather_than_html(self) -> None:
        # send_error renders an HTML body, so the same endpoint would answer in two content types.
        with self.assertRaises(urllib.error.HTTPError) as raised:
            urllib.request.urlopen(f"{self.base}/metrics", timeout=5)

        self.assertEqual(raised.exception.status, 404)
        self.assertEqual(raised.exception.headers["Content-Type"], "application/json")

    def test_the_not_found_body_is_actually_parseable_json(self) -> None:
        # Declaring application/json and sending nothing is a contradiction: a client that parses
        # every response unconditionally raises on the empty payload.
        with self.assertRaises(urllib.error.HTTPError) as raised:
            urllib.request.urlopen(f"{self.base}/metrics", timeout=5)

        body = json.loads(raised.exception.read())
        self.assertEqual(body["status"], "not-found")
        self.assertEqual(body["service"], "document-processing")

    def test_no_response_echoes_the_requested_path(self) -> None:
        # Telemetry and payloads alike stay content-free, including on the failure path.
        with self.assertRaises(urllib.error.HTTPError) as raised:
            urllib.request.urlopen(f"{self.base}/DISTINCTIVE_MARKER", timeout=5)

        self.assertNotIn("DISTINCTIVE_MARKER", raised.exception.read().decode("utf-8"))

    def test_no_response_discloses_the_interpreter_version(self) -> None:
        with urllib.request.urlopen(f"{self.base}/health", timeout=5) as response:
            server_header = response.headers["Server"]

        self.assertEqual(server_header, "document-processing")
        self.assertNotIn("Python", server_header)


if __name__ == "__main__":
    unittest.main()
