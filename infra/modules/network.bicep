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
// the ratified egress table on the Security and Data Protection wiki page, which is empty for this
// zone. Deny rules do not need an allowlist to be correct.
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
// internet access. The ratified egress table approves no destination for the core, processing, AI or
// private-endpoint zones, so for those four this deny IS the approved posture rather than a
// placeholder for one. The functions zone is the single exception and needs no egress yet.
var denyInternetEgress = [
  {
    name: 'DenyInternetEgress'
    properties: {
      description: 'Approved posture: no egress. A destination is added here, never removed from here.'
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

// The edge. This NSG deliberately carries no DenyInternetEgress rule, unlike the other five: API
// Management is the one component whose job is to face the internet, and a baseline deny here would
// have to be punched through immediately, which is the pattern that makes a deny rule meaningless.
// Inbound rules belong with the APIM resource itself, which needs the tier from REVIEW.md R-03 and
// the hostname from R-07 before it can be written.
resource apimNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${name}-apim'
  location: location
  tags: tags
  properties: {
    securityRules: []
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
        // Allocated by the ratified address plan and reserved now, ahead of the API Management resource that
        // will occupy it. Reserving early is the cheap half of the decision: the prefix cannot be
        // taken by something else, and a subnet with nothing in it can still be changed.
        //
        // No delegation is set, deliberately. API Management's v2 tiers integrate through a
        // delegated subnet and the classic tiers in internal mode do not, so the correct delegation
        // depends on the tier R-03 settles. Guessing it would produce a value that reads as decided
        // and is only conditionally right — and unlike the Functions subnet, this one is empty, so
        // setting the delegation later costs nothing. `validate_foundation.py` has no rule for this
        // one yet for the same reason: there is no SKU to check it against.
        name: 'snet-apim'
        properties: {
          addressPrefix: string(subnetPrefixes.apim)
          networkSecurityGroup: { id: apimNsg.id }
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
// Emitted now so the APIM module has something to consume the day it is written, rather than the
// module and the output landing in the same change and neither being reviewable on its own.
output apimSubnetId string = resourceId('Microsoft.Network/virtualNetworks/subnets', vnet.name, 'snet-apim')

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

resource apimNsgDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  // The edge NSG carries no rules yet, so this logs nothing today. It is written now because the
  // diagnostic-coverage rule requires it, and because the moment APIM occupies this subnet its flow
  // logs are the most interesting ones in the VNet.
  scope: apimNsg
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
