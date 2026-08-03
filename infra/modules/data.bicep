targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}
param sqlEntraAdminObjectId string
param sqlEntraAdminDisplayName string

var suffix = take(uniqueString(subscription().id, resourceGroup().id, name), 6)
// Globally scoped names use only a fixed safe stem plus uniqueString output. The reviewed
// environment name still participates in the suffix without leaking unsafe characters.
var compactName = 'lapluma'

resource sqlServer 'Microsoft.Sql/servers@2023-05-01-preview' = {
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

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-05-01-preview' = {
  parent: sqlServer
  name: 'lapluma'
  location: location
  sku: {
    name: 'GP_S_Gen5'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 2
  }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
    zoneRedundant: false
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
      { locationName: location, failoverPriority: 0, isZoneRedundant: false }
    ]
  }
}

resource projectionsDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmos
  name: 'derived'
  properties: { resource: { id: 'derived' } }
}

resource projectionsContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: projectionsDatabase
  name: 'case-projections'
  properties: {
    resource: {
      id: 'case-projections'
      partitionKey: {
        paths: ['/tenantId', '/caseId']
        kind: 'MultiHash'
        version: 2
      }
    }
    options: { autoscaleSettings: { maxThroughput: 1000 } }
  }
}

var storagePurposes = ['quarantine', 'documents', 'packages', 'audit']
resource storageAccounts 'Microsoft.Storage/storageAccounts@2023-05-01' = [for purpose in storagePurposes: {
  name: take('st${compactName}${take(purpose, 3)}${suffix}', 24)
  location: location
  tags: union(tags, { purpose: purpose })
  sku: { name: purpose == 'audit' ? 'Standard_ZRS' : 'Standard_LRS' }
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
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
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
