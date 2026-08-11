# Runbook — incident response

> Draft. No step here has been executed against a live environment. See
> [Operational Runbooks](Operational-Runbooks).

## Scope

Any event that breaks a stated guarantee. The guarantees this pilot makes are specific, and an
incident is best defined as a violation of one of them rather than as "something went wrong":

| Guarantee | Violated when |
|-----------|---------------|
| Case data stays within its tenant | Any cross-tenant read or write |
| The processing zone has no database route | Any successful connection from processing to SQL or Cosmos |
| The AI zone writes nothing authoritative | Any authoritative write attributable to an AI-zone identity |
| No form is activated without its approvals | An edition serves outside `CATALOG_ONLY` without a complete R-14 record |
| Erasure completes within its SLA | A participant's content survives past the 30-day window |
| The UPL gate fails closed | A response is served while the classifier or its audit trail is unavailable |
| No secret is held outside a managed identity | Any credential, key, or connection string in configuration, logs, or source |

Service degradation that violates none of these is an operational issue, handled under
[On-Call Procedure](Runbook-On-Call-Procedure). Treating every slow request as an incident is how
incident response stops meaning anything.

## Severity

| Severity | Definition | First response |
|----------|------------|----------------|
| **Sev 1** | A guarantee above is violated, or is suspected to be, involving real participant data | Immediate. Page the security owner and the privacy owner |
| **Sev 2** | A guarantee is violated in `dev` or `staging` with synthetic data only, or a control is confirmed ineffective with no evidence of exploitation | Same business day. Notify the security owner |
| **Sev 3** | The pilot is unavailable or materially degraded, no guarantee violated | Same business day |

Classify up when uncertain. Downgrading a Sev 1 after investigation is cheap; discovering during a
retrospective that a Sev 3 was a Sev 1 is not.

A suspected Sev 1 is a Sev 1. "We are not sure whether any real data was involved" is the state most
Sev 1 incidents begin in, and waiting for certainty before responding wastes the window in which
containment is cheapest.

## Detect

Signals to check, in order of how directly each maps to a guarantee:

1. **Diagnostic settings.** Every resource routes `allLogs` to the Log Analytics workspace
   (`TODO.md` 3.1). NSG flow denies from the processing subnet, SQL audit events, Key Vault and
   Managed HSM access events, and Service Bus operational logs are the highest-signal sources.
2. **Application Insights failures and dependency failures** on the Core API and the workers.
3. **Dead-letter depth** on the `domain-events` subscriptions. A sudden rise means messages are
   failing consistently, which is usually a deployment or a schema disagreement.
4. **Quarantine container growth**, which means documents are failing acquisition or extraction.

```bash
# Correlated failures for one operation. Correlation IDs only — this telemetry carries no
# document path, query string, route value, or applicant identifier by design.
az monitor log-analytics query \
  --workspace <workspace-id> \
  --analytics-query "union AppRequests, AppDependencies, AppExceptions
    | where OperationId == '<correlation-id>'
    | order by TimeGenerated asc"
```

If an investigation appears to need case content to proceed, stop. That is a finding about the
telemetry design — record it in `TODO.md` and escalate to the privacy owner. Do not fetch the
content to close the gap.

## Contain

Containment before diagnosis. The goal is to stop the guarantee being violated further, not to
understand why it was.

| Situation | Containment |
|-----------|-------------|
| Suspected compromise of a workload identity | Remove its role assignments. `infra/modules/rbac.bicep` is the inventory of what to remove |
| Suspected key compromise | Revoke the affected key only. Keys are per-purpose (ADR 0005) so one store goes down, not the data plane |
| A processing-zone escape | The NSG denies are already in place; verify they held before assuming they did not |
| A bad revision | Roll back the Container Apps revision. Do not redeploy forward under time pressure |
| Suspected data exfiltration | Preserve first: the audit container is WORM, but Log Analytics is not, and the workspace retention window is finite |

```bash
# Roll back to the previously known-good revision.
az containerapp revision list -g <resource-group> -n <app-name> -o table
az containerapp ingress traffic set -g <resource-group> -n <app-name> \
  --revision-weight <previous-revision>=100
```

Containment steps that destroy evidence — deleting a resource, purging a queue, redeploying over a
failing revision — need the security owner's agreement first for Sev 1 and Sev 2. The instinct under
pressure is to clear the error, and clearing the error frequently clears the only record of it.

## Preserve

Before remediation, capture:

- The Log Analytics queries and their results, exported. Workspace retention is finite and the
  investigation will outlive it.
- The affected revision names, image digests, and deployment timestamps. Images are digest-pinned,
  so the digest identifies exactly what ran.
- The role assignments as they stood, before any are removed.
- The correlation IDs spanning the incident.

The audit container is immutable and needs no preservation step. That is its purpose, and it is why
the ratified contract sets seven years for it.

## Remediate and close

Fix the cause, not the symptom, and add the check that would have caught it. The repository
convention is to verify a fix by breaking it: make the change, confirm the new check fails without
it, then restore. A fix accompanied by a test that passes both before and after is not yet a fix.

Closure needs, for Sev 1 and Sev 2:

- A written timeline: detection, containment, remediation, closure.
- The guarantee that was violated, and whether real participant data was involved.
- The control that should have prevented it, and why it did not.
- A `TODO.md` item for the check that would have caught it, filed even if it is also fixed in the
  same change — the record is what makes the gap visible later.
- Privacy owner sign-off where participant data was involved, including whether the participant
  notice obliges a disclosure.

## Escalation

| Trigger | Escalate to |
|---------|-------------|
| Any Sev 1 | Security owner and privacy owner, immediately |
| Real participant data involved or suspected | Privacy owner, immediately, regardless of severity |
| A guarantee is found to be unenforceable rather than merely broken | Security owner, and record it in `REVIEW.md` if the fix needs a decision |
| Suspected UPL escape | Compliance owner, immediately. The gate fails closed, so an escape means the gate did not run |

Roles are R-04 groups, not individuals. If R-04 is unresolved, there is nobody to escalate to and
this runbook cannot be executed — which is what makes R-04 a blocker rather than an inconvenience.

## Related pages

- [On-Call Procedure](Runbook-On-Call-Procedure)
- [Security and Data Protection](Security-and-Data-Protection)
- [ADR 0002](ADR-0002-Three-Container-Apps-environments) — the zone isolation being defended
