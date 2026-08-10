targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}

@description('Workspace retention. A planning baseline pending REVIEW.md R-11, not a policy decision.')
@minValue(30)
@maxValue(730)
param logAnalyticsRetentionDays int = 365

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-${name}'
  location: location
  tags: tags
  properties: {
    retentionInDays: logAnalyticsRetentionDays
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
    publicNetworkAccessForIngestion: 'Disabled'
    publicNetworkAccessForQuery: 'Disabled'
  }
}

output workspaceId string = workspace.id
output applicationInsightsId string = applicationInsights.id
