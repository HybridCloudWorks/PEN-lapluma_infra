targetScope = 'resourceGroup'

// Every data and AI service in this foundation sets publicNetworkAccess: 'Disabled'. Without the
// endpoints and zones below, provisioning produces a set of services that nothing can reach — the
// subnet exists, with privateEndpointNetworkPolicies disabled, and stays empty.
//
// The module is data-driven rather than one block per service because the blocks would be identical
// apart from three strings, and a copied block is where a wrong groupId or a mismatched zone hides.

@description('Base resource name, used to compose endpoint and link names.')
@minLength(1)
param name string

param location string = resourceGroup().location
param tags object = {}

@description('Virtual network the zones are linked to.')
@minLength(1)
param vnetId string

@description('Subnet the endpoints are created in. Must have privateEndpointNetworkPolicies disabled.')
@minLength(1)
param subnetId string

@description('''
One private endpoint. `groupId` is the sub-resource the endpoint targets, and it is not free text:
the wrong value produces a deployment error rather than a mis-wired endpoint, which is why it is
declared here beside the zone it must agree with.
''')
type PrivateLinkTarget = {
  @minLength(1)
  name: string

  @minLength(1)
  serviceId: string

  @minLength(1)
  groupId: string

  @minLength(1)
  zone: string
}

param targets PrivateLinkTarget[]

@description('''
Zones to create and link even though nothing points at them yet. A zone is inert until an endpoint
registers a record in it, so creating it early costs nothing and means the service that arrives
later — Document Intelligence, under REVIEW.md R-12 — needs an endpoint rather than a zone, a link,
and an endpoint.
''')
param additionalZones string[] = []

// union() deduplicates, so several targets may share a zone — all four storage accounts do — and
// deriving the list from the targets is what guarantees indexOf below always finds a match.
var zoneNames = union(map(targets, target => target.zone), additionalZones)

resource zones 'Microsoft.Network/privateDnsZones@2024-06-01' = [for zone in zoneNames: {
  name: zone
  location: 'global'
  tags: tags
}]

resource zoneLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = [for (zone, index) in zoneNames: {
  parent: zones[index]
  name: 'link-${name}'
  location: 'global'
  tags: tags
  properties: {
    // Workload records are written by the endpoints, not by VM auto-registration.
    registrationEnabled: false
    virtualNetwork: { id: vnetId }
  }
}]

resource endpoints 'Microsoft.Network/privateEndpoints@2023-11-01' = [for target in targets: {
  name: 'pe-${name}-${target.name}'
  location: location
  tags: tags
  properties: {
    subnet: { id: subnetId }
    privateLinkServiceConnections: [
      {
        name: target.name
        properties: {
          // use-resource-id-functions cannot see through a property access on an object parameter.
          // The rule exists to stop resource IDs being assembled from strings; these are `.id` on
          // real symbolic resources, handed over by main.bicep, which is exactly what it wants.
          #disable-next-line use-resource-id-functions
          privateLinkServiceId: target.serviceId
          groupIds: [target.groupId]
        }
      }
    ]
  }
}]

// Without this the endpoint exists and resolves to nothing: the A record lives in the zone group,
// not in the endpoint. An endpoint with no zone group is the failure that looks like success.
resource endpointZoneGroups 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-11-01' = [for (target, index) in targets: {
  parent: endpoints[index]
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: replace(target.zone, '.', '-')
        properties: { privateDnsZoneId: zones[indexOf(zoneNames, target.zone)].id }
      }
    ]
  }
}]

output zoneNames array = zoneNames
output endpointNames array = [for (target, index) in targets: endpoints[index].name]
