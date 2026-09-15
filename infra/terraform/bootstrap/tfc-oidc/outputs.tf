output "workload_identity_pool_name" {
  description = "The full name of the Workload Identity Pool"
  value       = google_iam_workload_identity_pool.tfc_pool.name
}

output "workload_identity_provider_name" {
  description = "The full name of the Workload Identity Provider to configure in Terraform Cloud"
  value       = google_iam_workload_identity_pool_provider.tfc_provider.name
}

output "service_account_email" {
  description = "The email of the Service Account to configure in Terraform Cloud"
  value       = google_service_account.tfc_sa.email
}

output "tfc_environment_variables" {
  description = "Key-value environment variables to set in Terraform Cloud workspace or variable set"
  value = {
    TFC_GCP_PROVIDER_AUTH          = "true"
    TFC_GCP_WORKLOAD_PROVIDER_NAME = google_iam_workload_identity_pool_provider.tfc_provider.name
    TFC_GCP_SERVICE_ACCOUNT_EMAIL  = google_service_account.tfc_sa.email
  }
}
