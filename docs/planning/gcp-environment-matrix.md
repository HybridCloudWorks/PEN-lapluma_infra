# Isolated GCP Environment Matrix & Operational Policies

**Status:** Approved Reference  
**ADR Reference:** [ADR-019](../../docs/adr/ADR-019-lean-gcp-document-library.md) (Lean Managed GCP Platform & Document Library Architecture)  
**Last Updated:** 2026-09-14  

---

## 1. Environment Architecture & Isolation Matrix

LaPluma provisions three completely isolated Google Cloud Platform (GCP) environments. Each environment resides in a distinct GCP Project with no shared credentials, no inter-project VPC peering, and independent Cloud IAM boundaries.

| Attribute | Dev (`dev`) | Staging (`staging`) | Pilot (`pilot`) |
| :--- | :--- | :--- | :--- |
| **GCP Project ID** | `lapluma-dev-gcp` | `lapluma-staging-gcp` | `lapluma-pilot-gcp` |
| **Project Number** | Assigned at creation | Assigned at creation | Assigned at creation |
| **Primary Region** | `us-central1` | `us-central1` | `us-central1` |
| **Environment Role** | Rapid developer sandbox & unit verification | Pre-production integration & release validation | Production customer pilot (<$100/mo hard cap) |
| **Data Classification** | **Synthetic Only** (Strict prohibition of PII) | **Synthetic Only** (Reconciled test fixtures) | **Production Pilot PII** (Encrypted & isolated) |
| **VPC Network** | `vpc-lapluma-dev` (`10.10.0.0/20`) | `vpc-lapluma-staging` (`10.20.0.0/20`) | `vpc-lapluma-pilot` (`10.30.0.0/20`) |
| **Cross-Project Peering** | **None** (Disallowed) | **None** (Disallowed) | **None** (Disallowed) |
| **Database Engine** | Cloud SQL PostgreSQL 16 | Cloud SQL PostgreSQL 16 | Cloud SQL PostgreSQL 16 |
| **Database Tier** | `db-g1-small` (1 vCPU, 1.7 GB RAM) | `db-g1-small` (1 vCPU, 1.7 GB RAM) | `db-g1-small` (1 vCPU, 1.7 GB RAM) |
| **Database High Availability** | `ZONAL` | `ZONAL` | `ZONAL` (Pilot SLA) |
| **Database Connectivity** | Private IP only (VPC Peering/PSA) | Private IP only (VPC Peering/PSA) | Private IP only (VPC Peering/PSA) |
| **Automated DB Backups** | Disabled (Ephemeral) | Enabled (7-day retention) | Enabled (14-day retention + PITR) |
| **Compute Engine** | Cloud Run v2 (Scale-to-zero) | Cloud Run v2 (Scale-to-zero) | Cloud Run v2 (Scale-to-zero, max 1 instance) |
| **Cloud Run Ingress** | Internal + Cloud Load Balancing/Gateway | Internal + Cloud Load Balancing/Gateway | Internal + Cloud Load Balancing/Gateway |
| **Cloud Run Invoker IAM** | Restricted to `lp-gw-dev` SA | Restricted to `lp-gw-staging` SA | Restricted to `lp-gw-pilot` SA |
| **API Gateway** | `gw-lapluma-dev` | `gw-lapluma-staging` | `gw-lapluma-pilot` |
| **OIDC Audience** | `api://lapluma-workforce-dev` | `api://lapluma-workforce-staging` | `api://lapluma-workforce-pilot` |
| **Storage Buckets** | `gs://lapluma-dev-*` | `gs://lapluma-staging-*` | `gs://lapluma-pilot-*` |
| **Bucket Access Control** | Uniform Bucket-Level Access (UBLA) | Uniform Bucket-Level Access (UBLA) | Uniform Bucket-Level Access (UBLA) |
| **Public Access Prevention** | `enforced` | `enforced` | `enforced` |
| **Soft Delete Policy** | 7 days | 7 days | 7 days |
| **Monthly Budget Cap** | $25.00 / month | $50.00 / month | **$100.00 / month** (Pilot hard target) |
| **Billing Alerts** | 50%, 80%, 100% | 50%, 80%, 100% | 50%, 75%, 90%, 100% |
| **Deployment Authorization** | CI automated plan | CI automated plan | **Dual-custody human sign-off required** |

---

## 2. Security Boundaries & Invariants

### 2.1 Complete Project Separation
- **No Shared IAM Roles:** Service accounts in `lapluma-dev-gcp` have zero permissions in `lapluma-staging-gcp` or `lapluma-pilot-gcp`.
- **Workload Identity Federation:** GitHub Actions authenticates via project-specific Workload Identity Pools (`pool-github-${env}`). Tokens are strictly scoped to `assertion.repository == 'HybridCloudWorks/PEN-lapluma_infra'` and the target environment branch.
- **No Network Bridges:** Inter-project VPC peering is prohibited. Microservices communicate solely across API Gateway with verified cryptographic assertions.

### 2.2 Strict Synthetic Data Boundaries (Dev & Staging)
- **Zero Real PII:** Developers and automated tests must never upload real applicant names, Alien Registration Numbers, Social Security Numbers, or scanned identifying documents to Dev or Staging.
- **Fixture Enforcement:** Automated tests and seeds must use the standardized synthetic fixtures (`tests/fixtures/`, synthetic blueprints, and deterministic fake identities).
- **Audit Requirement:** Any introduction of live applicant data outside `lapluma-pilot-gcp` constitutes an immediate security incident requiring project-level crypto-shredding.

### 2.3 Lean Pilot Budget & Scale-to-Zero Architecture
- The pilot environment is engineered to operate strictly beneath the **$100/mo** budget ceiling:
  - **Cloud SQL:** Micro instance (`db-g1-small`) with 10 GB storage and `ZONAL` deployment (~$18–$25/mo).
  - **Cloud Run v2:** `min-instances = 0`, scaling to zero when idle (~$5–$15/mo based on invocation volume).
  - **Cloud Storage:** Standard class with lifecycle rules auto-deleting temp artifacts after 14 days (~$1–$5/mo).
  - **API Gateway:** Usage-metered API calls (~$0.00 per million calls up to free tier, then minimal).
  - **Pub/Sub:** Free tier covers up to 10 GB/mo of message volume.

---

## 3. Deployment & Change Management Gates

### 3.1 Continuous Integration (CI) Checks
Every pull request in `PEN-lapluma_infra` and `PEN-lapluma_app` must execute:
1. `terraform fmt -check -recursive infra/terraform`
2. `terraform init -backend=false` and `terraform validate` across all three environments (`dev`, `staging`, `pilot`).
3. Contract synchronization tests (`tools/validate_foundation.py`, `tools/run_test_suites.py`).

### 3.2 Human Authorization Gate for Pilot Apply
- **Automated Apply Prohibited on Pilot:** CI pipelines are never granted permissions to execute `terraform apply` against `lapluma-pilot-gcp` unattended.
- **Dual-Custody Sign-Off:**
  1. A speculative `terraform plan -out=pilot.tfplan` is generated by CI or an authorized engineer.
  2. The plan is reviewed by an independent reviewer (`reviewer != author`).
  3. Execution of `terraform apply pilot.tfplan` requires explicit approval from the Cloud Architecture & Security Leads.
- **Rollback Readiness:** Every migration script must include a verified rollback script (e.g. `down.sql`).
