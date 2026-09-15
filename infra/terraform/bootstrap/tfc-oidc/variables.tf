variable "project_id" {
  description = "The GCP project ID where the Workload Identity Pool will be created"
  type        = string
}

variable "tfc_organization_name" {
  description = "Terraform Cloud Organization Name allowed to assume credentials"
  type        = string
  default     = "hcw"
}

variable "tfc_pool_id" {
  description = "The ID of the Workload Identity Pool for Terraform Cloud"
  type        = string
  default     = "tfc-pool"
}

variable "tfc_provider_id" {
  description = "The ID of the Workload Identity Pool Provider for Terraform Cloud"
  type        = string
  default     = "tfc-provider"
}

variable "tfc_service_account_id" {
  description = "The account ID for the dedicated Terraform Cloud deployment service account"
  type        = string
  default     = "tfc-deployer"
}
