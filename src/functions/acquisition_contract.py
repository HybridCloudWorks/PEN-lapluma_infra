"""Pure acquisition proposal contract shared by the Durable Functions adapter and tests."""

from __future__ import annotations

from typing import Any


PRIORITY_FORM_IDS = ["I-130", "I-485", "DS-11", "FAFSA"]
PRIORITY_FORMS = {
    "I-130": {"authority": "USCIS", "artifactKind": "OFFICIAL_PDF", "fillCapability": "AUTOMATIC_FILL"},
    "I-485": {"authority": "USCIS", "artifactKind": "OFFICIAL_PDF", "fillCapability": "AUTOMATIC_FILL"},
    "DS-11": {"authority": "U.S. Department of State", "artifactKind": "OFFICIAL_PDF", "fillCapability": "AUTOMATIC_FILL"},
    "FAFSA": {"authority": "Federal Student Aid", "artifactKind": "EXTERNAL_WORKFLOW", "fillCapability": "REFERENCE_ONLY"},
}


def propose_acquisition_batch(request: dict[str, Any]) -> list[dict[str, Any]]:
    if request.get("contractVersion") != "0.2.0":
        raise ValueError("unsupported acquisition contract version")
    priority_forms = request.get("priorityForms")
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


def publish_acquisition_proposals(proposals: list[dict[str, Any]]) -> dict[str, Any]:
    return {"acceptedProposalCount": len(proposals), "published": False}
