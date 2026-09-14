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

variable "core_api_url" {
  type = string
}

variable "workflow_api_url" {
  type = string
}

variable "oidc_issuer" {
  type        = string
  description = "Application OIDC issuer URL for gateway edge authentication"
  default     = "https://login.microsoftonline.com/common/v2.0"
}

variable "oidc_audience" {
  type        = string
  description = "Application OIDC audience client ID for gateway edge authentication"
  default     = "api://lapluma-workforce-pilot"
}

variable "gateway_sa_email" {
  type        = string
  description = "Service account email assumed by API Gateway to invoke Cloud Run backends"
  default     = ""
}
