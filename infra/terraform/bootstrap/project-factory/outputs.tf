output "project_id" {
  description = "The ID of the created GCP Project"
  value       = google_project.env_project.project_id
}

output "project_number" {
  description = "The numeric Project Number assigned by GCP"
  value       = google_project.env_project.number
}

output "artifact_registry_repository" {
  description = "The Artifact Registry Docker repository for microservice images"
  value       = google_artifact_registry_repository.services.id
}

output "github_workload_identity_provider" {
  description = "The full resource name of the Workload Identity Provider for GitHub Actions"
  value       = google_iam_workload_identity_pool_provider.github_provider.name
}

output "github_deployer_service_account_email" {
  description = "Email of the deployer service account assumed by GitHub Actions"
  value       = google_service_account.github_deployer_sa.email
}

output "tfc_workload_identity_provider" {
  description = "The full resource name of the Workload Identity Provider for Terraform Cloud"
  value       = google_iam_workload_identity_pool_provider.tfc_provider.name
}

output "tfc_deployer_service_account_email" {
  description = "Email of the service account assumed by Terraform Cloud"
  value       = google_service_account.tfc_sa.email
}

output "tfc_workspace_variables" {
  description = "Environment variables to configure in Terraform Cloud workspace (hcw/lapluma-<env>)"
  value = {
    TFC_GCP_PROVIDER_AUTH          = "true"
    TFC_GCP_WORKLOAD_PROVIDER_NAME = google_iam_workload_identity_pool_provider.tfc_provider.name
    TFC_GCP_SERVICE_ACCOUNT_EMAIL  = google_service_account.tfc_sa.email
  }
}
