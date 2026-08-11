# Runbook — deletion drill

> Draft. Never executed. Its pass criteria are the ratified retention numbers on the
> [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates) page. See
> [Operational Runbooks](Operational-Runbooks).

## Why this is a drill

The pilot issues a deletion receipt. A receipt is a claim, and this drill is the only thing that
makes it a true one. The failure being guarded against is not a refusal to delete — it is deletion
that reaches eight stores out of ten and reports success, because nothing checked the other two.

Run in `staging`, against a synthetic participant created for the drill and used for nothing else.

## The two stores everyone forgets

Stated first, because a checklist read top to bottom loses attention exactly where these sit:

1. **Blob versions and soft-deleted blobs.** Deleting a blob with versioning enabled creates a
   version. The current blob is gone and the content is not. A deletion sweep that deletes blobs and
   not their versions passes every naive check and is wrong.
2. **Delivery links.** A short-lived revocable link issued before erasure is a live path to content
   that erasure believes it removed. `TODO.md` 5.7 covers the links; this drill covers whether
   erasure revokes them.

## Scope

Erasure must reach every one of these, and the drill fails if any single row is unverified:

| Store | Verification |
|-------|--------------|
| Azure SQL | No row keyed to the participant in any table |
| Cosmos projections | No document; and a rebuild from post-erasure SQL still produces none |
| Blob content | No current blob **and no version** in `quarantine`, `documents`, or `packages` |
| Soft-deleted blobs | Nothing recoverable via undelete |
| Audit container | Records **remain**, content-free and pseudonymized |
| Temporary stores | Nothing in Functions working storage or the deployment container |
| Delivery links | Every issued link revoked and refused |
| Service Bus | No in-flight or dead-lettered message referencing the participant |
| Log Analytics | Content-free by design; verify no identifier leaked into telemetry |
| Backups | Content expires within the 12-month backup window |

The audit row is the only one that inverts. Audit records survive erasure by design — they are the
evidence that erasure happened — which is why the contract requires them to be content-free and
pseudonymized. A drill that finds the audit container empty after erasure has found a failure, not a
success.

## Steps

### 1. Seed

Create a synthetic participant and drive it through the full flow: a case, at least one upload
reaching each of the three working containers, an extraction, a projection, a delivery link, and at
least one dead-lettered message deliberately produced. Record every identifier created.

The dead-lettered message is deliberate. Dead-letter queues are the classic surviving copy — a
message that failed processing sits in a queue that nothing sweeps, holding a reference nobody
remembers.

### 2. Record the pre-state

For each row in the scope table, record what exists. A drill that only checks the post-state cannot
distinguish "erasure removed it" from "it was never written", and the second is the more common
reason a check passes.

### 3. Erase

Invoke the erasure path, not a hand-written script. The thing under test is the implementation
(`TODO.md` 5.6), and a drill that bypasses it tests nothing that will run in production.

Record the wall-clock time from invocation to completion, and compare it to the ratified 30-day SLA
for active data, which the mechanism must be capable of meeting under real volume, not only for a
single synthetic case.

### 4. Verify

Work the scope table top to bottom. Two checks deserve their own commands:

```bash
# Versions and soft-deleted blobs are separate from current blobs. Both must be gone.
az storage blob list \
  --account-name <storage-account> --container-name documents \
  --include vd --auth-mode login \
  --query "[?contains(name, '<case-id>')]"
```

```bash
# Dead-letter queues survive ordinary sweeps. Check them explicitly.
az servicebus queue show \
  -g <resource-group> --namespace-name <namespace> -n <queue> \
  --query "countDetails.deadLetterMessageCount"
```

Then the strongest check available, and the one that justifies ADR 0003: drop the
`case-projections` container and rebuild it from post-erasure SQL. The rebuilt projection must
contain nothing about the participant. This proves erasure at the authoritative source rather than
at the projection, and no amount of projection-level deletion can fake it.

### 5. Verify the receipt

The deletion receipt must state what was actually done. Compare its wording against the verified
results — including the audit records that deliberately survive. A receipt claiming complete removal
while pseudonymized audit metadata is retained for seven years is inaccurate, and the participant
notice has to say the same thing the receipt says.

### 6. Backups

The longest tail and the easiest to defer. Confirm that backups containing the participant's content
expire within the 12-month backup window, and that no restore path can reintroduce erased content
afterwards.

This is where a backup product that only expires whole vaults on a long schedule makes the erasure
SLA unmeetable regardless of how good the rest of the implementation is. If that is what the drill
finds, it is a finding about the backup product and belongs in `REVIEW.md`, not in `TODO.md`.

## Record

Per drill: date, environment, operator role, the pre-state and post-state for every row, the elapsed
time, and any row that could not be verified. An unverified row is a **failure**, not a gap — the
drill's entire value is that it distinguishes "checked and clean" from "not checked".

## Cadence

Proposed: before `pilot` first accepts real data, then quarterly, and after any change to the
storage topology, the projection schema, or the erasure implementation. `TODO.md` 3.4 covers the
integration tests and 3.5 the automation.

## Related pages

- [Restore Drill](Runbook-Restore-Drill)
- [ADR 0003](ADR-0003-SQL-authoritative-Cosmos-rebuildable)
- [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates)
