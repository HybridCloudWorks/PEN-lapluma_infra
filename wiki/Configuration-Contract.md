# Configuration contract

Every non-secret configuration input the `lapluma-infra-0.0` foundation consumes, its expected
format, the role that owns the value, and the component that consumes it.

This page is the **contract**. It does not track whether a value has been supplied or approved:
values that are still waiting on a human decision, approval, or access grant are listed as blockers
in `REVIEW.md`, and implementation work is listed in `TODO.md`.

> **No secret value belongs on this page, in the repository, in a Bicep parameter file, in a
> committed AZD environment file, in a build log, or in a deployment output.** See
> [Security and Data Protection](Security-and-Data-Protection).

## How configuration reaches Azure

`infra/main.parameters.json` is an AZD-substituted parameter file: each value is an
`${ENVIRONMENT_VARIABLE}` reference resolved from the AZD environment at provision time. It contains
no literal values and no secrets. `infra/main.bicep` is subscription-scoped and passes the resolved
parameters into the `network`, `observability`, `security`, `messaging`, and `data` modules.

## Azure context

| Variable | Expected format | Owner | Notes |
|----------|-----------------|-------|-------|
| `AZURE_TENANT_ID` | Azure tenant GUID | Platform | Confirm together with the tenant display name; never infer from CLI context |
| `AZURE_SUBSCRIPTION_ID` | Azure subscription GUID | Platform | Confirm together with the subscription display name; never infer from defaults |
| `AZURE_LOCATION` | `eastus2` | Platform | US-only pilot data plane |
| `AZURE_ENV_NAME` | Approved AZD-safe environment name | Platform | Consumed by `infra/main.parameters.json` and the `azd-env-name` tag |
| `AZURE_ENV_NAME_DEV` | `dev` or an approved AZD-safe name | Platform | Synthetic data only |
| `AZURE_ENV_NAME_STAGING` | `staging` or an approved AZD-safe name | Platform | Production-equivalent controls |
| `AZURE_ENV_NAME_PILOT` | `pilot` or an approved AZD-safe name | Platform | Creation blocked until the real-data gates pass |
| `AZURE_RESOURCE_NAME_PREFIX` | 3–12 lowercase alphanumeric characters | Platform | Enforced by `@minLength(3)` / `@maxLength(12)` in `infra/main.bicep`; globally unique names must be validated before Azure validation or provisioning |
| `AZURE_RESOURCE_OWNER` | Accountable team or role | Governance | `owner` resource tag |
| `AZURE_RESOURCE_TAGS` | Owner, environment, data classification, cost center, system | Governance | Required on every resource |
| `AZURE_COST_CENTER` | Organization cost-center identifier | Finance | `cost-center` resource tag; required before cost-bearing deployment |
| `AZURE_DEPLOYMENT_PRINCIPAL_OBJECT_ID` | Entra object GUID | Platform | OIDC and workload identity; no client secret |
| `AZURE_SECURITY_GROUP_OBJECT_ID` | Entra group object GUID | Security | Prefer groups over individual role assignments |
| `AZURE_OPERATIONS_GROUP_OBJECT_ID` | Entra group object GUID | Operations | Least privilege, with PIM where applicable |
| `AZURE_PRIVACY_GROUP_OBJECT_ID` | Entra group object GUID | Privacy | Access to evidence, not to case content by default |

## Identity and public interface

| Variable | Expected format | Owner | Notes |
|----------|-----------------|-------|-------|
| `ENTRA_APPLICANT_API_APP_ID` | Entra application or client GUID | Identity | Public-client and API registration design requires approval |
| `ENTRA_API_AUDIENCE` | Verified application URI | Identity | Must match APIM token validation |
| `ENTRA_STAFF_TENANT_ID` | Approved workforce tenant GUID | Identity | Confirm whether this is the same tenant as the Azure resources |
| `ENTRA_STAFF_API_APP_ID` | Entra application or client GUID | Identity | Staff access requires phishing-resistant MFA and Conditional Access |
| `APIM_PUBLIC_HOSTNAME` | Approved HTTPS hostname | Platform | DNS and certificate ownership must be proven |
| `APIM_PUBLISHER_NAME` | Organization display name | Product | Non-secret deployment parameter |
| `APIM_PUBLISHER_EMAIL` | Role mailbox | Operations | Use a monitored role account, never a personal mailbox |
| `IOS_BUNDLE_IDENTIFIER` | Reverse-DNS identifier | Mobile | Needed for App Attest, associated domains, and API policy |
| `IOS_APP_ATTEST_ENVIRONMENT` | `development` or `production`, per environment | Security and mobile | The pilot must use the approved production attestation policy |

## Foundation inputs

| Variable | Proposed value or default | Owner | Consumer |
|----------|---------------------------|-------|----------|
| `enableProvisioning` | `false`, structurally restricted | Platform and security | Bicep safety interlock; enabling it requires a reviewed code change after every deployment blocker closes |
| `AZURE_SQL_ENTRA_ADMIN_OBJECT_ID` | Entra group GUID | Data and platform | SQL Entra-only administrator |
| `AZURE_SQL_ENTRA_ADMIN_DISPLAY_NAME` | Entra group display name | Data and platform | SQL administrator metadata |
| `AZURE_HSM_INITIAL_ADMIN_OBJECT_ID` | Entra principal GUID | CISO and platform | Managed HSM bootstrap; the PIM-versus-group decision is still open |
| `LAPLUMA_VNET_ADDRESS_PREFIX` | Ratified `10.42.0.0/16` | Network | VNet address space |
| `LAPLUMA_CORE_SUBNET_PREFIX` | Ratified `10.42.0.0/23` | Network | Core ACA environment |
| `LAPLUMA_PROCESSING_SUBNET_PREFIX` | Ratified `10.42.2.0/23` | Network | Processing ACA environment |
| `LAPLUMA_AI_SUBNET_PREFIX` | Ratified `10.42.4.0/23` | Network | AI ACA environment |
| `LAPLUMA_FUNCTIONS_SUBNET_PREFIX` | Ratified `10.42.6.0/24` | Network | Functions integration |
| `LAPLUMA_PRIVATE_ENDPOINTS_SUBNET_PREFIX` | Ratified `10.42.7.0/24` | Network | Private endpoints |
| `LAPLUMA_APIM_SUBNET_PREFIX` | Ratified `10.42.8.0/24` | Network | API Management edge; reserved ahead of the APIM resource |
| `ASPNETCORE_URLS` | Container default `http://+:8080` | Backend | Core API listen address, set in `src/core-api/Dockerfile` |
| `PORT` | Container default `8080` | Backend | Processing health listener, read by `src/document-processing/worker.py` |
| `ACQUISITION_SCHEDULE` | Six-field NCRONTAB expression | Catalog operations | Timer trigger in `src/functions/function_app.py` |
| `DURABLE_TASK_HUB_NAME` | Environment-unique safe name | Platform | Durable Functions task hub in `src/functions/host.json` |
| `AzureWebJobsStorage__accountName` | Storage account name | Platform | Functions identity-based runtime storage |
| `SERVICEBUS__fullyQualifiedNamespace` | Namespace FQDN | Platform | Functions identity-based Service Bus binding |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Generated deployment output; treat as sensitive configuration | Observability | Workload telemetry; never hard-code |
| `AZURE_CLIENT_ID` | Generated user-assigned managed-identity client ID | Platform | Azure SDK credential selection |
| `DOCUMENT_INTELLIGENCE_ENDPOINT` | Private endpoint URI | AI and platform | Processing adapter; no key fallback |
| `DOCUMENT_INTELLIGENCE_API_VERSION` | Explicit pinned service API version | AI and platform | Processing adapter; no implicit latest version |
| `PROCESSING_INPUT_CONTAINER_URL` | Private quarantine container URI | Data and platform | Processing input; read one object only |
| `PROCESSING_OUTPUT_CONTAINER_URL` | Private create-only staging URI | Data and platform | Processing output |
| `PROCESSING_QUEUE_NAME` | Proposed `document-processing` | Integration | Worker trigger; matches the queue name in `infra/modules/messaging.bicep` |

The foundation defines no application secret. If a future dependency cannot use managed identity,
add a row recording its purpose, owner, secret-store destination, rotation interval, and consuming
workload before any code references it — never its value.

## Document and AI configuration

| Variable | Expected format | Owner | Notes |
|----------|-----------------|-------|-------|
| `DOCUMENT_INTELLIGENCE_ACCOUNT_NAME` | Generated Azure-safe name | Platform | Private endpoint only |
| `DOCUMENT_INTELLIGENCE_MODEL_IDS` | Approved source-document-class model IDs | RAI and catalog | Do not use government form IDs as source extractor classes |
| `AZURE_OPENAI_RESOURCE_ID` | Full Azure resource ID | AI | Only if approved generative features enter Alpha 0.2 |
| `AZURE_OPENAI_DEPLOYMENT_NAMES` | Pinned deployment identifiers | AI | No automatic model-version upgrades |
| `AZURE_AI_CONTENT_SAFETY_RESOURCE_ID` | Full Azure resource ID | RAI and security | Required for an approved generative path; not a substitute for the UPL classifier |
| `MODIFIED_ABUSE_MONITORING_STATUS` | `approved`, `denied`, or `not applicable`, with an evidence reference | Privacy and RAI | The identity-document Path A remains non-generative regardless |
| `UPL_CLASSIFIER_VERSION` | Immutable release or version identifier | Compliance and RAI | Fail-closed release gate |

## Infrastructure baselines

These were literals in the infrastructure modules. They are now parameters whose defaults reproduce
exactly the values they replaced, so each can differ between `dev` and `pilot` without editing
Bicep. **A default here is a planning baseline, not an approved value** — the gating decision is
named in the last column.

AZD substitutes environment variables into `infra/main.parameters.json` textually, and that file has
to remain valid JSON, so every one of these arrives as a string whatever it represents.
`infra/main.bicep` converts them once and passes typed values to the modules, which carry the range
and allowed-value constraints. Leaving a variable unset substitutes an empty string, which fails the
`@minLength(1)` guard on its field rather than deploying something unintended.

| Variable | Default | Accepted | Gated by |
|----------|---------|----------|----------|
| `LAPLUMA_LOG_ANALYTICS_RETENTION_DAYS` | `365` | 30–730 | Ratified |
| `LAPLUMA_BLOB_SOFT_DELETE_DAYS` | `7` | 1–365 | Ratified |
| `LAPLUMA_CONTAINER_SOFT_DELETE_DAYS` | `7` | 1–365 | Ratified |
| `LAPLUMA_KEY_VAULT_SOFT_DELETE_DAYS` | `90` | 7–90 | Ratified |
| `LAPLUMA_HSM_SOFT_DELETE_DAYS` | `90` | 7–90 | Ratified |
| `LAPLUMA_BLOB_VERSION_DAYS` | `7` | 1–365 | Ratified |
| `LAPLUMA_ERASURE_SLA_DAYS` | `30` | 1–365 | Ratified |
| `LAPLUMA_SQL_SKU_NAME` | `GP_S_Gen5` | any SKU name | R-03 |
| `LAPLUMA_SQL_SKU_CAPACITY` | `2` | ≥ 1 vCores | R-03 |
| `LAPLUMA_SQL_MIN_CAPACITY` | `0.5` | decimal vCores | R-03 |
| `LAPLUMA_SQL_AUTO_PAUSE_MINUTES` | `60` | ≥ -1; -1 disables | TODO 3.1 |
| `LAPLUMA_COSMOS_MAX_THROUGHPUT` | `1000` | 1000–1000000 RU/s | R-03 |
| `LAPLUMA_SERVICE_BUS_CAPACITY` | `1` | 1, 2, 4, 8, 16 | R-03 |
| `LAPLUMA_SERVICE_BUS_PARTITIONS` | `1` | 1–4 | R-03 |
| `LAPLUMA_HSM_SKU_NAME` | `Standard_B1` | `Standard_B1`, `Custom_B32` | R-03 |
| `LAPLUMA_SQL_ZONE_REDUNDANT` | `false` | `true`, `false` | TODO 3.1 |
| `LAPLUMA_COSMOS_ZONE_REDUNDANT` | `false` | `true`, `false` | TODO 3.1 |
| `LAPLUMA_AUDIT_STORAGE_SKU` | `Standard_ZRS` | LRS, ZRS, GRS, GZRS | TODO 3.1 |
| `LAPLUMA_DEFAULT_STORAGE_SKU` | `Standard_LRS` | LRS, ZRS, GRS, GZRS | TODO 3.1 |
| `LAPLUMA_DUPLICATE_DETECTION_WINDOW` | `PT1H` | ISO 8601 duration | — |
| `LAPLUMA_QUEUE_MESSAGE_TTL` | `P7D` | ISO 8601 duration | Ratified |
| `LAPLUMA_QUEUE_LOCK_DURATION` | `PT5M` | ISO 8601 duration, max `PT5M` | — |
| `LAPLUMA_QUEUE_MAX_DELIVERY_COUNT` | `5` | 1–2000 | — |
| `LAPLUMA_TOPIC_MESSAGE_TTL` | `P14D` | ISO 8601 duration | Ratified |

### Deliberately not parameters

These stayed literals because making them adjustable would make a guarantee optional:

| Value | Location | Why it is fixed |
|-------|----------|-----------------|
| Blob versioning enabled | `infra/modules/data.bicep` | A data-protection guarantee. The retention windows are tunable; whether versions exist at all is not. |
| Key Vault and Managed HSM purge protection | `infra/modules/security.bicep` | Purge protection cannot be turned off once set, and an environment that could skip it is an environment where a key is destroyable. |
| Cosmos `Session` consistency | `infra/modules/data.bicep` | A correctness property of the read path. Varying it per environment would let a consistency bug pass in `dev` and appear in `pilot`. |
| Cosmos hierarchical partition key `/tenantId` + `/caseId` | `infra/modules/data.bicep` | Fixed at container creation; changing it is a data migration, not configuration. |
| `allowSharedKeyAccess`, `disableLocalAuth`, `publicNetworkAccess`, TLS floors | all modules | Trust-zone invariants. They are the posture, not settings within it. |

## Related pages

- [Environments and Release Path](Environments-and-Release-Path)
- [Security and Data Protection](Security-and-Data-Protection)
- [Architecture Overview](Architecture-Overview)
