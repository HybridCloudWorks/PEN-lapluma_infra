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

    ip_configuration {
      ipv4_enabled    = true
      private_network = var.vpc_network_id
    }

    backup_configuration {
      enabled                        = true
      point_in_time_recovery_enabled = true
      start_time                     = "03:00"
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
