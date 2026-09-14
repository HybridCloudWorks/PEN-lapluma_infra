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

variable "core_api_sa_email" {
  type = string
}

variable "workflow_api_sa_email" {
  type = string
}

variable "processing_worker_sa_email" {
  type = string
}

variable "acquisition_sa_email" {
  type = string
}
