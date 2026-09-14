variable "project_id" {
  description = "GCP Project ID for dev"
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
  default     = "dev"
}

variable "github_repository" {
  description = "GitHub repository for Workload Identity Federation"
  type        = string
  default     = "HybridCloudWorks/PEN-lapluma_infra"
}

variable "oidc_issuer" {
  description = "Application OIDC issuer URL"
  type        = string
  default     = "https://login.microsoftonline.com/common/v2.0"
}

variable "oidc_audience" {
  description = "Application OIDC audience client ID"
  type        = string
  default     = "api://lapluma-workforce-pilot"
}
