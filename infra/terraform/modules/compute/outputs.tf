output "core_api_url" {
  value = google_cloud_run_v2_service.core_api.uri
}

output "workflow_api_url" {
  value = google_cloud_run_v2_service.workflow_api.uri
}

output "processing_worker_url" {
  value = google_cloud_run_v2_service.processing_worker.uri
}

output "artifact_registry_repository_id" {
  value = google_artifact_registry_repository.services.repository_id
}

output "artifact_registry_repository_url" {
  value = "${var.region}-docker.pkg.dev/${var.project_id}/${google_artifact_registry_repository.services.repository_id}"
}
