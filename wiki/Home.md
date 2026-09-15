# LaPluma infrastructure wiki

[Approved GCP target and Document Library](GCP-Document-Library-Decision.md) · [Cost model](GCP-Cost-Model.md) · [Board handoff](GCP-Board-Handoff.md) · [Environment Matrix](../docs/planning/gcp-environment-matrix.md)

The Lean GCP architecture under [ADR-019](GCP-Document-Library-Decision.md) and the Document Library platform have been fully implemented and verified across 18 engineering phases. Historical Azure pages below are preserved for architectural lineage; active infrastructure definitions reside in `infra/terraform/`.


Long-form documentation for the `PEN-lapluma_infra` repository: the backend contracts, Azure
infrastructure as code, and backend services for the LaPluma `lapluma-app-0.2` supervised pilot.

This wiki is the authoritative destination for architecture, design, policy, procedure, research,
and knowledge-transfer documentation. Live work state is **not** kept here — see
[Where things live](#where-things-live).

## Pages

| Page | Contents |
|------|----------|
| [Azure Deployment Plan](Azure-Deployment-Plan) | Project goal, delivery recipe, requirements, hard context gate, mandatory `azure-prepare → azure-validate → azure-deploy` workflow |
| [Architecture Overview](Architecture-Overview) | Trust zones, planned components and dependencies, Azure service mapping, data ownership and flow |
| [Environments and Release Path](Environments-and-Release-Path) | `dev` / `staging` / `pilot` definitions, promotion gates, provisioning interlock |
| [Configuration Contract](Configuration-Contract) | Every non-secret configuration input, its expected format, owning role, and consuming component |
| [Security and Data Protection](Security-and-Data-Protection) | Governing constraints, secret-handling policy, network and key-management boundaries |
| [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates) | Alpha 0.2 catalog scope, pilot policy baselines, UPL gate, retention and erasure targets |
| [Azure Component Research Record](Azure-Component-Research-Record) | Research findings that shaped the generated foundation, recorded 2026-08-02 |
| [Architecture Decision Records](Architecture-Decision-Records) | Foundational decisions, each with the options that were rejected and why |
| [Operational Runbooks](Operational-Runbooks) | Incident response, on-call, restore drill, deletion drill — drafts, never yet executed |
| [Documentation Standards](Documentation-Standards) | The repository documentation model and how to classify a new document |

## Where things live

| Content type | Destination |
|--------------|-------------|
| Repository purpose, install, configuration overview, navigation | `README.md` in the repository root |
| Completed work | `CHANGELOG.md` in the repository root |
| Blockers that only a human decision, approval, or access grant can clear | `REVIEW.md` in the repository root |
| Actionable engineering work | `TODO.md` in the repository root |
| Everything else | This wiki |

See [Documentation Standards](Documentation-Standards) for the classification rules.

## Current posture
 
 The repository has transitioned from the initial scaffold to a fully tested and verified Lean GCP
 infrastructure and Document Library platform (`lapluma-infra-0.2`) implementing ADR-019.
 Modular Terraform definitions are in place for `dev`, `staging`, and `pilot` environments in
 `infra/terraform/`. All 49 cross-repository implementation cards across Projects 2, 3, and 4
 are complete and merged to `main`. The next operational milestone is staging environment bring-up
 and deployment verification.

