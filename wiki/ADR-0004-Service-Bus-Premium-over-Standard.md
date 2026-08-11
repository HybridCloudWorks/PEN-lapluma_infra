# ADR 0004 — Service Bus Premium over Standard

## Status

Accepted. Implemented in `infra/modules/messaging.bicep`.

## Context

Service Bus is the transport between trust zones. The processing zone receives its work over it and
has no other inbound path; domain events fan out to projection workers over it. Every zone that
touches it is inside the VNet, and the design's stated posture is that no data-plane service is
reachable from the public internet.

Service Bus offers Standard and Premium tiers. Standard bills per operation and is materially
cheaper at pilot volumes. Premium bills continuously per messaging unit whether or not a message
flows.

## Options considered

**Standard tier with a private endpoint.** This is the option everyone reaches for, and it does not
exist. Private endpoints require Premium. Standard namespaces are reachable only over their public
endpoint, with access controlled by IP filtering and identity.

**Standard tier with public endpoint, IP filtering, and managed identity.** The honest version of
the cheap option, and it was considered rather than dismissed. Managed identity is a strong control,
and the namespace would refuse an unauthenticated caller.

It was rejected because it breaks the property the architecture is actually built around: that the
processing zone has no route to or from the internet. Reaching a public Service Bus endpoint means
the processing subnet needs outbound internet access, which means the `DenyInternet` rule in
`infra/modules/network.bicep` comes off, which means the zone's isolation now rests on an IP filter
and an identity check rather than on the absence of a route. That trade is not worth the saving —
the whole reason the processing zone is separated is the assumption that something running in it may
one day be hostile.

**Azure Storage queues instead.** Cheaper still, private endpoints available on Standard storage.
Rejected for capability: no topics and subscriptions, so the domain-event fan-out would be
hand-built; no duplicate detection, so idempotency moves into every consumer; no native dead-letter
queue, so poison-message handling becomes application code. Each of those is a control this design
currently gets from the platform and would otherwise have to write and test.

**Event Hubs.** Wrong shape. It is a streaming log, not a work queue with per-message locks and
dead-lettering.

## Decision

Service Bus Premium, baseline one messaging unit and one partition, with a private endpoint and
public network access disabled. Local authentication is disabled; senders and receivers use managed
identity.

The tier is chosen by the network posture, not by throughput. One messaging unit is the smallest
Premium configuration and is far beyond pilot volume — the capacity is a side effect of buying
private networking, not a capacity decision.

Queues enable duplicate detection and dead-lettering on message expiration, with the windows and
TTLs from the ratified retention contract.

## Consequences

Service Bus bills continuously from creation, in every environment, whether or not a message is ever
sent. It is one of the three continuously billing lines that R-03's cost record subtotals separately,
and in a low-traffic `dev` environment it is a large fraction of the total.

The processing zone keeps a genuinely empty egress allowlist — now ratified — because its
inbound work and outbound results both travel over private endpoints. That is only true because of
this decision.

Duplicate detection, dead-lettering, and message locks come from the platform, so idempotency and
poison-message handling are configuration rather than code. `TODO.md` 5.4 added the dead-letter
policy on the `domain-events` subscriptions for this reason.

The revisit condition: if the private-only posture is ever relaxed for other reasons, Standard
becomes viable again and the saving is real. It should not be revisited on cost alone, because the
cost is buying the network property, and dropping the tier drops the property.

## References

- [Architecture Overview](Architecture-Overview) — Azure service mapping
- [ADR 0002](ADR-0002-Three-Container-Apps-environments) — the zone isolation this supports
- `REVIEW.md` R-03 (cost); [Security and Data Protection](Security-and-Data-Protection) (the ratified egress table)
- `infra/modules/messaging.bicep`
