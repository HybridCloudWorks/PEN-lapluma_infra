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
| `LAPLUMA_VNET_ADDRESS_PREFIX` | Proposed `10.42.0.0/16` | Network | VNet address space |
| `LAPLUMA_CORE_SUBNET_PREFIX` | Proposed `10.42.0.0/23` | Network | Core ACA environment |
| `LAPLUMA_PROCESSING_SUBNET_PREFIX` | Proposed `10.42.2.0/23` | Network | Processing ACA environment |
| `LAPLUMA_AI_SUBNET_PREFIX` | Proposed `10.42.4.0/23` | Network | AI ACA environment |
| `LAPLUMA_FUNCTIONS_SUBNET_PREFIX` | Proposed `10.42.6.0/24` | Network | Functions integration |
| `LAPLUMA_PRIVATE_ENDPOINTS_SUBNET_PREFIX` | Proposed `10.42.7.0/24` | Network | Private endpoints |
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

## Hard-coded baselines in the generated Bicep

These values are currently literals in the infrastructure modules rather than parameters. Each one
is a planning baseline and several are pending a policy decision; the work to parameterize them is
tracked in `TODO.md`.

| Value | Location |
|-------|----------|
| Log Analytics retention `365` days | `infra/modules/observability.bicep` |
| Blob soft delete and container soft delete `7` days, versioning enabled | `infra/modules/data.bicep` |
| SQL `GP_S_Gen5` capacity `2`, auto-pause `60` minutes, minimum capacity `0.5`, `zoneRedundant: false` | `infra/modules/data.bicep` |
| Cosmos `Session` consistency, autoscale max throughput `1000` RU/s, `isZoneRedundant: false`, hierarchical partition key `/tenantId` + `/caseId` | `infra/modules/data.bicep` |
| Storage redundancy `Standard_ZRS` for the audit account, `Standard_LRS` for the others | `infra/modules/data.bicep` |
| Service Bus `Premium` capacity `1`, one partition, duplicate-detection window `PT1H`, max delivery count `5`, lock duration `PT5M`, topic TTL `P14D` | `infra/modules/messaging.bicep` |
| Key Vault soft-delete retention `90` days with purge protection | `infra/modules/security.bicep` |
| Managed HSM `Standard_B1`, soft-delete retention `90` days with purge protection | `infra/modules/security.bicep` |

## Related pages

- [Environments and Release Path](Environments-and-Release-Path)
- [Security and Data Protection](Security-and-Data-Protection)
- [Architecture Overview](Architecture-Overview)
