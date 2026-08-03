targetScope = 'subscription'

@description('AZD environment name. This is a non-secret placeholder until Azure context is confirmed.')
param environmentName string

@description('Azure region approved in principle; availability and quota must still be verified.')
param location string = 'eastus2'

@description('Lowercase alphanumeric resource naming prefix. Placeholder until governance approval.')
@minLength(3)
@maxLength(12)
param resourceNamePrefix string

@description('Safety interlock. Must remain false until tenant, subscription, cost, and deployment approval are recorded.')
@allowed([
  false
])
param enableProvisioning bool = false

@description('Entra group object ID for Azure SQL administration. Placeholder only.')
param sqlEntraAdminObjectId string

@description('Display name of the Entra SQL administrator group.')
param sqlEntraAdminDisplayName string

@description('Initial Managed HSM administrator object ID. Placeholder only.')
param hsmInitialAdminObjectId string

@description('Base VNet address prefix.')
param vnetAddressPrefix string

@description('Dedicated subnet CIDR values.')
param subnetPrefixes object

@description('Required governance tags; no PII or secrets.')
param tags object

var commonTags = union(tags, {
  'azd-env-name': environmentName
  system: 'lapluma'
  release: 'lapluma-infra-0.0'
  'correlated-app-release': 'lapluma-app-0.2'
  'data-residency': 'us'
})

resource resourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = if (enableProvisioning) {
  name: 'rg-${resourceNamePrefix}-${environmentName}'
  location: location
  tags: commonTags
}

module network './modules/network.bicep' = if (enableProvisioning) {
  name: 'network-foundation'
  scope: resourceGroup
  params: {
    name: '${resourceNamePrefix}-${environmentName}'
    location: location
    tags: commonTags
    vnetAddressPrefix: vnetAddressPrefix
    subnetPrefixes: subnetPrefixes
  }
}

module observability './modules/observability.bicep' = if (enableProvisioning) {
  name: 'observability-foundation'
  scope: resourceGroup
  params: {
    name: '${resourceNamePrefix}-${environmentName}'
    location: location
    tags: commonTags
  }
}

module security './modules/security.bicep' = if (enableProvisioning) {
  name: 'security-foundation'
  scope: resourceGroup
  params: {
    name: '${resourceNamePrefix}-${environmentName}'
    location: location
    tags: commonTags
    hsmInitialAdminObjectId: hsmInitialAdminObjectId
  }
}

module messaging './modules/messaging.bicep' = if (enableProvisioning) {
  name: 'messaging-foundation'
  scope: resourceGroup
  params: {
    name: '${resourceNamePrefix}-${environmentName}'
    location: location
    tags: commonTags
  }
}

module data './modules/data.bicep' = if (enableProvisioning) {
  name: 'data-foundation'
  scope: resourceGroup
  params: {
    name: '${resourceNamePrefix}-${environmentName}'
    location: location
    tags: commonTags
    sqlEntraAdminObjectId: sqlEntraAdminObjectId
    sqlEntraAdminDisplayName: sqlEntraAdminDisplayName
  }
}

output AZURE_RESOURCE_GROUP string = enableProvisioning ? resourceGroup.name : ''
output AZURE_LOCATION string = location
output PROVISIONING_INTERLOCK string = enableProvisioning ? 'enabled' : 'blocked'
