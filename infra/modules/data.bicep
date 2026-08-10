targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}
param sqlEntraAdminObjectId string
param sqlEntraAdminDisplayName string

@description('Blob soft-delete window. Pending REVIEW.md R-11. Versioning is not a parameter.')
@minValue(1)
@maxValue(365)
param blobSoftDeleteRetentionDays int = 7

@description('Container soft-delete window. Pending REVIEW.md R-11.')
@minValue(1)
@maxValue(365)
param containerSoftDeleteRetentionDays int = 7

@description('Azure SQL SKU name. Pending REVIEW.md R-03.')
@minLength(1)
param sqlSkuName string = 'GP_S_Gen5'

@description('Serverless maximum vCores. Pending REVIEW.md R-03.')
@minValue(1)
param sqlSkuCapacity int = 2

@description('Serverless auto-pause delay in minutes; -1 disables it. Pending TODO 3.2.')
@minValue(-1)
param sqlAutoPauseDelayMinutes int = 60

@description('Serverless minimum vCores. A decimal, so it stays a string and is read through json().')
@minLength(1)
param sqlMinCapacity string = '0.5'

@description('SQL zone redundancy. Pending TODO 3.2 and an agreed SLO.')
param sqlZoneRedundant bool = false

@description('Cosmos autoscale ceiling in RU/s. Pending REVIEW.md R-03.')
@minValue(1000)
@maxValue(1000000)
param cosmosMaxThroughput int = 1000

@description('Cosmos zone redundancy. Pending TODO 3.2 and an agreed SLO.')
param cosmosZoneRedundant bool = false

@description('Redundancy for the audit account, which is the one with a retention obligation.')
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
  'Standard_GZRS'
])
param auditStorageSku string = 'Standard_ZRS'

@description('Redundancy for the quarantine, documents, and packages accounts. Pending TODO 3.2.')
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
  'Standard_GZRS'
])
param defaultStorageSku string = 'Standard_LRS'

var suffix = take(uniqueString(subscription().id, resourceGroup().id, name), 6)
// Globally scoped names use only a fixed safe stem plus uniqueString output. The reviewed
// environment name still participates in the suffix without leaking unsafe characters.
var compactName = 'lapluma'

// GA API versions throughout. A preview version can change shape or be withdrawn between
// deployments, which turns an unrelated redeploy into a failed one.
resource sqlServer 'Microsoft.Sql/servers@2023-08-01' = {
  name: 'sql-${name}-${suffix}'
  location: location
  tags: tags
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: sqlEntraAdminDisplayName
      sid: sqlEntraAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Disabled'
    restrictOutboundNetworkAccess: 'Enabled'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sqlServer
  name: 'lapluma'
  location: location
  tags: tags
  sku: {
    name: sqlSkuName
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: sqlSkuCapacity
  }
  properties: {
    autoPauseDelay: sqlAutoPauseDelayMinutes
    minCapacity: json(sqlMinCapacity)
    zoneRedundant: sqlZoneRedundant
  }
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: 'cosmos-lapluma-${suffix}'
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    disableLocalAuth: true
    publicNetworkAccess: 'Disabled'
    minimalTlsVersion: 'Tls12'
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    locations: [
      { locationName: location, failoverPriority: 0, isZoneRedundant: cosmosZoneRedundant }
    ]
  }
}

resource projectionsDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmos
  name: 'derived'
  tags: tags
  properties: { resource: { id: 'derived' } }
}

resource projectionsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: projectionsDatabase
  name: 'case-projections'
  tags: tags
  properties: {
    resource: {
      id: 'case-projections'
      partitionKey: {
        paths: ['/tenantId', '/caseId']
        kind: 'MultiHash'
        version: 2
      }
    }
    options: { autoscaleSettings: { maxThroughput: cosmosMaxThroughput } }
  }
}

var storagePurposes = ['quarantine', 'documents', 'packages', 'audit']
resource storageAccounts 'Microsoft.Storage/storageAccounts@2023-05-01' = [for purpose in storagePurposes: {
  name: take('st${compactName}${take(purpose, 3)}${suffix}', 24)
  location: location
  tags: union(tags, { purpose: purpose })
  sku: { name: purpose == 'audit' ? auditStorageSku : defaultStorageSku }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Disabled'
    supportsHttpsTrafficOnly: true
  }
}]

resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = [for (purpose, index) in storagePurposes: {
  parent: storageAccounts[index]
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: blobSoftDeleteRetentionDays }
    containerDeleteRetentionPolicy: { enabled: true, days: containerSoftDeleteRetentionDays }
    isVersioningEnabled: true
  }
}]

resource purposeContainers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = [for (purpose, index) in storagePurposes: {
  parent: blobServices[index]
  name: purpose
  properties: { publicAccess: 'None' }
}]

output sqlServerName string = sqlServer.name
output sqlDatabaseName string = sqlDatabase.name
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output storageAccountNames array = [for (_, index) in storagePurposes: storageAccounts[index].name]
output sqlServerId string = sqlServer.id
output cosmosId string = cosmos.id
output cosmosAccountName string = cosmos.name
output storageAccountIds array = [for (_, index) in storagePurposes: storageAccounts[index].id]
output storagePurposeNames array = storagePurposes
