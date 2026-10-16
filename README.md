# LaPluma GCP infrastructure and backend

**Documentation:** [hybridcloudworks.github.io/PEN-lapluma_infra](https://hybridcloudworks.github.io/PEN-lapluma_infra/)

Planning navigation: [approved GCP target and Document Library](https://hybridcloudworks.github.io/PEN-lapluma_infra/GCP-Document-Library-Decision.html), [cost model](https://hybridcloudworks.github.io/PEN-lapluma_infra/GCP-Cost-Model.html), and [board delivery record](https://hybridcloudworks.github.io/PEN-lapluma_infra/GCP-Board-Handoff.html). The boards themselves are private.

Backend contracts, Google Cloud infrastructure as code (Terraform), and backend services for the
LaPluma supervised pilot.

The platform runs on Google Cloud: Cloud Run services behind API Gateway, Cloud SQL PostgreSQL,
Cloud Storage and Pub/Sub, defined in `infra/terraform/`. `deploy-gcp.yml` deployed the Cloud Run
services to the `dev` environment on 2026-09-16, and the iOS app's beta build targets a `staging`
API Gateway. The pilot environment has not been deployed. The original Azure Bicep scaffold under
`infra/` is superseded and kept for reference; its entrypoint accepts only
`enableProvisioning: false`.

The native iOS client lives in its own repository. Two contracts are shared with it: the versioned
package identity and composition handshake,
[`contracts/catalog-package-compatibility.json`](contracts/catalog-package-compatibility.json), and
the workflow API the client is generated from, [`contracts/openapi/`](contracts/openapi). The
workflow contract is mirrored byte-identically from the app repository and pinned by SHA-256 in
`tools/validate_foundation.py`, so editing the mirror without deliberately adopting a new revision
fails the build.

## Repository layout

| Path | Contents |
|------|----------|
| `contracts/` | OpenAPI 3.1 catalog contract, the workflow and upload contracts under `openapi/`, and the iOS package-compatibility handshake |
| `blueprints/` | Document Blueprint definitions (`official/`, `institutional/`, `external/`) for the Document Library |
| `infra/terraform/` | GCP Terraform: `bootstrap/`, reusable `modules/`, and `environments/` for `dev`, `staging`, and `pilot` |
| `infra/` | Also holds the superseded Azure scaffold (subscription-scope Bicep entrypoint, modules, and the AZD parameter file), PostgreSQL migrations in `sql/`, and `scripts/` |
| `src/core-api/` | .NET 10 catalog and health API |
| `src/core-api.tests/` | xUnit tests for the catalog API, over the real request pipeline |
| `src/workflow-api/` | .NET 10 workflow API: session, client directory, case workspace, document upload sessions |
| `src/workflow-api.tests/` | xUnit tests for the workflow API, over the real request pipeline |
| `src/document-processing/` | Python 3.13 isolated processing worker |
| `src/functions/` | Durable Functions catalog-acquisition skeleton |
| `tools/` | `validate_foundation.py`, the dependency-free contract and interlock validator |
| `azure.yaml` | AZD service definitions |
| `docs/` | Runbooks, planning, the shared design specification, and architecture diagrams; `docs/wiki/` is the source of the [documentation site](#documentation) |

## Requirements

- Python 3.13
- .NET 10 SDK
- Terraform 1.5 or later
- Google Cloud CLI (`gcloud`)
- Azure CLI with the Bicep extension, for the superseded Azure scaffold
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
dotnet build src/workflow-api/LaPluma.WorkflowApi.csproj --configuration Release
dotnet test src/workflow-api.tests/LaPluma.WorkflowApi.Tests.csproj --configuration Release

# Bicep compilation (no provisioning)
az bicep build --file infra/main.bicep

# Container images
docker build --tag lapluma-core-api:validation src/core-api
docker build --tag lapluma-workflow-api:validation src/workflow-api
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
expected format, owning role, and consuming component is documented on the
[Configuration Contract](https://hybridcloudworks.github.io/PEN-lapluma_infra/Configuration-Contract.html) page.

### Authentication

> **Staging and dev are offline** (Cloud SQL stopped, Cloud Run ingress internal) until the
> authentication fix below is deployed. Bring an environment back only by following
> [the session-token runbook](docs/runbooks/session-tokens-and-saml-identity-providers.md#4-bringing-staging-and-dev-back-online),
> in order.

Clinic users sign in through SAML with their institution's Google Workspace or Microsoft Entra ID.
The core API verifies the Assertion's XML signature against the IdP certificate pinned in
configuration and returns a one-hour RS256 session JWT (`tenant_id`, `email`, `role`). API Gateway
requires that token on every route except health and readiness, `/.well-known/jwks.json`, and the
SAML endpoints, and both APIs validate it again with the standard JWT bearer handler. No test token
or unsigned credential is accepted in any environment.

| Setting | Service | Source |
|---------|---------|--------|
| `Authentication__Issuer`, `Authentication__Audience` | both | Terraform `session_token_issuer`, `session_token_audience` |
| `Authentication__JwksUri` | workflow | Terraform, defaults to `<issuer>/.well-known/jwks.json` |
| `SessionTokens__SigningKeyPem` | core | Secret Manager `lp-session-signing-key-<env>` |
| `Saml__IdentityProvidersJson` | core | Secret Manager `lp-saml-idps-<env>` |

Missing values fail closed; malformed ones stop the service at startup. The runbook covers
generating the signing key, the IdP JSON format, registering LaPluma with an IdP, and rotation.

## Repository conventions

- **Never** commit credentials, private keys, connection strings, passkey material, tokens, document
  content, applicant identifiers, or any other production data. `tools/validate_foundation.py`
  scans for several of these classes on every CI run.
- Managed identity is the default for workload access. If a dependency genuinely cannot use it,
  record the secret's purpose, owner, rotation interval, destination store, and consumer on the
  [Configuration Contract](https://hybridcloudworks.github.io/PEN-lapluma_infra/Configuration-Contract.html) page — never its value.
- Documentation belongs in exactly one place. See [Documentation](#documentation).
- Dot-prefixed folders hold configuration only, never documentation.

## Documentation

| Where | What |
|-------|------|
| `README.md` | This file: repository purpose, setup, and navigation |
| [`CHANGELOG.md`](CHANGELOG.md) | Completed work |
| [`REVIEW.md`](REVIEW.md) | Blockers only a human decision, approval, or access grant can clear |
| Private LaPluma project boards | The engineering work queue (private; not visible to the public) |
| [Documentation site](https://hybridcloudworks.github.io/PEN-lapluma_infra/) | Architecture, deployment plan, configuration contract, security policy, pilot gates, research record, decision records, operational runbooks |
| [`docs/runbooks/`](docs/runbooks/) | Operational runbooks including Staging Environment Power & Cost Management |

The documentation site is built from `docs/wiki/` by `.github/workflows/pages.yml` on every push to
`main` that changes it. Edit the pages there through a pull request, not on the site.

The classification rules that decide where a new document goes are on the
[Documentation Standards](https://hybridcloudworks.github.io/PEN-lapluma_infra/Documentation-Standards.html) page.

Repository metadata lives outside that model, because GitHub reads it from fixed locations:
[`.github/CONTRIBUTING.md`](.github/CONTRIBUTING.md) (how to pick up work and what review looks
for), [`.github/SECURITY.md`](.github/SECURITY.md) (how to report a vulnerability privately),
`.github/pull_request_template.md`, and `.github/dependabot.yml`. They sit under `.github/` rather
than the repository root so the root keeps the three markdown files the Documentation Standards
allow.

## Licence

All rights reserved. See [LICENSE](LICENSE).
