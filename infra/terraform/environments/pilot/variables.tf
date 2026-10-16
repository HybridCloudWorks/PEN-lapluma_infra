variable "project_id" {
  description = "GCP Project ID for pilot"
  type        = string
}

variable "region" {
  description = "Deployment region"
  type        = string
  default     = "us-central1"
}

variable "environment" {
  description = "Environment name"
  type        = string
  default     = "pilot"
}

variable "github_repository" {
  description = "GitHub repository for Workload Identity Federation"
  type        = string
  default     = "HybridCloudWorks/PEN-lapluma_infra"
}

variable "session_token_issuer" {
  description = "Issuer of LaPluma session tokens and this environment's public API base URL: https://<API Gateway hostname> (terraform output gateway_url) or the custom domain in front of it, no trailing slash"
  type        = string
}

variable "session_token_audience" {
  description = "Audience of LaPluma session tokens, shared by both APIs and API Gateway"
  type        = string
  default     = "lapluma-api-pilot"
}

variable "session_token_jwks_uri" {
  description = "Optional override for the session-token JWK Set. Empty means <session_token_issuer>/.well-known/jwks.json"
  type        = string
  default     = ""
}
