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

variable "vpc_network_id" {
  type = string
}

variable "private_vpc_connection" {
  description = "Dependency hook for VPC peering"
  type        = any
}

variable "db_tier" {
  description = "Database machine type (defaults to cost-optimized db-g1-small for pilot)"
  type        = string
  default     = "db-g1-small"
}

variable "availability_type" {
  description = "ZONAL for pilot/dev, REGIONAL for growth HA"
  type        = string
  default     = "ZONAL"
}

variable "edition" {
  description = "Cloud SQL edition: ENTERPRISE or ENTERPRISE_PLUS"
  type        = string
  default     = "ENTERPRISE"
}
