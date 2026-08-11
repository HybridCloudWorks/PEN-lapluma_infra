# Operational runbooks

Procedures for operating the pilot: what to do when something breaks, who is expected to do it, and
how the two recovery obligations — restore and deletion — are proved rather than assumed.

> **These runbooks are drafts and no step in them has ever been executed.** No Azure environment
> exists. Every command is written against the resource shapes declared in `infra/`, not against a
> deployed resource, and every timing figure is an intention rather than a measurement. A runbook
> that has never been run is a hypothesis about a runbook.
>
> They are written now anyway, because the alternative is authoring them during the first incident.
> The first `dev` environment is where they get corrected, and correcting them is expected work,
> not a sign they were written badly.

## Pages

| Runbook | Covers |
|---------|--------|
| [Incident Response](Runbook-Incident-Response) | Detecting, classifying, containing, and closing an incident |
| [On-Call Procedure](Runbook-On-Call-Procedure) | Rotation, escalation, handover, and what on-call is not expected to do |
| [Restore Drill](Runbook-Restore-Drill) | Proving each store can be restored, on a schedule, with evidence |
| [Deletion Drill](Runbook-Deletion-Drill) | Proving erasure reaches every store, including the ones that are easy to forget |

## Before these can be relied on

| Prerequisite | Where it is tracked |
|--------------|---------------------|
| A named operations and on-call owner | `REVIEW.md` R-04 |
| ~~An approved retention and erasure contract~~ | Ratified — see [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates) |
| A `staging` environment to validate the steps against | `REVIEW.md` R-01 through R-05 |
| Automated restore and deletion drills | `TODO.md` 3.5 |
| Erasure and retention sweep integration tests | `TODO.md` 3.4 |

The runbooks name roles from R-04 — operations owner, security owner, privacy owner — rather than
people. When R-04 resolves, the roles resolve with it and these pages need no edit.

## Conventions

**Roles, not names.** A runbook naming an individual is a runbook that expires when that person
changes jobs.

**Content-free by default.** The telemetry these procedures read carries correlation IDs and never a
document path, a query string, a route value, or an applicant identifier. If a step appears to
require case content to proceed, that is a finding about the design, not a reason to fetch the
content — record it and escalate.

**Placeholders are placeholders.** `<resource-group>`, `<environment>`, and similar are filled in
from the AZD environment, never from memory. No real subscription, tenant, resource, or hostname
value appears on these pages, and none should be added.

## Related pages

- [Security and Data Protection](Security-and-Data-Protection)
- [Environments and Release Path](Environments-and-Release-Path)
- [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates)
