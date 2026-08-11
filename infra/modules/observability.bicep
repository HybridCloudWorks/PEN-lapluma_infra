targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}

@description('Workspace retention. A planning baseline pending REVIEW.md R-11, not a policy decision.')
@minValue(30)
@maxValue(730)
param logAnalyticsRetentionDays int = 365

@description('''
Whether the workspace and component accept public ingestion and query. Only ever set 'Disabled'
alongside the Azure Monitor Private Link Scope below: disabling it without one is the deadlock this
module used to ship — workloads cannot send telemetry and operators cannot query it, and the pilot
runs blind. The scope is created here, so the default is safe.
''')
@allowed([
  'Enabled'
  'Disabled'
])
param monitorPublicNetworkAccess string = 'Disabled'

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  tags: tags
  properties: {
    retentionInDays: logAnalyticsRetentionDays
    // These were deliberately left unset until an Azure Monitor Private Link Scope existed, because
    // disabling them without one extends the component's ingestion deadlock to the workspace.
    publicNetworkAccessForIngestion: monitorPublicNetworkAccess
    publicNetworkAccessForQuery: monitorPublicNetworkAccess
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
      // The workspace stores what Application Insights ingests, so it needs the same posture.
      // Without this it keeps accepting the legacy workspace-ID-plus-shared-key ingestion path,
      // against the no-local-auth invariant every other service here implements.
      disableLocalAuth: true
    }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
    DisableLocalAuth: true
    publicNetworkAccessForIngestion: monitorPublicNetworkAccess
    publicNetworkAccessForQuery: monitorPublicNetworkAccess
  }
}

// The scope is what makes the two settings above survivable. Without it, ingestion is disabled and
// there is no private path to replace it.
resource privateLinkScope 'Microsoft.Insights/privateLinkScopes@2021-07-01-preview' = {
  name: 'ampls-${name}'
  location: 'global'
  tags: tags
  properties: {
    // Both modes are set independently, and both are PrivateOnly to match the stated posture. This
    // applies to every resource in the scope, not only the two added below.
    accessModeSettings: {
      ingestionAccessMode: 'PrivateOnly'
      queryAccessMode: 'PrivateOnly'
    }
  }
}

resource workspaceScope 'Microsoft.Insights/privateLinkScopes/scopedResources@2021-07-01-preview' = {
  parent: privateLinkScope
  name: 'scoped-workspace'
  properties: { linkedResourceId: workspace.id }
}

resource applicationInsightsScope 'Microsoft.Insights/privateLinkScopes/scopedResources@2021-07-01-preview' = {
  parent: privateLinkScope
  name: 'scoped-application-insights'
  properties: { linkedResourceId: applicationInsights.id }
}

output workspaceId string = workspace.id
output applicationInsightsId string = applicationInsights.id
output privateLinkScopeId string = privateLinkScope.id
output connectionString string = applicationInsights.properties.ConnectionString
output workspaceName string = workspace.name
output applicationInsightsName string = applicationInsights.name
