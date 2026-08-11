# ADR 0003 — Azure SQL authoritative, Cosmos DB rebuildable projections

## Status

Accepted. Implemented in `infra/modules/data.bicep` and `src/core-api/`.

## Context

The pilot holds case records, upload metadata, and a form catalog, and it must be able to answer two
questions with certainty: what is true about a case, and has all of a participant's data been
erased. It also needs read paths — case lists, catalog views — that are shaped differently from the
write model and are read far more often than they are written.

Erasure is the constraint that shapes this decision more than performance does. `REVIEW.md` R-11
proposes a 30-day erasure SLA across SQL, Cosmos, blob versions, projections, temporary stores,
delivery links, logs, and backups. Every store that holds an authoritative copy is a store that
erasure must reach, prove it reached, and be re-verifiable against.

## Options considered

**Cosmos DB as the single store.** Attractive on paper: one store, one consistency model, no
projection lag, global distribution available if the pilot ever needs it. Rejected because the case
model is relational in the way that matters — cases relate to uploads, uploads to documents,
documents to extracted fields — and enforcing referential integrity in application code is
enforcing it in the place most likely to have a bug. A partially written case is a wrong answer
about a legal filing, not a stale read.

**Azure SQL as the single store.** The nearest miss, and the option most likely to be revisited. It
would remove an entire store from the erasure surface, remove the projection consistency window, and
remove a class of "the projection is wrong" incidents outright. It was set aside because the read
paths are document-shaped and hierarchical — a case with its uploads, extractions, and package
state — and serving those from a normalized schema at read time means either wide joins on every
request or a materialized view, which is a projection with a different name.

This option deserves the explicit revisit condition: **if measured read volume does not justify the
projection layer, collapsing to SQL alone is a net simplification, not a regression.** The Cosmos
autoscale ceiling proposed in R-03 is 1000 RU/s, the minimum — which is itself a signal that nobody
has yet measured a need.

**Both stores authoritative, each owning different entities.** Rejected firmly. Two authoritative
stores means two erasure implementations, two backup and restore procedures, and a reconciliation
question with no answer when they disagree. The deletion receipt promised in the data-flow design
becomes a claim about two systems agreeing, which is a much weaker claim than it sounds.

## Decision

Azure SQL is authoritative. Cosmos DB holds derived projections that are **rebuildable from SQL at
any time** and are never the source of truth for anything.

The database is `derived` and the container is `case-projections` — named so that a future engineer
reading a query does not have to ask which store they are in.

The container uses a hierarchical partition key of `/tenantId` then `/caseId`. Tenant first because
every cross-tenant read is a boundary violation and the partition key is where that boundary is
cheapest to hold; case second because it is the natural read unit.

`disableLocalAuth: true` on the account and `azureADOnlyAuthentication: true` on the SQL server. No
key or password path exists to either store.

## Consequences

Erasure has one authoritative target. Cosmos still has to be swept — a projection holding case
content after erasure is a real failure — but the sweep is verifiable in a way a second
authoritative store never is: drop the projection, rebuild it from post-erasure SQL, and the
result must contain nothing about the erased participant. That check is only available because the
projection is rebuildable, and it is the strongest erasure assurance in the design.

Projections are eventually consistent, and the window is visible to users. A case updated through
the Core API and immediately listed may show its previous state. The design accepts this; anything
requiring read-your-writes reads from SQL.

Every projection write is a place the two can diverge. `src/core-api/CatalogProjectionWriter.cs`
upserts rather than inserts for this reason, so a replayed message converges rather than duplicating.
A divergence is recoverable by rebuild, which is the property the whole decision rests on — and it
stops being true the moment anything writes to Cosmos that SQL does not also hold. That is the
invariant to defend in review.

Cosmos is a second store to provision, secure, monitor, and pay for. `TODO.md` 3.2's invariant
tests and R-11's erasure integration tests both have to cover it.

## References

- [Architecture Overview](Architecture-Overview) — data ownership and flow
- [Azure Component Research Record](Azure-Component-Research-Record) — SQL and Cosmos DB section
- `REVIEW.md` R-11 — retention and erasure contract
- `infra/modules/data.bicep`, `src/core-api/CatalogProjectionWriter.cs`
