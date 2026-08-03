"""Durable acquisition proposal skeleton.

This function does not download, activate, or mutate an official-form edition. It creates an
acquisition proposal that a later allowlisted adapter and two-person approval workflow may process.
"""

from __future__ import annotations

from datetime import UTC, datetime
from typing import Any

import azure.durable_functions as df
import azure.functions as func

from acquisition_contract import PRIORITY_FORM_IDS, propose_acquisition_batch, publish_acquisition_proposals


app = df.DFApp(http_auth_level=func.AuthLevel.FUNCTION)


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
    await client.start_new(
        "catalog_acquisition_orchestrator",
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
    yield context.call_activity("publish_acquisition_activity", proposals)
    return {"proposalCount": len(proposals), "activatedEditionCount": 0}


@app.activity_trigger(input_name="request")
def propose_acquisition_activity(request: dict[str, Any]) -> list[dict[str, Any]]:
    return propose_acquisition_batch(request)


@app.activity_trigger(input_name="proposals")
def publish_acquisition_activity(proposals: list[dict[str, Any]]) -> dict[str, Any]:
    # The Service Bus adapter is intentionally deferred. Returning metadata keeps local tests
    # deterministic and prevents this scaffold from pretending to publish or activate anything.
    return publish_acquisition_proposals(proposals)
