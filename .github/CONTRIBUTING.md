# Contributing

## Where work is tracked

Pick up work from [`TODO.md`](../TODO.md) — the authoritative backlog, in dependency order. Anything
needing a human decision, approval, credential, or access grant is in [`REVIEW.md`](../REVIEW.md)
instead, referenced by ID (`R-nn`) where it gates an item. Completed work is recorded in
[`CHANGELOG.md`](../CHANGELOG.md); a completed `TODO.md` item is deleted rather than marked done.

Everything else — architecture, deployment plan, configuration contract, security policy, pilot
gates — lives on the wiki. The rules deciding where a new document goes are on its **Documentation
Standards** page. Do not add markdown files to the repository root or to `.github/` beyond the ones
already there.

## Before you push

```
python tools/validate_foundation.py
python -m unittest discover -s tools -p 'test_*.py'
python -m unittest discover -s src/document-processing -p 'test_*.py'
python -m unittest discover -s src/functions -p 'test_*.py'
dotnet test src/core-api.tests/LaPluma.CoreApi.Tests.csproj
az bicep build --file infra/main.bicep
```

CI runs all of these. `bicepconfig.json` sets twenty linter rules to `error` and the workflow fails
on any Bicep diagnostic, warnings included.

## What review looks for

The scaffold's value is in guarantees that hold rather than features that demo, so a change is read
against the invariants first. They are listed in the pull request template and stated in full on the
wiki's **Security and Data Protection** page. The short version:

- No credential, key, token, connection string, document content, or applicant identifier reaches
  source control — including test fixtures, expected-output examples, and comments.
- Telemetry is content-free: correlation identifiers, never a path, query string, route value, or
  document identifier.
- The processing zone has no SQL or Cosmos route; the AI zone has no authoritative write.
- Automated components propose. They do not activate, approve, sign, or file.
- `enableProvisioning` stays restricted to `false` until the approvals in `REVIEW.md` are recorded.

## Two conventions worth knowing

**Verify by breaking it.** A test that passes against both the fix and the bug proves nothing. The
convention here is to mutate the fix, confirm a test fails, and record what failed. Assert that the
mutation actually landed — two harnesses in this repository's history passed vacuously because the
string they patched was not in the file.

**Record what you find, don't absorb it.** Noticing a second problem while fixing the first is
normal. Add it to `TODO.md` with the evidence rather than widening the change, so the diff stays
reviewable and the finding survives whether or not you fix it.
