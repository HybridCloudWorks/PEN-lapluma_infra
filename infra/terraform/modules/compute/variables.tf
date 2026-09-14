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

variable "core_api_image" {
  type    = string
  default = "us-central1-docker.pkg.dev/placeholder/lapluma/core-api:latest"
}

variable "workflow_api_image" {
  type    = string
  default = "us-central1-docker.pkg.dev/placeholder/lapluma/workflow-api:latest"
}

variable "processing_worker_image" {
  type    = string
  default = "us-central1-docker.pkg.dev/placeholder/lapluma/processing-worker:latest"
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

variable "db_connection_name" {
  type = string
}
