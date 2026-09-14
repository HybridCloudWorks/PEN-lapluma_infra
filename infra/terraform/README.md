# LaPluma Lean GCP Infrastructure as Code (Terraform)

This directory contains the modular, production-grade, and cost-optimized Terraform infrastructure for LaPluma on Google Cloud Platform, delivering on task **INF-16** and implementing the approved **Lean Managed GCP Architecture**.

## Architecture & Security Invariants

1. **Zero Static Deployment Keys**:
   - Authenticated via GitHub Actions OIDC Workload Identity Federation (`modules/iam`). No long-lived service account JSON keys exist.
2. **Least Privilege & Role Isolation**:
   - `core-api` & `workflow-api`: Authorized for Cloud SQL and private storage buckets.
   - `processing-worker`: Strictly isolated from the database. Denied Cloud SQL roles, credentials, and database network routes. Operates purely through signed storage objects and Pub/Sub messages.
   - `acquisition-runner`: Authorized to stage official forms in `lp-artifacts` and trigger Pub/Sub notifications.
3. **Cost Optimization (<$100/mo Total Pilot Footprint)**:
   - **Cloud SQL PostgreSQL 16**: Defaults to `db-g1-small` (1 shared vCPU, 1.7 GB RAM) for Pilot at ~$25–$35/month (vs. legacy $100–$130/month) with SSD storage auto-grow and automated backups.
   - **Scale-to-Zero Compute**: Cloud Run v2 services (`core-api`, `workflow-api`, `processing-worker`) are configured with `min_instance_count = 0` and `cpu_idle = true`, keeping low-traffic pilot compute within GCP's 2M monthly free requests tier.
   - **Zero Cloud NAT Costs**: Direct IAM Unix sockets and Direct VPC Egress eliminate the ~$32.40/month idle Cloud NAT gateway charge.
   - **Storage Lifecycle Policies**: 1-day auto-deletion for temporary scratch chunks; 30-day Nearline transition for retained evidence.
   - **Serverless Event Bus**: Pub/Sub topic and subscription with exponential backoff and dead-letter queue (`lp-processing-dlq`).

## Directory Layout

```text
infra/terraform/
├── modules/
│   ├── iam/           # Workload Identity Federation & service accounts
│   ├── network/       # VPC, subnet, and Private Services Access for Cloud SQL
│   ├── storage/       # GCS buckets (artifacts, documents, scratch) + lifecycle
│   ├── database/      # Cloud SQL PostgreSQL 16 + Secret Manager credentials
│   ├── messaging/     # Pub/Sub topics, subscriptions, and DLQ
│   ├── compute/       # Cloud Run v2 services with scale-to-zero
│   └── gateway/       # Google API Gateway config and endpoint
└── environments/
    ├── dev/           # Ephemeral development environment
    ├── staging/       # Pre-pilot integration validation
    └── pilot/         # Supervised pilot environment
```

## How to Plan and Apply

1. Copy `terraform.tfvars.example` to `terraform.tfvars` in your target environment:
   ```bash
   cd infra/terraform/environments/pilot
   cp terraform.tfvars.example terraform.tfvars
   ```
2. Initialize and validate:
   ```bash
   terraform init
   terraform validate
   ```
3. Plan:
   ```bash
   terraform plan -out=tfplan
   ```
