targetScope = 'resourceGroup'

// Four managed identities existed with no role assignments anywhere, while every service had local
// authentication and shared keys disabled. The result was a foundation in which no workload could
// read or write anything.
//
// Every assignment below is scoped to a single resource, never to the resource group. A
// group-scoped assignment would silently grant the processing zone access to SQL and Cosmos, which
// is the one thing this design must not permit.

// Constrained to the real resource-name minimum, not a token 1: a parameter looser than the
// target it feeds produces a compile warning, and warnings fail this repository's build.
@minLength(3)
@maxLength(24)
type StorageAccountName = string

param storageAccountNames StorageAccountName[]

@description('Purpose of each storage account, in the same order as storageAccountNames.')
@minLength(1)
param storagePurposes array

@minLength(6)
param serviceBusNamespaceName string

@minLength(1)
param processingQueueName string

@minLength(1)
param acquisitionQueueName string

@minLength(3)
param keyVaultName string

@minLength(3)
param cosmosAccountName string

@minLength(5)
param registryName string

@minLength(3)
param functionsStorageName string

@minLength(36)
param corePrincipalId string

@minLength(36)
param processingPrincipalId string

@minLength(36)
param functionsPrincipalId string

// Built-in role definition GUIDs. Named here so a reviewer can check each against the published
// list rather than decode a bare GUID at its use site.
var roles = {
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageBlobDataReader: '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1'
  storageQueueDataContributor: '974c5e8b-45b9-4653-ba55-5f855dd0fb88'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
}

// The Cosmos data plane does not use Microsoft.Authorization at all. This is its built-in
// Data Contributor definition, assigned through the account's own sqlRoleAssignments.
var cosmosDataContributorDefinition = '00000000-0000-0000-0000-000000000002'

var documentsIndex = indexOf(storagePurposes, 'documents')
var packagesIndex = indexOf(storagePurposes, 'packages')
var quarantineIndex = indexOf(storagePurposes, 'quarantine')

resource storageAccounts 'Microsoft.Storage/storageAccounts@2023-05-01' existing = [for accountName in storageAccountNames: {
  name: accountName
}]

resource functionsStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: functionsStorageName
}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' existing = {
  name: serviceBusNamespaceName
}

resource processingQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' existing = {
  parent: serviceBus
  name: processingQueueName
}

resource acquisitionQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' existing = {
  parent: serviceBus
  name: acquisitionQueueName
}

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
}

resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = {
  name: cosmosAccountName
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: registryName
}

// ---------------------------------------------------------------------------------------------
// Core zone — the only zone with authoritative data access.
// ---------------------------------------------------------------------------------------------

resource coreDocuments 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAccounts[documentsIndex]
  name: guid(storageAccounts[documentsIndex].id, corePrincipalId, roles.storageBlobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalId: corePrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource corePackages 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAccounts[packagesIndex]
  name: guid(storageAccounts[packagesIndex].id, corePrincipalId, roles.storageBlobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalId: corePrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource coreServiceBusSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: serviceBus
  name: guid(serviceBus.id, corePrincipalId, roles.serviceBusDataSender)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
    principalId: corePrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource coreKeyVault 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: keyVault
  name: guid(keyVault.id, corePrincipalId, roles.keyVaultSecretsUser)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.keyVaultSecretsUser)
    principalId: corePrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource coreCosmos 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, corePrincipalId, cosmosDataContributorDefinition)
  properties: {
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/${cosmosDataContributorDefinition}'
    principalId: corePrincipalId
    scope: cosmos.id
  }
}

// ---------------------------------------------------------------------------------------------
// Processing zone — quarantine read, one queue, and nothing else.
//
// There is deliberately no SQL role and no Cosmos role here. If a change appears to need one, the
// design is wrong: route it through a governed Core API call instead.
// ---------------------------------------------------------------------------------------------

resource processingQuarantine 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: storageAccounts[quarantineIndex]
  name: guid(storageAccounts[quarantineIndex].id, processingPrincipalId, roles.storageBlobDataReader)
  properties: {
    // Reader, not Contributor. This zone treats every upload as hostile and must not be able to
    // alter the artefact it was handed.
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataReader)
    principalId: processingPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource processingQueueReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  // Scoped to the one queue, not to the namespace: namespace scope would also grant the
  // catalog-acquisition queue, which this zone has no business reading.
  scope: processingQueue
  name: guid(processingQueue.id, processingPrincipalId, roles.serviceBusDataReceiver)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
    principalId: processingPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------------------------
// Functions zone — host storage plus the acquisition queue.
// ---------------------------------------------------------------------------------------------

resource functionsHostBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: functionsStorage
  name: guid(functionsStorage.id, functionsPrincipalId, roles.storageBlobDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource functionsHostQueue 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: functionsStorage
  name: guid(functionsStorage.id, functionsPrincipalId, roles.storageQueueDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageQueueDataContributor)
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource functionsHostTable 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  // Durable Functions keeps its task hub in tables on this same account.
  scope: functionsStorage
  name: guid(functionsStorage.id, functionsPrincipalId, roles.storageTableDataContributor)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageTableDataContributor)
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource functionsAcquisitionSender 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acquisitionQueue
  name: guid(acquisitionQueue.id, functionsPrincipalId, roles.serviceBusDataSender)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataSender)
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource functionsAcquisitionReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acquisitionQueue
  name: guid(acquisitionQueue.id, functionsPrincipalId, roles.serviceBusDataReceiver)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataReceiver)
    principalId: functionsPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// ---------------------------------------------------------------------------------------------
// Registry pull. Every workload needs its image; none needs to push one — that is CI's job.
// ---------------------------------------------------------------------------------------------

resource corePull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, corePrincipalId, roles.acrPull)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: corePrincipalId
    principalType: 'ServicePrincipal'
  }
}

resource processingPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: registry
  name: guid(registry.id, processingPrincipalId, roles.acrPull)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalId: processingPrincipalId
    principalType: 'ServicePrincipal'
  }
}

// The AI identity appears nowhere in this file, and that absence is the point: the AI zone holds no
// authoritative data-plane role of any kind. tools/validate_foundation.py asserts it stays absent.
