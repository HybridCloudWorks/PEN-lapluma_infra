targetScope = 'resourceGroup'

param name string
param location string = resourceGroup().location
param tags object = {}

@description('Service Bus Premium messaging units. Pending REVIEW.md R-03 and TODO 3.2.')
@allowed([
  1
  2
  4
  8
  16
])
param serviceBusCapacity int = 1

@description('Premium messaging partitions. Pending REVIEW.md R-03 and TODO 3.2.')
@minValue(1)
@maxValue(4)
param serviceBusPartitions int = 1

@description('Duplicate-detection window, ISO 8601 duration.')
@minLength(1)
param duplicateDetectionWindow string = 'PT1H'

@description('Queue message time to live, ISO 8601 duration. Pending REVIEW.md R-11.')
@minLength(1)
param queueMessageTimeToLive string = 'P7D'

@description('Queue lock duration, ISO 8601 duration. Maximum PT5M.')
@minLength(1)
param queueLockDuration string = 'PT5M'

@description('Deliveries before a message is dead-lettered.')
@minValue(1)
@maxValue(2000)
param queueMaxDeliveryCount int = 5

@description('Topic message time to live, ISO 8601 duration. Pending REVIEW.md R-11.')
@minLength(1)
param topicMessageTimeToLive string = 'P14D'

// Service Bus namespace names share one global DNS namespace, exactly like the Key Vault, Cosmos,
// SQL, and storage names, all of which already carry a suffix. This one previously did not, so
// deployment depended on whether anyone else had taken the plain 'sb-<name>' it resolved to.
//
// `name` is not dropped: it is an input to the suffix, so two environments still get distinct
// namespaces. It is kept out of the literal stem for the reason data.bicep states over its own
// `compactName` — a globally scoped name is built from a fixed safe stem plus uniqueString output,
// so an environment name that is legal in Bicep but illegal in a global DNS label cannot reach it.
// Which of the two conventions in data.bicep should win repository-wide is REVIEW.md R-05.
var suffix = take(uniqueString(subscription().id, resourceGroup().id, name), 6)

resource serviceBus 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: 'sb-lapluma-${suffix}'
  location: location
  tags: tags
  sku: { name: 'Premium', tier: 'Premium', capacity: serviceBusCapacity }
  properties: {
    premiumMessagingPartitions: serviceBusPartitions
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
    duplicateDetectionHistoryTimeWindow: duplicateDetectionWindow
    // Without a TTL the default is effectively infinite, so the dead-letter policy below can never
    // fire and a stuck message is retained instead of surfaced. Window pending R-11.
    defaultMessageTimeToLive: queueMessageTimeToLive
    deadLetteringOnMessageExpiration: true
    maxDeliveryCount: queueMaxDeliveryCount
    lockDuration: queueLockDuration
  }
}

resource processingQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBus
  name: 'document-processing'
  properties: {
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: duplicateDetectionWindow
    // Without a TTL the default is effectively infinite, so the dead-letter policy below can never
    // fire and a stuck message is retained instead of surfaced. Window pending R-11.
    defaultMessageTimeToLive: queueMessageTimeToLive
    deadLetteringOnMessageExpiration: true
    maxDeliveryCount: queueMaxDeliveryCount
    lockDuration: queueLockDuration
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
    duplicateDetectionHistoryTimeWindow: duplicateDetectionWindow
    defaultMessageTimeToLive: topicMessageTimeToLive
  }
}

output namespaceName string = serviceBus.name
output acquisitionQueueName string = acquisitionQueue.name
output processingQueueName string = processingQueue.name
output eventsTopicName string = eventsTopic.name
