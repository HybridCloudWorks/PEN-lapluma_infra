output "vpc_id" {
  value = google_compute_network.vpc.id
}

output "vpc_name" {
  value = google_compute_network.vpc.name
}

output "subnet_id" {
  value = google_compute_subnetwork.app_subnet.id
}

output "subnet_name" {
  value = google_compute_subnetwork.app_subnet.name
}

output "private_vpc_connection" {
  value = google_service_networking_connection.private_vpc_connection
}
