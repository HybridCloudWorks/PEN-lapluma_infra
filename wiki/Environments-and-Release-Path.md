# Environments and release path

## Environments

| Environment | Data | Purpose | Promotion gate |
|-------------|------|---------|----------------|
| `dev` | Synthetic only | Developer integration and contract testing | Automated tests and policy checks |
| `staging` | Synthetic; consented pilot fixtures only by explicit approval | Production-equivalent security, load, failure, and deletion drills | Security, privacy, UPL, accessibility, fidelity, and operations signoff |
| `pilot` | Approved real participant data | Initial ~40 supervised cases, later controlled expansion below 1,000 users | Manual protected-environment approval and every real-data gate |

No environment exists today. Environment names, resource naming, subscription placement, tenant, and
deployment principals are finalized only after the hard context gate described in the
[Azure Deployment Plan](Azure-Deployment-Plan) closes.

## Provisioning interlock

`infra/main.bicep` declares:

```bicep
@description('Safety interlock. Must remain false until tenant, subscription, cost, and deployment approval are recorded.')
@allowed([
  false
])
param enableProvisioning bool = false
```

Every resource group and module in the entrypoint is guarded by `if (enableProvisioning)`, and
`infra/main.parameters.json` pins the value to `false`. The template therefore compiles and can be
reviewed, but it cannot create a resource.

`tools/validate_foundation.py` enforces the interlock on every run and in CI: the Bicep default must
be `false`, the `@allowed` list must reject a `true` override, and the parameter file value must be
`false`.

Unlocking provisioning is a reviewed code change, not a parameter change, and it may not be proposed
until the missing private connectivity, workload hosts, RBAC, encryption bindings, diagnostics, and
lifecycle controls are implemented. That work is tracked in `TODO.md`; the approvals that must
precede it are tracked in `REVIEW.md`.

## Release path

1. Review the placeholder-only foundation and confirm contract compatibility with the iOS
   application repository.
2. Confirm the Azure tenant and subscription by display name and ID.
3. Verify East US 2 capability, quotas, private-network features, and expected pilot cost.
4. Record the approval owners before creating any AZD environment or changing the provisioning
   interlock.
5. Model and validate private endpoints and DNS, RBAC, workload hosts, APIM, ACR, Document
   Intelligence, customer-managed-key bindings, diagnostics, and lifecycle controls.
6. Run `azure-validate` and record the evidence.
7. Obtain separate explicit deployment approval, then run `azure-deploy` against `dev`, then
   `staging`.
8. Create `pilot` only after every real-data gate in
   [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates) has passed.

## Resource tagging

Every resource carries the common tag set applied by `infra/main.bicep`:

| Tag | Source |
|-----|--------|
| `azd-env-name` | AZD environment name parameter |
| `system` | Fixed value `lapluma` |
| `release` | Fixed value `lapluma-infra-0.0` |
| `correlated-app-release` | Fixed value `lapluma-app-0.2` |
| `data-residency` | Fixed value `us` |
| `owner` | `AZURE_RESOURCE_OWNER` |
| `cost-center` | `AZURE_COST_CENTER` |
| `data-classification` | Fixed value `sensitive-pii` |

Storage accounts additionally carry a `purpose` tag of `quarantine`, `documents`, `packages`, or
`audit`.

## Related pages

- [Azure Deployment Plan](Azure-Deployment-Plan)
- [Configuration Contract](Configuration-Contract)
- [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates)
