"""End-to-end integration tests for AcroForm generation over real HTTP sockets.

Validates the complete chain from mobile-captured client facts to Cloud Run worker:
1. Full USCIS I-130 petition generation with realistic mobile-captured facts.
2. AcroForm PDF field mapping and deterministic output digests.
3. Overflow addendum generation for repeated sections exceeding form capacity (Part 11).
4. Unicode NFC normalization and multi-script character preservation.
5. Fail-closed rejection of malicious script injection attempts.
6. Multi-mode blueprint preparation (FILLABLE_PDF, STATIC_ASSISTED, EXTERNAL_REFERENCE).
7. Network boundary security: 2MB payload cap (HTTP 413), malformed JSON (HTTP 400),
   and safe content-free error envelopes.
"""

from __future__ import annotations

import json
import pathlib
import threading
import unicodedata
import unittest
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer
from typing import Any

from worker import HealthHandler, MAX_REQUEST_BYTES


REPO_ROOT = pathlib.Path(__file__).resolve().parents[2]
BLUEPRINTS_DIR = REPO_ROOT / "blueprints"


def load_blueprint(relative_path: str) -> dict[str, Any]:
    path = BLUEPRINTS_DIR / relative_path
    with open(path, "r", encoding="utf-8") as f:
        return json.load(f)


class AcroFormGenerationIntegrationTests(unittest.TestCase):
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

    def _post(self, path: str, payload: dict[str, Any] | bytes | str, headers: dict[str, str] | None = None) -> tuple[int, dict[str, Any]]:
        url = f"{self.base}{path}"
        req_headers = {"Content-Type": "application/json"}
        if headers:
            req_headers.update(headers)

        if isinstance(payload, dict):
            data = json.dumps(payload).encode("utf-8")
        elif isinstance(payload, str):
            data = payload.encode("utf-8")
        else:
            data = payload

        if "Content-Length" not in req_headers:
            req_headers["Content-Length"] = str(len(data))

        req = urllib.request.Request(url, data=data, headers=req_headers, method="POST")
        try:
            with urllib.request.urlopen(req, timeout=5) as response:
                status = response.status
                body = json.loads(response.read().decode("utf-8"))
                return status, body
        except urllib.error.HTTPError as exc:
            body = json.loads(exc.read().decode("utf-8"))
            return exc.code, body

    def test_end_to_end_i130_petition_generation(self) -> None:
        """Full USCIS Form I-130 petition generated from mobile-captured client facts."""
        blueprint = load_blueprint("official/uscis/i-130/blueprint.json")

        # Realistic facts captured on mobile device for a spousal petition
        client_facts = {
            "petitioner.given_name": "María",
            "petitioner.family_name": "Santos-Hernández",
            "petitioner.dob": "1985-06-15",
            "petitioner.ssn": "123-45-6789",
            "beneficiary.given_name": "Carlos",
            "beneficiary.family_name": "Santos",
            "beneficiary.relationship": "Spouse",
            "marriage.date": "2015-10-24",
        }

        status, body = self._post("/process", {"blueprint": blueprint, "inputs": client_facts})

        self.assertEqual(status, 200)
        self.assertEqual(body["preparationMode"], "FILLABLE_PDF")
        self.assertTrue(body["isValid"])
        self.assertEqual(body["errors"], [])

        # AcroForm fields correctly mapped
        fields = body["pdfFieldValues"]
        self.assertEqual(fields["form1[0].#subform[0].Pt1Line1a_GivenName[0]"], "María")
        self.assertEqual(fields["form1[0].#subform[0].Pt1Line1b_FamilyName[0]"], "Santos-Hernández")

        # Audit metadata populated with deterministic manifest digest
        audit = body["auditMetadata"]
        self.assertEqual(audit["blueprint"], "uscis/i-130@r1")
        self.assertEqual(len(audit["manifestDigest"]), 64)
        self.assertGreater(audit["pdfFieldCount"], 0)
        self.assertEqual(audit["addendumEntryCount"], 0)

    def test_post_map_alias_matches_process(self) -> None:
        """POST /map behaves as a functional equivalent to POST /process."""
        blueprint = load_blueprint("official/uscis/i-130/blueprint.json")
        client_facts = {
            "petitioner.given_name": "Elena",
            "petitioner.family_name": "Hernández",
            "petitioner.dob": "1960-03-12",
            "petitioner.ssn": "987-65-4321",
            "beneficiary.given_name": "Sofia",
            "beneficiary.family_name": "Hernández",
            "beneficiary.relationship": "Parent",
        }

        status, body = self._post("/map", {"blueprint": blueprint, "inputs": client_facts})
        self.assertEqual(status, 200)
        self.assertTrue(body["isValid"])
        self.assertEqual(body["pdfFieldValues"]["form1[0].#subform[0].Pt1Line1a_GivenName[0]"], "Elena")

    def test_repeated_section_overflow_to_part11_addendum(self) -> None:
        """Repeated beneficiary children exceeding section capacity (5) overflow to Part 11 addendum."""
        blueprint = load_blueprint("official/uscis/i-130/blueprint.json")

        # 8 children provided: first 5 fit on official form, remaining 3 overflow to addendum
        client_facts = {
            "petitioner.given_name": "María",
            "petitioner.family_name": "Santos",
            "petitioner.dob": "1985-06-15",
            "petitioner.ssn": "123-45-6789",
            "beneficiary.given_name": "Carlos",
            "beneficiary.family_name": "Santos",
            "beneficiary.relationship": "Spouse",
            "marriage.date": "2015-10-24",
            "beneficiary_children": [
                {"name": "Child One"},
                {"name": "Child Two"},
                {"name": "Child Three"},
                {"name": "Child Four"},
                {"name": "Child Five"},
                {"name": "Child Six (Overflow)"},
                {"name": "Child Seven (Overflow)"},
                {"name": "Child Eight (Overflow)"},
            ],
        }

        status, body = self._post("/process", {"blueprint": blueprint, "inputs": client_facts})

        self.assertEqual(status, 200)
        self.assertTrue(body["isValid"])

        # First 5 children mapped to AcroForm slots 0..4
        fields = body["pdfFieldValues"]
        self.assertEqual(fields["form1[0].#subform[0].Pt4Line0a_ChildName[0]"], "Child One")
        self.assertEqual(fields["form1[0].#subform[0].Pt4Line4a_ChildName[0]"], "Child Five")
        # Ensure slot 5 was not created in form fields (it overflowed)
        self.assertNotIn("form1[0].#subform[0].Pt4Line5a_ChildName[0]", fields)

        # 3 children overflowed to Part 11 Supplemental Information addendum
        addendum = body["overflowAddendum"]
        self.assertEqual(len(addendum), 3)

        self.assertEqual(addendum[0]["sectionId"], "sec_beneficiary_children")
        self.assertEqual(addendum[0]["canonicalPath"], "beneficiary_children[5].name")
        self.assertEqual(addendum[0]["content"], "Child Six (Overflow)")
        self.assertEqual(addendum[0]["pageNumber"], None)
        self.assertEqual(addendum[0]["partNumber"], None)
        self.assertIn("Exceeded form capacity", addendum[0]["explanation"])

        self.assertEqual(addendum[1]["content"], "Child Seven (Overflow)")
        self.assertEqual(addendum[2]["content"], "Child Eight (Overflow)")
        self.assertEqual(body["auditMetadata"]["addendumEntryCount"], 3)

    def test_unicode_normalization_and_multiscript_preservation(self) -> None:
        """Decomposed Unicode characters are normalized to NFC; multi-script text is preserved."""
        blueprint = load_blueprint("official/uscis/i-130/blueprint.json")

        # Construct decomposed Unicode (NFD): 'e' + combining acute accent
        nfd_given = "E" + "\u0301" + "lodie"  # Élodie in NFD
        self.assertNotEqual(nfd_given, unicodedata.normalize("NFC", nfd_given))

        client_facts = {
            "petitioner.given_name": nfd_given,
            "petitioner.family_name": "Renard",
            "petitioner.dob": "1990-01-01",
            "petitioner.ssn": "111-22-3333",
            "beneficiary.given_name": "Kenji",
            "beneficiary.family_name": "Tanaka",
            "beneficiary.relationship": "Spouse",
            "marriage.date": "2020-05-01",
        }

        status, body = self._post("/process", {"blueprint": blueprint, "inputs": client_facts})

        self.assertEqual(status, 200)
        self.assertTrue(body["isValid"])
        # Verified normalized to NFC
        given_val = body["pdfFieldValues"]["form1[0].#subform[0].Pt1Line1a_GivenName[0]"]
        self.assertEqual(given_val, "Élodie")
        self.assertEqual(given_val, unicodedata.normalize("NFC", given_val))

    def test_malicious_script_injection_fails_closed(self) -> None:
        """PDF script injection attempts are rejected with fail-closed HTTP 422."""
        blueprint = load_blueprint("official/uscis/i-130/blueprint.json")

        malicious_facts = {
            "petitioner.given_name": "/JavaScript (app.alert('PWNED'))",
            "petitioner.family_name": "Santos",
            "petitioner.dob": "1985-06-15",
            "petitioner.ssn": "123-45-6789",
            "beneficiary.given_name": "<script>alert(1)</script>",
            "beneficiary.family_name": "Santos",
            "beneficiary.relationship": "Spouse",
            "marriage.date": "2015-10-24",
        }

        status, body = self._post("/process", {"blueprint": blueprint, "inputs": malicious_facts})

        self.assertEqual(status, 422)
        self.assertFalse(body.get("isValid", True))
        errors = body.get("errors", [])
        self.assertTrue(any("malicious script" in err.lower() for err in errors))

    def test_static_assisted_mode_preparation(self) -> None:
        """STATIC_ASSISTED blueprint produces verified preparation checklist and evidence cross-references."""
        blueprint = load_blueprint("institutional/clinic-intake/blueprint.json")

        intake_facts = {
            "client.full_name": "Ana Morales",
            "client.household_size": 3,
            "client.annual_income": 28000,
            "client.eligible_for_fee_waiver": True,
        }

        status, body = self._post("/process", {"blueprint": blueprint, "inputs": intake_facts})

        self.assertEqual(status, 200)
        self.assertEqual(body["preparationMode"], "STATIC_ASSISTED")
        self.assertTrue(body["isValid"])

        checklist = body["assistedChecklist"]
        self.assertGreater(len(checklist), 0)

        # Completed items have sanitized values
        completed = [item for item in checklist if item["status"] == "COMPLETED"]
        self.assertTrue(any(c["canonicalPath"] == "client.full_name" and c["value"] == "Ana Morales" for c in completed))

        # Evidence requirements cross-referenced
        self.assertIn("evidenceRequired", body["auditMetadata"])

    def test_external_reference_mode_preparation(self) -> None:
        """EXTERNAL_REFERENCE blueprint produces official portal URL and prerequisite list."""
        blueprint = load_blueprint("external/student-aid-fafsa/blueprint.json")

        status, body = self._post("/process", {"blueprint": blueprint, "inputs": {}})

        self.assertEqual(status, 200)
        self.assertEqual(body["preparationMode"], "EXTERNAL_REFERENCE")
        self.assertTrue(body["isValid"])

        ext = body["externalReference"]
        self.assertEqual(ext["officialPortalUrl"], "https://studentaid.gov/h/apply-for-aid/fafsa")
        self.assertGreater(len(ext["prerequisites"]), 0)

    def test_payload_too_large_fails_closed(self) -> None:
        """Payloads exceeding MAX_REQUEST_BYTES (2MB) fail closed with HTTP 413."""
        huge_payload = "A" * (MAX_REQUEST_BYTES + 1024)
        headers = {"Content-Length": str(len(huge_payload))}

        status, body = self._post("/process", huge_payload, headers=headers)

        self.assertEqual(status, 413)
        self.assertEqual(body["status"], "payload-too-large")
        self.assertEqual(body["service"], "document-processing")

    def test_missing_content_length_fails_closed(self) -> None:
        """Requests without Content-Length fail closed with HTTP 400."""
        import http.client
        conn = http.client.HTTPConnection("127.0.0.1", self.server.server_address[1], timeout=5)
        conn.putrequest("POST", "/process")
        conn.putheader("Content-Type", "application/json")
        conn.endheaders()
        response = conn.getresponse()

        self.assertEqual(response.status, 400)
        body = json.loads(response.read().decode("utf-8"))
        self.assertEqual(body["status"], "bad-request")
        conn.close()

    def test_malformed_json_fails_closed(self) -> None:
        """Malformed JSON payloads fail closed with HTTP 400."""
        status, body = self._post("/process", "{this is not valid json")

        self.assertEqual(status, 400)
        self.assertEqual(body["status"], "bad-request")

    def test_missing_blueprint_fails_closed(self) -> None:
        """Payloads missing required top-level keys fail closed with HTTP 400."""
        status, body = self._post("/process", {"inputs": {}})

        self.assertEqual(status, 400)
        self.assertEqual(body["status"], "bad-request")

    def test_unknown_post_path_answers_404_json(self) -> None:
        """Unknown POST paths answer 404 with content-free JSON envelope."""
        status, body = self._post("/nonexistent", {})

        self.assertEqual(status, 404)
        self.assertEqual(body["status"], "not-found")


if __name__ == "__main__":
    unittest.main()
