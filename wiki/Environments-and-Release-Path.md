# Environments and release path

## Environments

| Environment | Data | Purpose | Promotion gate |
|-------------|------|---------|----------------|
| `dev` | Synthetic only | Developer integration and contract testing | Automated tests and policy checks |
| `staging` | Synthetic; consented pilot fixtures only by explicit approval | Production-equivalent security, load, failure, and deletion drills | Security, privacy, UPL, accessibility, fidelity, and operations signoff |
| `pilot` | Approved real participant data | Initial ~40 supervised cases, later controlled expansion below 1,000 users | Manual protected-environment approval and every real-data gate |

No environment exists today. Environment names, resource naming, tenant, and the specific
subscription are finalized only after the hard context gate described in the
[Azure Deployment Plan](Azure-Deployment-Plan) closes.

## Subscription and identity model

Two decisions that do not depend on knowing *which* tenant or subscription, and are therefore
settled ahead of the gate.

**One subscription, three resource groups.** All three environments share a subscription and are
separated by resource group — which is what `infra/main.bicep` already does, creating one group per
environment named `rg-{prefix}-{environment}`. One authorization to obtain, one cost centre to
report against.

The environment is identifiable from the `azd-env-name` tag, which every resource carries. A
dedicated `environment` tag holding just `dev`, `staging` or `pilot` is proposed under `REVIEW.md`
R-05 and is **not applied today** — worth knowing before writing a governance query against it.

The cost is blast radius, and it should be stated rather than glossed: a subscription-level
misconfiguration, policy assignment, or role grant reaches all three environments, and `pilot` is
the one holding real participant data. The mitigations available at this size are resource-group
scoping — every role assignment in `infra/modules/rbac.bicep` is already scoped to a single
resource, not to the group or the subscription — and the `dev` synthetic-only rule below. If the
estate grows past a supervised pilot, splitting `pilot` into its own subscription is the first
change to make.

**Deployment authenticates by federated workload identity.** No client secret is stored in the
repository, in Actions secrets, or anywhere else. See
[ADR 0006](ADR-0006-Federated-deployment-identity).

## Data classification

`dev` holds **synthetic data only**. This is a decision, not an aspiration, and two things depend
on it: the restore and deletion drills can only run somewhere that holds no real data, because a
drill against real participant data creates a second copy of it with its own retention obligation;
and the proposal to run `dev` without a Managed HSM pool rests on `dev` protecting nothing that
needs one.

It is expressed as the `dataClassification` parameter, constrained by an `@allowed` list to
`synthetic` or `production-sensitive-pii`. Through AZD it is effectively **required**: the parameter
file always supplies a value, so leaving `LAPLUMA_DATA_CLASSIFICATION` unset substitutes an empty
string and the deployment fails at submission naming the parameter. That is the same fail-closed
convention every other variable uses, and it is the right one here — an environment's data
classification should be stated by whoever authorizes it, not inherited from a template default.

The `synthetic` default covers only a direct `az deployment` that omits the parameter file entirely,
where landing on the restrictive claim is the safe outcome. Either way a typo fails at submission
rather than tagging the estate wrongly.

What this does *not* do is stop somebody uploading a real document to `dev`. The tag is a
declaration and a governance filter, not an access control. The control that would enforce it is the
`dev` environment having no route to a production data source, which the trust-zone model already
provides, plus the operational rule that nobody carries real material into it.

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
3. Verify South Central US capability, quotas, private-network features, and expected pilot cost.
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
| `data-classification` | `LAPLUMA_DATA_CLASSIFICATION`: `synthetic` in `dev`, `production-sensitive-pii` in `staging` and `pilot` |

Storage accounts additionally carry a `purpose` tag of `quarantine`, `documents`, `packages`, or
`audit`.

## Related pages

- [Azure Deployment Plan](Azure-Deployment-Plan)
- [Configuration Contract](Configuration-Contract)
- [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates)
