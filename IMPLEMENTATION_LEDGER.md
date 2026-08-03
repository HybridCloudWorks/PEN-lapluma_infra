# LaPluma infrastructure implementation ledger

This file records configuration contracts, missing decisions, and approval owners. It must never
contain secret values or pilot-user data. Secrets belong in approved secret stores and are accessed
by managed identity wherever the Azure service supports it.

## Planning status

| Item | Status | Required evidence or decision | Gate |
|------|--------|-------------------------------|------|
| Azure plan | Executing locally | Placeholder-only Sprint 2 generation approved | Blocks Azure environment/provisioning until context is confirmed |
| Azure tenant | Unknown | Display name and tenant ID, explicitly confirmed | Blocks Azure environment and deployment |
| Azure subscription | Unknown | Display name and subscription ID, explicitly confirmed | Blocks Azure environment and deployment |
| Subscription authorization | Unknown | Owner confirms real pilot PII and cost-bearing services are permitted | Blocks deployment |
| Primary region | Approved in principle | `eastus2`; verify all service/SKU/private-network capabilities and quota | Blocks Azure preflight/deployment |
| Cost approval | Unknown | Estimated monthly `dev`, `staging`, and `pilot` costs, including Managed HSM and APIM | Blocks deployment |
| Deployment authority | Unknown | Named owner/group for protected-environment approval | Blocks deployment |
| Security approval | Unknown | Security owner and SRB decision record | Blocks real pilot data |
| Privacy approval | Unknown | Privacy owner, DPIA, retention/erasure schedule, notice/consent approval | Blocks real pilot data |
| Compliance approval | Unknown | Compliance owner, counsel boundary approval, UPL gate evidence | Blocks real pilot data |
| Operations approval | Unknown | On-call owner, incident plan, restore/deletion drill evidence | Blocks real pilot data |

## Non-secret Azure context

| Variable or value | Status | Expected format | Owner | Notes |
|-------------------|--------|-----------------|-------|-------|
| `AZURE_TENANT_ID` | Missing | Azure tenant GUID | Platform owner | Confirm with tenant display name; never infer from CLI context |
| `AZURE_SUBSCRIPTION_ID` | Missing | Azure subscription GUID | Platform owner | Confirm with subscription display name; never infer from defaults |
| `AZURE_LOCATION` | Approved pending verification | `eastus2` | Platform owner | US-only pilot data plane |
| `AZURE_ENV_NAME_DEV` | Proposed | `dev` or approved AZD-safe name | Platform owner | Synthetic data only |
| `AZURE_ENV_NAME_STAGING` | Proposed | `staging` or approved AZD-safe name | Platform owner | Production-equivalent controls |
| `AZURE_ENV_NAME_PILOT` | Proposed | `pilot` or approved AZD-safe name | Platform owner | Creation blocked until real-data gates pass |
| `AZURE_RESOURCE_NAME_PREFIX` | Missing | Lowercase organization-approved prefix | Platform owner | Validate globally unique names before Azure validation/provisioning |
| `AZURE_RESOURCE_TAGS` | Missing | Owner, environment, data classification, cost center, system | Governance owner | Required on every resource |
| `AZURE_DEPLOYMENT_PRINCIPAL_OBJECT_ID` | Missing | Entra object GUID | Platform owner | OIDC/workload identity; no client secret |
| `AZURE_SECURITY_GROUP_OBJECT_ID` | Missing | Entra group object GUID | Security owner | Prefer groups over individual role assignments |
| `AZURE_OPERATIONS_GROUP_OBJECT_ID` | Missing | Entra group object GUID | Operations owner | Least privilege and PIM where applicable |
| `AZURE_PRIVACY_GROUP_OBJECT_ID` | Missing | Entra group object GUID | Privacy owner | Access to evidence, not case content by default |
| `AZURE_COST_CENTER` | Missing | Organization cost-center identifier | Finance owner | Required before cost-bearing deployment |

## Identity and public interface contracts

| Variable or value | Status | Expected format | Owner | Notes |
|-------------------|--------|-----------------|-------|-------|
| `ENTRA_APPLICANT_API_APP_ID` | Missing | Entra application/client GUID | Identity owner | Public-client/API registration design requires approval |
| `ENTRA_API_AUDIENCE` | Missing | Verified application URI | Identity owner | Must match APIM token validation |
| `ENTRA_STAFF_TENANT_ID` | Missing | Approved workforce tenant GUID | Identity owner | Confirm whether it is the Azure resource tenant |
| `ENTRA_STAFF_API_APP_ID` | Missing | Entra application/client GUID | Identity owner | Staff access requires phishing-resistant MFA and Conditional Access |
| `APIM_PUBLIC_HOSTNAME` | Missing | Approved HTTPS hostname | Platform owner | DNS/certificate ownership must be proven |
| `APIM_PUBLISHER_NAME` | Missing | Organization display name | Product owner | Non-secret deployment parameter |
| `APIM_PUBLISHER_EMAIL` | Missing | Role mailbox | Operations owner | Use a monitored role account, not a personal mailbox |
| `IOS_BUNDLE_IDENTIFIER` | Missing | Reverse-DNS identifier | Mobile owner | Needed for App Attest, associated domains, and API policy |
| `IOS_APP_ATTEST_ENVIRONMENT` | Missing | `development` or `production` per environment | Security/mobile owners | Pilot must use approved production attestation policy |

## Generated foundation inputs

| Variable | Secret | Status/default | Owner | Consumer |
|----------|--------|----------------|-------|----------|
| `enableProvisioning` | No | Structurally restricted to `false` in `lapluma-infra-0.0` | Platform/security | Bicep safety interlock; enabling requires a reviewed code change after all deployment blockers close |
| `AZURE_ENV_NAME` | No | Missing; approved AZD environment name | Platform | `infra/main.parameters.json` |
| `AZURE_RESOURCE_NAME_PREFIX` | No | Missing; 3–12 lowercase characters | Platform | Resource naming |
| `AZURE_RESOURCE_OWNER` | No | Missing; accountable team/role | Governance | Resource tags |
| `AZURE_SQL_ENTRA_ADMIN_OBJECT_ID` | No | Missing Entra group GUID | Data/platform | SQL Entra-only administrator |
| `AZURE_SQL_ENTRA_ADMIN_DISPLAY_NAME` | No | Missing group display name | Data/platform | SQL administrator metadata |
| `AZURE_HSM_INITIAL_ADMIN_OBJECT_ID` | No | Missing Entra principal GUID | CISO/platform | Managed HSM bootstrap; PIM/group decision pending |
| `LAPLUMA_VNET_ADDRESS_PREFIX` | No | Proposed `10.42.0.0/16`; not approved | Network | VNet |
| `LAPLUMA_CORE_SUBNET_PREFIX` | No | Proposed `10.42.0.0/23`; not approved | Network | Core ACA environment |
| `LAPLUMA_PROCESSING_SUBNET_PREFIX` | No | Proposed `10.42.2.0/23`; not approved | Network | Processing ACA environment |
| `LAPLUMA_AI_SUBNET_PREFIX` | No | Proposed `10.42.4.0/23`; not approved | Network | AI ACA environment |
| `LAPLUMA_FUNCTIONS_SUBNET_PREFIX` | No | Proposed `10.42.6.0/24`; not approved | Network | Functions integration |
| `LAPLUMA_PRIVATE_ENDPOINTS_SUBNET_PREFIX` | No | Proposed `10.42.7.0/24`; not approved | Network | Private endpoints |
| `ASPNETCORE_URLS` | No | Container default `http://+:8080` | Backend | Core API listen address |
| `PORT` | No | Container default `8080` | Backend | Processing health listener |
| `ACQUISITION_SCHEDULE` | No | Missing six-field NCRONTAB expression | Catalog operations | Timer trigger |
| `DURABLE_TASK_HUB_NAME` | No | Missing environment-unique safe name | Platform | Durable Functions task hub |
| `AzureWebJobsStorage__accountName` | No | Missing storage account name | Platform | Functions identity-based runtime storage |
| `SERVICEBUS__fullyQualifiedNamespace` | No | Missing namespace FQDN | Platform | Functions identity-based Service Bus binding |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | No | Generated deployment output; treat as sensitive configuration | Observability | Workload telemetry; never hard-code |
| `AZURE_CLIENT_ID` | No | Generated user-assigned managed-identity client ID | Platform | Azure SDK credential selection |
| `DOCUMENT_INTELLIGENCE_ENDPOINT` | No | Missing private endpoint URI | AI/platform | Processing adapter; no key fallback |
| `DOCUMENT_INTELLIGENCE_API_VERSION` | No | Missing pinned API version | AI/platform | Processing adapter |
| `PROCESSING_INPUT_CONTAINER_URL` | No | Missing private quarantine container URI | Data/platform | Processing input, read one object only |
| `PROCESSING_OUTPUT_CONTAINER_URL` | No | Missing private create-only staging URI | Data/platform | Processing output |
| `PROCESSING_QUEUE_NAME` | No | Proposed `document-processing` | Integration | Worker trigger |

The foundation intentionally has no values yet for Container Apps environments/apps, Function
hosting, API Management, Container Registry, Document Intelligence, private endpoints/private DNS,
RBAC assignments, CMK bindings, diagnostic settings, or protected deployment environments. Their
names, identities, resource IDs, DNS zones, SKU/capacity values, and policy inputs must be added to
this ledger before those resources are modeled. Until then, this scaffold is compileable planning
code, not a deployable platform.

The generated foundation defines no application secret. If a future dependency cannot use managed
identity, add a ledger row with owner, secret-store destination, rotation, and consumer before code
references it; never add its value here.

## Network, data, and key decisions

| Decision | Status | Owner | Required before Azure validation/deployment |
|----------|--------|-------|----------------------------|
| VNet and subnet CIDR plan for Edge, Core, Processing, AI, Functions, and private endpoints | Missing | Network owner | Yes |
| DNS ownership and private DNS linking model | Missing | Network owner | Yes |
| Approved egress destinations and firewall/control mechanism | Missing | Security owner | Yes |
| SQL server Entra administrator group | Missing | Data/platform owners | Yes |
| SQL General Purpose serverless floor, maximum, zone redundancy, and backup policy | Missing | Data/operations owners | Yes |
| Cosmos account consistency, autoscale ceiling, partition strategy, and US backup policy | Missing | Data owner | Yes |
| Blob account/container lifecycle and soft-delete/version-purge windows by purpose | Missing | Privacy/data owners | Yes |
| Service Bus Premium capacity, queues/topics, DLQ ownership, and duplicate-detection windows | Missing | Integration owner | Yes |
| Key Vault recovery, rotation, and RBAC owner groups | Missing | Security owner | Yes |
| Managed HSM administrators, key hierarchy, backup/restore procedure, quota, and monthly cost | Missing | CISO/platform/finance | Yes |
| Customer-managed key coverage and key-rotation policy | Missing | CISO/data owner | Yes |
| Log Analytics retention, archive, redaction, and access model | Proposed: 12 months | Security/privacy owners | Yes |

## Document and AI configuration

| Variable or value | Status | Expected format | Owner | Notes |
|-------------------|--------|-----------------|-------|-------|
| `DOCUMENT_INTELLIGENCE_ACCOUNT_NAME` | Missing | Generated Azure-safe name | Platform owner | Private endpoint only |
| `DOCUMENT_INTELLIGENCE_API_VERSION` | Missing | Explicit pinned service API version | AI/platform owners | No implicit latest version |
| `DOCUMENT_INTELLIGENCE_MODEL_IDS` | Missing | Approved source-document-class model IDs | RAI/catalog owners | Do not use government form IDs as source extractor classes |
| `AZURE_OPENAI_RESOURCE_ID` | Missing/conditional | Full Azure resource ID | AI owner | Only if approved generative features enter Alpha 0.2 |
| `AZURE_OPENAI_DEPLOYMENT_NAMES` | Missing/conditional | Pinned deployment identifiers | AI owner | No automatic model-version upgrades |
| `AZURE_AI_CONTENT_SAFETY_RESOURCE_ID` | Missing/conditional | Full Azure resource ID | RAI/security owners | Required for approved generative path; not a substitute for UPL classifier |
| `MODIFIED_ABUSE_MONITORING_STATUS` | Missing | Approved, denied, or not applicable with evidence reference | Privacy/RAI owners | Identity-document Path A remains non-generative regardless |
| `UPL_CLASSIFIER_VERSION` | Missing | Immutable release/version identifier | Compliance/RAI owners | Fail-closed release gate |

## Pilot policy values

| Variable or value | Approved baseline | Owner | Change rule |
|-------------------|-------------------|-------|-------------|
| `PILOT_INITIAL_CASE_TARGET` | `40` | Product/compliance | Expansion requires checkpoint evidence |
| `PILOT_MAX_ENROLLED_USERS` | `999` | Product/security | Server-enforced; increase requires a new approval cycle |
| `PILOT_DATA_RESIDENCY` | `US` | Privacy | New geography requires a new data-plane and legal review |
| `PILOT_PRIORITY_FORMS` | I-130; I-485; DS-11; FAFSA | Product/compliance | Priority is not activation; every edition remains fail-closed pending source/workflow approval |
| `PILOT_I130_ARTIFACT_MODE` | Proposed `OFFICIAL_PDF` / `AUTOMATIC_FILL` | Catalog/compliance | Activation requires verified official artifact, encoding, hash, edition, and two-person field-map approval |
| `PILOT_I485_ARTIFACT_MODE` | Proposed `OFFICIAL_PDF` / `AUTOMATIC_FILL` | Catalog/compliance | Activation requires verified official artifact, encoding, hash, edition, and two-person field-map approval |
| `PILOT_DS11_ARTIFACT_MODE` | Proposed `OFFICIAL_PDF` / `AUTOMATIC_FILL` | Catalog/compliance | No electronic signature; verify current official artifact encoding and round-trip fidelity before activation |
| `PILOT_FAFSA_ARTIFACT_MODE` | `EXTERNAL_WORKFLOW` / `REFERENCE_ONLY` | Catalog/compliance | No portal automation, credential handling, or claim that LaPluma files FAFSA |
| `PILOT_AUTOMATED_FILING_ENABLED` | `false` | Compliance | Architectural invariant |
| `PILOT_AUTOMATED_APPROVAL_ENABLED` | `false` | Compliance | Architectural invariant |
| `PILOT_ELECTRONIC_SIGNATURE_ENABLED` | `false` | Compliance | Wet-ink points only until form-specific counsel approval |
| `PILOT_REALTIME_VOICE_ENABLED` | `false` by default | Compliance/RAI | Enable only after latency, consent, retention, and 100% post-hoc gates pass |
| `CASE_CONTENT_RETENTION_TRIGGER` | Pending final policy alignment | Privacy/data owners | Must have one consistent contract before real data |
| `ACCOUNT_ERASURE_ACTIVE_DATA_SLA_DAYS` | Proposed `30` | Privacy owner | Must match approved notice and implementation |
| `BACKUP_EXPIRY_MAX_MONTHS` | Proposed `12` | Privacy/data owners | Must be stated truthfully to participants |
| `AUDIT_METADATA_RETENTION_YEARS` | Proposed `7` | Compliance/privacy | Content-free and pseudonymized on erasure |
| `SECURITY_LOG_RETENTION_MONTHS` | Proposed `12` | Security/privacy | Logs must contain no case content |

## Secret values deliberately excluded

Do not add any of the following to this ledger, repository, Bicep parameter file, AZD environment
file committed to Git, build log, or deployment output:

- API keys, connection strings, shared access signatures, database credentials, or client secrets.
- Managed HSM or Key Vault key material, recovery domains, private certificates, or signing keys.
- Access, refresh, session, upload, delivery, App Attest, passkey, or recovery tokens.
- Pilot participant names, email addresses, documents, case IDs, extracted values, or form answers.
- Real endpoint credentials or reviewer-account credentials.

When a required secret cannot be replaced by managed identity, document only its purpose, owner,
rotation interval, destination secret store, and consuming workload—not its value.
