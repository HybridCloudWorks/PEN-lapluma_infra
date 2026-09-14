# ------------------------------------------------------------------------------
# GitHub Actions Workload Identity Federation (Zero static service-account keys)
# ------------------------------------------------------------------------------
resource "google_iam_workload_identity_pool" "github_pool" {
  workload_identity_pool_id = "gh-pool-${var.environment}"
  display_name              = "GitHub Actions Pool - ${var.environment}"
  description               = "OIDC federation identity pool for GitHub Actions CI/CD"
}

resource "google_iam_workload_identity_pool_provider" "github_provider" {
  workload_identity_pool_id          = google_iam_workload_identity_pool.github_pool.workload_identity_pool_id
  workload_identity_pool_provider_id = "gh-provider-${var.environment}"
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

resource "google_service_account" "deployment_sa" {
  account_id   = "lp-deployer-${var.environment}"
  display_name = "LaPluma Deployment SA (${var.environment})"
  description  = "CI/CD deployment runner authenticated via GitHub OIDC federation"
}

resource "google_service_account_iam_member" "deployment_workload_identity_user" {
  service_account_id = google_service_account.deployment_sa.name
  role               = "roles/iam.workloadIdentityUser"
  member             = "principalSet://iam.googleapis.com/${google_iam_workload_identity_pool.github_pool.name}/attribute.repository/${var.github_repository}"
}

# ------------------------------------------------------------------------------
# Workload Service Accounts (Least Privilege)
# ------------------------------------------------------------------------------

# Core API Identity
resource "google_service_account" "core_api_sa" {
  account_id   = "lp-core-api-${var.environment}"
  display_name = "LaPluma Core API SA (${var.environment})"
}

# Workflow API Identity
resource "google_service_account" "workflow_api_sa" {
  account_id   = "lp-wf-api-${var.environment}"
  display_name = "LaPluma Workflow API SA (${var.environment})"
}

# Document Processing Worker Identity (Strictly isolated from SQL)
resource "google_service_account" "processing_worker_sa" {
  account_id   = "lp-proc-worker-${var.environment}"
  display_name = "LaPluma Processing Worker SA (${var.environment})"
  description  = "Untrusted document processing worker. Denied direct database routes and credentials."
}

# Acquisition Job Identity
resource "google_service_account" "acquisition_sa" {
  account_id   = "lp-acq-runner-${var.environment}"
  display_name = "LaPluma Official Form Acquisition SA (${var.environment})"
}

# API Gateway Identity (assumed by API Gateway to securely invoke backends)
resource "google_service_account" "gateway_sa" {
  account_id   = "lp-gw-${var.environment}"
  display_name = "LaPluma API Gateway SA (${var.environment})"
  description  = "Identity assumed by API Gateway to invoke backend Cloud Run services"
}

# Cloud SQL client access for Core and Workflow APIs only
resource "google_project_iam_member" "core_api_cloudsql" {
  project = var.project_id
  role    = "roles/cloudsql.client"
  member  = "serviceAccount:${google_service_account.core_api_sa.email}"
}

resource "google_project_iam_member" "workflow_api_cloudsql" {
  project = var.project_id
  role    = "roles/cloudsql.client"
  member  = "serviceAccount:${google_service_account.workflow_api_sa.email}"
}

# Pub/Sub roles
resource "google_project_iam_member" "workflow_api_pubsub_pub" {
  project = var.project_id
  role    = "roles/pubsub.publisher"
  member  = "serviceAccount:${google_service_account.workflow_api_sa.email}"
}

resource "google_project_iam_member" "processing_worker_pubsub_sub" {
  project = var.project_id
  role    = "roles/pubsub.subscriber"
  member  = "serviceAccount:${google_service_account.processing_worker_sa.email}"
}

resource "google_project_iam_member" "processing_worker_pubsub_pub" {
  project = var.project_id
  role    = "roles/pubsub.publisher"
  member  = "serviceAccount:${google_service_account.processing_worker_sa.email}"
}
