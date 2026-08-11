# ADR 0002 — Three Container Apps environments over one

## Status

Accepted. Implemented in `infra/modules/compute.bicep`.

## Context

The architecture defines four trust zones — core, processing, AI, and functions — with two
properties that must hold under adversarial conditions rather than by convention:

- The **processing** zone parses hostile documents and must have no route to Azure SQL or Cosmos DB.
- The **AI** zone must hold no authoritative data-plane role and must never approve or write.

Azure Container Apps offers isolation at two levels. Applications inside one managed environment
share a subnet and can reach each other over the environment's internal network. Separate managed
environments each get their own subnet, and traffic between them crosses a network boundary where a
network security group can act on it.

## Options considered

**One managed environment, isolation by application configuration.** Cheaper and simpler: one
environment to provision, one subnet, one set of workload profiles. Isolation would come from
per-application ingress settings, managed identity scoping, and RBAC.

Rejected because the isolation would be entirely identity-layer. RBAC does stop the processing
worker from *authenticating* to SQL — but the network route stays open, and every subsequent control
depends on nothing going wrong in the identity layer. A code-execution bug in a document parser is
exactly the scenario where the identity layer is what fails first, and it is the scenario the
processing zone exists for. Isolation whose only enforcement is the layer most likely to be
compromised is not defence in depth.

**Three environments, one shared subnet.** Not actually available — a Container Apps managed
environment binds to its own infrastructure subnet — but worth recording, because it is the shape
someone reaching for a cost saving will propose. Without distinct subnets there are no distinct
NSGs, and the network control disappears while the environment count stays.

**Separate AKS namespaces or separate clusters.** Rejected as a scale mismatch. AKS would give
finer-grained network policy, and it comes with a control plane, node pools, upgrade cycles, and an
operational burden that a pilot with three workloads does not have the team to carry.

## Decision

Three managed environments — `core`, `processing`, and `ai` — each bound to its own subnet, each
with its own NSG. Functions runs on a Flex Consumption plan with its own integration subnet, giving
four network-isolated compute zones in total.

The network rules are in `infra/modules/network.bicep`. The processing NSG carries explicit outbound
denies to the `Sql` and `AzureCosmosDB` service tags **and** to the private-endpoint subnet prefix.
The third rule is the one worth understanding: an NSG carries an implicit `AllowVnetOutBound` at
priority 65000, and private endpoints live inside the VNet, so a `DenyInternet` rule never touched
traffic to them. Service tags alone did not close it either, because the endpoint addresses are
VNet-local. That gap existed in the first version of this design and was found by review.

## Consequences

The processing zone's "no database route" property is enforced at the network layer, so it survives
a compromised identity, a misconfigured role assignment, and a connection string that should not
exist. It is checkable from the compiled ARM rather than inferred from application code.

The cost is three managed environments instead of one, and three sets of workload profile capacity
to reason about. For a pilot this is a real line item, and it buys a control that the identity layer
cannot provide.

Adding a workload means deciding which zone it belongs to before it is written, which is a small
friction with a useful effect: a workload that does not obviously belong to a zone is usually a
workload doing two things.

Cross-zone communication has to be deliberate. The processing worker's inputs arrive over Service
Bus and its outputs go to storage — there is no path by which it calls the Core API directly, and
adding one would mean adding a network rule, which is visible in review.

## References

- [Architecture Overview](Architecture-Overview) — trust zones
- [Security and Data Protection](Security-and-Data-Protection) — network boundaries
- [ADR 0004](ADR-0004-Service-Bus-Premium-over-Standard) — why the inter-zone transport is private
- `infra/modules/compute.bicep`, `infra/modules/network.bicep`
