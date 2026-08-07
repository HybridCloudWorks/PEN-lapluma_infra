# Security and data protection

## Governing constraints

These constraints are architectural invariants for the `lapluma-app-0.2` supervised pilot. They are
not negotiable against cost or schedule.

- Treat all pilot data as sensitive production PII. Development uses synthetic data only; staging
  may use synthetic or explicitly consented pilot data; pilot data stays in the US data plane.
- The user or an authorized human selects the form package. AI and catalog services cannot inspect
  case data to recommend a form, determine eligibility, predict outcomes, approve, sign, or file.
- Every extracted value retains provenance and requires human confirmation. A separately authorized
  human must approve a package before export; all automated components return proposals only.
- The processing zone treats every upload as hostile, has no database route, and cannot write to
  authoritative document stores. It receives one scoped input and writes only to a create-only
  staging target.
- Managed identities are the default for workload access. Secrets never enter source control, Bicep
  outputs, deployment logs, or ordinary pipeline variables.
- Public network access is disabled on PII-bearing data and AI services. Private endpoints, private
  DNS, least-privilege RBAC, customer-managed encryption, auditability, and fail-closed security
  controls are pilot prerequisites, not later hardening.
- No SOC 2 certification claim is permitted on the basis of architecture or Azure inheritance.
  Alpha 0.2 may describe evidence collection or readiness only after claim-owner approval.

## Controls present in the generated foundation

| Control | Where |
|---------|-------|
| Provisioning interlock that structurally rejects `true` | `infra/main.bicep` |
| Entra-only SQL authentication (`azureADOnlyAuthentication: true`), TLS 1.2 floor, `publicNetworkAccess: 'Disabled'`, `restrictOutboundNetworkAccess: 'Enabled'` | `infra/modules/data.bicep` |
| Cosmos local authentication disabled, public network access disabled, TLS 1.2 floor | `infra/modules/data.bicep` |
| Storage: shared-key access disabled, blob public access disabled, OAuth default, TLS 1.2 floor, public network access disabled, purpose-separated accounts | `infra/modules/data.bicep` |
| Service Bus local authentication disabled, public network access disabled, TLS 1.2 floor | `infra/modules/messaging.bicep` |
| Key Vault RBAC authorization, soft delete, purge protection, `defaultAction: 'Deny'`, `bypass: 'None'`, public network access disabled | `infra/modules/security.bicep` |
| Managed HSM purge protection, network ACL deny-by-default, public network access disabled | `infra/modules/security.bicep` |
| Four separate user-assigned managed identities for core, processing, AI, and functions workloads | `infra/modules/security.bicep` |
| Per-zone network security groups and an explicit `DenyInternetEgress` outbound rule on the processing subnet | `infra/modules/network.bicep` |
| Application Insights local authentication disabled; ingestion and query public access disabled | `infra/modules/observability.bicep` |
| Log Analytics resource-permission-only log access | `infra/modules/observability.bicep` |
| Non-root container users in both service images | `src/core-api/Dockerfile`, `src/document-processing/Dockerfile` |
| Health-endpoint logging suppressed so no path, query string, document ID, or free text is emitted | `src/document-processing/worker.py` |
| Repository-wide scan for private keys, storage connection strings, JWT-like tokens, and concrete tenant or subscription assignments | `tools/validate_foundation.py`, run in CI |

Controls that are specified but **not yet implemented** — private endpoints and private DNS, RBAC
role assignments, customer-managed-key bindings, diagnostic settings, egress control beyond the NSG
rule, and Azure Monitor Private Link Scope — are tracked in `TODO.md`.

## Secret values deliberately excluded

Do not add any of the following to the repository, a wiki page, a Bicep parameter file, an AZD
environment file committed to Git, a build log, or a deployment output:

- API keys, connection strings, shared access signatures, database credentials, or client secrets.
- Managed HSM or Key Vault key material, recovery domains, private certificates, or signing keys.
- Access, refresh, session, upload, delivery, App Attest, passkey, or recovery tokens.
- Pilot participant names, email addresses, documents, case IDs, extracted values, or form answers.
- Real endpoint credentials or reviewer-account credentials.

When a required secret cannot be replaced by managed identity, document only its purpose, owner,
rotation interval, destination secret store, and consuming workload — never its value. Record it in
[Configuration Contract](Configuration-Contract).

## Decision areas awaiting an owner

Each of the following is a security-relevant decision that an engineer cannot settle alone. They are
tracked as blockers with named owners in `REVIEW.md`; the list here exists so the decision surface
is documented in one place.

- VNet and subnet CIDR plan for the Edge, Core, Processing, AI, Functions, and private-endpoint
  zones.
- DNS ownership and the private-DNS linking model.
- Approved egress destinations and the firewall or control mechanism that enforces them.
- The Azure SQL Entra administrator group.
- SQL General Purpose serverless floor, maximum, zone redundancy, and backup policy.
- Cosmos account consistency, autoscale ceiling, partition strategy, and US backup policy.
- Blob account and container lifecycle plus soft-delete and version-purge windows, by purpose.
- Service Bus Premium capacity, queues and topics, dead-letter ownership, and duplicate-detection
  windows.
- Key Vault recovery, rotation, and RBAC owner groups.
- Managed HSM administrators, key hierarchy, backup and restore procedure, quota, and monthly cost.
- Customer-managed-key coverage and key-rotation policy.
- Log Analytics retention, archive, redaction, and access model. Proposed baseline: 12 months.

## Related pages

- [Architecture Overview](Architecture-Overview)
- [Configuration Contract](Configuration-Contract)
- [Pilot Policy and Compliance Gates](Pilot-Policy-and-Compliance-Gates)
