targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}
param sqlEntraAdminObjectId string
param sqlEntraAdminDisplayName string

@description('Blob soft-delete window. Ratified at 7 days. Versioning is not a parameter.')
@minValue(1)
@maxValue(365)
param blobSoftDeleteRetentionDays int = 7

@description('Container soft-delete window. Ratified at 7 days.')
@minValue(1)
@maxValue(365)
param containerSoftDeleteRetentionDays int = 7

@description('''
Days a non-current blob version is kept before it is purged. Versioning is on, so deleting a blob
creates a version: the current blob is gone and the content is not. This is the window that actually
bounds how long erased content survives, and the ratified ordering rule requires it to stay strictly below
the erasure SLA.
''')
@minValue(1)
@maxValue(365)
param blobVersionRetentionDays int = 7

@description('''
Account erasure SLA in days, ratified at 30. Nothing here deletes anything on this
schedule — the erasure orchestration is TODO 5.6. It is a parameter so the ordering rule can be
checked against it: every window above that extends the life of case content must be strictly
shorter, or the deletion receipt promises something the storage configuration contradicts.
''')
@minValue(1)
@maxValue(365)
param erasureSlaDays int = 30

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

@description('''
Log Analytics workspace every diagnostic setting routes to. Empty disables them, which is what keeps
each module compilable on its own; main.bicep always supplies it.
''')
param diagnosticsWorkspaceId string = ''

@description('''
Immutability window for the audit container, in days. The audit account is described as holding
immutable evidence and was configured identically to the other three: deletion evidence that can be
deleted is not evidence. Seven years, ratified.
''')
@minValue(1)
@maxValue(146000)
param auditImmutabilityDays int = 2555


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

// Version purge. Versioning is enabled above, which means deleting a blob does not remove its
// content — it moves it to a non-current version. Without this policy those versions are kept
// forever, so the storage account would quietly retain every document the erasure sweep believes it
// deleted, and the deletion receipt in the data-flow design would be false.
//
// The audit account is included deliberately: its immutability policy protects the current blobs it
// is meant to protect, and a stale non-current version of an audit record is not evidence, it is a
// second copy with no obligation attached.
//
// The ratified ordering rule fixes the number, and it moved 30 days of proposal to 7 days of setting.
// The proposal put version purge at 30 against a 30-day erasure SLA, which reads fine until the
// clocks are lined up: a version is created when the blob is deleted, so its 30 days start *after*
// however long the deletion itself took, and the total runs past the SLA rather than inside it.
// `validate_retention_ordering` in tools/validate_foundation.py rejects equality for exactly that
// reason, and it rejected the proposed pairing on its first run.
//
// Seven days matches the soft-delete window, so the recovery story is one number rather than two:
// content is recoverable for a week, and after that it is gone. The Pilot Policy and Compliance
// Gates wiki page records the change and the reasoning.
//
// This policy is a backstop for versions produced by ordinary overwrites. TODO 5.6's erasure
// orchestration must purge versions explicitly rather than waiting for it — lifecycle management
// runs once a day at Azure's discretion, which is not a schedule an erasure promise can rest on.
resource blobLifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2023-05-01' = [for (purpose, index) in storagePurposes: {
  parent: storageAccounts[index]
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'purge-noncurrent-versions'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: { blobTypes: ['blockBlob'] }
            actions: {
              version: {
                delete: { daysAfterCreationGreaterThan: blobVersionRetentionDays }
              }
            }
          }
        }
      ]
    }
  }
}]

// WORM on the audit container only. The other three hold working material that retention and
// erasure policy must be able to remove — an immutability policy there would collide with the
// erasure obligation rather than support it.
//
// This policy is created UNLOCKED, and there is deliberately no parameter offering to lock it.
// Locking is not a declarative property: ARM exposes it as an explicit action on the policy
// (`az storage container immutability-policy lock`), so a `lock: true` here would be a setting that
// reads like a guarantee and enforces nothing. An unlocked policy can still be shortened or removed,
// which is exactly why the lock is a deliberate, irreversible, out-of-band step taken in `staging`
// and `pilot`, now that the seven-year period is ratified — and never in `dev`, where test data has to
// be removable. TODO.md carries the runbook step.
resource auditImmutability 'Microsoft.Storage/storageAccounts/blobServices/containers/immutabilityPolicies@2023-05-01' = {
  parent: purposeContainers[indexOf(storagePurposes, 'audit')]
  name: 'default'
  properties: {
    immutabilityPeriodSinceCreationInDays: auditImmutabilityDays
    // Protects append-only evidence written over time rather than only whole blobs.
    allowProtectedAppendWrites: true
  }
}

output auditImmutabilityDays int = auditImmutabilityDays
output blobVersionRetentionDays int = blobVersionRetentionDays
output erasureSlaDays int = erasureSlaDays
// Written out rather than composed from environment(): sqlServerHostname carries a leading dot,
// so this is '<name>' + '.database.windows.net' by way of the suffix function.
output sqlServerFullyQualifiedName string = '${sqlServer.name}${environment().suffixes.sqlServerHostname}'
output sqlServerName string = sqlServer.name
output sqlDatabaseName string = sqlDatabase.name
output cosmosEndpoint string = cosmos.properties.documentEndpoint
output storageAccountNames array = [for (_, index) in storagePurposes: storageAccounts[index].name]
output sqlServerId string = sqlServer.id
output cosmosId string = cosmos.id
output cosmosAccountName string = cosmos.name
output storageAccountIds array = [for (_, index) in storagePurposes: storageAccounts[index].id]
output storagePurposeNames array = storagePurposes

// ---------------------------------------------------------------------------------------------
// Diagnostics. Nothing in this foundation logged anywhere: no SQL security log, no Key Vault access
// log, no Service Bus operational log reached the workspace.
//
// `allLogs` rather than an enumerated category list, deliberately. A list has to be revised every
// time Azure adds a category, and the failure mode of a stale list is silence — the category simply
// never arrives, and nothing says so.
// ---------------------------------------------------------------------------------------------

var diagnosticsEnabled = !empty(diagnosticsWorkspaceId)

resource sqlDatabaseDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (diagnosticsEnabled) {
  // On the database, not the server: the server exposes no log categories of its own.
  scope: sqlDatabase
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource cosmosDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (diagnosticsEnabled) {
  scope: cosmos
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'Requests', enabled: true }]
  }
}

resource storageAccountDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = [for (purpose, index) in storagePurposes: if (diagnosticsEnabled) {
  // The account itself emits metrics only. Read and write operations are logged by the blob
  // service below, which is where an access to case material actually shows up.
  scope: storageAccounts[index]
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    metrics: [{ category: 'Transaction', enabled: true }]
  }
}]

resource blobServiceDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = [for (purpose, index) in storagePurposes: if (diagnosticsEnabled) {
  scope: blobServices[index]
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'Transaction', enabled: true }]
  }
}]
