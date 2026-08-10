targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}

// Service Bus namespace names share one global DNS namespace, exactly like the Key Vault, Cosmos,
// SQL, and storage names, all of which already carry a suffix. Without one, deployment depends on
// whether anyone else has taken 'sb-lapluma-dev'.
var suffix = take(uniqueString(subscription().id, resourceGroup().id, name), 6)

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: 'sb-lapluma-${suffix}'
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

resource acquisitionQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'catalog-acquisition'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
    // Without a TTL the default is effectively infinite, so the dead-letter policy below can never
    // fire and a stuck message is retained instead of surfaced. Window pending R-11.
    defaultMessageTimeToLive: 'P7D'
    deadLetteringOnMessageExpiration: true
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
  }
}

resource processingQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'document-processing'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
    // Without a TTL the default is effectively infinite, so the dead-letter policy below can never
    // fire and a stuck message is retained instead of surfaced. Window pending R-11.
    defaultMessageTimeToLive: 'P7D'
    deadLetteringOnMessageExpiration: true
    maxDeliveryCount: 5
    lockDuration: 'PT5M'
  }
}

resource eventsTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBus
  name: 'domain-events'
  properties: {
    // No dead-letter setting here on purpose: deadLetteringOnMessageExpiration is a property of
    // Microsoft.ServiceBus/namespaces/topics/subscriptions, not of the topic — SBTopicProperties
    // does not accept it. This TTL is what a subscription inherits when it sets none of its own, so
    // every subscription added by TODO 5.4 must set deadLetteringOnMessageExpiration itself or its
    // expired messages are discarded silently.
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT1H'
    defaultMessageTimeToLive: 'P14D'
  }
}

output namespaceName string = serviceBus.name
output acquisitionQueueName string = acquisitionQueue.name
output processingQueueName string = processingQueue.name
output eventsTopicName string = eventsTopic.name
