# Runbook — restore drill

> Draft. Never executed. Every command is written against the resource shapes declared in `infra/`,
> not against a deployed resource. See [Operational Runbooks](Operational-Runbooks).

## Why this is a drill and not a document

A backup that has never been restored is a belief. The real-user pilot prerequisites require restore
drill evidence, and the evidence that counts is a restored store that was verified, with a recorded
time, not a screenshot of a backup policy.

Run in `staging` against synthetic data. Never in `pilot`, and never against real participant data —
a restore drill that touches real data has created a second copy of it, with its own retention
obligation, which is a privacy incident dressed as diligence.

## Scope

Six things have to come back, and two of them are the ones drills usually forget:

| Store | Mechanism | Notes |
|-------|-----------|-------|
| Azure SQL | Point-in-time restore | Restores to a **new** database; the swap is a separate step |
| Cosmos DB projections | Rebuild from SQL | Not a restore at all — see below |
| Blob content | Soft delete and versioning | Windows are R-11 values |
| Audit container | Immutable, cannot be lost | Verify it is *readable*, which is a different claim |
| Managed HSM keys | Pool backup and security domain | **The one that matters most** |
| Infrastructure | Redeploy from `infra/` | Templates are the source of truth |

Cosmos is deliberately not restored from a backup. Projections are rebuildable from SQL by design
(ADR 0003), so the drill rebuilds rather than restores — and the rebuild is a stronger check,
because a projection that cannot be rebuilt has stopped being derived, which is a design violation
the drill is well placed to catch.

## Order

Keys first, then infrastructure, then SQL, then blob, then projections. Restoring a database whose
encryption key is unavailable produces a database that cannot be opened, and discovering that after
the database restore has completed wastes the most expensive step in the drill.

## Steps

### 1. Key material

The step everything else depends on and the one most likely to be skipped because it is
uncomfortable.

- Confirm the security domain is present and its quorum holders are reachable — as people, not as
  a file path. A quorum recorded but never contacted is not a quorum.
- Confirm a pool backup exists and is newer than the most recent key rotation.
- Confirm the three per-purpose keys (`cmk-sql-tde`, `cmk-storage`, `cmk-cosmos`) are listed and
  enabled.

Do not test a full pool restore in the same subscription as a working pool without the CISO's
agreement. Recovery is destructive and purge protection makes mistakes expensive.

### 2. Infrastructure

```bash
azd provision --environment <staging-environment>
```

The templates are the source of truth. If a restored environment differs from the deployed one, the
drill has found configuration drift, which is a finding worth more than the drill itself.

### 3. Azure SQL

```bash
az sql db restore \
  -g <resource-group> -s <sql-server-name> -n lapluma \
  --dest-name lapluma-restore-<yyyymmdd> \
  --time <restore-point-utc>
```

Restore lands in a new database, so record it and clean it up afterwards — a restore database left
behind is an unbudgeted store holding a full copy of everything.

Verify, in order: the database opens (proving the key path); row counts on the case and catalog
tables are plausible; the `catalog` schema's CHECK constraints are present, since a restore that
silently drops constraints restores the data and not the contract.

### 4. Blob content

```bash
# Undelete a specific soft-deleted blob.
az storage blob undelete \
  --account-name <storage-account> --container-name documents --name <blob-name> \
  --auth-mode login
```

`--auth-mode login` is not optional in these commands. Shared key access is disabled on all four
accounts, so a key-based command fails — and it should, since a drill that needed a shared key would
prove the wrong thing.

Verify: a soft-deleted blob is recoverable inside the R-11 window; a previous version is retrievable;
and a blob deleted **beyond** the window is *not* recoverable. That last check is the one that
matters for the deletion promise, and it belongs in the restore drill because it is the same
mechanism seen from the other side.

### 5. Audit container

Verify the immutability policy is present, the retention period matches R-11, and existing blobs are
readable. Then attempt a delete and confirm it is refused. A WORM policy that has never been tested
is the same kind of belief as an untested backup.

### 6. Cosmos projections

Drop the `case-projections` container and rebuild it from the restored SQL. Verify the rebuilt
projection matches what the Core API serves.

Any divergence is a real finding: it means something wrote to Cosmos that SQL does not hold, and
ADR 0003's central invariant no longer holds.

## Record

Per drill: date, environment, operator role, which stores were restored, the wall-clock time for
each, what was verified, what failed, and what was corrected. File failures as `TODO.md` items.

The time figures are the point. Recovery expectations agreed without a measured restore time are
guesses, and this drill is how they stop being guesses.

## Cadence

Proposed: before `pilot` first accepts real data, then quarterly, and after any change to the key
hierarchy or the storage topology. `TODO.md` 3.5 automates the drill; automation does not remove the
key-material step, which needs a human to confirm the quorum is real.

## Related pages

- [Deletion Drill](Runbook-Deletion-Drill)
- [ADR 0003](ADR-0003-SQL-authoritative-Cosmos-rebuildable)
- [ADR 0005](ADR-0005-Managed-HSM-over-Key-Vault-keys)
