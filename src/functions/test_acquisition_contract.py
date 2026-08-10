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


class OrchestrationShapeTests(unittest.TestCase):
    """Structural assertions over function_app.py.

    The Durable Functions runtime cannot run here — the azure packages are deliberately absent so
    this suite stays offline — so these read the source rather than execute it. They pin the
    decisions that are invisible at runtime until the failure they prevent actually happens.
    """

    @staticmethod
    def source() -> "ast.Module":
        import ast
        from pathlib import Path

        return ast.parse(Path(__file__).with_name("function_app.py").read_text(encoding="utf-8"))

    @staticmethod
    def calls(tree: "ast.Module", name: str) -> list["ast.Call"]:
        import ast

        return [
            node
            for node in ast.walk(tree)
            if isinstance(node, ast.Call)
            and isinstance(node.func, ast.Attribute)
            and node.func.attr == name
        ]

    def test_the_sweep_is_started_with_a_fixed_instance_id(self) -> None:
        # Without one, Durable Functions mints a fresh GUID per firing and a slow sweep overlaps
        # itself, proposing the same editions twice.
        import ast

        starts = self.calls(self.source(), "start_new")
        self.assertEqual(len(starts), 1)
        self.assertGreaterEqual(
            len(starts[0].args), 2, "start_new must be given an explicit instance ID"
        )
        instance_argument = starts[0].args[1]
        self.assertIsInstance(instance_argument, ast.Name)
        self.assertEqual(instance_argument.id, "ACQUISITION_INSTANCE_ID")

    def test_an_in_flight_sweep_is_checked_before_starting_another(self) -> None:
        self.assertEqual(len(self.calls(self.source(), "get_status")), 1)

    def test_the_publish_is_retried_and_the_proposal_is_not(self) -> None:
        # A proposal failure is a deterministic scope-drift rejection. Retrying it would fail three
        # times more slowly and must not look like a transient error.
        import ast

        tree = self.source()
        retried = {
            call.args[0].value
            for call in self.calls(tree, "call_activity_with_retry")
            if call.args and isinstance(call.args[0], ast.Constant)
        }
        plain = {
            call.args[0].value
            for call in self.calls(tree, "call_activity")
            if call.args and isinstance(call.args[0], ast.Constant)
        }

        self.assertEqual(retried, {"publish_acquisition_activity"})
        self.assertEqual(plain, {"propose_acquisition_activity"})

    def test_the_orchestration_result_reports_what_the_publisher_accepted(self) -> None:
        # Returning only proposalCount would let a publisher that accepted three of four proposals
        # report success for all four.
        import ast

        returns = [
            node
            for node in ast.walk(self.source())
            if isinstance(node, ast.Return) and isinstance(node.value, ast.Dict)
        ]
        self.assertEqual(len(returns), 1)
        keys = {key.value for key in returns[0].value.keys if isinstance(key, ast.Constant)}
        self.assertEqual(
            keys,
            {"proposalCount", "acceptedProposalCount", "published", "activatedEditionCount"},
        )

    def test_nothing_can_report_an_activated_edition(self) -> None:
        # activatedEditionCount: 0 is an assertion about behaviour, not a placeholder.
        import ast

        tree = self.source()
        for node in ast.walk(tree):
            if isinstance(node, ast.Dict):
                for key, value in zip(node.keys, node.values, strict=False):
                    if isinstance(key, ast.Constant) and key.value == "activatedEditionCount":
                        self.assertIsInstance(value, ast.Constant)
                        self.assertEqual(value.value, 0)


if __name__ == "__main__":
    unittest.main()
