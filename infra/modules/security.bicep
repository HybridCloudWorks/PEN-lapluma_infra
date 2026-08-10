targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}
param hsmInitialAdminObjectId string

@description('Key Vault soft-delete window. Pending REVIEW.md R-11. Purge protection is not a parameter.')
@minValue(7)
@maxValue(90)
param keyVaultSoftDeleteRetentionDays int = 90

@description('Managed HSM soft-delete window. Pending REVIEW.md R-11. Purge protection is not a parameter.')
@minValue(7)
@maxValue(90)
param hsmSoftDeleteRetentionDays int = 90

@description('Managed HSM SKU. Pending REVIEW.md R-03.')
@allowed([
  'Standard_B1'
  'Custom_B32'
])
param hsmSkuName string = 'Standard_B1'

var suffix = take(uniqueString(subscription().id, resourceGroup().id, name), 6)
// Globally scoped names use only a fixed safe stem plus uniqueString output.
var compactName = 'lapluma'

resource keyVault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: 'kv-lapluma-${suffix}'
  location: location
  tags: tags
  properties: {
    tenantId: subscription().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: keyVaultSoftDeleteRetentionDays
    enablePurgeProtection: true
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
    }
  }
}

resource managedHsm 'Microsoft.KeyVault/managedHSMs@2023-07-01' = {
  name: 'hsm${compactName}${suffix}'
  location: location
  tags: tags
  sku: { family: 'B', name: hsmSkuName }
  properties: {
    tenantId: subscription().tenantId
    initialAdminObjectIds: [hsmInitialAdminObjectId]
    enablePurgeProtection: true
    softDeleteRetentionInDays: hsmSoftDeleteRetentionDays
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      bypass: 'None'
      defaultAction: 'Deny'
    }
  }
}

resource coreIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-core'
  location: location
  tags: tags
}

resource processingIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-processing'
  location: location
  tags: tags
}

resource aiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-ai'
  location: location
  tags: tags
}

resource functionsIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-${name}-functions'
  location: location
  tags: tags
}

output keyVaultName string = keyVault.name
output managedHsmName string = managedHsm.name
output coreIdentityId string = coreIdentity.id
output processingIdentityId string = processingIdentity.id
output aiIdentityId string = aiIdentity.id
output functionsIdentityId string = functionsIdentity.id
