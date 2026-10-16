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

variable "session_token_issuer" {
  type        = string
  description = "Issuer (iss) of LaPluma session tokens, exactly as the core API's Authentication__Issuer: this environment's public API base URL, with no trailing slash"

  validation {
    condition     = can(regex("^https://", var.session_token_issuer)) && !endswith(var.session_token_issuer, "/")
    error_message = "session_token_issuer must be an https URL with no trailing slash."
  }
}

variable "session_token_audience" {
  type        = string
  description = "Audience (aud) of LaPluma session tokens, exactly as Authentication__Audience on both APIs"

  validation {
    condition     = length(trimspace(var.session_token_audience)) > 0
    error_message = "session_token_audience must not be empty."
  }
}

variable "session_token_jwks_uri" {
  type        = string
  description = "JWK Set the gateway validates session tokens against. Empty means <session_token_issuer>/.well-known/jwks.json, which this gateway routes anonymously to the core API"
  default     = ""

  validation {
    condition     = var.session_token_jwks_uri == "" || can(regex("^https://", var.session_token_jwks_uri))
    error_message = "session_token_jwks_uri must be empty or an https URL."
  }
}

variable "gateway_sa_email" {
  type        = string
  description = "Service account email assumed by API Gateway to invoke Cloud Run backends"
  default     = ""
}
