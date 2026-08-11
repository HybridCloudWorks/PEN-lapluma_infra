# LaPluma infrastructure wiki

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

The repository is a placeholder-only planning and scaffold foundation (`lapluma-infra-0.0`). No AZD
environment has been created, no Azure subscription has been contacted, and no resource has been
provisioned or deployed. The Bicep entrypoint structurally accepts only `enableProvisioning: false`.
Open approvals are tracked in `REVIEW.md`; remaining engineering work is tracked in `TODO.md`.
