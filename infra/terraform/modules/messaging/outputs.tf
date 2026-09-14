output "document_uploaded_topic_id" {
  value = google_pubsub_topic.document_uploaded_topic.id
}

output "document_processed_topic_id" {
  value = google_pubsub_topic.document_processed_topic.id
}

output "dlq_topic_id" {
  value = google_pubsub_topic.dlq_topic.id
}

output "processing_subscription_id" {
  value = google_pubsub_subscription.processing_subscription.id
}
