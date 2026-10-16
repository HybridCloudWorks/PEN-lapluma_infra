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

variable "github_repository_id" {
  description = "Immutable numeric ID of github_repository (gh api repos/<owner>/<repo> --jq .id)"
  type        = string
  default     = "1321114423"

  validation {
    condition     = can(regex("^[0-9]+$", var.github_repository_id))
    error_message = "github_repository_id must be the repository's numeric ID."
  }
}

variable "github_repository_owner_id" {
  description = "Immutable numeric ID of the repository owner (gh api repos/<owner>/<repo> --jq .owner.id)"
  type        = string
  default     = "312844660"

  validation {
    condition     = can(regex("^[0-9]+$", var.github_repository_owner_id))
    error_message = "github_repository_owner_id must be the owner's numeric ID."
  }
}
