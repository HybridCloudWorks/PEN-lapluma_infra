# Changelog

Completed work only. Planned work lives in `TODO.md`; blockers awaiting a human decision live in
`REVIEW.md`.

## Unreleased

### Changed

- Consolidated repository documentation into a single model: `README.md` for repository purpose,
  `CHANGELOG.md` for completed work, `REVIEW.md` for human-resolvable blockers, `TODO.md` for the
  engineering work queue, and the GitHub Wiki for everything else.
- Rewrote `README.md` so it covers only repository purpose, layout, requirements, quick start,
  configuration overview, conventions, and navigation.

### Added

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

.NET and container builds have not been verified locally — no .NET SDK is installed and the Docker
daemon is not running in the development environment. They are exercised by the CI workflow;
recording that evidence is tracked as `TODO.md` item **4.2**. Azure subscription preflight remains
blocked by design until the context gate in `REVIEW.md` closes.
