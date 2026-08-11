# ADR 0006 — Federated deployment identity, no stored secret

## Status

Accepted, not yet implemented. No deployment pipeline exists — `enableProvisioning` is pinned
`false` — so this decision governs the pipeline that gets written rather than one that exists.

## Context

Something has to authenticate to Azure in order to provision. Every *workload* in this design
already authenticates by managed identity: `disableLocalAuth` on Service Bus, Cosmos and Log
Analytics, `allowSharedKeyAccess: false` on all four storage accounts, RBAC-only Key Vault,
Entra-only SQL. The deployment principal is the one identity that sits outside that pattern, because
it runs in GitHub Actions rather than in Azure.

`tools/validate_foundation.py` scans the tree for five credential shapes and fails the build on any
of them. Whatever this decision picks has to survive that scan, and more importantly has to deserve
to.

## Options considered

**Client secret or certificate in Actions secrets.** The conventional answer, and the one most
people reach for first. A service principal is created, its secret is stored as a repository secret,
and the workflow signs in with it.

Rejected because it reintroduces exactly the thing the rest of the estate eliminates: a long-lived
shared credential, held outside the system that consumes it, with no expiry anyone watches. It also
brings a rotation obligation that nobody has yet been named to own — and an unrotated deployment
credential is worse than an unrotated workload credential, because the deployment principal is the
most privileged identity in the estate. A certificate is marginally better than a secret and carries
the same objection.

**Federated workload identity (OIDC).** GitHub Actions presents a short-lived OIDC token; Entra
trusts it through a federated credential scoped to this repository and a named branch or
environment. Nothing is stored. There is no secret to leak, to rotate, or to find in a log.

**No pipeline; humans run `azd` from an authenticated workstation.** Removes the credential question
entirely, and removes repeatability with it. Considered seriously because the pilot is small, and
rejected because provisioning would then be unreviewable and unreproducible — and because
`enableProvisioning` is a structural interlock that works better as a pipeline gate than as an
instruction somebody remembers.

## Decision

Federated workload identity, no stored secret.

The federated credential is scoped as narrowly as the pipeline allows: to this repository, and to a
specific branch or GitHub environment rather than to any workflow in the org. A federation subject
of "any branch" would let a pull request from a fork borrow the deployment identity, which defeats
the point.

The deployment principal is **not** a member of any approval group (`REVIEW.md` R-04). The principal
that performs a deployment must not be one that approves it.

## Consequences

No deployment credential exists to leak, so the failure mode this removes is the common one: a
secret pasted into a log, a fork, or a support ticket. It also means the credential-scanning rules
in the validator and in GitHub secret scanning (R-18) have nothing to catch here, which is the right
kind of silence — nothing to find rather than nothing looked for.

Setup is more involved than a secret. It needs an app registration or user-assigned managed identity,
a federated credential with a correctly spelled subject, and role assignments at the right scope.
A wrong subject fails at sign-in with an error that reads like a permissions problem, which is worth
knowing before debugging it.

Federation binds to a repository and a branch or environment. Moving the pipeline, renaming the
default branch, or deploying from a fork all require the credential to be updated — a small tax,
and one that only bites on changes that *should* be deliberate.

Nothing here is exercised until R-01 and R-02 clear and a pipeline is written. The decision is
recorded now so the pipeline is built this way the first time, rather than built with a secret and
migrated later — migrations of this kind tend to leave the secret in place "temporarily".

## References

- [Environments and Release Path](Environments-and-Release-Path) — subscription and identity model
- [Security and Data Protection](Security-and-Data-Protection) — secret-handling policy
- `REVIEW.md` R-01 and R-02 (tenant, subscription, authorization), R-04 (approval owners),
  R-18 (secret scanning and push protection)
- `tools/validate_foundation.py` — the credential-shape scan this decision has to satisfy
