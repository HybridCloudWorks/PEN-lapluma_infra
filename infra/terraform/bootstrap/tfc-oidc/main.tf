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
  project = var.project_id
}

data "google_project" "current" {
  project_id = var.project_id
}

# ------------------------------------------------------------------------------
# Required APIs for Workload Identity Federation & Dynamic Credentials
# ------------------------------------------------------------------------------
resource "google_project_service" "iam" {
  project            = var.project_id
  service            = "iam.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "iamcredentials" {
  project            = var.project_id
  service            = "iamcredentials.googleapis.com"
  disable_on_destroy = false
}

resource "google_project_service" "sts" {
  project            = var.project_id
  service            = "sts.googleapis.com"
  disable_on_destroy = false
}

# ------------------------------------------------------------------------------
# Workload Identity Pool for Terraform Cloud
# ------------------------------------------------------------------------------
resource "google_iam_workload_identity_pool" "tfc_pool" {
  workload_identity_pool_id = var.tfc_pool_id
  display_name              = "Terraform Cloud Pool"
  description               = "Workload Identity Pool for Terraform Cloud dynamic provider credentials"

  depends_on = [
    google_project_service.iam,
    google_project_service.sts
  ]
}

# ------------------------------------------------------------------------------
# Workload Identity Pool Provider for Terraform Cloud (OIDC)
# ------------------------------------------------------------------------------
resource "google_iam_workload_identity_pool_provider" "tfc_provider" {
  workload_identity_pool_id          = google_iam_workload_identity_pool.tfc_pool.workload_identity_pool_id
  workload_identity_pool_provider_id = var.tfc_provider_id
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
    issuer_uri        = "https://app.terraform.io"
    allowed_audiences = [
      "gcp.workload.identity",
      "//iam.googleapis.com/${google_iam_workload_identity_pool.tfc_pool.name}/providers/${var.tfc_provider_id}"
    ]
  }
}

# ------------------------------------------------------------------------------
# Dedicated Service Account for Terraform Cloud Deployments
# ------------------------------------------------------------------------------
resource "google_service_account" "tfc_sa" {
  account_id   = var.tfc_service_account_id
  display_name = "Terraform Cloud Deployer"
  description  = "Dedicated service account assumed by Terraform Cloud via OIDC federation"
}

# Allow Terraform Cloud workflows in org 'hcw' to impersonate this Service Account
resource "google_service_account_iam_member" "tfc_sa_wif_user" {
  service_account_id = google_service_account.tfc_sa.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.tfc_pool.name}/attribute.terraform_organization_name/${var.tfc_organization_name}"
}

# Assign deployment permissions to the Terraform Cloud service account
resource "google_project_iam_member" "tfc_sa_editor" {
  project = var.project_id
  role    = "roles/editor"
  member  = "serviceAccount:${google_service_account.tfc_sa.email}"
}

resource "google_project_iam_member" "tfc_sa_iam_admin" {
  project = var.project_id
  role    = "roles/resourcemanager.projectIamAdmin"
  member  = "serviceAccount:${google_service_account.tfc_sa.email}"
}
