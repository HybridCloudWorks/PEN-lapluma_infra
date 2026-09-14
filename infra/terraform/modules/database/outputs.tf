output "instance_connection_name" {
  value = google_sql_database_instance.postgres.connection_name
}

output "instance_private_ip" {
  value = google_sql_database_instance.postgres.private_ip_address
}

output "database_user" {
  value = google_sql_user.app_user.name
}

output "catalog_db_name" {
  value = google_sql_database.catalog_db.name
}

output "workflow_db_name" {
  value = google_sql_database.workflow_db.name
}

output "db_password_secret_id" {
  value = google_secret_manager_secret.db_password_secret.secret_id
}
