import unittest

from contracts import AnchoredValueProposal, ProcessingRequest


class ProcessingContractTests(unittest.TestCase):
    def test_valid_request_is_source_only_and_uses_distinct_https_targets(self) -> None:
        request = ProcessingRequest.from_mapping(
            {
                "requestId": "request_fixture",
                "tenantId": "tenant_fixture",
                "caseId": "case_fixture",
                "documentId": "document_fixture",
                "inputBlobUri": "https://example.invalid/quarantine/input",
                "outputBlobUri": "https://example.invalid/staging/output",
                "sha256": "a" * 64,
                "artifactKind": "SOURCE_DOCUMENT",
                "contractVersion": "0.2.0",
            }
        )
        self.assertEqual(request.artifact_kind, "SOURCE_DOCUMENT")

    def test_unknown_authority_field_fails_closed(self) -> None:
        with self.assertRaises(ValueError):
            ProcessingRequest.from_mapping(
                {
                    "requestId": "request_fixture",
                    "tenantId": "tenant_fixture",
                    "caseId": "case_fixture",
                    "documentId": "document_fixture",
                    "inputBlobUri": "https://example.invalid/quarantine/input",
                    "outputBlobUri": "https://example.invalid/staging/output",
                    "sha256": "a" * 64,
                    "artifactKind": "SOURCE_DOCUMENT",
                    "contractVersion": "0.2.0",
                    "approve": True,
                }
            )

    # One invariant per test. Violating two at once lets the earlier check short-circuit the
    # later one, so the test passes even after the later invariant is deleted outright.
    def test_unanchored_proposal_fails_closed(self) -> None:
        proposal = AnchoredValueProposal(
            field_name="person.name.given",
            value="Sample",
            page=1,
            polygon=(0.0, 0.0),
            engine_confidence=0.92,
        )
        with self.assertRaisesRegex(ValueError, "polygon"):
            proposal.validate()

    def test_unconfirmed_proposal_fails_closed(self) -> None:
        proposal = AnchoredValueProposal(
            field_name="person.name.given",
            value="Sample",
            page=1,
            polygon=(0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0),
            engine_confidence=0.92,
            requires_human_confirmation=False,
        )
        with self.assertRaisesRegex(ValueError, "human confirmation"):
            proposal.validate()


if __name__ == "__main__":
    unittest.main()
