locals {
  # The session-token issuer doubles as the public base URL clients reach the API on, so the SAML
  # service provider's entity ID and assertion consumer URLs are derived from it and cannot drift.
  public_base_url        = var.session_token_issuer
  session_token_jwks_uri = var.session_token_jwks_uri != "" ? var.session_token_jwks_uri : "${local.public_base_url}/.well-known/jwks.json"

  # Created and populated outside Terraform (see docs/runbooks/session-tokens-and-saml-identity-providers.md)
  # so key material never enters Terraform state. Apply fails while either secret is missing.
  session_signing_key_secret_id     = "lp-session-signing-key-${var.environment}"
  saml_identity_providers_secret_id = "lp-saml-idps-${var.environment}"
}

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

  # The revision cannot read its secrets until the accessor bindings exist.
  depends_on = [
    google_secret_manager_secret_iam_member.core_api_session_signing_key,
    google_secret_manager_secret_iam_member.core_api_saml_identity_providers,
  ]

  template {
    service_account = var.core_api_sa_email

    scaling {
      min_instance_count = 0
      max_instance_count = var.environment == "pilot" ? 5 : 2
    }

    # Direct VPC egress to reach Cloud SQL on its private address (INF-27). Only private ranges
    # go through the VPC; Google APIs and the session-token key set are reached as before.
    vpc_access {
      network_interfaces {
        network    = var.vpc_network
        subnetwork = var.vpc_subnetwork
      }
      egress = "PRIVATE_RANGES_ONLY"
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
        name  = "Authentication__Audience"
        value = var.session_token_audience
      }
      env {
        name  = "Authentication__Issuer"
        value = var.session_token_issuer
      }
      env {
        name = "SessionTokens__SigningKeyPem"
        value_source {
          secret_key_ref {
            secret  = local.session_signing_key_secret_id
            version = "latest"
          }
        }
      }
      env {
        name  = "Saml__SpEntityId"
        value = "${local.public_base_url}/auth/saml/metadata"
      }
      env {
        name  = "Saml__SpAcsBaseUrl"
        value = "${local.public_base_url}/auth/saml/acs"
      }
      env {
        name = "Saml__IdentityProvidersJson"
        value_source {
          secret_key_ref {
            secret  = local.saml_identity_providers_secret_id
            version = "latest"
          }
        }
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

    # Direct VPC egress to reach Cloud SQL on its private address (INF-27). Only private ranges
    # go through the VPC; Google APIs and the session-token key set are reached as before.
    vpc_access {
      network_interfaces {
        network    = var.vpc_network
        subnetwork = var.vpc_subnetwork
      }
      egress = "PRIVATE_RANGES_ONLY"
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
        name  = "Authentication__Audience"
        value = var.session_token_audience
      }
      env {
        name  = "Authentication__Issuer"
        value = var.session_token_issuer
      }
      env {
        name  = "Authentication__JwksUri"
        value = local.session_token_jwks_uri
      }
      env {
        name  = "Workflow__Source"
        value = "fixture"
      }
      env {
        name  = "CLOUD_SQL_CONNECTION_NAME"
        value = var.db_connection_name
      }

      # Upload sessions mint V4 signed PUT URLs for the quarantine bucket, signed by this
      # service's own account through IAM signBlob (INF-25). Without a bucket, upload issuing
      # stays fail-closed (503).
      dynamic "env" {
        for_each = var.quarantine_bucket_name != "" ? {
          Workflow__GcsQuarantineBucket     = var.quarantine_bucket_name
          Workflow__GcsSignerServiceAccount = var.workflow_api_sa_email
        } : {}
        content {
          name  = env.key
          value = env.value
        }
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
#
# Pub/Sub push reaches it without opening ingress (INF-28): Cloud Run treats a push subscription in
# the same project, delivering to the service's default run.app URL, as an internal source. The
# subscription (modules/messaging) authenticates with an OIDC token for lp-ps-invoker-<env>, the
# only principal granted run.invoker below; Cloud Run checks that token's signature, expiry and
# audience (the service URL) before the worker sees the request. Nothing on the internet can reach
# the service, authenticated or not. No VPC access: the worker never reaches the database.
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

# ------------------------------------------------------------------------------
# Session-token signing key and SAML identity providers (INF-21, INF-22)
# Only the core API reads them. The workflow API validates tokens against the public
# key set and never holds the private key.
# ------------------------------------------------------------------------------
resource "google_secret_manager_secret_iam_member" "core_api_session_signing_key" {
  project   = var.project_id
  secret_id = local.session_signing_key_secret_id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${var.core_api_sa_email}"
}

resource "google_secret_manager_secret_iam_member" "core_api_saml_identity_providers" {
  project   = var.project_id
  secret_id = local.saml_identity_providers_secret_id
  role      = "roles/secretmanager.secretAccessor"
  member    = "serviceAccount:${var.core_api_sa_email}"
}

# The push subscription's OIDC identity is the worker's only invoker.
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
# Connects over Direct VPC egress to the instance's private address with TLS required (INF-27).
# The built-in Cloud SQL socket (/cloudsql) needs a public address, which the instance no longer
# has, so it is gone.
# ------------------------------------------------------------------------------
resource "google_cloud_run_v2_job" "db_migration" {
  name     = "lp-db-migration-${var.environment}"
  location = var.region

  template {
    template {
      service_account = var.core_api_sa_email

      vpc_access {
        network_interfaces {
          network    = var.vpc_network
          subnetwork = var.vpc_subnetwork
        }
        egress = "PRIVATE_RANGES_ONLY"
      }

      containers {
        image = var.migration_image

        resources {
          limits = {
            cpu    = "1000m"
            memory = "512Mi"
          }
        }

        env {
          name  = "CLOUD_SQL_CONNECTION_NAME"
          value = var.db_connection_name
        }
        env {
          name  = "PGHOST"
          value = var.db_private_ip
        }
        env {
          name  = "PGPORT"
          value = "5432"
        }
        env {
          name  = "PGSSLMODE"
          value = "require"
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
