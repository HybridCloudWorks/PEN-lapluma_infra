output "deployment_sa_email" {
  value = google_service_account.deployment_sa.email
}

output "workload_identity_provider_name" {
  value = google_iam_workload_identity_pool_provider.github_provider.name
}

output "core_api_sa_email" {
  value = google_service_account.core_api_sa.email
}

output "workflow_api_sa_email" {
  value = google_service_account.workflow_api_sa.email
}

output "processing_worker_sa_email" {
  value = google_service_account.processing_worker_sa.email
}

output "acquisition_sa_email" {
  value = google_service_account.acquisition_sa.email
}

output "gateway_sa_email" {
  value = google_service_account.gateway_sa.email
}

output "pubsub_invoker_sa_email" {
  value = google_service_account.pubsub_invoker_sa.email
}
