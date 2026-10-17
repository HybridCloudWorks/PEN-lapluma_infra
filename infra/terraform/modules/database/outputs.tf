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

# Connection strings for the two databases: the private address, TLS required (INF-27). Nothing
# reads them yet, because the services still serve fixtures (INF-30); whatever wires
# Database__ConnectionString starts from these, never from a public address.
output "catalog_connection_string" {
  value     = "Host=${google_sql_database_instance.postgres.private_ip_address};Port=5432;Database=${google_sql_database.catalog_db.name};Username=${google_sql_user.app_user.name};Password=${random_password.app_user_password.result};SslMode=Require"
  sensitive = true
}

output "workflow_connection_string" {
  value     = "Host=${google_sql_database_instance.postgres.private_ip_address};Port=5432;Database=${google_sql_database.workflow_db.name};Username=${google_sql_user.app_user.name};Password=${random_password.app_user_password.result};SslMode=Require"
  sensitive = true
}
