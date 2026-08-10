targetScope = 'subscription'

// ---------------------------------------------------------------------------------------------
// Baselines
//
// AZD substitutes environment variables into infra/main.parameters.json textually, and that file
// must stay valid JSON — tools/validate_foundation.py parses it. Every value arriving that way is
// therefore a JSON string, whatever it represents. These parameters are declared as strings for
// that reason and converted exactly once, below, before any module sees them; the modules take
// properly typed parameters carrying range and allowed-value constraints, so an out-of-range value
// fails deployment validation naming the parameter rather than producing a surprising resource.
//
// Each group is named for the decision that gates its values, not for the module that consumes
// them, so it is obvious what unblocks a change. Defaults reproduce exactly the literals these
// replaced: this change makes the baselines adjustable, it does not adjust them.
// ---------------------------------------------------------------------------------------------

@description('Retention windows in days. Values pending REVIEW.md R-11.')
type RetentionBaseline = {
  @minLength(1)
  logAnalyticsDays: string

  @minLength(1)
  blobSoftDeleteDays: string

  @minLength(1)
  containerSoftDeleteDays: string

  @minLength(1)
  keyVaultSoftDeleteDays: string

  @minLength(1)
  hsmSoftDeleteDays: string
}

@description('Sizing and throughput. Values pending REVIEW.md R-03.')
type CapacityBaseline = {
  @minLength(1)
  sqlSkuName: string

  @minLength(1)
  sqlSkuCapacity: string

  @minLength(1)
  sqlMinCapacity: string

  @minLength(1)
  sqlAutoPauseMinutes: string

  @minLength(1)
  cosmosMaxThroughput: string

  @minLength(1)
  serviceBusCapacity: string

  @minLength(1)
  serviceBusPartitions: string

  // A union rather than a bare string: the module constrains this with @allowed, and mirroring the
  // constraint here keeps the boundary type-checked instead of deferring to deployment.
  hsmSkuName: 'Standard_B1' | 'Custom_B32'
}

@description('Redundancy. Values pending TODO 3.2 and an agreed SLO.')
type ResilienceBaseline = {
  @minLength(1)
  sqlZoneRedundant: string

  @minLength(1)
  cosmosZoneRedundant: string

  auditStorageSku: StorageRedundancy

  defaultStorageSku: StorageRedundancy
}

type StorageRedundancy = 'Standard_LRS' | 'Standard_ZRS' | 'Standard_GRS' | 'Standard_GZRS'

@description('Message handling windows. TTLs pending REVIEW.md R-11.')
type MessagingBaseline = {
  @minLength(1)
  duplicateDetectionWindow: string

  @minLength(1)
  queueMessageTimeToLive: string

  @minLength(1)
  queueLockDuration: string

  @minLength(1)
  queueMaxDeliveryCount: string

  @minLength(1)
  topicMessageTimeToLive: string
}


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

@description('Retention windows, in days.')
param retention RetentionBaseline = {
  logAnalyticsDays: '365'
  blobSoftDeleteDays: '7'
  containerSoftDeleteDays: '7'
  keyVaultSoftDeleteDays: '90'
  hsmSoftDeleteDays: '90'
}

@description('Sizing and throughput.')
param capacity CapacityBaseline = {
  sqlSkuName: 'GP_S_Gen5'
  sqlSkuCapacity: '2'
  sqlMinCapacity: '0.5'
  sqlAutoPauseMinutes: '60'
  cosmosMaxThroughput: '1000'
  serviceBusCapacity: '1'
  serviceBusPartitions: '1'
  hsmSkuName: 'Standard_B1'
}

@description('Redundancy.')
param resilience ResilienceBaseline = {
  sqlZoneRedundant: 'false'
  cosmosZoneRedundant: 'false'
  auditStorageSku: 'Standard_ZRS'
  defaultStorageSku: 'Standard_LRS'
}

@description('Message handling windows.')
param messagingBaseline MessagingBaseline = {
  duplicateDetectionWindow: 'PT1H'
  queueMessageTimeToLive: 'P7D'
  queueLockDuration: 'PT5M'
  queueMaxDeliveryCount: '5'
  topicMessageTimeToLive: 'P14D'
}

// Composed once and lowercased here: environmentName flows into globally unique names, and Azure
// SQL rejects uppercase. Catching it at composition beats failing after the network has deployed.
var environmentSuffix = toLower(environmentName)
var resourceBaseName = '${resourceNamePrefix}-${environmentSuffix}'

// The single conversion point. Everything below this line is typed.
var retentionDays = {
  logAnalytics: int(retention.logAnalyticsDays)
  blobSoftDelete: int(retention.blobSoftDeleteDays)
  containerSoftDelete: int(retention.containerSoftDeleteDays)
  keyVaultSoftDelete: int(retention.keyVaultSoftDeleteDays)
  hsmSoftDelete: int(retention.hsmSoftDeleteDays)
}
var sizing = {
  sqlSkuCapacity: int(capacity.sqlSkuCapacity)
  sqlAutoPauseMinutes: int(capacity.sqlAutoPauseMinutes)
  cosmosMaxThroughput: int(capacity.cosmosMaxThroughput)
  serviceBusCapacity: int(capacity.serviceBusCapacity)
  serviceBusPartitions: int(capacity.serviceBusPartitions)
}
var redundancy = {
  sqlZoneRedundant: bool(resilience.sqlZoneRedundant)
  cosmosZoneRedundant: bool(resilience.cosmosZoneRedundant)
}
var queueMaxDeliveryCount = int(messagingBaseline.queueMaxDeliveryCount)

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
    logAnalyticsRetentionDays: retentionDays.logAnalytics
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
    keyVaultSoftDeleteRetentionDays: retentionDays.keyVaultSoftDelete
    hsmSoftDeleteRetentionDays: retentionDays.hsmSoftDelete
    hsmSkuName: capacity.hsmSkuName
  }
}

module messaging './modules/messaging.bicep' = if (enableProvisioning) {
  name: 'messaging-foundation'
  scope: targetResourceGroup
  params: {
    name: resourceBaseName
    location: location
    tags: commonTags
    serviceBusCapacity: sizing.serviceBusCapacity
    serviceBusPartitions: sizing.serviceBusPartitions
    duplicateDetectionWindow: messagingBaseline.duplicateDetectionWindow
    queueMessageTimeToLive: messagingBaseline.queueMessageTimeToLive
    queueLockDuration: messagingBaseline.queueLockDuration
    queueMaxDeliveryCount: queueMaxDeliveryCount
    topicMessageTimeToLive: messagingBaseline.topicMessageTimeToLive
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
    blobSoftDeleteRetentionDays: retentionDays.blobSoftDelete
    containerSoftDeleteRetentionDays: retentionDays.containerSoftDelete
    sqlSkuName: capacity.sqlSkuName
    sqlSkuCapacity: sizing.sqlSkuCapacity
    sqlAutoPauseDelayMinutes: sizing.sqlAutoPauseMinutes
    sqlMinCapacity: capacity.sqlMinCapacity
    sqlZoneRedundant: redundancy.sqlZoneRedundant
    cosmosMaxThroughput: sizing.cosmosMaxThroughput
    cosmosZoneRedundant: redundancy.cosmosZoneRedundant
    auditStorageSku: resilience.auditStorageSku
    defaultStorageSku: resilience.defaultStorageSku
  }
}

output AZURE_RESOURCE_GROUP string = enableProvisioning ? targetResourceGroup.name : ''
output AZURE_LOCATION string = location
output PROVISIONING_INTERLOCK string = enableProvisioning ? 'enabled' : 'blocked'
