# Azure component research record

Research completed on 2026-08-02, before the `lapluma-infra-0.0` foundation was generated. It is
preserved so a future engineer can see why each generated artifact looks the way it does without
repeating the investigation.

> This record reflects Azure guidance as understood on 2026-08-02. Re-verify anything
> version-sensitive before relying on it for a deployment decision.

## AZD and Bicep

Subscription-scope entrypoint, resource-group-scoped modules, required AZD tags, managed identities,
non-secret outputs, and no hard-coded tenant, subscription, or resource-group IDs.

This is why `infra/main.bicep` uses `targetScope = 'subscription'`, applies an `azd-env-name` tag,
emits only non-secret outputs, and why `tools/validate_foundation.py` asserts that
`subscription().tenantId` appears only inside scoped modules and never in the entrypoint.

## Container Apps

Independent managed environments for the Core, Processing, and AI zones; explicit health probes; a
minimum of one API replica; queue-driven workers may scale to zero; non-root containers.

## Functions and Durable Functions

Identity-based storage and Service Bus settings, Flex Consumption as the preferred hosting baseline,
separation of timer, client, orchestrator, and activity roles, and no connection strings for new
deployments.

This is why `src/functions/function_app.py` splits `schedule_catalog_acquisition` (timer + Durable
client), `catalog_acquisition_orchestrator`, and the `propose_acquisition_activity` and
`publish_acquisition_activity` activities, and why the configuration contract uses
`AzureWebJobsStorage__accountName` and `SERVICEBUS__fullyQualifiedNamespace` rather than connection
strings.

Note that Flex Consumption VNet integration requires the Functions subnet to be delegated to
`Microsoft.App/environments`, which is what `infra/modules/network.bicep` currently declares. If the
hosting SKU changes, the delegation must be revisited.

## SQL and Cosmos DB

Entra-only SQL administration with managed-identity users; SQL remains the authoritative store;
Cosmos carries tenant- and case-partitioned, rebuildable projections with local authentication
disabled.

This is why `infra/modules/data.bicep` sets `azureADOnlyAuthentication: true`, `disableLocalAuth:
true`, and a hierarchical `/tenantId` + `/caseId` partition key on the `case-projections` container.

## Storage and Service Bus

Purpose-separated storage accounts, public blob and shared-key access disabled, purpose-scoped data
roles, a Premium Service Bus baseline, duplicate detection, dead-letter handling, and
managed-identity sender and receiver roles.

This is why four storage accounts are generated for the `quarantine`, `documents`, `packages`, and
`audit` purposes, and why the Service Bus queues enable duplicate detection and dead-lettering on
message expiration.

## Key management and observability

RBAC-authorized Key Vault with purge protection; Managed HSM gated behind administrator,
key-hierarchy, quota, restore, and cost approval; workspace-based Application Insights; content-free
Log Analytics telemetry.

## Document Intelligence

Document Intelligence proposes anchored source-document extraction only. It does not infer
official-form schemas, fill a form, approve a value, or receive an API-key fallback in code.

## Region

Region guidance indicated that the selected foundation services are broadly available. East US 2
SKU, private-network, Document Intelligence model, quota, and cost verification nevertheless remains
a hard gate before any live deployment.

## Related pages

- [Azure Deployment Plan](Azure-Deployment-Plan)
- [Architecture Overview](Architecture-Overview)
- [Configuration Contract](Configuration-Contract)
