variable "environment" {
  type = string
}

variable "processing_worker_url" {
  type        = string
  description = "URL of the processing worker Cloud Run service"
  default     = ""
}

variable "pubsub_invoker_sa_email" {
  type        = string
  description = "Email of the service account used by Pub/Sub to invoke the worker"
  default     = ""
}

variable "quarantine_bucket_name" {
  type        = string
  description = "Name of the quarantine bucket to monitor for object finalize events"
  default     = ""
}
