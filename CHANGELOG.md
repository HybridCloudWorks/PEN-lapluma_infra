# Changelog

Completed work only. Planned work lives in `TODO.md`; blockers awaiting a human decision live in
`REVIEW.md`.

## Unreleased

### Changed

- Moved the Core API from .NET 9 to **.NET 10**. `LaPluma.CoreApi.csproj` targets `net10.0`, the
  Dockerfile builds on `dotnet/sdk:10.0` and runs on `dotnet/aspnet:10.0`, and CI installs the
  10.0.x SDK. .NET 9 is a Standard Term Support release whose support window has closed; .NET 10 is
  the current Long Term Support release, which suits a pilot that must stay patchable. The README,
  `TODO.md` item 5.1, and the Architecture Overview and Azure Deployment Plan wiki pages were
  updated to match, so the stated stack and the built stack agree.
- Pinned all three container base images by digest: `mcr.microsoft.com/dotnet/sdk:10.0` and
  `mcr.microsoft.com/dotnet/aspnet:10.0` in `src/core-api/Dockerfile`, and `python:3.12-slim` in
  `src/document-processing/Dockerfile`. Two builds of the same commit now resolve the same base
  layers. Each pin keeps its tag alongside the digest for readability; the digest is what resolves.
- Removed the `main` and `codex/**` branch filter from the foundation validation workflow's `push`
  trigger, so every branch gets push validation rather than only two prefixes.
- Consolidated repository documentation into a single model: `README.md` for repository purpose,
  `CHANGELOG.md` for completed work, `REVIEW.md` for human-resolvable blockers, `TODO.md` for the
  engineering work queue, and the GitHub Wiki for everything else.
- Rewrote `README.md` so it covers only repository purpose, layout, requirements, quick start,
  configuration overview, conventions, and navigation.

### Fixed

- The processing worker's health listener no longer holds threads open indefinitely, disclose the
  interpreter version, or answer one endpoint in two content types. Requests now time out,
  `ThreadingHTTPServer` shuts down on SIGTERM so container stop lets in-flight probes finish, the
  `Server` header is the service name rather than `BaseHTTP/0.6 Python/3.12.x`, and an unknown path
  returns an empty JSON-typed 404 instead of an HTML error page. `PORT` is validated rather than
  passed straight to `int()`, which previously crashed at startup with an unhandled `ValueError` on
  a non-numeric value.
- The prohibited-input rule now sees every parameter declaration. It collected names from operation
  objects only, so a `parameters` list declared at path-item level — a documented OpenAPI construct
  that applies to every operation beneath it — was invisible, and a `caseId` declared that way
  walked past the rule that exists to keep person, case, and eligibility identifiers out of the
  catalog API. A `$ref` parameter has no `name`, so the old code raised `KeyError` and CI failed
  with a traceback rather than a diagnosis; such declarations are now rejected, because a gate that
  cannot read a declaration must not pass it. Request bodies are rejected outright, since no catalog
  operation declares one.
- Structural drift in the contract now produces a diagnosis rather than a traceback: the remaining
  direct dictionary indexing in the validator is guarded. Each check owns its failures instead of
  appending to a module-level global, so a check can run twice, or alone, without inheriting
  another's results — which is why these functions previously had no unit tests.
- Request logging no longer emits the URL. ASP.NET Core's `Hosting.Diagnostics` logs the full
  request URL including the query string at `Information`, on by default, which the content-free
  telemetry constraint does not allow. That category and `Microsoft.AspNetCore.Routing` are raised
  to `Warning`; the service logs every rejection itself with a correlation identifier and no request
  content, so the signal worth keeping is kept. A test asserts that no log from *any* category
  carries a path, query value, or route value — measured by capturing at `Trace`, so removing the
  filters makes the suppressed logging reappear and fails the test.
- The acquisition sweep is a singleton and reports what the publisher accepted. The timer trigger
  called `start_new` with no instance ID, so Durable Functions minted a fresh GUID per firing and a
  sweep running longer than the schedule interval — or a `use_monitor` catch-up landing on a normal
  firing — started a second sweep proposing the same editions to the same downstream. It now uses a
  fixed instance ID and skips the start when a run is already `Running`, `Pending`, or
  `ContinuedAsNew`. The publish activity is retried on transient failure; the proposal activity
  deliberately is not, because a scope-drift rejection is deterministic and must fail closed on the
  first attempt. The orchestration result now carries `acceptedProposalCount` and `published` rather
  than discarding them, so a publisher accepting three of four proposals cannot report success for
  all four.
- The Core API's `correlationId` is now findable. It was a fresh `Guid.NewGuid()` per response,
  written nowhere and derived from nothing, so the identifier a user reported to support could not
  be located in any log or trace. It is now derived from the ambient W3C trace identifier — sixteen
  bytes, exactly a GUID, so the contract's `format: uuid` is unchanged while the value is something
  that exists in the trace backend — and each problem is logged once at construction. The service
  previously performed no logging at all. The log records the problem type, status, and correlation
  identifier only; a test asserts these logs carry no path, query string, or route value.
- `/ready` reports readiness rather than a literal. It answered from a constant and never resolved
  `CatalogRepository`, so it could not detect the one way the catalog actually breaks: the fixture
  is built in a static constructor that throws on an unrecognised form number, which makes every
  `/v1/catalog/*` route return 500 while a literal probe stays green. Readiness now resolves the
  repository and returns 503 when it cannot be built, with `/health` left as pure liveness so an
  orchestrator does not restart a process that is running correctly.
- The secret scan matches an Azure storage connection string in any key ordering. Connection
  strings are unordered key/value pairs, and the rule required one specific ordering, so the same
  credential written any other way passed the scan. Added rules for a bare storage account key and
  for a shared-access signature — the shapes that matter most in a repository whose invariant is
  that `allowSharedKeyAccess` is false everywhere and access is managed-identity only.
- The Core API build context is an allowlist. `src/core-api/Dockerfile` does `COPY . ./` while
  `.dockerignore` excluded only build output, so a developer's local `appsettings.Development.json`,
  `.env`, or certificate sitting in that directory would have been copied into a build layer. The
  file now denies everything and re-includes only `*.cs` and `*.csproj`, so new source files are
  picked up automatically and anything else has to be allowed deliberately.
- The acquisition contract rejects an unknown key instead of ignoring it.
  `propose_acquisition_batch` read two keys with `.get()` and ignored everything else, while its
  sibling in the processing zone computed an exact key-set difference and had a test proving an
  injected `approve` key fails closed — two contract modules taking opposite positions on the same
  question at the same kind of boundary. The request is now checked as a whole shape. This matters
  beyond consistency: the dict is the Durable Functions orchestration input, which is persisted to
  the task hub and replayed, so a personal or case field reaching this function would be written to
  durable storage. `host.json`'s `traceInputsAndOutputs: false` suppresses tracing, not history.
  `requestedAt` is required and must parse as ISO 8601, and a test reads the timer trigger's
  `client_input` keys out of `function_app.py` so the caller and the contract cannot drift apart.
- The processing zone's request contract is validated rather than prefix-matched. The blob URIs
  that bound which single object the isolated worker may touch were checked only for an `https://`
  prefix, which accepted a URI with no host at all, an arbitrary external host, a shared-access
  signature smuggled through the query string, and two spellings of one object differing by a
  trailing slash — defeating the create-only staging guarantee. They are now parsed: HTTPS only,
  pinned to the Azure Blob host suffix, no embedded credentials, no query or fragment, a
  container-and-blob path, and the normalised forms compared for distinctness.
- Anchored value proposals validate their polygon rather than counting it. The previous check
  counted coordinates while its message promised a "non-degenerate polygon", so a zero-area polygon
  and a tuple of eight strings both passed. Coordinates must now be finite, non-negative numbers
  enclosing a non-zero extent, and `AnchoredValueProposal` validates in `__post_init__`, so an
  invalid proposal cannot be constructed at all — previously the "all proposals require human
  confirmation" invariant depended on every caller remembering to call `validate()`.
- The `sha256` error message no longer states a rule the code does not apply: the value is
  normalised to lowercase on ingest, so the message says so instead of demanding lowercase input.
- Core API error responses now conform to the published contract. Every problem document is served
  as `application/problem+json` rather than `application/json`, and its `status` is derived from the
  same value as the HTTP status so the two cannot disagree. A malformed `editionDate` previously
  failed framework route binding, which returns a plain-text diagnostic in Development and a bare
  empty 400 in Production — the error a client saw depended on the environment. The route now binds
  the value as a string and parses it, so the response is a problem document everywhere, and
  `contracts/catalog.openapi.json` declares the 400 the route can actually return.
- Catalog codes are validated against the patterns the contract declares. A malformed
  `categoryCode` previously returned 200 with an empty list, indistinguishable from a category that
  legitimately has no packages; it now returns 400. With inputs validated, the lookups compare
  ordinally rather than case-insensitively, matching the contract's uppercase-only declaration.
- `CatalogRepository` asserts package-code uniqueness once at startup instead of throwing from
  `SingleOrDefault` on a request, and the service version is a single constant pinned by test to
  `info.version` in the contract rather than a literal repeated per endpoint.
- Removed the global `JsonStringEnumConverter`. Type-level attributes take precedence over a
  globally registered converter, so it never applied to any existing enum while making a new enum
  added without those attributes look handled — it would have serialised in PascalCase.
- The catalog fixture parser now rejects a form declared more than once. Classifications are keyed
  by form number, so a second declaration of the same form silently replaced the first and hid
  whatever it said.
- The catalog priority and fill-mode rules in `tools/validate_foundation.py` now bind each
  classification to the form it is declared on. They previously asserted that a literal such as
  `FormArtifactKind.ExternalWorkflow` appeared somewhere in `CatalogRepository.cs`, which passed
  just as happily when the classifications were swapped between forms — the validator reported
  success on a tree where FAFSA had become an automatically fillable official PDF. The
  retired-form check now covers `src/functions/acquisition_contract.py` as well as the catalog
  fixture; it previously read only the fixture, so restoring `N-400` or `I-765` to the acquisition
  scope passed unnoticed.
- A `FormPackage` with no forms now derives `UNAVAILABLE` rather than `PILOT`. The derivation is a
  chain of `Any` calls expressing "a package is only as activated as its weakest form"; for an
  empty list every one of them is false and the expression fell through to the most permissive
  state, the one that permits case creation.
- The Log Analytics workspace now sets `features.disableLocalAuth: true`. Application Insights
  already disabled local auth, but the workspace that stores what it ingests did not, so the
  telemetry and audit store still accepted the legacy workspace-key ingestion path.
- Split `test_unanchored_or_unconfirmed_proposal_fails_closed`, which violated two invariants at
  once and asserted only that some `ValueError` was raised. The polygon check short-circuited, so
  the human-confirmation invariant it was named for could be deleted outright with the suite still
  green. Each invariant now has its own test, matched on message.
- The validator's secret scan no longer walks generated output. It previously read every file under
  the repository root except its own source, so compiling the validator wrote a `.pyc` containing its
  own pattern literals and the next run failed as though a storage connection string had leaked.
  `__pycache__`, `bin`, `obj`, `.venv`, and `node_modules` are now skipped, matching `.gitignore`.
  The scan's own source stays excluded — it defines the patterns as literals and would match itself.

### Added

- `src/core-api.tests`, a xUnit project holding the Core API to its published contract. Twenty-nine
  tests run against the real request pipeline through `WebApplicationFactory<Program>`, so routing,
  parameter binding, serialization, and status codes are exercised rather than handler bodies:
  the catalog hierarchy, package list with taxonomy and activation-state filters, package detail,
  the fail-closed schema lookup, both 404 problem documents, and every branch of the
  `activationState` parse. Alongside them, unit tests pin the package activation derivation —
  including that a package with no forms is `UNAVAILABLE` — and assert that the C# enum wire names
  match the enum values published in `contracts/catalog.openapi.json`, so the two copies of each
  enum in this repository cannot drift apart unnoticed. The project sets `TreatWarningsAsErrors`,
  matching the project under test, and CI runs `dotnet test`.
- `.github/workflows/security-scanning.yml`, running CodeQL for C# and Python, dependency review on
  pull requests, a Trivy scan of both container images, and a Trivy scan of the ARM template the
  Bicep compiles to, plus a weekly schedule so a newly published advisory surfaces without waiting
  for the next commit. Every action is pinned by commit SHA. Both Trivy jobs report to code scanning
  without failing the build until their baseline is triaged; that threshold, and the secret-scanning
  and push-protection repository settings, remain open under `TODO.md` item **2.5**.
- `bicepconfig.json`, setting twenty security-relevant linter rules to `error`, including
  `no-hardcoded-location`, `secure-parameter-default`, and `no-unused-params`. The Bicep CI step now
  fails on any diagnostic, so a rule left at warning level cannot pass silently. CI pins the Bicep
  CLI to `v0.46.1`, because `az bicep` otherwise installs whatever is current and an upstream release
  could change the rule set or the diagnostic format under a step that now treats any diagnostic as a
  build failure.
- `tools/test_validate_foundation.py`, covering the secret scan: every pattern is still detected in
  source, generated directories and binary suffixes are skipped, a file merely named like a skipped
  directory is still scanned, and the repository tree itself is clean. Wired into CI beside the
  existing contract tests.
- `.env.example`, listing every environment variable `infra/main.parameters.json` substitutes, with
  no values and a short comment each. `tools/validate_foundation.py` now fails if the file drifts
  from the parameter file in either direction, or if any variable is committed carrying a value.
- `CHANGELOG.md`, `REVIEW.md`, and `TODO.md`.
- Nine GitHub Wiki pages staged under `wiki/`: an index plus the architecture, deployment plan,
  environment and release path, configuration contract, security policy, pilot and compliance gates,
  Azure research record, and documentation standards migrated out of `.azure/plan.md` and
  `IMPLEMENTATION_LEDGER.md`.

### Removed

- `.azure/plan.md`. Documentation does not belong in a dot-prefixed configuration folder, and
  `.azure/` is AZD's own environment directory. Its content was validated against the repository and
  split across `CHANGELOG.md`, `REVIEW.md`, `TODO.md`, and the staged wiki pages.
- `IMPLEMENTATION_LEDGER.md`. Its configuration contract moved to the Configuration Contract wiki
  page, its pilot policy values to the Pilot Policy and Compliance Gates wiki page, its
  secret-exclusion policy to the Security and Data Protection wiki page, and its missing decisions
  and approvals to `REVIEW.md`.

## lapluma-infra-0.0 — 2026-08-02

Initial placeholder-only contract, service, and infrastructure scaffold for the supervised pilot.
Nothing in this release was deployed, and no Azure environment was created.

### Added

- **Contracts.** `contracts/catalog.openapi.json`, an OpenAPI 3.1.0 catalog contract covering
  `/health`, `/ready`, the catalog hierarchy and package endpoints, and an authority-aware
  form-edition schema endpoint. `contracts/catalog-package-compatibility.json` pins the
  `lapluma-app-0.2` package identity and composition handshake shared with the iOS repository.
- **AZD configuration.** `azure.yaml` declaring the `core-api`, `processing-worker`, and
  `acquisition-functions` services.
- **Infrastructure.** `infra/main.bicep`, a subscription-scope entrypoint, with `network`,
  `observability`, `security`, `messaging`, and `data` modules under `infra/modules/`, plus the
  secret-free, environment-substituted `infra/main.parameters.json`.
- **Core API.** `src/core-api`, a .NET 9 minimal API exposing health, readiness, catalog hierarchy,
  catalog package list and detail, and form-edition schema endpoints over an in-memory fixture.
- **Processing worker.** `src/document-processing`, a Python 3.12 health surface for the isolated
  worker zone, with queue and Document Intelligence adapters deliberately absent.
- **Orchestration.** `src/functions`, a Durable Functions catalog-acquisition proposal skeleton
  separating timer, client, orchestrator, and activity roles. It creates proposals only; it does not
  download, activate, or mutate an official-form edition.
- **Validation tooling.** `tools/validate_foundation.py`, a dependency-free validator covering the
  OpenAPI contract shape, the Alpha 0.2 priority forms and fill modes, the Azure provisioning
  interlock, and repository-wide secret absence.
- **CI.** `.github/workflows/foundation-validation.yml`, running the validator, the Python contract
  tests, a .NET build, Bicep compilation, and both container builds, with all actions pinned by
  commit SHA.

### Security

- Provisioning safety interlock: `enableProvisioning` defaults to `false`, is restricted to `false`
  by the Bicep `@allowed` list, pinned to `false` in the parameter file, and guards every resource
  group and module. Enabling it requires a reviewed code change, not a parameter change.
- Azure SQL configured for Entra-only authentication with a TLS 1.2 floor, public network access
  disabled, and outbound network access restricted.
- Cosmos DB, Service Bus, and Application Insights configured with local authentication disabled.
- All four storage accounts configured with shared-key access disabled, blob public access disabled,
  OAuth as the default, a TLS 1.2 floor, and public network access disabled, separated by
  `quarantine`, `documents`, `packages`, and `audit` purpose.
- Key Vault configured with RBAC authorization, soft delete, purge protection, a deny-by-default
  network ACL with no bypass, and public network access disabled. Managed HSM configured with purge
  protection, a deny-by-default network ACL, and public network access disabled.
- Four separate user-assigned managed identities generated for the core, processing, AI, and
  functions workloads.
- Per-zone network security groups, with an explicit `DenyInternetEgress` outbound rule on the
  processing subnet.
- Non-root users in both container images, and health-endpoint logging suppressed in the processing
  worker so no path, query string, document ID, or free text is emitted.
- Repository-wide scanning for private keys, storage connection strings, JWT-like tokens, and
  concrete tenant or subscription ID assignments, enforced in CI.

### Validation evidence

Recorded 2026-08-02 and re-confirmed on 2026-08-07 where the toolchain was available.

| Check | Command | Result | Date |
|-------|---------|--------|------|
| Foundation contract and interlock scan | `python3 tools/validate_foundation.py` | Passed | 2026-08-07 |
| Python contract tests | `python3 -m unittest discover` for `src/document-processing` and `src/functions` | 5 passed | 2026-08-07 |
| Python source compilation | `python3 -m py_compile` | Passed | 2026-08-02 |
| JSON and YAML syntax | Local Python parsers | Passed | 2026-08-02 |
| Whitespace validation | `git diff --check` | Passed | 2026-08-02 |
| Bicep compilation | Bicep CLI v0.46.1, independent agent review | Passed with no diagnostics | 2026-08-02 |
| Bicep compilation | `az bicep build --file infra/main.bicep`, foundation validation workflow | Passed | 2026-08-07 |
| .NET core API build | `dotnet build --configuration Release`, foundation validation workflow | Passed | 2026-08-07 |
| Non-root container images | `docker build` for `src/core-api` and `src/document-processing`, foundation validation workflow | Both passed | 2026-08-07 |

Every check above has now been observed passing. The .NET and container builds — pending since
generation because no local .NET SDK or Docker daemon was available — were confirmed by run
[31149584806](https://github.com/HybridCloudWorks/PEN-lapluma_infra/actions/runs/31149584806), in
which all nine workflow steps succeeded.

Azure subscription preflight remains blocked by design until the context gate in `REVIEW.md` closes.
