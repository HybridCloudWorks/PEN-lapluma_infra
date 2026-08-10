"""Durable acquisition proposal skeleton.

This function does not download, activate, or mutate an official-form edition. It creates an
acquisition proposal that a later allowlisted adapter and two-person approval workflow may process.
"""

from __future__ import annotations

import logging
from datetime import UTC, datetime
from typing import Any

import azure.durable_functions as df
import azure.functions as func

from acquisition_contract import PRIORITY_FORM_IDS, propose_acquisition_batch, publish_acquisition_proposals


app = df.DFApp(http_auth_level=func.AuthLevel.FUNCTION)

# A fixed instance ID makes the sweep a singleton. Without one, Durable Functions mints a fresh GUID
# per firing, so a run lasting longer than the schedule interval — or a use_monitor catch-up landing
# on a normal firing — starts a second sweep proposing the same editions to the same downstream.
ACQUISITION_INSTANCE_ID = "catalog-acquisition-singleton"

# Retry the publish, never the proposal. A proposal failure is a deterministic scope-drift or
# contract rejection: retrying it would just fail three times more slowly, and it must fail closed
# on the first attempt.
PUBLISH_RETRY = df.RetryOptions(first_retry_interval_in_milliseconds=5_000, max_number_of_attempts=3)


@app.timer_trigger(
    schedule="%ACQUISITION_SCHEDULE%",
    arg_name="timer",
    run_on_startup=False,
    use_monitor=True,
)
@app.durable_client_input(client_name="client")
async def schedule_catalog_acquisition(
    timer: func.TimerRequest,
    client: df.DurableOrchestrationClient,
) -> None:
    del timer
    existing = await client.get_status(ACQUISITION_INSTANCE_ID)
    if existing is not None and existing.runtime_status in {
        df.OrchestrationRuntimeStatus.Running,
        df.OrchestrationRuntimeStatus.Pending,
        df.OrchestrationRuntimeStatus.ContinuedAsNew,
    }:
        logging.info("Acquisition sweep already in flight; skipping this firing.")
        return

    await client.start_new(
        "catalog_acquisition_orchestrator",
        ACQUISITION_INSTANCE_ID,
        client_input={
            "contractVersion": "0.2.0",
            "requestedAt": datetime.now(UTC).isoformat(),
            "priorityForms": PRIORITY_FORM_IDS,
        },
    )


@app.orchestration_trigger(context_name="context")
def catalog_acquisition_orchestrator(
    context: df.DurableOrchestrationContext,
) -> Any:
    request = context.get_input()
    proposals = yield context.call_activity("propose_acquisition_activity", request)
    result = yield context.call_activity_with_retry(
        "publish_acquisition_activity", PUBLISH_RETRY, proposals
    )
    return {
        "proposalCount": len(proposals),
        # Report what the publisher accepted, not what was offered. Discarding this would let a
        # publisher that accepted three of four proposals report success for all four.
        "acceptedProposalCount": result["acceptedProposalCount"],
        "published": result["published"],
        # Not a placeholder. This function proposes; activation requires two-person approval.
        "activatedEditionCount": 0,
    }


@app.activity_trigger(input_name="request")
def propose_acquisition_activity(request: dict[str, Any]) -> list[dict[str, Any]]:
    return propose_acquisition_batch(request)


@app.activity_trigger(input_name="proposals")
def publish_acquisition_activity(proposals: list[dict[str, Any]]) -> dict[str, Any]:
    # The Service Bus adapter is intentionally deferred. Returning metadata keeps local tests
    # deterministic and prevents this scaffold from pretending to publish or activate anything.
    return publish_acquisition_proposals(proposals)
