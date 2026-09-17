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

  dynamic "push_config" {
    for_each = var.processing_worker_url != "" && var.pubsub_invoker_sa_email != "" ? [1] : []
    content {
      push_endpoint = "${var.processing_worker_url}/pubsub"
      oidc_token {
        service_account_email = var.pubsub_invoker_sa_email
        audience              = var.processing_worker_url
      }
    }
  }

  dead_letter_policy {
    dead_letter_topic     = google_pubsub_topic.dlq_topic.id
    max_delivery_attempts = 5
  }

  retry_policy {
    minimum_backoff = "10s"
    maximum_backoff = "600s"
  }
}

# Allow GCS service account to publish to document_uploaded_topic
data "google_storage_project_service_account" "gcs_account" {}

resource "google_pubsub_topic_iam_member" "gcs_publisher" {
  count  = var.quarantine_bucket_name != "" ? 1 : 0
  topic  = google_pubsub_topic.document_uploaded_topic.name
  role   = "roles/pubsub.publisher"
  member = "serviceAccount:${data.google_storage_project_service_account.gcs_account.email_address}"
}

# Storage notification on the quarantine bucket for object creation
resource "google_storage_notification" "quarantine_notification" {
  count          = var.quarantine_bucket_name != "" ? 1 : 0
  bucket         = var.quarantine_bucket_name
  payload_format = "JSON_API_V1"
  topic          = google_pubsub_topic.document_uploaded_topic.id
  event_types    = ["OBJECT_FINALIZE"]

  depends_on = [google_pubsub_topic_iam_member.gcs_publisher]
}
