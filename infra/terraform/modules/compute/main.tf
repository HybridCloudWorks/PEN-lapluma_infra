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
        name  = "CLOUD_SQL_CONNECTION_NAME"
        value = var.db_connection_name
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
        name  = "CLOUD_SQL_CONNECTION_NAME"
        value = var.db_connection_name
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
