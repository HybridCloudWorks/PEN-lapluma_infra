resource "google_compute_network" "vpc" {
  name                    = "lp-vpc-${var.environment}"
  auto_create_subnetworks = false
  description             = "VPC for LaPluma ${var.environment}"
}

resource "google_compute_subnetwork" "app_subnet" {
  name                     = "lp-snet-${var.environment}"
  ip_cidr_range            = var.subnet_cidr
  region                   = var.region
  network                  = google_compute_network.vpc.id
  private_ip_google_access = true
}

# Private Service Access for Cloud SQL (Private IP)
resource "google_compute_global_address" "private_ip_alloc" {
  name          = "lp-sql-range-${var.environment}"
  purpose       = "VPC_PEERING"
  address_type  = "INTERNAL"
  prefix_length = 20
  network       = google_compute_network.vpc.id
}

resource "google_service_networking_connection" "private_vpc_connection" {
  network                 = google_compute_network.vpc.id
  service                 = "servicenetworking.googleapis.com"
  reserved_peering_ranges = [google_compute_global_address.private_ip_alloc.name]
}
