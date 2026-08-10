# ADR 0001 — AZD and Bicep over Terraform

## Status

Accepted. Implemented in `azure.yaml`, `infra/main.bicep`, and `infra/modules/`.

## Context

The pilot needs infrastructure as code that a small team can operate, that expresses a private-only
Azure posture faithfully, and that can be validated in CI without contacting Azure. It also needs to
support a hard interlock: the entrypoint must be structurally incapable of provisioning until the
approvals in `REVIEW.md` clear.

The estate is entirely Azure. There is no second cloud, no on-premises component, and no third-party
provider in the service mapping.

## Options considered

**Terraform with the AzureRM provider.** The strongest alternative, and it wins on several axes that
matter elsewhere: a mature plan/apply cycle that shows a real diff before anything changes, state
that models drift explicitly, and a provider ecosystem that reaches beyond Azure. If this estate had
a second cloud or a significant third-party surface, this would be the decision.

What ruled it out here is the state file. Terraform state contains resource attributes, and for this
estate that would include values from a data plane classified production-sensitive. Storing,
locking, encrypting, and controlling access to that state is a real body of work with its own
blast radius, and it exists to solve a problem — cross-provider orchestration — that this estate
does not have. ARM's deployment history serves the same purpose here without a new artifact to
protect.

**Bicep invoked directly, without AZD.** Viable, and lighter. Rejected because AZD supplies the
environment model (`dev` / `staging` / `pilot`), the parameter substitution the configuration
contract depends on, and the service-to-resource binding, all of which would otherwise be
hand-rolled scripts. The scripts are the part that rots.

**ARM JSON templates.** Rejected outright. Bicep compiles to exactly this and is legible.

## Decision

AZD with Bicep. `infra/main.bicep` uses `targetScope = 'subscription'`, applies the required
`azd-env-name` tag, emits only non-secret outputs, and hard-codes no tenant, subscription, or
resource-group identifier.

Two constraints beyond stock AZD conventions:

- `bicepconfig.json` sets twenty linter rules to `error`, and CI fails the build on any diagnostic.
  A warning nobody reads is a warning nobody acts on.
- `enableProvisioning` is pinned `false` in `infra/main.parameters.json` and restricted to `false`
  by an `@allowed` list in the Bicep itself. The interlock is structural rather than procedural: it
  cannot be bypassed by overriding a parameter file.

## Consequences

Templates validate offline, in CI, on every push, with no Azure credential — which is what makes the
foundation reviewable while `REVIEW.md` R-01 and R-02 are still open.

`tools/validate_foundation.py` exists because Bicep's linter checks syntax and idiom, not the
invariants this estate cares about: that no AI-zone identity holds a data-plane role, that every
diagnosable resource has a diagnostic setting, that Python versions agree across six declaration
points. Those are assertions about the compiled ARM, and they had to be written here rather than
configured.

The cost being paid: Bicep has no plan step. There is no diff to read before an apply, so the
review burden falls on the template rather than on a preview. That is a genuine loss relative to
Terraform, and it is mitigated only by the linter, the validator, and the provisioning interlock —
not solved.

Moving to Terraform later means rewriting the templates and importing existing resources into state.
It is not a one-way door, but it is an expensive one, and the point at which it becomes worth paying
is the point at which a second cloud or a significant third-party provider enters the estate.

## References

- [Azure Component Research Record](Azure-Component-Research-Record) — AZD and Bicep section
- [Environments and Release Path](Environments-and-Release-Path) — the provisioning interlock
- `infra/main.bicep`, `bicepconfig.json`, `tools/validate_foundation.py`
