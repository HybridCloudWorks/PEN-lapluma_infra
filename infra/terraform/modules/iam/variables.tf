variable "project_id" {
  description = "The GCP Project ID"
  type        = string
}

variable "environment" {
  description = "Deployment environment name (dev, staging, pilot)"
  type        = string
}

variable "github_repository" {
  description = "The GitHub repository authorized for deployment federation"
  type        = string
  default     = "HybridCloudWorks/PEN-lapluma_infra"
}
