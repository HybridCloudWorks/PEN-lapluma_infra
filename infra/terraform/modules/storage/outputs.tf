output "artifacts_bucket_name" {
  value = google_storage_bucket.artifacts_bucket.name
}

output "documents_bucket_name" {
  value = google_storage_bucket.documents_bucket.name
}

output "scratch_bucket_name" {
  value = google_storage_bucket.scratch_bucket.name
}
