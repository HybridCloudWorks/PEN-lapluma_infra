variable "project_name" {
  description = "Display name of the GCP project to create"
  type        = string
  default     = "LaPluma Dev"
}

variable "project_id" {
  description = "The globally unique ID of the GCP project to create"
  type        = string
  default     = "lapluma-dev-gcp"
}

variable "org_id" {
  description = "The Google Cloud Organization ID under which the project will be created"
  type        = string
  default     = "42574862375"
}

variable "billing_account_id" {
  description = "The Google Cloud Billing Account ID to link to the project"
  type        = string
  default     = "01AEE9-772C7A-9720CC"
}

variable "environment" {
  description = "Target environment: dev, staging, or pilot"
  type        = string
  default     = "dev"
}

variable "region" {
  description = "Primary GCP region for regional resources like Artifact Registry"
  type        = string
  default     = "us-central1"
}

variable "github_repository" {
  description = "Full GitHub repository name allowed to authenticate via GitHub Actions OIDC"
  type        = string
  default     = "HybridCloudWorks/PEN-lapluma_infra"
}

variable "tfc_organization_name" {
  description = "HCP Terraform Cloud Organization name allowed to authenticate via TFC OIDC"
  type        = string
  default     = "hcw"
}
