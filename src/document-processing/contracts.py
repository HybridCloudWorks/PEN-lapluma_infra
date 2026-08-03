"""Fail-closed contracts for the isolated document-processing zone.

The worker may propose anchored values. It cannot select a form, write authoritative state,
approve a value, generate a package, or access a case database.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Any


CONTRACT_VERSION = "0.2.0"
ALLOWED_ARTIFACT_KINDS = {"SOURCE_DOCUMENT"}


@dataclass(frozen=True, slots=True)
class ProcessingRequest:
    request_id: str
    tenant_id: str
    case_id: str
    document_id: str
    input_blob_uri: str
    output_blob_uri: str
    sha256: str
    artifact_kind: str
    contract_version: str

    @classmethod
    def from_mapping(cls, value: dict[str, Any]) -> "ProcessingRequest":
        required = {
            "requestId",
            "tenantId",
            "caseId",
            "documentId",
            "inputBlobUri",
            "outputBlobUri",
            "sha256",
            "artifactKind",
            "contractVersion",
        }
        unknown = set(value) - required
        missing = required - set(value)
        if unknown or missing:
            raise ValueError(f"invalid processing request keys; missing={sorted(missing)}, unknown={sorted(unknown)}")

        request = cls(
            request_id=_required_text(value, "requestId"),
            tenant_id=_required_text(value, "tenantId"),
            case_id=_required_text(value, "caseId"),
            document_id=_required_text(value, "documentId"),
            input_blob_uri=_required_text(value, "inputBlobUri"),
            output_blob_uri=_required_text(value, "outputBlobUri"),
            sha256=_required_text(value, "sha256").lower(),
            artifact_kind=_required_text(value, "artifactKind"),
            contract_version=_required_text(value, "contractVersion"),
        )
        request.validate()
        return request

    def validate(self) -> None:
        if self.contract_version != CONTRACT_VERSION:
            raise ValueError("unsupported processing contract version")
        if self.artifact_kind not in ALLOWED_ARTIFACT_KINDS:
            raise ValueError("processing accepts source documents only")
        if len(self.sha256) != 64 or any(character not in "0123456789abcdef" for character in self.sha256):
            raise ValueError("sha256 must contain exactly 64 lowercase hexadecimal characters")
        if not self.input_blob_uri.startswith("https://") or not self.output_blob_uri.startswith("https://"):
            raise ValueError("blob URIs must use HTTPS")
        if self.input_blob_uri == self.output_blob_uri:
            raise ValueError("processing output must use a distinct create-only staging URI")


@dataclass(frozen=True, slots=True)
class AnchoredValueProposal:
    field_name: str
    value: str
    page: int
    polygon: tuple[float, ...]
    engine_confidence: float
    requires_human_confirmation: bool = True

    def validate(self) -> None:
        if not self.field_name or not self.value:
            raise ValueError("anchored proposals require a field name and source value")
        if self.page < 1 or len(self.polygon) < 8 or len(self.polygon) % 2 != 0:
            raise ValueError("anchored proposals require a page and non-degenerate polygon")
        if not 0 <= self.engine_confidence <= 1:
            raise ValueError("engine confidence must be between 0 and 1")
        if not self.requires_human_confirmation:
            raise ValueError("all processing proposals require human confirmation")


def _required_text(value: dict[str, Any], key: str) -> str:
    item = value.get(key)
    if not isinstance(item, str) or not item.strip():
        raise ValueError(f"{key} must be a non-empty string")
    return item.strip()
