# Dead-Letter Topic
resource "google_pubsub_topic" "dlq_topic" {
  name = "lp-processing-dlq-${var.environment}"
}

# Document Uploaded Event Topic
resource "google_pubsub_topic" "document_uploaded_topic" {
  name = "lp-document-uploaded-${var.environment}"
}

# Document Processed Event Topic
resource "google_pubsub_topic" "document_processed_topic" {
  name = "lp-document-processed-${var.environment}"
}

# Subscription for processing worker with dead-letter and exponential backoff
resource "google_pubsub_subscription" "processing_subscription" {
  name  = "lp-process-worker-sub-${var.environment}"
  topic = google_pubsub_topic.document_uploaded_topic.id

  ack_deadline_seconds = 60

  dead_letter_policy {
    dead_letter_topic     = google_pubsub_topic.dlq_topic.id
    max_delivery_attempts = 5
  }

  retry_policy {
    minimum_backoff = "10s"
    maximum_backoff = "600s"
  }
}
