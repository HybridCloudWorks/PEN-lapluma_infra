import unittest

from acquisition_contract import (
    REQUIRED_REQUEST_KEYS,
    propose_acquisition_batch,
    publish_acquisition_proposals,
)


def request_mapping(**overrides: object) -> dict[str, object]:
    mapping: dict[str, object] = {
        "contractVersion": "0.2.0",
        "requestedAt": "2026-08-10T00:00:00+00:00",
        "priorityForms": ["I-130", "I-485", "DS-11", "FAFSA"],
    }
    mapping.update(overrides)
    return mapping


class AcquisitionContractTests(unittest.TestCase):
    def test_priority_set_is_exact_and_nothing_is_activated(self) -> None:
        proposals = propose_acquisition_batch(request_mapping())

        self.assertEqual([item["formID"] for item in proposals], ["I-130", "I-485", "DS-11", "FAFSA"])
        self.assertTrue(all(item["status"] == "PROPOSED" for item in proposals))
        self.assertEqual(proposals[0]["authority"], "USCIS")
        self.assertEqual(proposals[-1]["artifactKind"], "EXTERNAL_WORKFLOW")
        self.assertEqual(proposals[-1]["fillCapability"], "REFERENCE_ONLY")
        self.assertFalse(publish_acquisition_proposals(proposals)["published"])

    def test_scope_change_fails_closed(self) -> None:
        with self.assertRaises(ValueError):
            propose_acquisition_batch(request_mapping(priorityForms=["I-130", "N-400"]))

    def test_unsupported_contract_version_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "contract version"):
            propose_acquisition_batch(request_mapping(contractVersion="0.3.0"))


class RequestShapeTests(unittest.TestCase):
    """The request becomes durable orchestration history, so its shape is the boundary."""

    def test_an_authority_field_fails_closed(self) -> None:
        # Mirrors test_unknown_authority_field_fails_closed in the processing contract: this
        # function proposes and never activates, so an approval field must never ride along.
        with self.assertRaisesRegex(ValueError, "unknown=\\['approve'\\]"):
            propose_acquisition_batch(request_mapping(approve=True))

    def test_an_applicant_identifier_fails_closed(self) -> None:
        # Anything here is persisted to the task hub and replayed. A person or case identifier
        # reaching this function would be written to durable storage.
        with self.assertRaisesRegex(ValueError, "unknown="):
            propose_acquisition_batch(request_mapping(personId="p-1", caseId="c-1"))

    def test_a_missing_key_fails_closed(self) -> None:
        mapping = request_mapping()
        del mapping["requestedAt"]

        with self.assertRaisesRegex(ValueError, "missing=\\['requestedAt'\\]"):
            propose_acquisition_batch(mapping)

    def test_a_non_mapping_request_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "must be a mapping"):
            propose_acquisition_batch(["I-130"])  # type: ignore[arg-type]

    def test_a_malformed_timestamp_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "ISO 8601"):
            propose_acquisition_batch(request_mapping(requestedAt="last Tuesday"))

    def test_a_non_string_timestamp_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "ISO 8601"):
            propose_acquisition_batch(request_mapping(requestedAt=1_754_784_000))

    def test_the_timer_trigger_sends_exactly_the_required_keys(self) -> None:
        # Read the keys out of function_app.py rather than restating them here: a copy in the test
        # would drift with the trigger and still pass. Parsed with ast so this stays offline —
        # importing function_app would pull in the azure.functions packages.
        import ast
        from pathlib import Path

        source = Path(__file__).with_name("function_app.py").read_text(encoding="utf-8")
        trigger_keys: set[str] = set()
        for node in ast.walk(ast.parse(source)):
            if (
                isinstance(node, ast.keyword)
                and node.arg == "client_input"
                and isinstance(node.value, ast.Dict)
            ):
                trigger_keys = {
                    key.value for key in node.value.keys if isinstance(key, ast.Constant)
                }

        self.assertEqual(trigger_keys, set(REQUIRED_REQUEST_KEYS))


if __name__ == "__main__":
    unittest.main()
