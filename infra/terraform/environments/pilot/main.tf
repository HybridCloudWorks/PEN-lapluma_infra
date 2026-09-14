terraform {
  required_version = ">= 1.5.0"
  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 6.0"
    }
    google-beta = {
      source  = "hashicorp/google-beta"
      version = "~> 6.0"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "google" {
  project = var.project_id
  region  = var.region
}

provider "google-beta" {
  project = var.project_id
  region  = var.region
}

module "iam" {
  source            = "../../modules/iam"
  project_id        = var.project_id
  environment       = var.environment
  github_repository = var.github_repository
}

module "network" {
  source      = "../../modules/network"
  project_id  = var.project_id
  environment = var.environment
  region      = var.region
}

module "storage" {
  source                     = "../../modules/storage"
  project_id                 = var.project_id
  environment                = var.environment
  region                     = var.region
  core_api_sa_email          = module.iam.core_api_sa_email
  workflow_api_sa_email      = module.iam.workflow_api_sa_email
  processing_worker_sa_email = module.iam.processing_worker_sa_email
  acquisition_sa_email       = module.iam.acquisition_sa_email
}

module "database" {
  source                 = "../../modules/database"
  project_id             = var.project_id
  environment            = var.environment
  region                 = var.region
  vpc_network_id         = module.network.vpc_id
  private_vpc_connection = module.network.private_vpc_connection
  db_tier                = "db-g1-small"
  availability_type      = "ZONAL"
}

module "messaging" {
  source      = "../../modules/messaging"
  environment = var.environment
}

module "compute" {
  source                     = "../../modules/compute"
  project_id                 = var.project_id
  environment                = var.environment
  region                     = var.region
  core_api_sa_email          = module.iam.core_api_sa_email
  workflow_api_sa_email      = module.iam.workflow_api_sa_email
  processing_worker_sa_email = module.iam.processing_worker_sa_email
  db_connection_name         = module.database.instance_connection_name
}

module "gateway" {
  source           = "../../modules/gateway"
  project_id       = var.project_id
  environment      = var.environment
  region           = var.region
  core_api_url     = module.compute.core_api_url
  workflow_api_url = module.compute.workflow_api_url
}
