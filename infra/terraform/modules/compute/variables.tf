variable "project_id" {
  type = string
}

variable "environment" {
  type = string
}

variable "region" {
  type    = string
  default = "us-central1"
}

variable "core_api_image" {
  type    = string
  default = "us-docker.pkg.dev/cloudrun/container/hello"
}

variable "workflow_api_image" {
  type    = string
  default = "us-docker.pkg.dev/cloudrun/container/hello"
}

variable "processing_worker_image" {
  type    = string
  default = "us-docker.pkg.dev/cloudrun/container/hello"
}

variable "core_api_sa_email" {
  type = string
}

variable "workflow_api_sa_email" {
  type = string
}

variable "processing_worker_sa_email" {
  type = string
}

variable "db_connection_name" {
  type = string
}

variable "gateway_sa_email" {
  type        = string
  description = "Service account email of API Gateway authorized to invoke Cloud Run services"
  default     = ""
}

variable "pubsub_invoker_sa_email" {
  type        = string
  description = "Service account email of Pub/Sub invoker authorized to invoke the worker service"
  default     = ""
}

variable "migration_image" {
  type        = string
  description = "Container image for Cloud Run PostgreSQL 16 schema migration job"
  default     = "postgres:16-alpine"
}
