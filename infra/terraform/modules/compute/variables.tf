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

variable "session_token_issuer" {
  type        = string
  description = "Issuer (iss) of LaPluma session tokens and the public base URL clients reach the API on (the API Gateway hostname or its custom domain), with no trailing slash"

  validation {
    condition     = can(regex("^https://", var.session_token_issuer)) && !endswith(var.session_token_issuer, "/")
    error_message = "session_token_issuer must be an https URL with no trailing slash."
  }
}

variable "session_token_audience" {
  type        = string
  description = "Audience (aud) of LaPluma session tokens, shared by both APIs and API Gateway"

  validation {
    condition     = length(trimspace(var.session_token_audience)) > 0
    error_message = "session_token_audience must not be empty."
  }
}

variable "session_token_jwks_uri" {
  type        = string
  description = "JWK Set the workflow API validates session tokens against. Empty means <session_token_issuer>/.well-known/jwks.json, served by the core API through API Gateway"
  default     = ""

  validation {
    condition     = var.session_token_jwks_uri == "" || can(regex("^https://", var.session_token_jwks_uri))
    error_message = "session_token_jwks_uri must be empty or an https URL."
  }
}
