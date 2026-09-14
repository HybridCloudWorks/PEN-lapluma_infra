output "gateway_url" {
  value = module.gateway.gateway_url
}

output "core_api_url" {
  value = module.compute.core_api_url
}

output "workflow_api_url" {
  value = module.compute.workflow_api_url
}

output "db_connection_name" {
  value = module.database.instance_connection_name
}

output "workload_identity_provider" {
  value = module.iam.workload_identity_provider_name
}

output "artifact_registry_repository_url" {
  value = module.compute.artifact_registry_repository_url
}
