# Azure Deployment Plan

> **Status:** Executing — placeholder-only Sprint 2 foundation complete; Azure validation and deployment prohibited

Generated: 2026-08-02

---

## 1. Project Overview

**Goal:** Build the Azure backend, service contracts, and infrastructure for the LaPluma
`lapluma-app-0.2` supervised pilot. The pilot begins with approximately 40 real cases and is
hard-capped below 1,000 enrolled users. It prepares and securely delivers verified form packages;
it does not recommend forms, provide legal advice, approve cases automatically, sign forms, or
file with an agency.

**Path:** New Project

**Repository boundary:** This repository owns backend services, API/event contracts, Azure
infrastructure as code, deployment configuration, operational controls, and evidence-producing
tests. The native iOS client remains in its existing application repository.

---

## 2. Requirements

| Attribute | Value |
|-----------|-------|
| Classification | Production-sensitive supervised pilot |
| Scale | Small: initial ~40 real cases; hard cap of 999 enrolled users |
| Budget | Balanced; managed services and security controls take priority over minimum cost |
| Data residency | US only |
| **Tenant** | **UNKNOWN — mandatory user confirmation before an AZD environment or deployment** |
| **Subscription** | **UNKNOWN — mandatory user confirmation by name and ID before an AZD environment or deployment** |
| **Location** | East US 2 (`eastus2`), user-approved; service and quota availability must be verified before deployment |
| Delivery recipe | Azure Developer CLI (AZD) with Bicep |
| Application stack | .NET 9 core services; Python 3.12 document-processing workers; Azure Functions and Durable Functions for event/scheduled orchestration |

### Governing constraints

- Treat all pilot data as sensitive production PII. Development uses synthetic data only; staging
  may use synthetic or explicitly consented pilot data; pilot data stays in the US data plane.
- The user or an authorized human selects the form package. AI and catalog services cannot inspect
  case data to recommend a form, determine eligibility, predict outcomes, approve, sign, or file.
- Every extracted value retains provenance and requires human confirmation. A separately
  authorized human must approve a package before export; all automated components return proposals
  only.
- The processing zone treats every upload as hostile, has no database route, and cannot write to
  authoritative document stores. It receives one scoped input and writes only to a create-only
  staging target.
- Managed identities are the default for workload access. Secrets never enter source control,
  Bicep outputs, deployment logs, or ordinary pipeline variables.
- Public network access is disabled on PII-bearing data and AI services. Private endpoints,
  private DNS, least-privilege RBAC, customer-managed encryption, auditability, and fail-closed
  security controls are pilot prerequisites, not later hardening.
- No SOC 2 certification claim is permitted from architecture or Azure inheritance. Alpha 0.2 may
  describe evidence collection or readiness only after claim-owner approval.

### Hard context gate

No Azure-connected environment, preflight, role assignment, resource-provider registration,
provisioning operation, or deployment may be created or changed until all of the following are
recorded and explicitly confirmed. Local placeholder-only contracts, application source,
`azure.yaml`, and Bicep may be generated and tested with provisioning disabled:

1. Azure tenant display name and tenant ID.
2. Azure subscription display name and subscription ID.
3. Confirmation that the subscription is authorized for real pilot PII and cost-bearing resources.
4. Confirmation that `eastus2` supports every selected service/SKU, required quota, private
   networking feature, and Azure AI Document Intelligence model used by the pilot.
5. Identities and owners for deployment approval, security, privacy, compliance, operations, and
   cost management.

Failure to satisfy any item blocks AZD environment creation, Azure preflight, provisioning, and
deployment. The user approved local placeholder-only contract, source, CI, and Bicep generation on
2026-08-02; this approval does not satisfy the Azure-context gate.

---

## 3. Components Detected

The workspace was empty at planning time: no application source, `azure.yaml`, infrastructure,
Dockerfiles, or CI/CD configuration existed.

| Planned component | Type | Technology | Planned path |
|-------------------|------|------------|--------------|
| Core API | API service | .NET 9 / ASP.NET Core | `src/core-api` |
| Identity and policy boundary | Core module/service | .NET 9; Entra-backed auth; policy enforcement | `src/core-api` initially; extract only with measured need |
| Catalog and case services | Core modules | .NET 9; edition-pinned catalog and case workflows | `src/core-api` initially |
| Package and delivery service | Core module/worker | .NET 9; deterministic PDF generation, verification, delivery | `src/package-worker` |
| Document processing | Isolated workers | Python 3.12 | `src/document-processing` |
| Event and schedule orchestration | Serverless glue | Azure Functions / Durable Functions | `src/functions` |
| Contracts | OpenAPI 3.1, JSON Schema, CloudEvents-compatible envelopes | Language-neutral | `contracts` |
| Infrastructure | Azure IaC | AZD + Bicep | `infra` and `azure.yaml` |

### Planned dependencies

| Component | Depends on | Boundary |
|-----------|------------|----------|
| iOS client | APIM-published Core API contract | HTTPS only; no direct data-service access |
| Core API | Azure SQL, Blob, Service Bus, Key Vault | Authoritative writes; managed identity |
| Functions/Durable glue | Service Bus, Blob events, Core APIs | Orchestration only; no bypass of service authorization |
| Processing workers | Quarantine Blob, create-only staging Blob, Service Bus, Document Intelligence | No SQL/Cosmos access and no general internet egress |
| Package worker | Confirmed field ledger, official pinned form assets, package Blob | Human-approved inputs only; deterministic round-trip verification |
| Derived projection workers | Service Bus, Cosmos DB | Rebuildable derived views; never authoritative |

---

## 4. Recipe Selection

**Selected:** AZD with Bicep

**Rationale:** This is a new Azure-first, multi-service repository. AZD provides explicit
environment management and a repeatable validation/deployment workflow, while Bicep provides
native Azure modules, policy visibility, and deterministic infrastructure review. Generation will
use reusable Bicep modules with environment-specific parameters and no secrets in parameter files.

The mandatory workflow is:

`azure-prepare` → user approval and Azure-context confirmation → generation → `azure-validate` →
explicit deployment approval → `azure-deploy`

---

## 5. Architecture

**Stack:** Three isolated Azure Container Apps zones plus serverless orchestration and managed data
services, deployed in East US 2.

### Trust zones

| Zone | Workloads | Required isolation |
|------|-----------|--------------------|
| Edge | API Management | Only public application ingress; JWT/schema/rate/size/idempotency enforcement; no data persistence |
| Core ACA environment | .NET 9 Core API and package worker | Private data-plane access; authoritative writes; no parsing of raw hostile documents |
| Processing ACA environment | Python 3.12 sanitizer/OCR/extraction workers | No database route; no general internet egress; least-privilege per-message/blob access; ephemeral workers |
| AI ACA environment | Guardrail and bounded AI proposal services | No authoritative write or approval authority; model access through private endpoints; fail closed |
| Orchestration | Azure Functions and Durable Functions | Timers, event intake, retries, and stateful coordination; calls governed services instead of bypassing them |

Each ACA zone uses a separate managed environment, subnet, workload identity, network security
policy, logging boundary, and narrowly scoped private connectivity. Cross-zone communication uses
APIM-governed APIs or Service Bus messages with versioned schemas; no shared database credentials
or implicit network trust.

### Service mapping

SKUs below are planning baselines. They may be changed only after East US 2 capability, quota,
security-feature, and cost validation; security controls may not be removed to fit budget.

| Component | Azure service | Planning baseline |
|-----------|---------------|-------------------|
| Public API gateway | API Management | Standard v2, one unit; private backend connectivity and managed identity |
| Core API | Azure Container Apps | Consumption workload profile with minimum one replica for pilot reliability |
| Package worker | Azure Container Apps Jobs or worker app | Consumption profile; queue-driven; scale to zero when safe |
| Processing workers | Separate Azure Container Apps environment | Consumption profile; ephemeral queue-driven replicas; deny-by-default egress |
| AI proposal/guardrail services | Separate Azure Container Apps environment | Consumption profile; no authoritative data-plane roles |
| Event and scheduled glue | Azure Functions + Durable Functions | Flex Consumption where required features are available; otherwise approved equivalent after validation |
| Container images | Azure Container Registry | Standard with private endpoint, content trust/provenance controls, and managed-identity pull |
| Authoritative relational store | Azure SQL Database | General Purpose serverless pilot baseline; zone redundancy and compute floor decided by SLO/cost validation |
| Rebuildable derived projections | Azure Cosmos DB for NoSQL | Autoscale provisioned throughput baseline; no authoritative records |
| Files and immutable evidence | Azure Blob Storage | Separate storage accounts by quarantine, documents, packages, and audit purpose; private endpoints and purpose-specific lifecycle policies |
| Messaging | Azure Service Bus | Premium baseline for predictable isolation/private networking; reassess only if Standard satisfies every control |
| Document extraction | Azure AI Document Intelligence | Standard tier with private endpoint; source-document extraction only |
| Application secrets/certificates | Azure Key Vault | Standard, RBAC, private endpoint, soft delete and purge protection |
| Customer key hierarchy | Azure Managed HSM | One pilot pool with approved key hierarchy; cost and quota approval required before deployment |
| Central logs | Log Analytics Workspace | 12-month security/operations retention baseline; content-free telemetry |
| APM | Application Insights workspace-based | Distributed tracing, availability, dependency, and failure telemetry |
| Network | VNet, dedicated subnets, private DNS, NSGs, controlled egress | Hub/spoke or equivalent approved topology; no public data endpoints |

### Data ownership and flow

1. APIM authenticates and validates an iOS request before forwarding it to the Core API.
2. The Core API creates authoritative case/upload metadata in Azure SQL and issues only a
   short-lived, operation-scoped upload grant to the quarantine account.
3. Blob creation emits a versioned event through Service Bus. Durable orchestration tracks work;
   it does not grant the processor broader access.
4. The isolated processing worker reads one quarantined object, sanitizes it, invokes Document
   Intelligence over private connectivity, and writes a create-only result to staging.
5. Core validation records anchored extraction proposals and provenance in SQL. Rebuildable,
   non-authoritative views may be projected to Cosmos DB.
6. A human confirms values and a separately authorized human approves the package. The package
   worker fills an edition-pinned official form, round-trip verifies every mapped field, flattens
   the approved output where allowed, hashes it, and makes it available through a short-lived,
   revocable delivery link.
7. Scheduled lifecycle orchestration deletes case content under the approved retention policy and
   records content-free, verifiable deletion evidence. A receipt is not issued until active copies,
   versions, indexes, links, and applicable key material have been verified.

### Environments and release path

| Environment | Data | Purpose | Promotion gate |
|-------------|------|---------|----------------|
| `dev` | Synthetic only | Developer integration and contract testing | Automated tests and policy checks |
| `staging` | Synthetic; consented pilot fixtures only by explicit approval | Production-equivalent security, load, failure, and deletion drills | Security, privacy, UPL, accessibility, fidelity, and operations signoff |
| `pilot` | Approved real participant data | Initial ~40 supervised cases, later controlled expansion below 1,000 users | Manual protected-environment approval and all real-data gates |

No environment is created during planning. Environment names, resource naming, subscription
placement, tenant, and deployment principals are finalized only after the hard context gate.

### Alpha 0.2 operational gates

- The Alpha 0.2 priority catalog is exactly I-130, I-485, DS-11, and FAFSA. Their artifact and fill
  modes remain explicit: official PDF versus online application, and automatic, assisted, or
  reference-only. Priority does not imply activation; every edition remains fail-closed until its
  source, encoding, field map or external-workflow boundary, and approvals are verified.
- Catalog/form versions use form ID plus edition date, official source URL, source SHA-256,
  encoding, and two-person-approved field-map version. Edition drift quarantines affected cases.
- The UPL release gate passes its development and held-out corpora with zero escapes per prohibited
  act and supported language. Classifier or audit unavailability fails closed.
- Cross-tenant, cross-folder, person-boundary, and agent-no-write invariant tests pass on every
  build. A generated package mismatch blocks delivery.
- Production account erasure and case-retention sweeps are integration-tested across SQL, Cosmos,
  Blob versions, search/projections, temporary stores, delivery links, logs, and backups/key policy.
- The initial real-user pilot requires approved privacy/consent/retention materials, partner and
  reviewer authorization, outside-counsel/Compliance gates, an independent penetration test with
  all high findings closed, incident response/on-call readiness, restore/deletion drills, and
  physical-device end-to-end validation.
- Expansion beyond the initial supervised cohort requires a documented CPO, CTO, CISO, and
  Compliance checkpoint, adequate human-review capacity, no unresolved Sev-1 incident, and an
  enforced server-side maximum of 999 enrolled users.

---

## 6. Execution Checklist

### Research record

Research completed before generation on 2026-08-02:

- AZD/Bicep rules: subscription-scope entrypoint, resource-group modules, required AZD tags,
  managed identities, non-secret outputs, and no hard-coded tenant/subscription/resource-group IDs.
- Container Apps: independent managed environments for Core, Processing, and AI; explicit health
  probes; minimum one API replica; queue-driven workers may scale to zero; non-root containers.
- Functions/Durable: identity-based storage and Service Bus settings, Flex Consumption as the
  preferred later hosting baseline, timer/client/orchestrator/activity separation, and no
  connection strings for new deployments.
- SQL/Cosmos: Entra-only SQL administration and managed-identity users; SQL remains authoritative;
  Cosmos uses tenant/case partitioned, rebuildable projections with local authentication disabled.
- Storage/Service Bus: purpose-separated storage, public blob/shared-key access disabled,
  purpose-scoped data roles, Premium Service Bus baseline, duplicate detection, DLQ handling, and
  managed-identity sender/receiver roles.
- Key management/observability: RBAC Key Vault with purge protection; Managed HSM remains gated by
  administrator, key-hierarchy, quota, restore, and cost approval; workspace-based Application
  Insights and content-free Log Analytics telemetry.
- Document Intelligence: it proposes anchored source-document extraction only. It does not infer
  official-form schemas, fill a form, approve a value, or receive an API-key fallback in code.
- Region guidance says the selected foundation services are broadly available, but East US 2 SKU,
  private-network, Document Intelligence model, quota, and cost verification remains a hard live-
  deployment gate.

### Phase 1: Planning

- [x] Analyze workspace: confirmed empty repository and selected NEW mode.
- [x] Gather approved requirements: supervised pilot, small scale, balanced budget, US-only East US 2.
- [x] Scan codebase: no application or infrastructure artifacts found.
- [x] Select AZD with Bicep.
- [x] Plan the application, trust zones, data flow, environments, and pilot gates.
- [ ] Confirm Azure tenant and subscription with the user. **Hard blocker.**
- [ ] Verify East US 2 service availability, SKU features, quotas, and cost approvals.
- [ ] Record security, privacy, compliance, operations, and deployment approvers.
- [x] User approved placeholder-only local generation; no Azure environment or deployment.

### Phase 2: Execution

- [x] Research each selected Azure component and load its preparation guidance.
- [ ] Record the confirmed Azure context before creating an AZD environment.
- [x] Define the initial OpenAPI catalog/schema contract for review.
- [x] Generate `azure.yaml` and placeholder environment conventions.
- [x] Generate modular, structurally locked Bicep foundation and checks; provisioning accepts only `false`.
- [x] Generate .NET 9, Python 3.12, and Functions/Durable service skeletons.
- [x] Generate container build definitions and CI validation.
- [ ] Implement security invariants, retention, audit, and evidence collection.
- [x] Keep plan status at `Executing` until the Azure context gate permits subscription-aware validation.

### Phase 3: Validation

- [ ] Invoke the `azure-validate` skill.
- [ ] Run static application, contract, Bicep, policy, security, and secret scans.
- [ ] Run deployment preflight against the confirmed subscription without provisioning.
- [ ] Run integration, isolation, authorization, deletion, fidelity, and failure-mode tests.
- [ ] Record validation proof below and update status to `Validated` only after every blocking check passes.

### Phase 4: Deployment

- [ ] Obtain explicit approval for cost-bearing pilot provisioning.
- [ ] Invoke the `azure-deploy` skill; do not deploy directly from preparation.
- [ ] Deploy `dev`, validate, then `staging`; do not create `pilot` until all real-data gates pass.
- [ ] Execute post-deployment smoke, private-network, identity, audit, restore, and deletion checks.
- [ ] Update status to `Deployed` only after evidence is recorded.

---

## 7. Validation Proof

> **Required:** The `azure-validate` skill must populate this section before status can become
> `Validated`. Planning evidence is not deployment validation.

| Check | Command run | Result | Timestamp |
|-------|-------------|--------|-----------|
| Foundation contract/interlock scan | `python3 tools/validate_foundation.py` | Passed | 2026-08-02 |
| Python contract tests | `python3 -m unittest discover` for processing and Functions | 5 passed | 2026-08-02 |
| Python source compilation | `python3 -m py_compile` | Passed | 2026-08-02 |
| JSON/YAML syntax | Local Python parsers | Passed | 2026-08-02 |
| Whitespace validation | `git diff --check` | Passed | 2026-08-02 |
| Bicep compilation | Official Bicep CLI v0.46.1, independent agent review | Passed with no diagnostics | 2026-08-02 |
| .NET and container builds | GitHub Actions foundation workflow | Pending; local .NET unavailable and Docker daemon stopped | 2026-08-02 |
| Azure subscription preflight | Azure context and plan approval are pending | Blocked as designed | 2026-08-02 |

**Validated by:** Pending `azure-validate` skill

**Validation timestamp:** Not yet validated

---

## 8. Files to Generate

| File | Purpose | Status |
|------|---------|--------|
| `.azure/plan.md` | Mandatory planning source of truth | Complete |
| `IMPLEMENTATION_LEDGER.md` | Missing values, owners, approvals, and non-secret configuration contract | Generated; values remain unresolved |
| `README.md` | Repository boundary and safe workflow | Generated |
| `azure.yaml` | AZD service and deployment configuration | Generated; no AZD environment created |
| `infra/main.bicep` and modules | Placeholder-only foundation with provisioning interlock | Generated; structurally non-deployable and not deployed |
| `contracts/*` | OpenAPI hierarchy, artifact, activation, edition, and schema contract | Generated |
| `src/*` | Minimal Core API, processing, and acquisition/Durable skeletons | Generated |
| `.github/workflows/foundation-validation.yml` | Build, contract, Bicep, and container validation | Generated; not run remotely |

---

## 9. Next Steps

> Current phase: Executing a bounded, placeholder-only Sprint 2 foundation. No Azure environment,
> preflight, provisioning, or deployment is authorized.

1. Review the placeholder-only foundation PR and contract compatibility with the app PR.
2. Provide and explicitly confirm the Azure tenant and subscription display names and IDs later.
3. Verify East US 2 capability, quotas, private-network features, and expected pilot cost before Azure validation or deployment.
4. Record approval owners before creating any AZD environment or allowing the provisioning interlock to change.
5. Model and validate private endpoints/DNS, RBAC, workload hosts, APIM, ACR, Document Intelligence,
   CMK bindings, diagnostics, and lifecycle controls before proposing any interlock change.
