# Architecture overview

**Stack:** three isolated Azure Container Apps zones plus serverless orchestration and managed data
services, deployed in South Central US.

The constraints this architecture exists to enforce are documented in
[Security and Data Protection](Security-and-Data-Protection).

## Planned components

| Component | Type | Technology | Path |
|-----------|------|------------|------|
| Core API | API service | .NET 10 / ASP.NET Core | `src/core-api` |
| Identity and policy boundary | Core module or service | .NET 10; Entra-backed auth; policy enforcement | `src/core-api` initially; extract only with measured need |
| Catalog and case services | Core modules | .NET 10; edition-pinned catalog and case workflows | `src/core-api` initially |
| Package and delivery service | Core module or worker | .NET 10; deterministic PDF generation, verification, delivery | `src/package-worker` |
| Document processing | Isolated workers | Python 3.13 | `src/document-processing` |
| Event and schedule orchestration | Serverless glue | Azure Functions / Durable Functions | `src/functions` |
| Contracts | OpenAPI 3.1, JSON Schema, CloudEvents-compatible envelopes | Language-neutral | `contracts` |
| Infrastructure | Azure IaC | AZD + Bicep | `infra` and `azure.yaml` |

`src/core-api`, `src/document-processing`, `src/functions`, `contracts`, and `infra` exist today as
placeholder scaffolds. `src/package-worker` has not been created yet; that gap is tracked in
`TODO.md`.

## Component dependencies

| Component | Depends on | Boundary |
|-----------|------------|----------|
| iOS client | APIM-published Core API contract | HTTPS only; no direct data-service access |
| Core API | Azure SQL, Blob, Service Bus, Key Vault | Authoritative writes; managed identity |
| Functions and Durable glue | Service Bus, Blob events, Core APIs | Orchestration only; no bypass of service authorization |
| Processing workers | Quarantine Blob, create-only staging Blob, Service Bus, Document Intelligence | No SQL or Cosmos access and no general internet egress |
| Package worker | Confirmed field ledger, official pinned form assets, package Blob | Human-approved inputs only; deterministic round-trip verification |
| Derived projection workers | Service Bus, Cosmos DB | Rebuildable derived views; never authoritative |

## Trust zones

| Zone | Workloads | Required isolation |
|------|-----------|--------------------|
| Edge | API Management | Only public application ingress; JWT, schema, rate, size, and idempotency enforcement; no data persistence |
| Core ACA environment | .NET 10 Core API and package worker | Private data-plane access; authoritative writes; no parsing of raw hostile documents |
| Processing ACA environment | Python 3.13 sanitizer, OCR, and extraction workers | No database route; no general internet egress; least-privilege per-message and per-blob access; ephemeral workers |
| AI ACA environment | Guardrail and bounded AI proposal services | No authoritative write or approval authority; model access through private endpoints; fail closed |
| Orchestration | Azure Functions and Durable Functions | Timers, event intake, retries, and stateful coordination; calls governed services instead of bypassing them |

Each ACA zone uses a separate managed environment, subnet, workload identity, network security
policy, logging boundary, and narrowly scoped private connectivity. Cross-zone communication uses
APIM-governed APIs or Service Bus messages with versioned schemas. There are no shared database
credentials and no implicit network trust.

The generated network module reflects this: dedicated `snet-core`, `snet-processing`, `snet-ai`,
`snet-functions`, and `snet-private-endpoints` subnets, each with its own network security group,
and an explicit `DenyInternetEgress` outbound rule on the processing NSG.

## Azure service mapping

The SKUs below are planning baselines. They may change only after South Central US capability, quota,
security-feature, and cost validation. Security controls may not be removed to fit budget.

| Component | Azure service | Planning baseline |
|-----------|---------------|-------------------|
| Public API gateway | API Management | Standard v2, one unit; private backend connectivity and managed identity |
| Core API | Azure Container Apps | Consumption workload profile with a minimum of one replica for pilot reliability |
| Package worker | Azure Container Apps Jobs or worker app | Consumption profile; queue-driven; scale to zero when safe |
| Processing workers | Separate Azure Container Apps environment | Consumption profile; ephemeral queue-driven replicas; deny-by-default egress |
| AI proposal and guardrail services | Separate Azure Container Apps environment | Consumption profile; no authoritative data-plane roles |
| Event and scheduled glue | Azure Functions + Durable Functions | Flex Consumption where the required features are available; otherwise an approved equivalent after validation |
| Container images | Azure Container Registry | Standard with private endpoint, content trust and provenance controls, and managed-identity pull |
| Authoritative relational store | Azure SQL Database | General Purpose serverless pilot baseline; zone redundancy and compute floor decided by SLO and cost validation |
| Rebuildable derived projections | Azure Cosmos DB for NoSQL | Autoscale provisioned throughput baseline; no authoritative records |
| Files and immutable evidence | Azure Blob Storage | Separate storage accounts for quarantine, documents, packages, and audit; private endpoints and purpose-specific lifecycle policies |
| Messaging | Azure Service Bus | Premium baseline for predictable isolation and private networking; reassess only if Standard satisfies every control |
| Document extraction | Azure AI Document Intelligence | Standard tier with private endpoint; source-document extraction only |
| Application secrets and certificates | Azure Key Vault | Standard, RBAC, private endpoint, soft delete, purge protection |
| Customer key hierarchy | Azure Managed HSM | One pilot pool with an approved key hierarchy; cost and quota approval required before deployment |
| Central logs | Log Analytics workspace | 12-month security and operations retention baseline; content-free telemetry |
| APM | Application Insights, workspace-based | Distributed tracing, availability, dependency, and failure telemetry |
| Network | VNet, dedicated subnets, private DNS, NSGs, controlled egress | Hub-and-spoke or an equivalent approved topology; no public data endpoints |

## Data ownership and flow

1. APIM authenticates and validates an iOS request before forwarding it to the Core API.
2. The Core API creates authoritative case and upload metadata in Azure SQL and issues only a
   short-lived, operation-scoped upload grant to the quarantine account.
3. Blob creation emits a versioned event through Service Bus. Durable orchestration tracks the work;
   it does not grant the processor broader access.
4. The isolated processing worker reads one quarantined object, sanitizes it, invokes Document
   Intelligence over private connectivity, and writes a create-only result to staging.
5. Core validation records anchored extraction proposals and their provenance in SQL. Rebuildable,
   non-authoritative views may be projected to Cosmos DB.
6. A human confirms the values and a separately authorized human approves the package. The package
   worker fills an edition-pinned official form, round-trip verifies every mapped field, flattens
   the approved output where allowed, hashes it, and makes it available through a short-lived,
   revocable delivery link.
7. Scheduled lifecycle orchestration deletes case content under the approved retention policy and
   records content-free, verifiable deletion evidence. A receipt is not issued until active copies,
   versions, indexes, links, and any applicable key material have been verified.

## Related pages

- [Architecture Decision Records](Architecture-Decision-Records) — why the trust zones, the data
  stores, and the transport are what they are, and what was rejected
- [Azure Deployment Plan](Azure-Deployment-Plan)
- [Environments and Release Path](Environments-and-Release-Path)
- [Security and Data Protection](Security-and-Data-Protection)
- [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates)
