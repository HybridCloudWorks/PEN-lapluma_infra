# LaPluma Unified Project Factory & OIDC Bootstrap

This module provisions a completely isolated Google Cloud Platform (GCP) project for LaPluma according to Google Cloud Well-Architected Framework and HashiCorp Project Factory best practices.

## What it Provisions in One Step

1. **GCP Project**: Creates the project under Organization `42574862375` and binds Billing Account `01AEE9-772C7A-9720CC`.
2. **Google Cloud APIs**: Enables the 13 required services (Cloud Run, Cloud SQL, Artifact Registry, API Gateway, Pub/Sub, IAM, STS, Secret Manager, etc.).
3. **Artifact Registry**: Establishes `lapluma-services-<env>` Docker registry in `us-central1`.
4. **GitHub Actions Workload Identity Federation**:
   - Identity Pool: `pool-github-<env>`
   - Provider: `provider-github`
   - Service Account: `lp-deployer-<env>` with `roles/run.admin`, `roles/artifactregistry.writer`, and `roles/iam.serviceAccountUser`.
5. **HCP Terraform Cloud Workload Identity Federation**:
   - Identity Pool: `tfc-pool`
   - Provider: `tfc-provider` (scoped to organization `hcw`)
   - Service Account: `tfc-deployer` with `roles/editor` and `roles/resourcemanager.projectIamAdmin`.

## How to Execute

### 1. Prerequisite: One-time Authentication
Ensure you have the Google Cloud CLI installed and authenticate with your organization credentials:

```bash
# If gcloud is not installed:
brew install --cask google-cloud-sdk

# Log in with your Google account (must hold Organization Administrator and Billing Account User roles):
gcloud auth application-default login
```

### 2. Initialize and Apply

```bash
cd infra/terraform/bootstrap/project-factory
cp terraform.tfvars.example terraform.tfvars

# Review or adjust variables in terraform.tfvars if needed

terraform init
terraform plan -out=bootstrap.tfplan
terraform apply bootstrap.tfplan
```

### 3. Immediate Results
* **GitHub Actions**: `.github/workflows/deploy-gcp.yml` can now authenticate and deploy without any static keys.
* **HCP Terraform**: Configure the three output variables in your workspace (`hcw/lapluma-<env>`) to enable automatic plan and apply.
