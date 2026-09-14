variable "project_id" {
  description = "GCP Project ID for staging"
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
  default     = "staging"
}

variable "github_repository" {
  description = "GitHub repository for Workload Identity Federation"
  type        = string
  default     = "HybridCloudWorks/PEN-lapluma_infra"
}
