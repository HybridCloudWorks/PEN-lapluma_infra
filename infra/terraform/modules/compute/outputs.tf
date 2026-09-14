output "core_api_url" {
  value = google_cloud_run_v2_service.core_api.uri
}

output "workflow_api_url" {
  value = google_cloud_run_v2_service.workflow_api.uri
}

output "processing_worker_url" {
  value = google_cloud_run_v2_service.processing_worker.uri
}
