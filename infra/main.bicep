targetScope = 'subscription'

// Every parameter below is environment-substituted by AZD. An unset variable substitutes to the
// empty string, which ARM treats as a supplied value — so an empty string silently overrides a
// default rather than falling back to it. @minLength(1) is what turns that into a parameter error
// at submission, before anything deploys.

@description('The five zone subnets. A typed object turns a misspelled key into a parameter error rather than a failure deep inside the network module.')
type SubnetPrefixes = {
  @minLength(1)
  core: string

  @minLength(1)
  processing: string

  @minLength(1)
  ai: string

  @minLength(1)
  functions: string

  @minLength(1)
  privateEndpoints: string
}

@description('AZD environment name. Lowercase; it is embedded in globally unique resource names, and Azure SQL server names permit only lowercase letters, digits, and hyphens.')
@minLength(2)
@maxLength(20)
param environmentName string

@description('Azure region approved in principle; availability and quota must still be verified.')
@minLength(1)
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
@minLength(36)
@maxLength(36)
param sqlEntraAdminObjectId string

@description('Display name of the Entra SQL administrator group.')
@minLength(1)
param sqlEntraAdminDisplayName string

@description('Initial Managed HSM administrator object ID. Placeholder only.')
@minLength(36)
@maxLength(36)
param hsmInitialAdminObjectId string

@description('Base VNet address prefix.')
@minLength(1)
param vnetAddressPrefix string

@description('Dedicated subnet CIDR values.')
param subnetPrefixes SubnetPrefixes

@description('Required governance tags; no PII or secrets.')
param tags object

// Composed once and lowercased here: environmentName flows into globally unique names, and Azure
// SQL rejects uppercase. Catching it at composition beats failing after the network has deployed.
var environmentSuffix = toLower(environmentName)
var resourceBaseName = '${resourceNamePrefix}-${environmentSuffix}'

var commonTags = union(tags, {
  'azd-env-name': environmentName
  system: 'lapluma'
  release: 'lapluma-infra-0.0'
  'correlated-app-release': 'lapluma-app-0.2'
  'data-residency': 'us'
})

resource targetResourceGroup 'Microsoft.Resources/resourceGroups@2024-03-01' = if (enableProvisioning) {
  name: 'rg-${resourceBaseName}'
  location: location
  tags: commonTags
}

module network './modules/network.bicep' = if (enableProvisioning) {
  name: 'network-foundation'
  scope: targetResourceGroup
  params: {
    name: resourceBaseName
    location: location
    tags: commonTags
    vnetAddressPrefix: vnetAddressPrefix
    subnetPrefixes: subnetPrefixes
  }
}

module observability './modules/observability.bicep' = if (enableProvisioning) {
  name: 'observability-foundation'
  scope: targetResourceGroup
  params: {
    name: resourceBaseName
    location: location
    tags: commonTags
  }
}

module security './modules/security.bicep' = if (enableProvisioning) {
  name: 'security-foundation'
  scope: targetResourceGroup
  params: {
    name: resourceBaseName
    location: location
    tags: commonTags
    hsmInitialAdminObjectId: hsmInitialAdminObjectId
  }
}

module messaging './modules/messaging.bicep' = if (enableProvisioning) {
  name: 'messaging-foundation'
  scope: targetResourceGroup
  params: {
    name: resourceBaseName
    location: location
    tags: commonTags
  }
}

module data './modules/data.bicep' = if (enableProvisioning) {
  name: 'data-foundation'
  scope: targetResourceGroup
  params: {
    name: resourceBaseName
    location: location
    tags: commonTags
    sqlEntraAdminObjectId: sqlEntraAdminObjectId
    sqlEntraAdminDisplayName: sqlEntraAdminDisplayName
  }
}

output AZURE_RESOURCE_GROUP string = enableProvisioning ? targetResourceGroup.name : ''
output AZURE_LOCATION string = location
output PROVISIONING_INTERLOCK string = enableProvisioning ? 'enabled' : 'blocked'
