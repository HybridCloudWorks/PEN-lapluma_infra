targetScope = 'resourceGroup'

// azure.yaml declares three services and the Bicep modelled nowhere to deploy them: no Container
// Apps environments, no function app, no registry. The four delegated subnets had no consumers.
//
// Three separate managed environments rather than one with three apps. An environment is the
// logging and networking boundary, so sharing one would put the processing zone on the same
// boundary as the core zone — the trust-zone split is the reason the subnets exist.

@description('Base resource name.')
@minLength(1)
param name string

param location string = resourceGroup().location
param tags object = {}

@minLength(1)
param coreSubnetId string

@minLength(1)
param processingSubnetId string

@minLength(1)
param aiSubnetId string

@minLength(1)
param functionsSubnetId string

@minLength(1)
param logAnalyticsWorkspaceId string

@minLength(1)
param applicationInsightsConnectionString string

@minLength(1)
param coreIdentityId string

@minLength(1)
param processingIdentityId string

@minLength(1)
param functionsIdentityId string

@description('Registry SKU. Premium is required for private endpoints, which this posture requires. Cost pending REVIEW.md R-03.')
@allowed([
  'Basic'
  'Standard'
  'Premium'
])
param registrySku string = 'Premium'

@description('''
Image the apps are created with before `azd deploy` pushes the real one. A container app cannot be
created without an image, and the registry is empty at provision time.

This default is a public image, which the processing zone cannot pull: its NSG denies Internet
egress, by design. That zone's first deployment therefore needs the registry seeded first, or the
app created and then updated. The interlock keeps that theoretical for now — see TODO.md.
''')
@minLength(1)
param placeholderImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Python version for the function host. Kept in step with the rest of the repository.')
@minLength(1)
param functionsPythonVersion string = '3.13'

@description('''
Log Analytics workspace every diagnostic setting routes to. Empty disables them, which is what keeps
each module compilable on its own; main.bicep always supplies it.
''')
param diagnosticsWorkspaceId string = ''

var suffix = take(uniqueString(subscription().id, resourceGroup().id, name), 6)
var compactName = 'lapluma'

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: take('cr${compactName}${suffix}', 50)
  location: location
  tags: tags
  sku: { name: registrySku }
  properties: {
    // Managed identity pull only. An admin user is a shared key by another name.
    adminUserEnabled: false
    publicNetworkAccess: 'Disabled'
    networkRuleBypassOptions: 'AzureServices'
  }
}

// ---------------------------------------------------------------------------------------------
// Managed environments — one per trust zone, each bound to its own subnet.
// ---------------------------------------------------------------------------------------------

resource coreEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${name}-core'
  location: location
  tags: tags
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: coreSubnetId
      internal: true
    }
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }
    ]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: reference(logAnalyticsWorkspaceId, '2023-09-01').customerId
        // No sharedKey: the workspace sets features.disableLocalAuth, so the shared-key ingestion
        // path is closed. Environment logging authenticates with its own managed identity.
      }
    }
  }
}

resource processingEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${name}-processing'
  location: location
  tags: tags
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: processingSubnetId
      internal: true
    }
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }
    ]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: reference(logAnalyticsWorkspaceId, '2023-09-01').customerId
      }
    }
  }
}

// No app runs here yet. The environment exists because snet-ai is delegated and would otherwise
// have no consumer, and because the AI zone's boundary is part of the design rather than a
// consequence of what happens to be deployed. Its workloads arrive with REVIEW.md R-12.
resource aiEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-${name}-ai'
  location: location
  tags: tags
  properties: {
    vnetConfiguration: {
      infrastructureSubnetId: aiSubnetId
      internal: true
    }
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }
    ]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: reference(logAnalyticsWorkspaceId, '2023-09-01').customerId
      }
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Container apps. The azd-service-name tag is what binds each to its azure.yaml service.
// ---------------------------------------------------------------------------------------------

resource coreApi 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${name}-core-api'
  location: location
  tags: union(tags, { 'azd-service-name': 'core-api' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${coreIdentityId}': {} }
  }
  properties: {
    managedEnvironmentId: coreEnvironment.id
    configuration: {
      // Internal only. Nothing publishes this to the internet; the edge is APIM's job and APIM is
      // not modelled yet — see TODO.md.
      ingress: {
        external: false
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: coreIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'core-api'
          image: placeholderImage
          env: [
            { name: 'ASPNETCORE_URLS', value: 'http://+:8080' }
            {
              name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
              value: applicationInsightsConnectionString
            }
          ]
          probes: [
            {
              // Liveness is 'is the process up'. It deliberately does not test the catalog: a
              // failing readiness probe must not have the orchestrator restart a healthy replica.
              type: 'Liveness'
              httpGet: { path: '/health', port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              // Readiness resolves the catalog repository and answers 503 when it cannot be built.
              type: 'Readiness'
              httpGet: { path: '/ready', port: 8080 }
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        // At least one replica. A scale-to-zero authoritative API would put a cold start in front
        // of the first request of every session.
        minReplicas: 1
        maxReplicas: 3
      }
    }
  }
}

resource processingWorker 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-${name}-processing-worker'
  location: location
  tags: union(tags, { 'azd-service-name': 'processing-worker' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${processingIdentityId}': {} }
  }
  properties: {
    managedEnvironmentId: processingEnvironment.id
    configuration: {
      // No ingress at all. This worker is queue-driven; the health surface is for probes inside the
      // environment, not for callers.
      registries: [
        {
          server: registry.properties.loginServer
          identity: processingIdentityId
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'processing-worker'
          image: placeholderImage
          env: [
            { name: 'PORT', value: '8080' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 30
              failureThreshold: 3
            }
            {
              type: 'Readiness'
              httpGet: { path: '/ready', port: 8080 }
              initialDelaySeconds: 5
              periodSeconds: 10
              failureThreshold: 3
            }
          ]
        }
      ]
      scale: {
        // Scales to zero when the queue is empty: this is batch work, not a request path.
        minReplicas: 0
        maxReplicas: 5
      }
    }
  }
}

// ---------------------------------------------------------------------------------------------
// Function host. Its own storage account: the four data accounts carry case material under a
// retention obligation, and host bookkeeping does not belong beside it.
// ---------------------------------------------------------------------------------------------

resource functionsStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: take('st${compactName}fnh${suffix}', 24)
  location: location
  tags: union(tags, { purpose: 'function-host' })
  sku: { name: 'Standard_LRS' }
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
}

resource functionsStorageBlob 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: functionsStorage
  name: 'default'
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: functionsStorageBlob
  name: 'deployments'
  properties: { publicAccess: 'None' }
}

resource functionsPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: 'plan-${name}-functions'
  location: location
  tags: tags
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  kind: 'functionapp'
  properties: { reserved: true }
}

resource functionApp 'Microsoft.Web/sites@2023-12-01' = {
  name: 'func-${name}-acquisition'
  location: location
  tags: union(tags, { 'azd-service-name': 'acquisition-functions' })
  kind: 'functionapp,linux'
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${functionsIdentityId}': {} }
  }
  properties: {
    serverFarmId: functionsPlan.id
    httpsOnly: true
    publicNetworkAccess: 'Disabled'
    virtualNetworkSubnetId: functionsSubnetId
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${functionsStorage.properties.primaryEndpoints.blob}${deploymentContainer.name}'
          authentication: {
            type: 'UserAssignedIdentity'
            userAssignedIdentityResourceId: functionsIdentityId
          }
        }
      }
      runtime: {
        name: 'python'
        version: functionsPythonVersion
      }
      scaleAndConcurrency: {
        maximumInstanceCount: 40
        instanceMemoryMB: 2048
      }
    }
    siteConfig: {
      // Identity-based AzureWebJobsStorage. The account sets allowSharedKeyAccess: false, so a
      // connection string would not work even if one were permitted here.
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: functionsStorage.name }
        { name: 'AzureWebJobsStorage__credential', value: 'managedidentity' }
        { name: 'AzureWebJobsStorage__clientId', value: reference(functionsIdentityId, '2023-01-31').clientId }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: applicationInsightsConnectionString }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
      ]
    }
  }
}

output registryId string = registry.id
output registryName string = registry.name
output registryLoginServer string = registry.properties.loginServer
output functionsStorageId string = functionsStorage.id
output functionsStorageName string = functionsStorage.name
output functionAppName string = functionApp.name
output coreApiName string = coreApi.name
output processingWorkerName string = processingWorker.name

// ---------------------------------------------------------------------------------------------
// Diagnostics. The managed environments already ship console and system logs to the workspace
// through appLogsConfiguration; these settings cover the control-plane categories that path does
// not — scaling decisions, revision changes, and registry pulls.
// ---------------------------------------------------------------------------------------------

// Scopes are written out rather than looped, for the same reason as the network module: a
// diagnostic setting's scope must be resolvable at the start of the deployment.

resource coreEnvironmentDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  scope: coreEnvironment
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource processingEnvironmentDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  scope: processingEnvironment
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource aiEnvironmentDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  scope: aiEnvironment
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource registryDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  // Image pulls and pushes. The record of which image a replica actually ran starts here.
  scope: registry
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource functionAppDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  scope: functionApp
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource functionsStorageDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  scope: functionsStorage
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    metrics: [{ category: 'Transaction', enabled: true }]
  }
}

resource functionsStorageBlobDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = if (!empty(diagnosticsWorkspaceId)) {
  // The deployment container lives here, so this is the record of what was published to the
  // function host and when. The account-level setting above carries metrics only.
  scope: functionsStorageBlob
  name: 'to-log-analytics'
  properties: {
    workspaceId: diagnosticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'Transaction', enabled: true }]
  }
}
