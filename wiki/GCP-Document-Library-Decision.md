# Approved target: lean GCP and Document Library

**Decision date:** 2026-09-13. **Status:** Accepted target; implementation remains planned.

This records the owner's approved direction and the three rebaselined GitHub boards. It does not
assert a deployed GCP environment or completed migration. Existing Azure code and validation gates
remain the current scaffold until the corresponding implementation cards replace them.

## Service and trust boundaries

| Responsibility | Approved target |
|---|---|
| Metadata and workflow APIs | Google API Gateway fronts existing .NET services on Cloud Run; services enforce tenant/person authorization |
| Document processing | Python containers on Cloud Run; Cloud Run Jobs for batch work; distinct identities, bounded resources, restricted egress and no direct authoritative database access |
| Acquisition | Cloud Scheduler and jobs; verified source artifacts and edition-drift detection |
| Persistence | Cloud SQL PostgreSQL only: relational case/customer/permission/approval records and indexed JSONB document definitions; no Cosmos or Firestore |
| Artifacts | Private Cloud Storage objects with Google-managed encryption at rest and TLS |
| Events | Usage-billed Pub/Sub; retry policy, dead-letter handling and application idempotency |
| Delivery | Terraform, Artifact Registry and GitHub workload identity federation; no long-lived deployment keys |

The existing application identity/session contract remains in force. GCP workload identities replace
Azure service integrations; a provider migration does not relax tenant or per-person boundaries,
human review, approval invalidation, content-free telemetry, or the separation of untrusted processing
from authoritative writes. AI may propose; it may not activate, approve, sign or file.

Platform encryption replaces dedicated HSM and mandatory customer-managed keys in the baseline.
API Gateway replaces Azure API Management. PostgreSQL replaces the planned SQL/Cosmos split.
Pub/Sub replaces the premium broker assumption. These are approved target decisions, not requests
to provision services or remove existing security checks before a tested replacement exists.

## File transfers

The existing 100 MB upload allowance exceeds API Gateway's 32 MB request limit. Document bytes
bypass Gateway through short-lived, narrowly scoped grants to private Cloud Storage objects over
Google's internet endpoint. This explicitly supersedes private-network-only client transfers.
The metadata API authorizes each grant from the trusted tenant/person session. Validate actual
size, checksum, ownership and processing results before accepting evidence. Storage paths and
client-supplied tenant IDs are not authorization. Expiration bounds grant exposure; outstanding
grants must not be represented as instantly revocable. Retention/deletion must cover artifacts,
derived outputs and backups according to approved policies; platform encryption does not promise
per-customer cryptographic erasure through deletion of a customer key.

## Reusable document model

**Document Library → Document Blueprints → Document Collections** is the customer-facing language.

- A Blueprint is declarative, versioned data: issuer, source/ownership, immutable source artifact and
  hash, edition/revision, document type, field mappings, validation/evidence rules, preparation mode,
  output behavior, synthetic tests and publication evidence. It is not executable extension code.
- A Collection is a versioned selection of Blueprints, ordering, institution-owned documents,
  explanatory text, branding and workflow settings. Official document content and requirements
  remain unchanged; customer presentation cannot weaken authorization or official requirements.
- Customer assignments determine access to Collections and private Blueprints on the server.
  Shared official definitions and tenant-private definitions remain distinct. Isolation applies to
  queries, search, caches, downloads, exports and background processing.

Supported first-release preparation modes are fillable PDFs, static documents with assisted
preparation, and externally completed workflows with reference links. Interchangeability means a
reusable onboarding pipeline, not automatic fillability for arbitrary uploaded documents. Other
output formats are explicit extensions.

Publication is draft → validation → independent review → publication, with immutable revisions,
withdrawal and rollback. Existing cases pin both Blueprint and Collection revisions. Updates and
edition drift never silently rewrite in-progress work or preserve invalid approvals.

LaPluma staff initially use a repeatable CLI/CI onboarding template capturing all definition,
access, workflow, test and publication inputs. Customer self-service publishing and a new
administration frontend are outside the first release. Supported definitions and Collections must
be addable without application code changes or deployment of a new service.

Versioned library and Collection interfaces sit alongside the existing catalog API. Preserve
existing package identifiers and client compatibility during transition; avoid indiscriminate
breaking renames of technical identifiers.

## Delivery sequence and proof

Architecture and the Blueprint/Collection foundations are both P0. Establish their contracts
together, then deliver one existing USCIS workflow and two clearly synthetic institution examples
through the same onboarding/publication pipeline. Demonstrate a shared Blueprint and a private
institutional Blueprint that the other institution cannot discover or retrieve.

Acceptance evidence must cover draft publication rejection, independent review, immutable revisions,
edition drift, case pinning, withdrawal/rollback, duplicate messages, failed processing, expired
grants, oversized uploads, tenant/person isolation, approval invalidation and official PDF fidelity.
Full coverage of [USCIS all forms](https://www.uscis.gov/forms/all-forms) remains the first major
library expansion, not a limit on the platform's document model. A complete current form count
was not verified; the earlier source request returned HTTP 403. Reconcile an official snapshot
before asserting complete coverage.

## Presentation

Follow the supplied DESIGN.md reference as far as each surface permits: flat cards with 12 px
corners, pill controls, clear typography and consistent spacing. App surfaces use white with
pastel red, yellow, green and blue. Any infra frontend uses gray/black/white. Styling follows the
P0 foundations and remains required; no new infra administration frontend is implied.
The original attachment is not included here; the preserved DESIGN-REF card is a labeled summary,
not a verbatim substitute. The board snapshot retains that reference and its limitations.

## Execution and planning records

- [Infrastructure board](https://github.com/orgs/HybridCloudWorks/projects/2)
- [App board](https://github.com/orgs/HybridCloudWorks/projects/3)
- [Integration board](https://github.com/orgs/HybridCloudWorks/projects/4)
- [Pilot/growth cost model](GCP-Cost-Model.md), including processing and additional environments
- [Dated board handoff](GCP-Board-Handoff.md) and [verified baseline snapshot](planning/gcp-board-verification.json)

Boards hold current cross-repository card status, priority, dependencies and acceptance criteria.
The repository TODO index links its work to those cards. The JSON snapshot is dated evidence,
not a second live backlog. Original IDs and history are retained. No cards are marked complete
merely because this record is published.

## Source references

- [PostgreSQL JSONB](https://www.postgresql.org/docs/18/datatype-json.html)
- [Google default encryption](https://docs.cloud.google.com/docs/security/encryption/default-encryption)
- [Pub/Sub delivery](https://docs.cloud.google.com/pubsub/docs/subscription-overview)
- [API Gateway quotas and limits](https://docs.cloud.google.com/api-gateway/docs/quotas)
