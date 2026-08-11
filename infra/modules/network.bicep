targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}
param vnetAddressPrefix string
param subnetPrefixes object

@description('''
Log Analytics workspace every diagnostic setting routes to. Empty disables them, which is what keeps
each module compilable on its own; main.bicep always supplies it.
''')
param diagnosticsWorkspaceId string = ''

// An NSG carries an implicit AllowVnetOutBound at priority 65000, and every private endpoint sits
// inside this VNet. DenyInternetEgress on the processing subnet therefore never stopped a
// processing replica reaching the SQL or Cosmos private endpoints — the traffic is intra-VNet, so
// the internet rule does not apply to it. That is code review finding F-04, and these rules are
// what close it.
//
// Rule *structure* is authored here; the destination *addresses* for a firewall allowlist are
// REVIEW.md R-09 and are not invented. Deny rules do not need an approved allowlist to be correct.
var denyDatabaseEgress = [
  {
    name: 'DenyProcessingToSql'
    properties: {
      description: 'The processing zone has no database route. Private endpoints are intra-VNet, so the internet rule does not cover this.'
      priority: 3000
      direction: 'Outbound'
      access: 'Deny'
      protocol: '*'
      sourcePortRange: '*'
      destinationPortRange: '*'
      sourceAddressPrefix: '*'
      destinationAddressPrefix: 'Sql'
    }
  }
  {
    name: 'DenyProcessingToCosmos'
    properties: {
      description: 'As above, for Cosmos. Route a genuine need through a governed Core API call.'
      priority: 3010
      direction: 'Outbound'
      access: 'Deny'
      protocol: '*'
      sourcePortRange: '*'
      destinationPortRange: '*'
      sourceAddressPrefix: '*'
      destinationAddressPrefix: 'AzureCosmosDB'
    }
  }
  {
    name: 'DenyProcessingToPrivateEndpoints'
    properties: {
      description: 'Service tags cover the platform services; this covers the endpoints themselves, whose addresses are VNet-local.'
      priority: 3020
      direction: 'Outbound'
      access: 'Deny'
      protocol: '*'
      sourcePortRange: '*'
      destinationPortRange: '*'
      sourceAddressPrefix: '*'
      destinationAddressPrefix: string(subnetPrefixes.privateEndpoints)
    }
  }
]

// The other four subnets had no rules at all, which left the AI zone with unrestricted outbound
// internet access. A baseline deny is not the approved allowlist R-09 owes — it is the default the
// allowlist will punch holes in, and it is the safe state to hold until then.
var denyInternetEgress = [
  {
    name: 'DenyInternetEgress'
    properties: {
      description: 'Baseline. Approved destinations are added by the R-09 allowlist, not removed from here.'
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

resource coreNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${name}-core'
  location: location
  tags: tags
  properties: {
    securityRules: denyInternetEgress
  }
}

resource processingNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${name}-processing'
  location: location
  tags: tags
  properties: {
    securityRules: concat(denyDatabaseEgress, denyInternetEgress)
  }
}

resource aiNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${name}-ai'
  location: location
  tags: tags
  properties: {
    securityRules: denyInternetEgress
  }
}

resource functionsNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${name}-functions'
  location: location
  tags: tags
  properties: {
    securityRules: denyInternetEgress
  }
}

resource privateEndpointsNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${name}-private-endpoints'
  location: location
  tags: tags
  properties: {
    securityRules: denyInternetEgress
  }
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
          // This delegation assumes Flex Consumption (`FC1`/`FlexConsumption`), which is the SKU
          // `functionsPlan` in compute.bicep declares. It is not a free choice: Flex Consumption
          // integrates through `Microsoft.App/environments`, while Elastic Premium requires
          // `Microsoft.Web/serverFarms`, and a delegation cannot be changed once a resource
          // occupies the subnet — correcting it after the fact means rebuilding the VNet.
          //
          // `tools/validate_foundation.py` holds the SKU-to-delegation map and fails if this and
          // the plan SKU disagree, so changing one without the other cannot pass. The map is
          // recorded from the Azure Component Research Record; re-verify it against current Azure
          // guidance if the hosting SKU changes. REVIEW.md R-03 is what confirms Flex Consumption
          // is actually available in the target region.
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

// Every NSG, not only the processing one. A deny that never appears in a log is indistinguishable
// from a rule that was never evaluated, and four of these carried no rules at all until recently.
//
// Written out rather than looped: a diagnostic setting's scope has to be resolvable at the start of
// the deployment, and an array of resource symbols is not.
// Network security groups emit no metrics, so these carry logs only.

resource coreNsgDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  scope: coreNsg
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
  }
}

resource processingNsgDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  scope: processingNsg
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
  }
}

resource aiNsgDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  scope: aiNsg
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
  }
}

resource functionsNsgDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  scope: functionsNsg
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
  }
}

resource privateEndpointsNsgDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  scope: privateEndpointsNsg
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
  }
}

resource vnetDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  scope: vnet
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}
