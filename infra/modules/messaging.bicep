targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}

resource serviceBus 'Microsoft.ServiceBus/namespaces@2023-01-01-preview' = {
  name: 'sb-${name}'
  location: location
  tags: tags
  sku: { name: 'Premium', tier: 'Premium', capacity: 1 }
  properties: {
    premiumMessagingPartitions: 1
    publicNetworkAccess: 'Disabled'
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

resource acquisitionQueue 'Microsoft.ServiceBus/namespaces/queues@2023-01-01-preview' = {
  parent: serviceBus
  name: 'catalog-acquisition'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
    deadLetteringOnMessageExpiration: true
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
  }
}

resource processingQueue 'Microsoft.ServiceBus/namespaces/queues@2023-01-01-preview' = {
  parent: serviceBus
  name: 'document-processing'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
    deadLetteringOnMessageExpiration: true
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
  }
}

resource eventsTopic 'Microsoft.ServiceBus/namespaces/topics@2023-01-01-preview' = {
  parent: serviceBus
  name: 'domain-events'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
    defaultMessageTimeToLive: 'P14D'
  }
}

output namespaceName string = serviceBus.name
output acquisitionQueueName string = acquisitionQueue.name
output processingQueueName string = processingQueue.name
output eventsTopicName string = eventsTopic.name
