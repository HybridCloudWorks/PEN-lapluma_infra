---
title: "LaPluma infrastructure wiki"
---

# LaPluma infrastructure wiki

[Approved GCP target and Document Library](GCP-Document-Library-Decision.md) · [Cost model](GCP-Cost-Model.md) · [Board handoff](GCP-Board-Handoff.md) · [Environment Matrix](https://github.com/HybridCloudWorks/PEN-lapluma_infra/blob/main/docs/planning/gcp-environment-matrix.md)

The Lean GCP architecture under [ADR-019](GCP-Document-Library-Decision.md) and the Document Library platform have been fully implemented and verified across 18 engineering phases. Historical Azure pages below are preserved for architectural lineage; active infrastructure definitions reside in `infra/terraform/`.


Long-form documentation for the `PEN-lapluma_infra` repository: the backend contracts, Azure
infrastructure as code, and backend services for the LaPluma `lapluma-app-0.2` supervised pilot.

This wiki is the authoritative destination for architecture, design, policy, procedure, research,
and knowledge-transfer documentation. Live work state is **not** kept here — see
[Where things live](#where-things-live).

## Pages

| Page | Contents |
|------|----------|
| [Azure Deployment Plan](Azure-Deployment-Plan.md) | Project goal, delivery recipe, requirements, hard context gate, mandatory `azure-prepare → azure-validate → azure-deploy` workflow |
| [Architecture Overview](Architecture-Overview.md) | Trust zones, planned components and dependencies, Azure service mapping, data ownership and flow |
| [Environments and Release Path](Environments-and-Release-Path.md) | `dev` / `staging` / `pilot` definitions, promotion gates, provisioning interlock |
| [Configuration Contract](Configuration-Contract.md) | Every non-secret configuration input, its expected format, owning role, and consuming component |
| [Security and Data Protection](Security-and-Data-Protection.md) | Governing constraints, secret-handling policy, network and key-management boundaries |
| [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates.md) | Alpha 0.2 catalog scope, pilot policy baselines, UPL gate, retention and erasure targets |
| [Azure Component Research Record](Azure-Component-Research-Record.md) | Research findings that shaped the generated foundation, recorded 2026-08-02 |
| [Architecture Decision Records](Architecture-Decision-Records.md) | Foundational decisions, each with the options that were rejected and why |
| [Operational Runbooks](Operational-Runbooks.md) | Incident response, on-call, restore drill, deletion drill — drafts, never yet executed |
| [Documentation Standards](Documentation-Standards.md) | The repository documentation model and how to classify a new document |

## Where things live

| Content type | Destination |
|--------------|-------------|
| Repository purpose, install, configuration overview, navigation | `README.md` in the repository root |
| Completed work | `CHANGELOG.md` in the repository root |
| Blockers that only a human decision, approval, or access grant can clear | `REVIEW.md` in the repository root |
| Actionable engineering work | The private LaPluma project boards (formerly `TODO.md`) |
| Everything else | This wiki |

See [Documentation Standards](Documentation-Standards.md) for the classification rules.

## Current posture
 
 The repository has transitioned from the initial scaffold to a fully tested and verified Lean GCP
 infrastructure and Document Library platform (`lapluma-infra-0.2`) implementing ADR-019.
 Modular Terraform definitions are in place for `dev`, `staging`, and `pilot` environments in
 `infra/terraform/`. All 49 cross-repository implementation cards across Projects 2, 3, and 4
 are complete and merged to `main`. The next operational milestone is staging environment bring-up
 and deployment verification.

## On this site

**Current target (GCP)**

- [Approved target: lean GCP and Document Library](GCP-Document-Library-Decision.md)
- [Lean GCP cost model](GCP-Cost-Model.md)
- [Board execution and delivery record](GCP-Board-Handoff.md)

**Architecture and configuration**

- [Architecture overview](Architecture-Overview.md)
- [Configuration contract](Configuration-Contract.md)
- [Environments and release path](Environments-and-Release-Path.md)
- [Security and data protection](Security-and-Data-Protection.md)
- [Pilot policy and compliance gates](Pilot-Policy-and-Compliance-Gates.md)
- [Documentation standards](Documentation-Standards.md)

**Operations**

- [Operational runbooks](Operational-Runbooks.md)
- [Runbook — incident response](Runbook-Incident-Response.md)
- [Runbook — on-call procedure](Runbook-On-Call-Procedure.md)
- [Runbook — restore drill](Runbook-Restore-Drill.md)
- [Runbook — deletion drill](Runbook-Deletion-Drill.md)

**Historical Azure planning** (conflicting assumptions superseded by the approved GCP target)

- [Azure deployment plan](Azure-Deployment-Plan.md)
- [Azure component research record](Azure-Component-Research-Record.md)

**Decision records** (the approved GCP target supersedes conflicting Azure assumptions in them)

- [Architecture decision records](Architecture-Decision-Records.md)
  - [ADR 0001 — AZD and Bicep over Terraform](ADR-0001-AZD-and-Bicep-over-Terraform.md)
  - [ADR 0002 — Three Container Apps environments](ADR-0002-Three-Container-Apps-environments.md)
  - [ADR 0003 — Azure SQL authoritative, Cosmos DB rebuildable](ADR-0003-SQL-authoritative-Cosmos-rebuildable.md)
  - [ADR 0004 — Service Bus Premium over Standard](ADR-0004-Service-Bus-Premium-over-Standard.md)
  - [ADR 0005 — Managed HSM over Key Vault-managed keys](ADR-0005-Managed-HSM-over-Key-Vault-keys.md)
  - [ADR 0006 — Federated deployment identity](ADR-0006-Federated-deployment-identity.md)
  - [ADR 0007 — Workflow API as a second core-zone service](ADR-0007-Workflow-API-as-second-core-service.md)

---

This site is built from `docs/wiki/` on the `main` branch of
[PEN-lapluma_infra](https://github.com/HybridCloudWorks/PEN-lapluma_infra). Edit the files there
through a pull request, not the site.
