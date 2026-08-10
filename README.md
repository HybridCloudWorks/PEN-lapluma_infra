# LaPluma Azure infrastructure and backend

Backend contracts, Azure infrastructure as code, and backend services for the LaPluma
`lapluma-app-0.2` supervised pilot.

This repository is a placeholder-only planning and scaffold foundation. Nothing here has been
deployed: no AZD environment exists, no Azure subscription has been contacted, and the Bicep
entrypoint structurally accepts only `enableProvisioning: false`.

The native iOS client lives in its own repository. The versioned package identity and composition
handshake shared with it is
[`contracts/catalog-package-compatibility.json`](contracts/catalog-package-compatibility.json).

## Repository layout

| Path | Contents |
|------|----------|
| `contracts/` | OpenAPI 3.1 catalog contract and the iOS package-compatibility handshake |
| `infra/` | Subscription-scope Bicep entrypoint, modules, and the AZD parameter file |
| `src/core-api/` | .NET 10 catalog and health API |
| `src/core-api.tests/` | xUnit tests for the catalog API, over the real request pipeline |
| `src/document-processing/` | Python 3.13 isolated processing worker |
| `src/functions/` | Durable Functions catalog-acquisition skeleton |
| `tools/` | `validate_foundation.py`, the dependency-free contract and interlock validator |
| `azure.yaml` | AZD service definitions |
| `wiki/` | Wiki pages staged for publication — see [Documentation](#documentation) |

## Requirements

- Python 3.13
- .NET 10 SDK
- Azure CLI with the Bicep extension
- Docker, to build the service images

## Quick start

```bash
# Contract, interlock, and secret-absence validation
python3 tools/validate_foundation.py

# Python contract tests
python3 -m unittest discover -s src/document-processing -p 'test_*.py'
python3 -m unittest discover -s src/functions -p 'test_*.py'

# .NET build and tests
dotnet build src/core-api/LaPluma.CoreApi.csproj --configuration Release
dotnet test src/core-api.tests/LaPluma.CoreApi.Tests.csproj --configuration Release

# Bicep compilation (no provisioning)
az bicep build --file infra/main.bicep

# Container images
docker build --tag lapluma-core-api:validation src/core-api
docker build --tag lapluma-processing-worker:validation src/document-processing
```

The same steps run in CI via `.github/workflows/foundation-validation.yml`.

## Configuration overview

`infra/main.parameters.json` resolves every value from an `${ENVIRONMENT_VARIABLE}` reference at
provision time. It holds no literal values and no secrets. `enableProvisioning` is pinned to
`false` there and is restricted to `false` by the Bicep `@allowed` list, so the template can be
compiled and reviewed but cannot create a resource.

`.env.example` is the checklist of those variables, ready to copy into an AZD environment. It is
tracked, so it carries names and comments only — never a value. Each configuration input, its
expected format, owning role, and consuming component is documented on the wiki's **Configuration
Contract** page.

## Repository conventions

- **Never** commit credentials, private keys, connection strings, passkey material, tokens, document
  content, applicant identifiers, or any other production data. `tools/validate_foundation.py`
  scans for several of these classes on every CI run.
- Managed identity is the default for workload access. If a dependency genuinely cannot use it,
  record the secret's purpose, owner, rotation interval, destination store, and consumer on the
  Configuration Contract wiki page — never its value.
- Documentation belongs in exactly one place. See [Documentation](#documentation).
- Dot-prefixed folders hold configuration only, never documentation.

## Documentation

| Where | What |
|-------|------|
| `README.md` | This file: repository purpose, setup, and navigation |
| [`CHANGELOG.md`](CHANGELOG.md) | Completed work |
| [`REVIEW.md`](REVIEW.md) | Blockers only a human decision, approval, or access grant can clear |
| [`TODO.md`](TODO.md) | The engineering work queue |
| [GitHub Wiki](https://github.com/HybridCloudWorks/PEN-lapluma_infra/wiki) | Architecture, deployment plan, configuration contract, security policy, pilot gates, research record |

The `wiki/` directory holds those pages as files, ready to publish. They are staged in the
repository because publishing requires GitHub Wiki write access, which automation does not have
(tracked as `R-17` in `REVIEW.md`). Once the pages are published, `wiki/` is deleted — see
`TODO.md` item **6.1**.

The classification rules that decide where a new document goes are on the wiki's **Documentation
Standards** page.

Repository metadata lives outside that model, because GitHub reads it from fixed locations:
[`.github/CONTRIBUTING.md`](.github/CONTRIBUTING.md) (how to pick up work and what review looks
for), [`.github/SECURITY.md`](.github/SECURITY.md) (how to report a vulnerability privately),
`.github/pull_request_template.md`, and `.github/dependabot.yml`. They sit under `.github/` rather
than the repository root so the root keeps the four markdown files the Documentation Standards
allow.

## Licence

[Apache License 2.0](LICENSE). Copyright 2026 HybridCloudWorks; see [`NOTICE`](NOTICE).
