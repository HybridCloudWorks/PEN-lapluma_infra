# LaPluma Azure infrastructure and backend

This repository will contain the backend contracts, Azure infrastructure as code, and backend
services for the LaPluma `lapluma-app-0.2` supervised pilot. It is a placeholder-only code and
planning foundation; it is not deployed.

The versioned package identity/composition handshake shared with the iOS repository is
[`contracts/catalog-package-compatibility.json`](contracts/catalog-package-compatibility.json).

Start with [`.azure/plan.md`](.azure/plan.md). That file is the source of truth for architecture,
scope, gates, generation, validation, and deployment. Missing non-secret values and approval owners
are tracked in [`IMPLEMENTATION_LEDGER.md`](IMPLEMENTATION_LEDGER.md).

## Current status

- Initial `lapluma-infra-0.0` contract, service, and infrastructure scaffold.
- Azure preparation path: AZD with Bicep.
- Region: US-only East US 2, subject to service/SKU/quota verification.
- Pilot: approximately 40 initial supervised cases, with a server-enforced maximum of 999 enrolled
  users before a new approval cycle.
- `azure.yaml`, modular Bicep, OpenAPI, and minimal backend source are generated for review.
- Bicep structurally permits only `enableProvisioning: false`; unlocking it requires a reviewed
  code change after the missing private connectivity, workload hosts, RBAC, encryption bindings,
  diagnostics, and lifecycle controls are implemented.
- No AZD environment or Azure resource has been created, validated against a subscription, or deployed.
- Azure tenant and subscription are unknown. Placeholder-only local generation is approved, but
  Azure environment creation, preflight, provisioning, and deployment remain blocked.

## Required workflow

1. Review `.azure/plan.md`, the contracts, and the generated placeholder scaffold.
2. Confirm the actual Azure tenant and subscription by display name and ID when deployment planning begins.
3. Verify East US 2 service availability, private-network features, quota, and expected cost.
4. Run `azure-validate` and record evidence in the plan before any deployment request.
5. Obtain separate explicit deployment approval, then use `azure-deploy`.

Do not store credentials, private keys, connection strings, passkey material, tokens, document
content, applicant identifiers, or other production data in this repository or its ledger.
