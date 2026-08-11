# Architecture decision records

Foundational decisions, each recorded with what was rejected and why. Several of these decisions
were previously documented only as conclusions — the templates said what was built, and nothing said
what was considered and set aside. A future engineer reading only the conclusion cannot tell whether
a constraint is load-bearing or incidental, and the usual result is that it gets changed by someone
who assumes it was arbitrary.

Every record below states its status honestly. "Accepted" means the decision is made and
implemented. "Accepted, partly gated" means the decision is made but something in `REVIEW.md` still
has to clear before it is fully realized. "Accepted, not yet implemented" means the decision governs
code that has not been written — recorded now so the thing is built this way the first time rather
than built wrongly and migrated. Neither is the same as an undecided question, and the difference
is marked rather than left to be inferred.

## Records

| Record | Decision | Status |
|--------|----------|--------|
| [ADR 0001](ADR-0001-AZD-and-Bicep-over-Terraform) | AZD with Bicep, not Terraform | Accepted |
| [ADR 0002](ADR-0002-Three-Container-Apps-environments) | Three Container Apps environments, not one with internal isolation | Accepted |
| [ADR 0003](ADR-0003-SQL-authoritative-Cosmos-rebuildable) | Azure SQL authoritative, Cosmos rebuildable projections | Accepted |
| [ADR 0004](ADR-0004-Service-Bus-Premium-over-Standard) | Service Bus Premium, not Standard | Accepted |
| [ADR 0005](ADR-0005-Managed-HSM-over-Key-Vault-keys) | Managed HSM, not Key Vault-managed keys | Accepted, partly gated |
| [ADR 0006](ADR-0006-Federated-deployment-identity) | Federated deployment identity, no stored secret | Accepted, not yet implemented |

## Writing a new record

Keep them short. A record that takes twenty minutes to read does not get read at the moment it
matters, which is when someone is about to change the thing it describes.

Six sections: **Status**, **Context**, **Options considered**, **Decision**, **Consequences**, and
**References**. The section that earns the page is *Options considered* — a record with one option is
not a decision record, it is a description. Include the option that was nearly chosen, and say what
would have to change for it to win, because that is what a future engineer is actually looking for.

State the costs of the decision in *Consequences*, including the ones that are still being paid. A
record that lists only benefits reads as advocacy and gets discounted accordingly.

Number records sequentially and never renumber. Superseding a decision means adding a new record and
marking the old one superseded, not editing it — the reasoning that was overturned is part of why
the new decision is right.

## Related pages

- [Architecture Overview](Architecture-Overview)
- [Azure Component Research Record](Azure-Component-Research-Record)
- [Security and Data Protection](Security-and-Data-Protection)
