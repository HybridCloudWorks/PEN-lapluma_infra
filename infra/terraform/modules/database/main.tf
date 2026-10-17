resource "random_id" "db_suffix" {
  byte_length = 4
}

resource "google_sql_database_instance" "postgres" {
  name             = "lp-postgres-${var.environment}-${random_id.db_suffix.hex}"
  database_version = "POSTGRES_16"
  region           = var.region
  depends_on       = [var.private_vpc_connection]

  settings {
    tier              = var.db_tier
    edition           = var.edition
    availability_type = var.availability_type
    disk_type         = "PD_SSD"
    disk_size         = 10
    disk_autoresize   = true

    # Private IP only, encrypted only (INF-27). With no public IPv4 address nothing outside the
    # VPC can reach the instance at all; the APIs connect over Direct VPC egress to the private
    # address (Private Service Access, modules/network). ENCRYPTED_ONLY refuses any connection that
    # is not TLS; the services also set SslMode=Require themselves.
    ip_configuration {
      ipv4_enabled    = false
      private_network = var.vpc_network_id
      ssl_mode        = "ENCRYPTED_ONLY"
    }

    backup_configuration {
      enabled                        = true
      point_in_time_recovery_enabled = true
      start_time                     = "03:00"
      transaction_log_retention_days = 7
      backup_retention_settings {
        retained_backups = 7
        retention_unit   = "COUNT"
      }
    }

    maintenance_window {
      day          = 7
      hour         = 4
      update_track = "stable"
    }

    database_flags {
      name  = "log_connections"
      value = "on"
    }
    database_flags {
      name  = "log_disconnections"
      value = "on"
    }
  }

  deletion_protection = var.environment == "pilot" ? true : false

  lifecycle {
    # Power state is not Terraform's to manage. settings sets no activation_policy, and the provider
    # then plans ALWAYS, so without this any apply would start an instance that was stopped on
    # purpose. infra/scripts/manage-environment.ps1 (pause/resume) owns it. Removing the public
    # address from an instance that is stopped follows a set order:
    # docs/runbooks/private-database-signed-uploads-and-worker-push.md.
    ignore_changes = [settings[0].activation_policy]
  }
}

resource "google_sql_database" "catalog_db" {
  name     = "lapluma_catalog"
  instance = google_sql_database_instance.postgres.name
}

resource "google_sql_database" "workflow_db" {
  name     = "lapluma_workflow"
  instance = google_sql_database_instance.postgres.name
}

resource "random_password" "app_user_password" {
  length  = 24
  special = false
}

resource "google_sql_user" "app_user" {
  name     = "lapluma_app"
  instance = google_sql_database_instance.postgres.name
  password = random_password.app_user_password.result
}

# Store database credentials securely in Secret Manager
resource "google_secret_manager_secret" "db_password_secret" {
  secret_id = "lp-db-password-${var.environment}"

  replication {
    auto {}
  }
}

resource "google_secret_manager_secret_version" "db_password_version" {
  secret      = google_secret_manager_secret.db_password_secret.id
  secret_data = random_password.app_user_password.result
}
