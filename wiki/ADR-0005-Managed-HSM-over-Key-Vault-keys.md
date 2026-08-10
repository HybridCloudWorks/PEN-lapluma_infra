# ADR 0005 — Managed HSM over Key Vault-managed keys

## Status

Accepted, partly gated. The pool is declared in `infra/modules/security.bicep`. Binding
customer-managed keys to the data stores is `TODO.md` 2.1, and the administrator set, key hierarchy,
quorum, and cost are `REVIEW.md` R-10 and R-03.

## Context

The pilot holds a data plane classified production-sensitive PII, and customer-managed encryption is
a stated pilot prerequisite rather than a preference. Three stores need key protection: Azure SQL
transparent data encryption, the four storage accounts, and Cosmos DB.

Azure offers three levels. Platform-managed keys need no key management at all. Key Vault Premium
provides HSM-backed keys in a multi-tenant service Microsoft administers. Managed HSM provides a
single-tenant pool with FIPS 140-3 Level 3 validated hardware, where administrative control is held
by the customer and Microsoft has no plane through which to access key material.

## Options considered

**Platform-managed keys.** Rejected: customer-managed encryption is a prerequisite, so this does not
meet the requirement regardless of its merits.

**Key Vault Premium with HSM-backed keys.** The serious alternative, and the one worth arguing for.
It satisfies "customer-managed" in the sense every compliance questionnaire means it, costs a small
fraction of a Managed HSM pool, has no bootstrap ceremony, no security domain to protect, and no
quorum to assemble. For most systems this is the right answer.

It was set aside on the administrative-control axis rather than the cryptographic one. In Key Vault,
Microsoft administers the service; the customer controls key *usage* through RBAC, and the trust
boundary includes Microsoft's operation of a multi-tenant service. In Managed HSM, the customer
holds the administrative roles and the security domain, and Microsoft's operators have no path to
the key material. For a pilot holding identity documents belonging to people who did not choose the
vendor, that distinction was judged worth paying for.

That judgement is contestable, and this is the record to contest it in. If the CISO's position under
R-10 is that Key Vault Premium meets the obligation, the consequence is a materially cheaper estate
and a simpler one, and nothing else in the architecture depends on the choice.

**Bring-your-own-key from an on-premises HSM.** Rejected as scope. It presumes an existing HSM
practice, an export ceremony, and an operational team to run it, none of which are in evidence.

## Decision

Managed HSM, SKU `Standard_B1`, with purge protection enabled and soft delete at 90 days.

One key per purpose — SQL TDE, storage, Cosmos — rather than one shared key, so that revoking a key
in response to a suspected compromise takes down one store instead of the whole data plane. That
choice only exists if the keys are separate, and it is not available retrospectively.

`AZURE_HSM_INITIAL_ADMIN_OBJECT_ID` is proposed under R-10 to be an Entra group, never an individual
and never the deployment principal, with the security domain downloaded under a quorum of three
holders before any key is created.

## Consequences

The bootstrap is irreversible. Purge protection means a pool created with the wrong administrator
set cannot simply be deleted and recreated, and a security domain with too few holders cannot be
re-quorumed later. This is why R-10 gates provisioning rather than following it, and why it is one of
the few items on that page where getting it wrong is expensive rather than merely inconvenient.

The pool bills continuously from activation, in every environment it exists in, whether or not a key
is ever used. R-03's proposal is that `dev` runs without a pool for exactly this reason, and accepts
the consequence that the bootstrap is then rehearsed in `staging` rather than twice.

Losing the security domain loses the keys, and losing the keys loses the data — no Microsoft support
path recovers it. That is the property being bought, stated in its unflattering direction. The
backup and restore rehearsal in R-10 is not a formality.

Key rotation at twelve months is proposed automatic rather than as a calendar task, on the grounds
that an annual manual task in a pilot happens once.

Log Analytics is proposed *out* of customer-managed-key scope: the workspace holds content-free
telemetry by design, so the marginal protection is small against a linked-workspace configuration
that is awkward to reverse.

## References

- [Security and Data Protection](Security-and-Data-Protection) — key management boundaries
- [Azure Component Research Record](Azure-Component-Research-Record) — key management section
- `REVIEW.md` R-10 (administration, key hierarchy, quorum), R-03 (cost)
- `TODO.md` 2.1 — binding the keys to the data stores
- `infra/modules/security.bicep`
