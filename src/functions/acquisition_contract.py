"""Pure acquisition proposal contract shared by the Durable Functions adapter and tests."""

from __future__ import annotations

import json
from collections.abc import Callable
from datetime import datetime
from typing import Any


CONTRACT_VERSION = "0.2.0"

# The exact input the orchestrator is started with. This dict becomes Durable Functions
# orchestration history, which is persisted to the task hub and replayed, so an unexpected key is
# not merely ignored — it is written to durable storage. `host.json` sets
# `traceInputsAndOutputs: false`, which suppresses tracing, not history.
REQUIRED_REQUEST_KEYS = frozenset({"contractVersion", "requestedAt", "priorityForms"})

PRIORITY_FORM_IDS = ["I-130", "I-485", "DS-11", "FAFSA"]
PRIORITY_FORMS = {
    "I-130": {"authority": "USCIS", "artifactKind": "OFFICIAL_PDF", "fillCapability": "AUTOMATIC_FILL"},
    "I-485": {"authority": "USCIS", "artifactKind": "OFFICIAL_PDF", "fillCapability": "AUTOMATIC_FILL"},
    "DS-11": {"authority": "U.S. Department of State", "artifactKind": "OFFICIAL_PDF", "fillCapability": "AUTOMATIC_FILL"},
    "FAFSA": {"authority": "Federal Student Aid", "artifactKind": "EXTERNAL_WORKFLOW", "fillCapability": "REFERENCE_ONLY"},
}


def propose_acquisition_batch(request: dict[str, Any]) -> list[dict[str, Any]]:
    # Reject the whole shape, not just the fields this function happens to read. Its sibling,
    # ProcessingRequest.from_mapping, already treats an unknown key as hostile; a request carrying
    # an `approve` field or an applicant identifier must fail closed here for the same reason.
    if not isinstance(request, dict):
        raise ValueError("acquisition request must be a mapping")
    unknown = set(request) - REQUIRED_REQUEST_KEYS
    missing = REQUIRED_REQUEST_KEYS - set(request)
    if unknown or missing:
        raise ValueError(
            f"invalid acquisition request keys; missing={sorted(missing)}, unknown={sorted(unknown)}"
        )

    if request["contractVersion"] != CONTRACT_VERSION:
        raise ValueError("unsupported acquisition contract version")
    _validate_requested_at(request["requestedAt"])
    priority_forms = request["priorityForms"]
    if priority_forms != PRIORITY_FORM_IDS:
        raise ValueError("acquisition scope does not match the approved Alpha 0.2 priorities")
    return [
        dict(PRIORITY_FORMS[form_id]) | {
            "formID": form_id,
            "status": "PROPOSED",
            "requiresOfficialSourceAllowlist": True,
            "requiresTwoPersonActivation": True,
        }
        for form_id in priority_forms
    ]


# Every field a published proposal may carry. The message goes onto a queue another component
# reads, so an extra key is a contract change made by accident — and an applicant identifier
# arriving here would be published rather than merely logged.
PUBLISHABLE_KEYS = frozenset({
    "formID",
    "authority",
    "artifactKind",
    "fillCapability",
    "status",
    "requiresOfficialSourceAllowlist",
    "requiresTwoPersonActivation",
})


def publish_acquisition_proposals(
    proposals: list[dict[str, Any]],
    send: Callable[[str], None] | None = None,
) -> dict[str, Any]:
    """Publish each proposal, or report that nothing was published when no sender is supplied.

    `send` is the seam. The Durable activity passes the Service Bus output binding; the tests pass
    a recorder, and passing nothing keeps the offline path deterministic rather than requiring a
    broker to exercise the contract. It is not a convenience: the alternative is a module that
    cannot be tested without the runtime it is deferred behind.
    """
    if not isinstance(proposals, list):
        raise ValueError("proposals must be a list")

    payloads = [_publishable(proposal) for proposal in proposals]

    if send is None:
        return {"acceptedProposalCount": len(payloads), "published": False}

    # No try/except. A send failure must propagate so the orchestrator's bounded retry sees it;
    # swallowing it here would report a publish that never happened, and the count is what the
    # orchestrator reports as accepted.
    for payload in payloads:
        send(json.dumps(payload, separators=(",", ":"), sort_keys=True))

    return {"acceptedProposalCount": len(payloads), "published": True}


def _publishable(proposal: Any) -> dict[str, Any]:
    if not isinstance(proposal, dict):
        raise ValueError("each proposal must be a mapping")
    unknown = set(proposal) - PUBLISHABLE_KEYS
    if unknown:
        raise ValueError(f"proposal carries unpublishable keys: {sorted(unknown)}")
    if proposal.get("status") != "PROPOSED":
        # This function publishes proposals. A status of anything else would mean something
        # upstream decided an outcome, which is the two-person approval's job and not this path's.
        raise ValueError("only PROPOSED items may be published")
    return dict(proposal)


def _validate_requested_at(value: Any) -> None:
    # Unused by the proposal itself, but it lands in durable history, where a malformed timestamp
    # is worthless for reconstructing when a sweep ran.
    if not isinstance(value, str) or not value.strip():
        raise ValueError("requestedAt must be a non-empty ISO 8601 timestamp")
    try:
        datetime.fromisoformat(value)
    except ValueError as error:
        raise ValueError("requestedAt must be an ISO 8601 timestamp") from error
