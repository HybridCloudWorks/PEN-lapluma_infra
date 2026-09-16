terraform {
  required_version = ">= 1.5.0"
  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 6.0"
    }
  }
}

provider "google" {
  # Authenticates via application-default login (gcloud auth application-default login)
}

# ------------------------------------------------------------------------------
# 1. Google Cloud Project Creation
# ------------------------------------------------------------------------------
resource "google_project" "env_project" {
  name            = var.project_name
  project_id      = var.project_id
  org_id          = var.org_id
  billing_account = var.billing_account_id
  deletion_policy = var.environment == "pilot" ? "PREVENT" : "DELETE"
}

# ------------------------------------------------------------------------------
# 2. Required Google Cloud APIs (Idempotent & non-destructive)
# ------------------------------------------------------------------------------
locals {
  required_services = [
    "iam.googleapis.com",
    "iamcredentials.googleapis.com",
    "sts.googleapis.com",
    "run.googleapis.com",
    "sqladmin.googleapis.com",
    "artifactregistry.googleapis.com",
    "apigateway.googleapis.com",
    "servicecontrol.googleapis.com",
    "servicemanagement.googleapis.com",
    "servicenetworking.googleapis.com",
    "pubsub.googleapis.com",
    "secretmanager.googleapis.com",
    "cloudresourcemanager.googleapis.com"
  ]
}

resource "google_project_service" "services" {
  for_each           = toset(local.required_services)
  project            = google_project.env_project.project_id
  service            = each.key
  disable_on_destroy = false
}

# ------------------------------------------------------------------------------
# 3. Artifact Registry (Docker Microservices Repository)
# ------------------------------------------------------------------------------
resource "google_artifact_registry_repository" "services" {
  project       = google_project.env_project.project_id
  location      = var.region
  repository_id = "lapluma-services-${var.environment}"
  description   = "Container images for LaPluma microservices (${var.environment})"
  format        = "DOCKER"

  depends_on = [
    google_project_service.services["artifactregistry.googleapis.com"]
  ]
}

# ------------------------------------------------------------------------------
# 4. GitHub Actions Workload Identity Federation (OIDC)
# ------------------------------------------------------------------------------
resource "google_iam_workload_identity_pool" "github_pool" {
  project                   = google_project.env_project.project_id
  workload_identity_pool_id = "pool-github-${var.environment}"
  display_name              = "GitHub Actions Pool - ${var.environment}"
  description               = "OIDC federation identity pool for GitHub Actions CI/CD"

  depends_on = [
    google_project_service.services["iam.googleapis.com"],
    google_project_service.services["sts.googleapis.com"]
  ]
}

resource "google_iam_workload_identity_pool_provider" "github_provider" {
  project                            = google_project.env_project.project_id
  workload_identity_pool_id          = google_iam_workload_identity_pool.github_pool.workload_identity_pool_id
  workload_identity_pool_provider_id = "provider-github"
  display_name                       = "GitHub Actions Provider"

  attribute_mapping = {
    "google.subject"       = "assertion.sub"
    "attribute.actor"      = "assertion.actor"
    "attribute.repository" = "assertion.repository"
    "attribute.ref"        = "assertion.ref"
  }

  attribute_condition = "assertion.repository == '${var.github_repository}'"

  oidc {
    issuer_uri = "https://token.actions.githubusercontent.com"
  }
}

resource "google_service_account" "github_deployer_sa" {
  project      = google_project.env_project.project_id
  account_id   = "lp-deployer-${var.environment}"
  display_name = "LaPluma Deployment SA (${var.environment})"
  description  = "CI/CD deployment runner authenticated via GitHub OIDC federation"

  depends_on = [
    google_project_service.services["iam.googleapis.com"]
  ]
}

resource "google_service_account_iam_member" "github_deployer_wif" {
  service_account_id = google_service_account.github_deployer_sa.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.github_pool.name}/attribute.repository/${var.github_repository}"
}

resource "google_project_iam_member" "github_deployer_run_admin" {
  project = google_project.env_project.project_id
  role    = "roles/run.admin"
  member  = "serviceAccount:${google_service_account.github_deployer_sa.email}"
}

resource "google_project_iam_member" "github_deployer_ar_writer" {
  project = google_project.env_project.project_id
  role    = "roles/artifactregistry.writer"
  member  = "serviceAccount:${google_service_account.github_deployer_sa.email}"
}

resource "google_project_iam_member" "github_deployer_sa_user" {
  project = google_project.env_project.project_id
  role    = "roles/iam.serviceAccountUser"
  member  = "serviceAccount:${google_service_account.github_deployer_sa.email}"
}

# ------------------------------------------------------------------------------
# 5. Terraform Cloud Workload Identity Federation (OIDC)
# ------------------------------------------------------------------------------
resource "google_iam_workload_identity_pool" "tfc_pool" {
  project                   = google_project.env_project.project_id
  workload_identity_pool_id = "tfc-pool"
  display_name              = "Terraform Cloud Pool"
  description               = "Workload Identity Pool for Terraform Cloud dynamic provider credentials"

  depends_on = [
    google_project_service.services["iam.googleapis.com"],
    google_project_service.services["sts.googleapis.com"]
  ]
}

resource "google_iam_workload_identity_pool_provider" "tfc_provider" {
  project                            = google_project.env_project.project_id
  workload_identity_pool_id          = google_iam_workload_identity_pool.tfc_pool.workload_identity_pool_id
  workload_identity_pool_provider_id = "tfc-provider"
  display_name                       = "Terraform Cloud Provider"

  attribute_mapping = {
    "google.subject"                        = "assertion.sub"
    "attribute.aud"                         = "assertion.aud"
    "attribute.terraform_run_phase"         = "assertion.terraform_run_phase"
    "attribute.terraform_project_id"        = "assertion.terraform_project_id"
    "attribute.terraform_project_name"      = "assertion.terraform_project_name"
    "attribute.terraform_workspace_id"      = "assertion.terraform_workspace_id"
    "attribute.terraform_workspace_name"    = "assertion.terraform_workspace_name"
    "attribute.terraform_organization_id"   = "assertion.terraform_organization_id"
    "attribute.terraform_organization_name" = "assertion.terraform_organization_name"
  }

  attribute_condition = "assertion.terraform_organization_name == '${var.tfc_organization_name}'"

  oidc {
    issuer_uri = "https://app.terraform.io"
    allowed_audiences = [
      "gcp.workload.identity",
      "//iam.googleapis.com/${google_iam_workload_identity_pool.tfc_pool.name}/providers/tfc-provider"
    ]
  }
}

resource "google_service_account" "tfc_sa" {
  project      = google_project.env_project.project_id
  account_id   = "tfc-deployer"
  display_name = "Terraform Cloud Deployer"
  description  = "Dedicated service account assumed by Terraform Cloud via OIDC federation"

  depends_on = [
    google_project_service.services["iam.googleapis.com"]
  ]
}

resource "google_service_account_iam_member" "tfc_sa_wif_user" {
  service_account_id = google_service_account.tfc_sa.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.tfc_pool.name}/attribute.terraform_organization_name/${var.tfc_organization_name}"
}

resource "google_project_iam_member" "tfc_sa_editor" {
  project = google_project.env_project.project_id
  role    = "roles/editor"
  member  = "serviceAccount:${google_service_account.tfc_sa.email}"
}

resource "google_project_iam_member" "tfc_sa_iam_admin" {
  project = google_project.env_project.project_id
  role    = "roles/resourcemanager.projectIamAdmin"
  member  = "serviceAccount:${google_service_account.tfc_sa.email}"
}
