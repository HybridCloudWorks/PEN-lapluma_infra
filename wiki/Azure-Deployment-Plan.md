# Azure deployment plan

Planning source recorded 2026-08-02, migrated from the repository on 2026-08-07.

This page describes the **plan and the process**. It deliberately carries no task checkboxes and no
status columns: completed work is recorded in `CHANGELOG.md`, remaining engineering work in
`TODO.md`, and decisions that require a human owner in `REVIEW.md`.

## Goal

Build the Azure backend, service contracts, and infrastructure for the LaPluma `lapluma-app-0.2`
supervised pilot. The pilot begins with approximately 40 real cases and is hard-capped below 1,000
enrolled users.

The system prepares and securely delivers verified form packages. It does **not** recommend forms,
provide legal advice, approve cases automatically, sign forms, or file with an agency.

**Path:** new project. The workspace was empty at planning time — no application source,
`azure.yaml`, infrastructure, Dockerfiles, or CI/CD configuration existed.

## Repository boundary

This repository owns backend services, API and event contracts, Azure infrastructure as code,
deployment configuration, operational controls, and evidence-producing tests. The native iOS client
remains in its own application repository. The versioned package identity and composition handshake
shared with the iOS repository is `contracts/catalog-package-compatibility.json`
(`contractVersion: lapluma-app-0.2`).

## Requirements

| Attribute | Value |
|-----------|-------|
| Classification | Production-sensitive supervised pilot |
| Scale | Small: initial ~40 real cases; hard cap of 999 enrolled users |
| Budget | Balanced; managed services and security controls take priority over minimum cost |
| Data residency | US only |
| Tenant | Not confirmed — mandatory confirmation before an AZD environment or deployment |
| Subscription | Not confirmed — mandatory confirmation by display name and ID before an AZD environment or deployment |
| Location | East US 2 (`eastus2`), approved in principle; service and quota availability must be verified before deployment |
| Delivery recipe | Azure Developer CLI (AZD) with Bicep |
| Application stack | .NET 10 core services; Python 3.13 document-processing workers; Azure Functions and Durable Functions for event and scheduled orchestration |

The governing constraints that shape every one of these choices are documented in
[Security and Data Protection](Security-and-Data-Protection).

## Hard context gate

No Azure-connected environment, preflight, role assignment, resource-provider registration,
provisioning operation, or deployment may be created or changed until all of the following are
recorded and explicitly confirmed:

1. Azure tenant display name and tenant ID.
2. Azure subscription display name and subscription ID.
3. Confirmation that the subscription is authorized for real pilot PII and cost-bearing resources.
4. Confirmation that `eastus2` supports every selected service and SKU, the required quota, the
   required private-networking features, and the Azure AI Document Intelligence models the pilot
   uses.
5. Identities and owners for deployment approval, security, privacy, compliance, operations, and
   cost management.

Failure to satisfy any item blocks AZD environment creation, Azure preflight, provisioning, and
deployment.

Local placeholder-only contracts, application source, `azure.yaml`, and Bicep may be generated and
tested with provisioning disabled. That local-generation approval was given on 2026-08-02 and does
**not** satisfy this gate.

Each gate item is tracked as an open blocker with a named owner in `REVIEW.md`.

## Recipe selection

**Selected:** AZD with Bicep.

**Rationale:** this is a new Azure-first, multi-service repository. AZD provides explicit
environment management and a repeatable validation and deployment workflow; Bicep provides native
Azure modules, policy visibility, and deterministic infrastructure review. Generation uses reusable
Bicep modules with environment-specific parameters and no secrets in parameter files.

## Mandatory workflow

```
azure-prepare
  → user approval and Azure-context confirmation
    → generation
      → azure-validate
        → explicit deployment approval
          → azure-deploy
```

Deployment is never invoked directly from preparation. The four phases below define what each stage
must produce.

### Phase 1 — Planning

Analyze the workspace, gather approved requirements, scan the codebase, select the delivery recipe,
and plan the application, trust zones, data flow, environments, and pilot gates. Close the hard
context gate: confirm tenant and subscription, verify East US 2 service availability, SKU features,
quotas, and cost approvals, and record the security, privacy, compliance, operations, and deployment
approvers.

### Phase 2 — Execution

Research each selected Azure component and load its preparation guidance. Record the confirmed
Azure context before creating an AZD environment. Define the OpenAPI catalog and schema contract,
generate `azure.yaml` and environment conventions, generate a modular and structurally locked Bicep
foundation whose provisioning parameter accepts only `false`, generate the service skeletons and
container build definitions, and implement the security invariants, retention, audit, and evidence
collection.

The plan status stays at `Executing` until the Azure context gate permits subscription-aware
validation.

### Phase 3 — Validation

Invoke the `azure-validate` skill. Run static application, contract, Bicep, policy, security, and
secret scans. Run deployment preflight against the confirmed subscription **without** provisioning.
Run integration, isolation, authorization, deletion, fidelity, and failure-mode tests. Record the
validation evidence and move the status to `Validated` only after every blocking check passes.

Planning evidence is not deployment validation. Validation evidence already collected is recorded in
`CHANGELOG.md`.

### Phase 4 — Deployment

Obtain explicit approval for cost-bearing pilot provisioning. Invoke the `azure-deploy` skill.
Deploy `dev`, validate it, then `staging`; do not create `pilot` until every real-data gate passes.
Execute post-deployment smoke, private-network, identity, audit, restore, and deletion checks. Move
the status to `Deployed` only after evidence is recorded.

## Generated foundation inventory

The `lapluma-infra-0.0` foundation generated on 2026-08-02 consists of:

| Artifact | Purpose |
|----------|---------|
| `azure.yaml` | AZD service and deployment configuration for `core-api`, `processing-worker`, and `acquisition-functions` |
| `infra/main.bicep` and `infra/modules/*` | Subscription-scope entrypoint plus network, observability, security, messaging, and data modules, all behind the provisioning interlock |
| `infra/main.parameters.json` | Environment-variable-substituted, secret-free parameter file |
| `contracts/catalog.openapi.json` | OpenAPI 3.1.0 catalog hierarchy, package, edition, and extracted-schema contract |
| `contracts/catalog-package-compatibility.json` | Package identity and composition handshake shared with the iOS repository |
| `src/core-api` | Minimal .NET 10 catalog and health API |
| `src/document-processing` | Python 3.13 isolated worker health surface |
| `src/functions` | Durable Functions catalog-acquisition proposal skeleton |
| `tools/validate_foundation.py` | Dependency-free contract, interlock, and secret-absence validation |
| `.github/workflows/foundation-validation.yml` | Build, contract, Bicep, and container validation |

What the foundation intentionally does **not** yet model — Container Apps environments and apps,
Functions hosting, API Management, Container Registry, Document Intelligence, private endpoints and
private DNS, RBAC assignments, customer-managed-key bindings, diagnostic settings, and protected
deployment environments — is tracked as engineering work in `TODO.md`.

## Related pages

- [Architecture Overview](Architecture-Overview)
- [Environments and Release Path](Environments-and-Release-Path)
- [Configuration Contract](Configuration-Contract)
- [Azure Component Research Record](Azure-Component-Research-Record)
