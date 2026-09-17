# Artifact Registry Repository for Service Images
resource "google_artifact_registry_repository" "services" {
  repository_id = "lapluma-services-${var.environment}"
  format        = "DOCKER"
  location      = var.region
  description   = "Container image repository for LaPluma Cloud Run microservices (${var.environment})"

  docker_config {
    immutable_tags = var.environment == "pilot" ? true : false
  }
}

# Core API Service
resource "google_cloud_run_v2_service" "core_api" {
  name     = "lp-core-api-${var.environment}"
  location = var.region
  ingress  = "INGRESS_TRAFFIC_ALL"

  template {
    service_account = var.core_api_sa_email

    scaling {
      min_instance_count = 0
      max_instance_count = var.environment == "pilot" ? 5 : 2
    }

    containers {
      image = var.core_api_image

      resources {
        limits = {
          cpu    = "1000m"
          memory = "512Mi"
        }
        cpu_idle = true
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = var.environment == "pilot" ? "Production" : "Development"
      }
      env {
        name  = "Catalog__Source"
        value = "fixture"
      }
      env {
        name  = "CLOUD_SQL_CONNECTION_NAME"
        value = var.db_connection_name
      }

      startup_probe {
        initial_delay_seconds = 0
        timeout_seconds       = 3
        period_seconds        = 10
        failure_threshold     = 3
        http_get {
          path = "/ready"
          port = 8080
        }
      }

      liveness_probe {
        initial_delay_seconds = 5
        timeout_seconds       = 2
        period_seconds        = 15
        failure_threshold     = 3
        http_get {
          path = "/health"
          port = 8080
        }
      }
    }
  }
}

# Workflow API Service
resource "google_cloud_run_v2_service" "workflow_api" {
  name     = "lp-wf-api-${var.environment}"
  location = var.region
  ingress  = "INGRESS_TRAFFIC_ALL"

  template {
    service_account = var.workflow_api_sa_email

    scaling {
      min_instance_count = 0
      max_instance_count = var.environment == "pilot" ? 5 : 2
    }

    containers {
      image = var.workflow_api_image

      resources {
        limits = {
          cpu    = "1000m"
          memory = "512Mi"
        }
        cpu_idle = true
      }

      env {
        name  = "ASPNETCORE_ENVIRONMENT"
        value = var.environment == "pilot" ? "Production" : "Development"
      }
      env {
        name  = "Workflow__Source"
        value = "fixture"
      }
      env {
        name  = "CLOUD_SQL_CONNECTION_NAME"
        value = var.db_connection_name
      }

      startup_probe {
        initial_delay_seconds = 0
        timeout_seconds       = 3
        period_seconds        = 10
        failure_threshold     = 3
        http_get {
          path = "/ready"
          port = 8080
        }
      }

      liveness_probe {
        initial_delay_seconds = 5
        timeout_seconds       = 2
        period_seconds        = 15
        failure_threshold     = 3
        http_get {
          path = "/health"
          port = 8080
        }
      }
    }
  }
}

# Processing Worker Service (Internal ingress only)
resource "google_cloud_run_v2_service" "processing_worker" {
  name     = "lp-proc-worker-${var.environment}"
  location = var.region
  ingress  = "INGRESS_TRAFFIC_INTERNAL_ONLY"

  template {
    service_account = var.processing_worker_sa_email

    scaling {
      min_instance_count = 0
      max_instance_count = var.environment == "pilot" ? 5 : 2
    }

    containers {
      image = var.processing_worker_image

      resources {
        limits = {
          cpu    = "2000m"
          memory = "1024Mi"
        }
        cpu_idle = true
      }

      startup_probe {
        initial_delay_seconds = 0
        timeout_seconds       = 3
        period_seconds        = 10
        failure_threshold     = 3
        http_get {
          path = "/ready"
          port = 8080
        }
      }

      liveness_probe {
        initial_delay_seconds = 5
        timeout_seconds       = 2
        period_seconds        = 15
        failure_threshold     = 3
        http_get {
          path = "/health"
          port = 8080
        }
      }
    }
  }
}

# ------------------------------------------------------------------------------
# IAM Access Restrictions (INT-02)
# Direct public invocation is denied; only API Gateway SA holds run.invoker
# ------------------------------------------------------------------------------
resource "google_cloud_run_v2_service_iam_member" "core_api_gateway_invoker" {
  count    = var.gateway_sa_email != "" ? 1 : 0
  project  = google_cloud_run_v2_service.core_api.project
  location = google_cloud_run_v2_service.core_api.location
  name     = google_cloud_run_v2_service.core_api.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${var.gateway_sa_email}"
}

resource "google_cloud_run_v2_service_iam_member" "workflow_api_gateway_invoker" {
  count    = var.gateway_sa_email != "" ? 1 : 0
  project  = google_cloud_run_v2_service.workflow_api.project
  location = google_cloud_run_v2_service.workflow_api.location
  name     = google_cloud_run_v2_service.workflow_api.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${var.gateway_sa_email}"
}

resource "google_cloud_run_v2_service_iam_member" "processing_worker_pubsub_invoker" {
  count    = var.pubsub_invoker_sa_email != "" ? 1 : 0
  project  = google_cloud_run_v2_service.processing_worker.project
  location = google_cloud_run_v2_service.processing_worker.location
  name     = google_cloud_run_v2_service.processing_worker.name
  role     = "roles/run.invoker"
  member   = "serviceAccount:${var.pubsub_invoker_sa_email}"
}

# ------------------------------------------------------------------------------
# Cloud Run Job: Database Migrations (PostgreSQL 16 under ADR-019)
# Executed on-demand or during deployment pipelines. Scale-to-zero compute footprint.
# ------------------------------------------------------------------------------
resource "google_cloud_run_v2_job" "db_migration" {
  name     = "lp-db-migration-${var.environment}"
  location = var.region

  template {
    template {
      service_account = var.core_api_sa_email

      volumes {
        name = "cloudsql"
        cloud_sql_instance {
          instances = [var.db_connection_name]
        }
      }

      containers {
        image = var.migration_image

        resources {
          limits = {
            cpu    = "1000m"
            memory = "512Mi"
          }
        }

        volume_mounts {
          name       = "cloudsql"
          mount_path = "/cloudsql"
        }

        env {
          name  = "CLOUD_SQL_CONNECTION_NAME"
          value = var.db_connection_name
        }
        env {
          name  = "POSTGRES_DB_CATALOG"
          value = "lapluma_catalog"
        }
        env {
          name  = "POSTGRES_DB_WORKFLOW"
          value = "lapluma_workflow"
        }
        env {
          name  = "ENVIRONMENT"
          value = var.environment
        }
      }
    }
  }
}
