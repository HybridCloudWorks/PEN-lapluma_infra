targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}
param vnetAddressPrefix string
param subnetPrefixes object

resource coreNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${name}-core'
  location: location
  tags: tags
}

resource processingNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${name}-processing'
  location: location
  tags: tags
  properties: {
    securityRules: [
      {
        name: 'DenyInternetEgress'
        properties: {
          priority: 4000
          direction: 'Outbound'
          access: 'Deny'
          protocol: '*'
          sourcePortRange: '*'
          destinationPortRange: '*'
          sourceAddressPrefix: '*'
          destinationAddressPrefix: 'Internet'
        }
      }
    ]
  }
}

resource aiNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${name}-ai'
  location: location
  tags: tags
}

resource functionsNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${name}-functions'
  location: location
  tags: tags
}

resource privateEndpointsNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${name}-private-endpoints'
  location: location
  tags: tags
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: 'vnet-${name}'
  location: location
  tags: tags
  properties: {
    addressSpace: {
      addressPrefixes: [vnetAddressPrefix]
    }
    subnets: [
      {
        name: 'snet-core'
        properties: {
          addressPrefix: string(subnetPrefixes.core)
          networkSecurityGroup: { id: coreNsg.id }
          delegations: [
            {
              name: 'aca-core'
              properties: { serviceName: 'Microsoft.App/environments' }
            }
          ]
        }
      }
      {
        name: 'snet-processing'
        properties: {
          addressPrefix: string(subnetPrefixes.processing)
          networkSecurityGroup: { id: processingNsg.id }
          delegations: [
            {
              name: 'aca-processing'
              properties: { serviceName: 'Microsoft.App/environments' }
            }
          ]
        }
      }
      {
        name: 'snet-ai'
        properties: {
          addressPrefix: string(subnetPrefixes.ai)
          networkSecurityGroup: { id: aiNsg.id }
          delegations: [
            {
              name: 'aca-ai'
              properties: { serviceName: 'Microsoft.App/environments' }
            }
          ]
        }
      }
      {
        name: 'snet-functions'
        properties: {
          addressPrefix: string(subnetPrefixes.functions)
          networkSecurityGroup: { id: functionsNsg.id }
          delegations: [
            {
              name: 'functions'
              properties: { serviceName: 'Microsoft.App/environments' }
            }
          ]
        }
      }
      {
        name: 'snet-private-endpoints'
        properties: {
          addressPrefix: string(subnetPrefixes.privateEndpoints)
          networkSecurityGroup: { id: privateEndpointsNsg.id }
          privateEndpointNetworkPolicies: 'Disabled'
        }
      }
    ]
  }
}

output vnetId string = vnet.id
output coreSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-core')
output processingSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-processing')
output aiSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-ai')
output functionsSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-functions')
output privateEndpointsSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-private-endpoints')
