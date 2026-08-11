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

@description('Retention windows in days. Ratified; see the Pilot Policy and Compliance Gates wiki page.')
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

  // How long a non-current blob version survives. Versioning is on, so a delete produces a version
  // rather than removing content, and this is the window that actually bounds how long erased
  // material persists.
  @minLength(1)
  blobVersionDays: string

  // The account erasure SLA. Nothing deploys on this schedule; it is here so the windows above can
  // be checked against it rather than each being chosen on its own.
  @minLength(1)
  erasureSlaDays: string

  // Not a soft-delete window: this one is a WORM period, and once its policy is locked it cannot
  // be shortened. Seven years, ratified.
  @minLength(1)
  auditImmutabilityDays: string
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

  // Premium is what private endpoints require, and this posture requires those.
  registrySku: 'Basic' | 'Standard' | 'Premium'
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

@description('Message handling windows. TTLs ratified.')
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

@description('The six zone subnets. A typed object turns a misspelled key into a parameter error rather than a failure deep inside the network module.')
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

  @minLength(1)
  apim: string
}

@description('AZD environment name. Lowercase; it is embedded in globally unique resource names, and Azure SQL server names permit only lowercase letters, digits, and hyphens.')
@minLength(2)
@maxLength(20)
param environmentName string

@description('Azure region approved in principle; availability and quota must still be verified.')
@minLength(1)
param location string = 'southcentralus'

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

@description('''
What class of data this environment is authorized to hold, applied as the `data-classification` tag.

`dev` is synthetic-only by decision, which is what makes the restore and deletion drills runnable at
all: a drill against real data creates a second copy of it with its own retention obligation, which
is a privacy incident dressed as diligence.

Through AZD this parameter is **required in practice**, and that is deliberate rather than an
oversight in the default. `infra/main.parameters.json` always supplies a value, so an unset
`LAPLUMA_DATA_CLASSIFICATION` substitutes an empty string, which ARM treats as supplied and the
allowed list then rejects — the deployment fails at submission naming this parameter. That is the
same fail-closed convention every other parameter in that file uses, and it is the right one here:
an environment's data classification should be stated by whoever authorizes it, not inherited from
whatever a template happened to default to.

The `'synthetic'` default therefore covers only the path where this parameter is omitted entirely —
a direct `az deployment` without the AZD parameter file. On that path the restrictive claim is the
safe one to land on.

A typo fails at submission rather than tagging the estate wrongly. The tag is what a governance
query filters on when someone asks which resources hold regulated data, so it has to be true.
''')
@allowed([
  'synthetic'
  'production-sensitive-pii'
])
param dataClassification string = 'synthetic'

@description('Python version for the function host. tools/validate_foundation.py holds this in step with the image, CI, and the documentation.')
@minLength(1)
param functionsPythonVersion string = '3.13'

@description('Retention windows, in days.')
param retention RetentionBaseline = {
  logAnalyticsDays: '365'
  blobSoftDeleteDays: '7'
  containerSoftDeleteDays: '7'
  keyVaultSoftDeleteDays: '90'
  hsmSoftDeleteDays: '90'
  blobVersionDays: '7'
  erasureSlaDays: '30'
  auditImmutabilityDays: '2555'
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
  registrySku: 'Premium'
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
  blobVersion: int(retention.blobVersionDays)
  erasureSla: int(retention.erasureSlaDays)
  auditImmutability: int(retention.auditImmutabilityDays)
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

// Composed from environment() rather than written out. The literals are correct for Azure Public
// and wrong everywhere else, which is what no-hardcoded-env-urls exists to catch; the remaining
// zone names below have no environment() suffix to derive them from.
// sqlServerHostname carries its own leading dot.
var sqlPrivateZone = 'privatelink${environment().suffixes.sqlServerHostname}'
var blobPrivateZone = 'privatelink.blob.${environment().suffixes.storage}'

var commonTags = union(tags, {
  'azd-env-name': environmentName
  system: 'lapluma'
  release: 'lapluma-infra-0.0'
  'correlated-app-release': 'lapluma-app-0.2'
  'data-residency': 'us'
  'data-classification': dataClassification
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
    diagnosticsWorkspaceId: observability!.outputs.workspaceId
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
    diagnosticsWorkspaceId: observability!.outputs.workspaceId
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
    diagnosticsWorkspaceId: observability!.outputs.workspaceId
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
    blobVersionRetentionDays: retentionDays.blobVersion
    erasureSlaDays: retentionDays.erasureSla
    sqlSkuName: capacity.sqlSkuName
    sqlSkuCapacity: sizing.sqlSkuCapacity
    sqlAutoPauseDelayMinutes: sizing.sqlAutoPauseMinutes
    sqlMinCapacity: capacity.sqlMinCapacity
    sqlZoneRedundant: redundancy.sqlZoneRedundant
    cosmosMaxThroughput: sizing.cosmosMaxThroughput
    cosmosZoneRedundant: redundancy.cosmosZoneRedundant
    auditImmutabilityDays: retentionDays.auditImmutability
    auditStorageSku: resilience.auditStorageSku
    defaultStorageSku: resilience.defaultStorageSku
    diagnosticsWorkspaceId: observability!.outputs.workspaceId
  }
}

module compute './modules/compute.bicep' = if (enableProvisioning) {
  name: 'compute-foundation'
  scope: targetResourceGroup
  params: {
    name: resourceBaseName
    location: location
    tags: commonTags
    coreSubnetId: network!.outputs.coreSubnetId
    processingSubnetId: network!.outputs.processingSubnetId
    aiSubnetId: network!.outputs.aiSubnetId
    functionsSubnetId: network!.outputs.functionsSubnetId
    logAnalyticsWorkspaceId: observability!.outputs.workspaceId
    applicationInsightsConnectionString: observability!.outputs.connectionString
    coreIdentityId: security!.outputs.coreIdentityId
    processingIdentityId: security!.outputs.processingIdentityId
    functionsIdentityId: security!.outputs.functionsIdentityId
    registrySku: capacity.registrySku
    functionsPythonVersion: functionsPythonVersion
    serviceBusFullyQualifiedNamespace: messaging!.outputs.namespaceFullyQualified
    sqlServerFullyQualifiedName: data!.outputs.sqlServerFullyQualifiedName
    sqlDatabaseName: data!.outputs.sqlDatabaseName
    cosmosEndpoint: data!.outputs.cosmosEndpoint
    diagnosticsWorkspaceId: observability!.outputs.workspaceId
  }
}

// One module for every private endpoint and zone. Passing the targets as data keeps the endpoint,
// its sub-resource, and the zone its record lands in on three adjacent lines, which is where a
// mismatch between them is visible.
module privatelink './modules/privatelink.bicep' = if (enableProvisioning) {
  name: 'privatelink-foundation'
  scope: targetResourceGroup
  params: {
    name: resourceBaseName
    location: location
    tags: commonTags
    vnetId: network!.outputs.vnetId
    subnetId: network!.outputs.privateEndpointsSubnetId
    additionalZones: [
      // No Document Intelligence resource exists yet — it arrives with REVIEW.md R-12 — but the
      // zone and its link are inert without an endpoint, so creating them now costs nothing.
      'privatelink.cognitiveservices.azure.com'
      // Azure Monitor resolves across four zones behind its single endpoint. Only the first is
      // named by a target above; without these three, ingestion and query resolve to nothing and
      // the deadlock the scope exists to break stays in place.
      'privatelink.oms.opinsights.azure.com'
      'privatelink.ods.opinsights.azure.com'
      'privatelink.agentsvc.azure-automation.net'
    ]
    targets: concat(
      [
        {
          name: 'sql'
          serviceId: data!.outputs.sqlServerId
          groupId: 'sqlServer'
          zone: sqlPrivateZone
        }
        {
          name: 'cosmos'
          serviceId: data!.outputs.cosmosId
          groupId: 'Sql'
          zone: 'privatelink.documents.azure.com'
        }
        {
          name: 'servicebus'
          serviceId: messaging!.outputs.serviceBusId
          groupId: 'namespace'
          zone: 'privatelink.servicebus.windows.net'
        }
        {
          name: 'keyvault'
          serviceId: security!.outputs.keyVaultId
          groupId: 'vault'
          zone: 'privatelink.vaultcore.azure.net'
        }
        {
          name: 'hsm'
          serviceId: security!.outputs.managedHsmId
          groupId: 'managedhsm'
          zone: 'privatelink.managedhsm.azure.net'
        }
        {
          name: 'registry'
          serviceId: compute!.outputs.registryId
          groupId: 'registry'
          zone: 'privatelink.azurecr.io'
        }
        {
          name: 'functions-host-storage'
          serviceId: compute!.outputs.functionsStorageId
          groupId: 'blob'
          zone: blobPrivateZone
        }
        {
          // Azure Monitor reaches ingestion and query through one endpoint on the scope, not one
          // per component, and it registers records in four zones rather than one.
          name: 'monitor'
          serviceId: observability!.outputs.privateLinkScopeId
          groupId: 'azuremonitor'
          zone: 'privatelink.monitor.azure.com'
        }
      ],
      // Indexed rather than iterated, matching how the accounts themselves are declared.
      map(range(0, length(data!.outputs.storagePurposeNames)), index => {
        name: 'storage-${data!.outputs.storagePurposeNames[index]}'
        serviceId: data!.outputs.storageAccountIds[index]
        groupId: 'blob'
        zone: blobPrivateZone
      })
    )
  }
}

module rbac './modules/rbac.bicep' = if (enableProvisioning) {
  name: 'rbac-foundation'
  scope: targetResourceGroup
  params: {
    storageAccountNames: data!.outputs.storageAccountNames
    storagePurposes: data!.outputs.storagePurposeNames
    serviceBusNamespaceName: messaging!.outputs.namespaceName
    processingQueueName: messaging!.outputs.processingQueueName
    acquisitionQueueName: messaging!.outputs.acquisitionQueueName
    keyVaultName: security!.outputs.keyVaultName
    cosmosAccountName: data!.outputs.cosmosAccountName
    registryName: compute!.outputs.registryName
    functionsStorageName: compute!.outputs.functionsStorageName
    corePrincipalId: security!.outputs.corePrincipalId
    processingPrincipalId: security!.outputs.processingPrincipalId
    functionsPrincipalId: security!.outputs.functionsPrincipalId
  }
}

output AZURE_RESOURCE_GROUP string = enableProvisioning ? targetResourceGroup.name : ''
output AZURE_LOCATION string = location
output PROVISIONING_INTERLOCK string = enableProvisioning ? 'enabled' : 'blocked'
