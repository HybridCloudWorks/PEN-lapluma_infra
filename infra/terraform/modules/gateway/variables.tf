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
