import unittest

from contracts import AnchoredValueProposal, ProcessingRequest


# Fixture URIs must satisfy the host pin, so they use the Azure Blob suffix with an obviously
# non-real account name. No value here is or resembles a real endpoint.
INPUT_URI = "https://stfixturequarantine.blob.core.windows.net/quarantine/document"
OUTPUT_URI = "https://stfixturestaging.blob.core.windows.net/staging/document"


def request_mapping(**overrides: object) -> dict[str, object]:
    mapping: dict[str, object] = {
        "requestId": "request_fixture",
        "tenantId": "tenant_fixture",
        "caseId": "case_fixture",
        "documentId": "document_fixture",
        "inputBlobUri": INPUT_URI,
        "outputBlobUri": OUTPUT_URI,
        "sha256": "a" * 64,
        "artifactKind": "SOURCE_DOCUMENT",
        "contractVersion": "0.2.0",
    }
    mapping.update(overrides)
    return mapping


def proposal(**overrides: object) -> AnchoredValueProposal:
    arguments: dict[str, object] = {
        "field_name": "person.name.given",
        "value": "Sample",
        "page": 1,
        "polygon": (0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0),
        "engine_confidence": 0.92,
    }
    arguments.update(overrides)
    return AnchoredValueProposal(**arguments)  # type: ignore[arg-type]


class ProcessingContractTests(unittest.TestCase):
    def test_valid_request_is_source_only_and_uses_distinct_https_targets(self) -> None:
        request = ProcessingRequest.from_mapping(request_mapping())

        self.assertEqual(request.artifact_kind, "SOURCE_DOCUMENT")

    def test_unknown_authority_field_fails_closed(self) -> None:
        with self.assertRaises(ValueError):
            ProcessingRequest.from_mapping(request_mapping(approve=True))


class BlobUriBoundaryTests(unittest.TestCase):
    """The URI check is what bounds which single object this zone may touch."""

    def test_a_uri_with_no_host_is_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "Azure Blob endpoint"):
            ProcessingRequest.from_mapping(request_mapping(inputBlobUri="https://"))

    def test_an_arbitrary_external_host_is_rejected(self) -> None:
        # Reading from a caller-named external host is an SSRF primitive the moment an HTTP
        # adapter exists.
        with self.assertRaisesRegex(ValueError, "Azure Blob endpoint"):
            ProcessingRequest.from_mapping(
                request_mapping(inputBlobUri="https://attacker.example.invalid/quarantine/document")
            )

    def test_a_host_merely_containing_the_suffix_is_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "Azure Blob endpoint"):
            ProcessingRequest.from_mapping(
                request_mapping(
                    inputBlobUri="https://x.blob.core.windows.net.example.invalid/c/document"
                )
            )

    def test_plain_http_is_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "HTTPS"):
            ProcessingRequest.from_mapping(
                request_mapping(inputBlobUri=INPUT_URI.replace("https://", "http://"))
            )

    def test_a_shared_access_signature_in_the_query_is_rejected(self) -> None:
        # Shared keys are disabled on every storage account; a SAS smuggled through the request
        # would be a credential arriving by a path the design does not permit.
        with self.assertRaisesRegex(ValueError, "query string"):
            ProcessingRequest.from_mapping(
                request_mapping(outputBlobUri=f"{OUTPUT_URI}?sig=redacted&se=2026-01-01")
            )

    def test_credentials_in_the_uri_are_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "credentials"):
            ProcessingRequest.from_mapping(
                request_mapping(
                    inputBlobUri="https://user:secret@stfixture.blob.core.windows.net/c/document"
                )
            )

    def test_a_uri_naming_only_a_container_is_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "container and a blob"):
            ProcessingRequest.from_mapping(
                request_mapping(inputBlobUri="https://stfixture.blob.core.windows.net/quarantine")
            )

    def test_a_non_default_port_is_rejected(self) -> None:
        with self.assertRaisesRegex(ValueError, "default HTTPS port"):
            ProcessingRequest.from_mapping(
                request_mapping(
                    inputBlobUri="https://stfixture.blob.core.windows.net:8443/c/document"
                )
            )

    def test_input_and_output_differing_only_by_a_trailing_slash_are_not_distinct(self) -> None:
        # Same object, two spellings. Treating them as distinct defeats create-only staging.
        with self.assertRaisesRegex(ValueError, "distinct create-only staging"):
            ProcessingRequest.from_mapping(
                request_mapping(inputBlobUri=OUTPUT_URI, outputBlobUri=f"{OUTPUT_URI}/")
            )

    def test_an_uppercase_digest_is_normalised_rather_than_rejected(self) -> None:
        request = ProcessingRequest.from_mapping(request_mapping(sha256="A" * 64))

        self.assertEqual(request.sha256, "a" * 64)


class AnchoredProposalTests(unittest.TestCase):
    def test_a_valid_proposal_is_constructable(self) -> None:
        self.assertTrue(proposal().requires_human_confirmation)

    # One invariant per test. Violating two at once lets the earlier check short-circuit the
    # later one, so the test passes even after the later invariant is deleted outright.
    def test_too_few_vertices_fail_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "vertices"):
            proposal(polygon=(0.0, 0.0))

    def test_an_odd_coordinate_count_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "vertices"):
            proposal(polygon=(0.0, 0.0, 1.0, 1.0, 2.0, 2.0, 3.0, 3.0, 4.0))

    def test_a_zero_area_polygon_fails_closed(self) -> None:
        # Eight coordinates, one point, nothing enclosed — and previously accepted by a check
        # whose message promised "non-degenerate".
        with self.assertRaisesRegex(ValueError, "non-degenerate"):
            proposal(polygon=(1.0,) * 8)

    def test_a_zero_height_polygon_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "non-degenerate"):
            proposal(polygon=(0.0, 5.0, 1.0, 5.0, 2.0, 5.0, 3.0, 5.0))

    def test_non_numeric_coordinates_fail_closed(self) -> None:
        # The tuple[float, ...] annotation is not enforced at runtime.
        with self.assertRaisesRegex(ValueError, "finite non-negative"):
            proposal(polygon=("a", "b", "c", "d", "e", "f", "g", "h"))

    def test_boolean_coordinates_fail_closed(self) -> None:
        # bool subclasses int, so True would otherwise pass as the coordinate 1.
        with self.assertRaisesRegex(ValueError, "finite non-negative"):
            proposal(polygon=(True, False, True, False, True, False, True, False))

    def test_infinite_coordinates_fail_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "finite non-negative"):
            proposal(polygon=(0.0, 0.0, float("inf"), 0.0, 1.0, 1.0, 0.0, 1.0))

    def test_negative_coordinates_fail_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "finite non-negative"):
            proposal(polygon=(-1.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0))

    def test_a_page_below_one_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "1-based page"):
            proposal(page=0)

    def test_confidence_outside_the_unit_interval_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "confidence"):
            proposal(engine_confidence=1.5)

    def test_a_not_a_number_confidence_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "confidence"):
            proposal(engine_confidence=float("nan"))

    def test_unconfirmed_proposal_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "human confirmation"):
            proposal(requires_human_confirmation=False)

    def test_an_invalid_proposal_cannot_be_constructed_at_all(self) -> None:
        # The invariant holds without any caller remembering to call validate().
        with self.assertRaises(ValueError):
            AnchoredValueProposal(
                field_name="person.name.given",
                value="Sample",
                page=1,
                polygon=(0.0, 0.0, 1.0, 0.0, 1.0, 1.0, 0.0, 1.0),
                engine_confidence=0.92,
                requires_human_confirmation=False,
            )


if __name__ == "__main__":
    unittest.main()
