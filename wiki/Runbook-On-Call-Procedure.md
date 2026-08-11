# Runbook — on-call procedure

> Draft. Never operated. See [Operational Runbooks](Operational-Runbooks).

## What on-call is for

A supervised pilot serving real participants needs somebody who will notice. On-call exists to
detect, contain, and escalate — not to fix everything, and specifically not to make decisions that
belong to a named owner under `REVIEW.md` R-04.

## What on-call is explicitly not expected to do

Worth stating first, because the failure mode of an under-specified rotation is a person at 02:00
making a decision they were never given authority for:

- **Approve anything.** Deployment, security, privacy, and compliance approvals belong to their R-04
  owners. On-call may roll back; on-call may not roll forward into `pilot`.
- **Change a retention or erasure window.** Those are R-11 values.
- **Activate or deactivate a form edition.** Activation derives from R-14 record completeness.
- **Read case content to diagnose a problem.** If a problem cannot be diagnosed from content-free
  telemetry, that is a finding, not a licence.
- **Lock or shorten an immutability policy.** Locking is irreversible and belongs to a planned
  change.
- **Bootstrap, rotate, or recover HSM key material.** See ADR 0005 for why this one is absolute.

Rolling back, scaling, restarting, escalating, and preserving evidence are all in scope and need no
approval.

## Rotation

Proposed shape, pending the R-04 operations owner:

- One primary and one secondary, weekly, handing over at a fixed time on a fixed day.
- The secondary is the escalation path when the primary does not acknowledge within the response
  target, not a second person also watching alerts.
- The security owner is separately reachable for any Sev 1, independent of the rotation.
- Nobody is on-call for a system they have never deployed. The first rotation begins after `staging`
  exists and at least one restore drill and one deletion drill have been executed by hand.

Response targets, proposed:

| Severity | Acknowledge | Begin containment |
|----------|-------------|-------------------|
| Sev 1 | 15 minutes, any hour | Immediately on acknowledgement |
| Sev 2 | 4 hours, business hours | Same business day |
| Sev 3 | Next business day | Next business day |

These are targets to agree, not measurements. Nothing has ever been paged.

## Alerts worth waking someone for

An alert that does not correspond to an action is an alert that trains people to ignore alerts. The
proposed initial set is deliberately small:

| Alert | Why it is worth waking for |
|-------|----------------------------|
| Core API liveness or readiness failing across all replicas | The pilot is down |
| Any successful connection from the processing subnet to SQL or Cosmos | A guarantee is violated |
| Any authoritative write attributable to an AI-zone identity | A guarantee is violated |
| Managed HSM or Key Vault access denied for a workload identity | Encryption path broken; data plane is about to fail |
| Dead-letter depth rising on `domain-events` | Messages failing consistently |
| UPL classifier or its audit trail unavailable | Fail-closed engaged; the pilot is serving refusals |
| Certificate expiry within 21 days | Public ingress is about to disappear |

Everything else waits for business hours. The three most valuable rows are the two guarantee
violations and the UPL row, because each is silent from a user's perspective — the system keeps
answering, and only the log knows the answer is wrong.

## Handover

At each rotation change, the outgoing on-call passes:

1. Anything currently open, with severity and current state.
2. Anything deliberately deferred, and until when.
3. Any alert that fired and was judged not actionable — this is the input that removes bad alerts.
4. Any runbook step that did not work as written. Correcting these pages is part of the rotation,
   not a separate project.

Point 4 is the one that decays first and matters most. These runbooks are drafts; the rotation is
what turns them into descriptions of what actually happens.

## Access

On-call needs read access to the Log Analytics workspace, Application Insights, and Container Apps
revision management, plus the ability to shift ingress traffic between revisions. It does **not**
need standing data-plane access to SQL, Cosmos, or the storage accounts — diagnosis is content-free
by design, and standing access to case content for a rotating role is exactly the access pattern the
trust-zone model exists to avoid.

Elevated access, if a specific incident genuinely requires it, comes through PIM activation with the
security owner's approval, and the activation is itself an audit record.

## Related pages

- [Incident Response](Runbook-Incident-Response)
- [Restore Drill](Runbook-Restore-Drill)
- [Deletion Drill](Runbook-Deletion-Drill)
