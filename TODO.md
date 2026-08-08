# TODO — engineering work queue

The authoritative backlog for this repository. Every actionable engineering item discovered anywhere
in the repository is recorded here, in dependency order, grouped by implementation phase, so an
engineer can resume work without rediscovering findings.

Items that require a human decision, approval, credential, or access grant are **not** here — they
are in `REVIEW.md` and referenced by ID (`R-nn`) where they gate an item.

**Priority scale.** P0 blocks the foundation from being coherent · P1 required before the pilot ·
P2 required before expansion, or repository hygiene · P3 opportunistic.

## Phase index

| Phase | Theme | Items |
|-------|-------|-------|
| [1](#phase-1--critical-fixes) | Critical fixes: the generated foundation is internally inconsistent | 1.1 – 1.4 |
| [2](#phase-2--security-improvements) | Security improvements | 2.1 – 2.6 |
| [3](#phase-3--stability-improvements) | Stability, observability, and evidence | 3.1 – 3.6 |
| [4](#phase-4--technical-debt) | Technical debt and repository hygiene | 4.1 – 4.6 |
| [5](#phase-5--feature-enhancements) | Feature and service completion | 5.1 – 5.7 |
| [6](#phase-6--documentation-improvements) | Documentation | 6.1 – 6.4 |

---

## Phase 1 — Critical fixes

The foundation as generated cannot function even once provisioning is unlocked. These four items
close that gap.

### 1.1 — Model private endpoints and private DNS zones

- **Priority:** P0
- **Description:** Every data and AI service in `infra/modules/` sets `publicNetworkAccess:
  'Disabled'` — SQL, Cosmos, all four storage accounts, Service Bus, Key Vault, and Managed HSM —
  but no `Microsoft.Network/privateEndpoints` or `Microsoft.Network/privateDnsZones` resources
  exist. A `snet-private-endpoints` subnet is created with `privateEndpointNetworkPolicies:
  'Disabled'` and then left empty. As written, provisioning would produce a set of services that
  nothing can reach.
- **Dependencies:** `REVIEW.md` **R-09** (address plan, DNS ownership, private DNS linking model).
- **Recommended action:** Add a `privatelink` Bicep module that creates one private endpoint per
  service into `snet-private-endpoints`, the corresponding `privatelink.*` private DNS zones, and
  the VNet links. Cover `privatelink.database.windows.net`, `privatelink.documents.azure.com`,
  `privatelink.blob.core.windows.net`, `privatelink.servicebus.windows.net`,
  `privatelink.vaultcore.azure.net`, `privatelink.managedhsm.azure.net`,
  `privatelink.cognitiveservices.azure.com`, and `privatelink.azurecr.io`. Extend
  `tools/validate_foundation.py` to fail if a resource disables public access without a matching
  private endpoint.
- **Status:** Not started
- **Notes for future engineers:** The four storage accounts are created with a `[for purpose in
  storagePurposes]` loop; the private endpoints must be indexed the same way. `validate_foundation.py`
  already asserts that resource collections are indexed rather than iterated in outputs — keep that
  pattern.

### 1.2 — Model the workload hosting layer

- **Priority:** P0
- **Description:** `azure.yaml` declares three services — `core-api` and `processing-worker` with
  `host: containerapp`, and `acquisition-functions` with `host: function` — but the Bicep models no
  Container Apps environments, no container apps, no Functions hosting plan or function app, no
  Container Registry, and no API Management. `azd deploy` has nowhere to deploy to, and the
  delegated `snet-core`, `snet-processing`, `snet-ai`, and `snet-functions` subnets have no
  consumers.
- **Dependencies:** 1.1 (registry and workload access rely on private endpoints); `REVIEW.md`
  **R-03** for the APIM SKU cost, **R-06** and **R-07** for the APIM identity and hostname
  configuration.
- **Recommended action:** Add a `compute` module creating three separate Container Apps managed
  environments — core, processing, and AI, each bound to its own subnet and each with its own
  logging boundary — plus the Core API container app with a minimum of one replica and explicit
  health probes against `/health` and `/ready`, a queue-driven processing worker app or job, the
  Functions hosting plan and function app, and Azure Container Registry with managed-identity pull.
  Add an `apim` module for the Edge zone. Tag each app with `azd-service-name` matching the
  corresponding `azure.yaml` service so AZD can bind them.
- **Status:** Not started
- **Notes for future engineers:** `src/core-api/Dockerfile` listens on `8080` via `ASPNETCORE_URLS`,
  and `src/document-processing/worker.py` reads `PORT` with a default of `8080`. Both images already
  run as non-root, so no `runAsUser` override is needed. The processing environment must have no
  route to SQL or Cosmos — that is a trust-zone invariant, not a configuration preference.

### 1.3 — Model RBAC role assignments

- **Priority:** P0
- **Description:** `infra/modules/security.bicep` creates four user-assigned managed identities —
  `id-*-core`, `id-*-processing`, `id-*-ai`, and `id-*-functions` — and outputs their resource IDs,
  but no `Microsoft.Authorization/roleAssignments` resource exists anywhere. Every service has local
  authentication and shared keys disabled, so with no role assignments no workload can read or write
  anything.
- **Dependencies:** 1.2 (the identities must be attached to real workloads first); `REVIEW.md`
  **R-04** for the security, operations, and privacy group object IDs.
- **Recommended action:** Add an `rbac` module granting the least privilege each zone actually
  needs. The core identity: SQL managed-identity user, Blob Data Contributor scoped to the documents
  and packages accounts, Service Bus Data Sender, Key Vault Secrets User. The processing identity:
  Blob Data Reader on quarantine only, Blob Data Contributor on the staging container only, Service
  Bus Data Receiver on `document-processing` only, Cognitive Services User on Document Intelligence.
  The functions identity: Storage Blob and Queue roles for identity-based `AzureWebJobsStorage`,
  Service Bus Data Sender and Receiver, Durable task hub access. The AI identity: no authoritative
  data-plane role at all. Prefer group-scoped assignments over individual ones.
- **Status:** Not started
- **Notes for future engineers:** The processing zone must never receive a SQL or Cosmos role. If a
  future change appears to need one, the design is wrong — route it through a governed Core API call
  instead.

### 1.4 — Resolve the Application Insights ingestion deadlock

- **Priority:** P0
- **Description:** `infra/modules/observability.bicep` sets both
  `publicNetworkAccessForIngestion: 'Disabled'` and `publicNetworkAccessForQuery: 'Disabled'` on the
  Application Insights component, but no Azure Monitor Private Link Scope exists. With ingestion
  disabled and no AMPLS, workloads cannot send telemetry and operators cannot query it — the pilot
  would run blind.
- **Dependencies:** 1.1 (AMPLS needs a private endpoint and the `privatelink.monitor.azure.com`,
  `privatelink.oms.opinsights.azure.com`, `privatelink.ods.opinsights.azure.com`, and
  `privatelink.agentsvc.azure-automation.net` zones).
- **Recommended action:** Add a `Microsoft.Insights/privateLinkScopes` resource, scope the Log
  Analytics workspace and the Application Insights component to it, create its private endpoint in
  `snet-private-endpoints`, and link the four required private DNS zones. Verify end to end that a
  container app can emit a trace and that a query returns it.
- **Status:** Not started
- **Notes for future engineers:** AMPLS access-mode settings (`Open` versus `PrivateOnly`) apply to
  ingestion and query independently. Choose `PrivateOnly` for both to match the stated posture, but
  be aware it affects every workspace in scope.

---

## Phase 2 — Security improvements

### 2.1 — Bind customer-managed keys from Managed HSM

- **Priority:** P1
- **Description:** `infra/modules/security.bicep` provisions a Managed HSM pool, but no storage
  account, SQL database, or Cosmos account references a customer-managed key. Customer-managed
  encryption is stated as a pilot prerequisite rather than later hardening, so the HSM currently
  costs money without protecting anything.
- **Dependencies:** 1.1, 1.3; `REVIEW.md` **R-10** (HSM administrators, key hierarchy, rotation
  policy, CMK coverage).
- **Recommended action:** Once the key hierarchy is approved, create the keys, grant each service's
  system- or user-assigned identity the `Managed HSM Crypto Service Encryption User` role, and set
  the CMK properties on the four storage accounts, the SQL database transparent-data-encryption
  protector, and the Cosmos account. Implement the approved rotation policy and verify that key
  revocation renders data inaccessible as expected.
- **Status:** Not started
- **Notes for future engineers:** Managed HSM has purge protection enabled and a 90-day soft-delete
  retention. Bootstrapping the pool with the wrong `initialAdminObjectIds` is expensive to undo —
  confirm R-10 before the first provisioning run, not after.

### 2.2 — Enforce processing-zone egress control

- **Priority:** P1
- **Description:** `infra/modules/network.bicep` puts a single `DenyInternetEgress` outbound rule on
  the processing NSG. That blocks direct internet destinations but does not implement the specified
  "no general internet egress" posture with an approved destination allowlist — there is no route
  table, no firewall, no DNS egress control, and the other four subnets have empty NSGs.
- **Dependencies:** 1.1; `REVIEW.md` **R-09** (approved egress destinations and enforcement
  mechanism).
- **Recommended action:** Implement the approved mechanism — an Azure Firewall with forced tunneling
  via UDR, or an equivalent — with an explicit allowlist. Add baseline deny rules to the core, AI,
  functions, and private-endpoint NSGs rather than leaving them empty. Add a validation test that
  asserts a processing replica cannot resolve or reach an arbitrary external host.
- **Status:** Not started
- **Notes for future engineers:** Container Apps environments need platform-level egress for image
  pulls and control-plane traffic. Use the ACR private endpoint plus the documented required FQDNs;
  do not widen the allowlist to "all Azure services".

### 2.3 — Add defense-in-depth authorization to the Core API

- **Priority:** P1
- **Description:** `src/core-api/Program.cs` registers no authentication or authorization at all.
  Every catalog endpoint is anonymous. The design places JWT validation at the APIM edge, but a
  service whose only protection is an upstream gateway fails open the moment anything reaches it
  directly — including from inside the core subnet.
- **Dependencies:** 1.2; `REVIEW.md` **R-06** (Entra registrations and API audience).
- **Recommended action:** Add JWT bearer authentication bound to the confirmed Entra audience and
  issuer, apply an authorization policy to the `/v1/catalog` group while leaving `/health` and
  `/ready` anonymous, and add a test that asserts an unauthenticated catalog request returns 401.
  Add the identity and policy boundary described in the architecture as a distinct module inside
  `src/core-api`.
- **Status:** Not started
- **Notes for future engineers:** The catalog endpoints deliberately accept no `userId`, `personId`,
  `folderId`, `caseId`, `documentId`, `eligibility`, or `facts` parameter, and
  `tools/validate_foundation.py` enforces that. Adding authentication must not introduce any of
  those as a query parameter.

### 2.4 — Apply immutability to the audit storage account

- **Priority:** P1
- **Description:** The `audit` storage account is described as holding immutable evidence and is the
  only account provisioned with `Standard_ZRS`, but it is configured identically to the others: no
  immutability policy, no legal hold, and the same 7-day soft-delete window. Deletion evidence that
  can be deleted is not evidence.
- **Dependencies:** `REVIEW.md` **R-11** (audit metadata retention, proposed 7 years).
- **Recommended action:** Add a time-based immutability policy (WORM) to the audit container sized
  to the approved retention period, with the policy locked in `staging` and `pilot`. Keep `dev`
  unlocked so test data can be cleaned up.
- **Status:** Not started
- **Notes for future engineers:** A locked immutability policy cannot be shortened or removed. Do
  not lock it until R-11 has ratified the retention period.

### 2.5 — Add supply-chain and code scanning to CI

- **Priority:** P2
- **Description:** `.github/workflows/foundation-validation.yml` runs the custom validator, tests,
  builds, and Bicep compilation, but there is no CodeQL analysis, no dependency review, no container
  image scan, and no IaC security scan. The custom secret scan in `tools/validate_foundation.py`
  covers five specific patterns and is not a substitute for GitHub secret scanning.
- **Dependencies:** None.
- **Recommended action:** Add CodeQL for C# and Python, dependency review on pull requests, a
  container image scan for both built images, and an IaC scanner over `infra/`. Enable GitHub secret
  scanning and push protection at the repository level. Keep every action pinned by commit SHA, as
  the existing workflow already does.
- **Status:** Not started
- **Notes for future engineers:** The existing workflow's SHA pinning is deliberate. Do not relax it
  to tags for convenience.

### 2.6 — Add Bicep linting configuration and enforce it

- **Priority:** P2
- **Description:** No `bicepconfig.json` exists, so `az bicep build` applies default linter rules
  only and CI does not fail on warnings. Rules such as `no-hardcoded-location`,
  `secure-parameter-default`, and `no-unused-params` should be errors in an infrastructure
  repository handling sensitive PII.
- **Dependencies:** None.
- **Recommended action:** Add `bicepconfig.json` with the security-relevant linter rules set to
  `error`, and change the CI step to fail on any diagnostic.
- **Status:** Not started
- **Notes for future engineers:** The modules currently default `location` to
  `resourceGroup().location`, which the `no-hardcoded-location` rule accepts. Verify the whole tree
  still compiles cleanly before making warnings fatal.

---

## Phase 3 — Stability improvements

### 3.1 — Add diagnostic settings to every resource

- **Priority:** P1
- **Description:** `infra/modules/observability.bicep` creates a Log Analytics workspace and an
  Application Insights component, but not one resource in the network, security, messaging, or data
  modules has a `Microsoft.Insights/diagnosticSettings` child. No audit log, no SQL security log, no
  Key Vault access log, and no Service Bus operational log reaches the workspace.
- **Dependencies:** 1.4.
- **Recommended action:** Add diagnostic settings to SQL, Cosmos, all four storage accounts and
  their blob services, Service Bus, Key Vault, Managed HSM, the NSGs, and — once 1.2 lands — the
  Container Apps environments, the function app, ACR, and APIM. Route them all to the workspace.
  Verify the emitted categories contain no case content, as the content-free telemetry constraint
  requires.
- **Status:** Not started
- **Notes for future engineers:** Log Analytics retention is currently hard-coded to 365 days; see
  item 4.4. Add the diagnostic settings before parameterizing retention, so the retention change can
  be validated against real ingested categories.

### 3.2 — Decide and implement resilience settings

- **Priority:** P1
- **Description:** SQL sets `zoneRedundant: false`, Cosmos sets `isZoneRedundant: false`, three of
  four storage accounts use `Standard_LRS`, and Service Bus Premium runs at capacity 1 with one
  messaging partition. These are cost-driven defaults, not measured decisions, and the SQL
  configuration also carries a 60-minute auto-pause that will produce cold-start latency on the
  authoritative store.
- **Dependencies:** `REVIEW.md` **R-03** (cost approval), **R-10** (SQL floor, maximum, zone
  redundancy, backup policy).
- **Recommended action:** Derive the resilience settings from an agreed SLO rather than from
  defaults, parameterize them per environment so `pilot` can differ from `dev`, and document the
  resulting RTO and RPO on the Environments and Release Path wiki page. Confirm whether auto-pause
  is acceptable for a user-facing authoritative store.
- **Status:** Not started
- **Notes for future engineers:** `GP_S_Gen5` with `minCapacity: 0.5` and `autoPauseDelay: 60` means
  the first request after an idle hour pays a resume penalty. For a ~40-case supervised pilot that
  may well be fine — but it should be a recorded decision, not an accident.

### 3.3 — Implement the invariant test suite

- **Priority:** P1
- **Description:** Cross-tenant, cross-folder, person-boundary, and agent-no-write invariant tests
  are required to pass on every build. Today the repository has five Python contract tests and no
  invariant tests at all.
- **Dependencies:** 1.2, 1.3, 2.3, 5.2.
- **Recommended action:** Build an integration test project that asserts, against a running `dev`
  environment: a token scoped to tenant A cannot read tenant B's data; a folder-scoped grant cannot
  traverse to a sibling folder; a person boundary cannot be crossed by any API path; and no AI or
  agent component can perform an authoritative write. Wire it into CI as a required check.
- **Status:** Not started
- **Notes for future engineers:** These are the tests that justify the trust-zone architecture. If
  they cannot be written against the implementation, the implementation has drifted from the design.

### 3.4 — Implement package round-trip fidelity verification

- **Priority:** P1
- **Description:** The design requires the package worker to round-trip verify every mapped field
  and to block delivery on a generated-package mismatch. Neither the worker nor the verification
  exists.
- **Dependencies:** 5.1; `REVIEW.md` **R-14** (verified official artifacts and approved field maps).
- **Recommended action:** After filling an edition-pinned form, re-read every mapped field from the
  generated artifact and compare it to the confirmed field ledger. Any mismatch fails closed and
  blocks delivery. Hash the approved output and record the hash with the delivery record. Add
  per-form fidelity fixtures for I-130, I-485, and DS-11.
- **Status:** Not started
- **Notes for future engineers:** DS-11 explicitly requires round-trip fidelity confirmation before
  activation and permits no electronic signature. Check the artifact's encoding — the contract
  distinguishes `ACROFORM`, `XFA`, and `FLAT`, and XFA round-tripping behaves very differently.

### 3.5 — Implement erasure and retention sweep integration tests

- **Priority:** P1
- **Description:** Account erasure and case-retention sweeps must be integration-tested across SQL,
  Cosmos, Blob versions, search and projections, temporary stores, delivery links, logs, and backups
  and key policy. None of this exists.
- **Dependencies:** 5.6; `REVIEW.md` **R-11** (one ratified retention contract).
- **Recommended action:** Write an integration test that seeds a synthetic case across every store,
  triggers erasure, and asserts that no active copy, version, index entry, projection, temporary
  artifact, or delivery link survives — and that the deletion receipt is withheld until every one of
  those checks passes.
- **Status:** Not started
- **Notes for future engineers:** Blob versioning is enabled with a 7-day soft-delete window, so a
  naive delete leaves recoverable versions behind. The test must assert on versions, not just on
  current blobs.

### 3.6 — Automate restore and deletion drills

- **Priority:** P2
- **Description:** Restore and deletion drill evidence is a real-user pilot prerequisite, and no
  drill procedure or automation exists.
- **Dependencies:** 3.5.
- **Recommended action:** Script a periodic drill against `staging` that restores SQL and Cosmos to
  a point in time, verifies data integrity, executes a deletion sweep, and writes content-free
  evidence to the audit account. Document the procedure as a runbook (item 6.2).
- **Status:** Not started
- **Notes for future engineers:** The drill evidence itself must be content-free and pseudonymized —
  it lands in the audit account, which is subject to the immutability policy from item 2.4.

---

## Phase 4 — Technical debt

### 4.1 — Add a .NET test project

- **Priority:** P2
- **Description:** `src/core-api` has no tests. `Program.cs` ends with `public partial class Program
  { }`, which exists specifically to enable `WebApplicationFactory` integration testing, but no test
  project consumes it. The CI workflow builds the API and never tests it.
- **Dependencies:** None.
- **Recommended action:** Add `src/core-api.tests` using `WebApplicationFactory<Program>`, covering
  the catalog hierarchy, package list and filter, package detail, schema lookup, the 404 problem
  responses, and the `activationState` parse failure path in `TryParseActivationState`. Add a
  `dotnet test` step to the workflow.
- **Status:** Not started
- **Notes for future engineers:** The project sets `TreatWarningsAsErrors`, so the test project
  should too. `TryParseActivationState` returns `true` for a null value and `false` for an
  unrecognized string — cover both.

### 4.2 — Update the CI branch filter

- **Priority:** P3
- **Description:** `.github/workflows/foundation-validation.yml` triggers on pushes to `main` and
  `codex/**`. That prefix reflects one historical automation and silently skips push validation for
  every other branch prefix. Pull requests are still validated, so the impact is limited to
  pre-PR feedback.
- **Dependencies:** None.
- **Recommended action:** Either drop the branch filter from the `push` trigger so all branches are
  validated, or replace the prefix list with one that matches current practice.
- **Status:** Not started
- **Notes for future engineers:** Dropping the filter entirely is the lower-maintenance option; the
  `pull_request` trigger already prevents anything unvalidated from merging.

### 4.3 — Add repository governance files

- **Priority:** P2
- **Description:** The repository has no `CODEOWNERS`, no Dependabot configuration, no pull-request
  template, no `SECURITY.md`, no `CONTRIBUTING.md`, and no licence file. For a repository whose
  design depends on two-person approval of field maps and on named security and privacy owners, the
  absence of `CODEOWNERS` is the most significant gap.
- **Dependencies:** `REVIEW.md` **R-04** (named owners) for `CODEOWNERS` content.
- **Recommended action:** Add `.github/CODEOWNERS` requiring review from the platform owner for
  `infra/`, the security owner for `infra/modules/security.bicep`, and the catalog and compliance
  owners for `contracts/`. Add `.github/dependabot.yml` for NuGet, pip, Docker, and GitHub Actions.
  Add a minimal PR template, `SECURITY.md`, `CONTRIBUTING.md`, and a licence. Keep each one short
  and link to the wiki rather than restating content.
- **Status:** Not started
- **Notes for future engineers:** These are the only markdown files permitted in `.github/`. Keep
  them minimal — the documentation model treats anything longer as content that belongs in the wiki.

### 4.4 — Parameterize the hard-coded baselines

- **Priority:** P2
- **Description:** Several policy-bearing values are literals in the Bicep rather than parameters:
  Log Analytics retention of 365 days, blob and container soft delete of 7 days, the SQL SKU and
  auto-pause settings, the Cosmos autoscale ceiling of 1000 RU/s, and the Service Bus Premium
  capacity. Each of them is subject to a pending policy or cost decision, and none can currently
  differ between `dev` and `pilot`.
- **Dependencies:** `REVIEW.md` **R-03** (cost), **R-11** (retention and lifecycle windows).
- **Recommended action:** Promote each value to a parameter with the current literal as its default,
  and supply per-environment values through the AZD environment. Add the new variables to the
  Configuration Contract wiki page and to `.env.example`.
- **Status:** Not started
- **Notes for future engineers:** The full list of current literals and their locations is in the
  "Hard-coded baselines in the generated Bicep" table on the Configuration Contract wiki page.

### 4.5 — Pin container base images by digest

- **Priority:** P2
- **Description:** `src/core-api/Dockerfile` uses `mcr.microsoft.com/dotnet/sdk:9.0` and
  `mcr.microsoft.com/dotnet/aspnet:9.0`, and `src/document-processing/Dockerfile` uses
  `python:3.12-slim`. All three are floating tags, so two builds of the same commit can produce
  different images — which undermines the deterministic, provenance-controlled image posture the
  design calls for.
- **Dependencies:** None.
- **Recommended action:** Pin all three to `image@sha256:...` digests and let Dependabot propose
  digest bumps once item 4.3 lands.
- **Status:** Not started
- **Notes for future engineers:** This pairs with the Container Registry content-trust and
  provenance controls in the service mapping — pinning at build time is the half of that story this
  repository owns.

### 4.6 — Confirm the Functions subnet delegation matches the hosting SKU

- **Priority:** P2
- **Description:** `infra/modules/network.bicep` delegates `snet-functions` to
  `Microsoft.App/environments`. That is correct for Flex Consumption, which is the stated preferred
  baseline, but wrong for Elastic Premium, which requires `Microsoft.Web/serverFarms`. The plan
  allows an approved equivalent if Flex Consumption features are unavailable in East US 2, so the
  delegation is only conditionally correct.
- **Dependencies:** 1.2; `REVIEW.md` **R-03** (region capability verification).
- **Recommended action:** When the region verification confirms the available Functions hosting SKU,
  re-check the delegation and correct it if the SKU changed. Add a comment in the network module
  recording which SKU the delegation assumes.
- **Status:** Not started
- **Notes for future engineers:** Subnet delegation cannot be changed while resources occupy the
  subnet, so getting this right before the first provisioning run avoids a rebuild.

---

## Phase 5 — Feature enhancements

### 5.1 — Implement the package worker

- **Priority:** P1
- **Description:** The architecture lists a package and delivery service at `src/package-worker`
  responsible for deterministic PDF generation, verification, and delivery. The directory does not
  exist, and `azure.yaml` declares no such service. Without it, step 6 of the data flow — the entire
  output half of the product — has no implementation.
- **Dependencies:** 1.2, 5.2; `REVIEW.md` **R-14** (verified artifacts and approved field maps).
- **Recommended action:** Create `src/package-worker` as a .NET 9 queue-driven worker or Container
  Apps Job. It fills an edition-pinned official form from a human-approved field ledger, round-trip
  verifies every mapped field (item 3.4), flattens the output where the artifact permits it, hashes
  it, writes it to the packages storage account, and emits a delivery event. Add it to `azure.yaml`,
  give it its own Dockerfile with a non-root user, and extend `tools/validate_foundation.py` to
  cover it.
- **Status:** Not started
- **Notes for future engineers:** The worker must accept only human-approved inputs. It has no
  authority to approve a value, and it must fail closed rather than deliver on a verification
  mismatch.

### 5.2 — Replace the in-memory catalog with the authoritative store

- **Priority:** P1
- **Description:** `src/core-api/CatalogRepository.cs` is an in-memory fixture registered as a
  singleton. Azure SQL is the authoritative relational store in the design, and nothing in the Core
  API connects to it.
- **Dependencies:** 1.2, 1.3.
- **Recommended action:** Add the SQL-backed catalog and case schema, connect using the
  managed-identity `AZURE_CLIENT_ID` credential with no connection string, keep the in-memory
  fixture behind a `dev`-only flag for contract tests, and add the Cosmos projection writer for
  rebuildable derived views. Preserve the existing contract exactly —
  `tools/validate_foundation.py` asserts the Alpha 0.2 priority forms, package composition, and the
  FAFSA external-workflow and reference-only modes against this file.
- **Status:** Not started
- **Notes for future engineers:** The validator reads `CatalogRepository.cs` as text and checks for
  literal strings such as `FAMILY_I130`, `"I-130A"`, and `FormArtifactKind.ExternalWorkflow`. If the
  fixture moves, update `validate_priority_and_modes()` in the same change or CI will fail
  misleadingly.

### 5.3 — Implement the processing worker adapters

- **Priority:** P1
- **Description:** `src/document-processing/worker.py` serves `/health` and `/ready` and nothing
  else. Its docstring states that the queue and Document Intelligence adapters are intentionally
  absent pending managed-identity endpoints and private-network approval. Those are step 4 of the
  data flow.
- **Dependencies:** 1.1, 1.2, 1.3, 2.2; `REVIEW.md` **R-12** (model IDs and pinned API version).
- **Recommended action:** Add a Service Bus receiver for `document-processing` that reads exactly
  one scoped message, a quarantine blob reader restricted to the single referenced object, a
  sanitization stage, a Document Intelligence client using the pinned API version over the private
  endpoint with no key fallback, and a create-only staging writer. The worker must retain no state
  between messages and must never hold a route to SQL or Cosmos.
- **Status:** Not started
- **Notes for future engineers:** The health handler deliberately suppresses all request logging so
  no path, query string, or document ID is emitted. Extend that discipline to the processing path —
  log correlation IDs, never content.

### 5.4 — Implement the acquisition Service Bus adapter

- **Priority:** P2
- **Description:** `publish_acquisition_proposals` in `src/functions/acquisition_contract.py` is a
  deliberate stub — the comment in `function_app.py` notes that the Service Bus adapter is deferred
  so local tests stay deterministic and the scaffold does not pretend to publish or activate
  anything. The orchestrator therefore returns metadata and publishes nothing.
- **Dependencies:** 1.2, 1.3; `REVIEW.md` **R-16** (`ACQUISITION_SCHEDULE`).
- **Recommended action:** Add an identity-based Service Bus output binding publishing to
  `catalog-acquisition`, keep the deterministic stub behind a test seam so the existing contract
  tests stay offline, and preserve the invariant that the function proposes and never activates.
- **Status:** Not started
- **Notes for future engineers:** The orchestrator returns `activatedEditionCount: 0`. That zero is
  an assertion about behaviour, not a placeholder — keep it, and add a test that fails if any code
  path can make it non-zero.

### 5.5 — Implement the UPL classifier and its fail-closed gate

- **Priority:** P1
- **Description:** The unauthorized-practice-of-law release gate is a stated Alpha 0.2 requirement
  with a zero-escape threshold and fail-closed behaviour on classifier or audit unavailability. No
  classifier, no gate, and no corpora exist in this repository.
- **Dependencies:** 1.2, 1.3; `REVIEW.md` **R-13** (corpora ownership, prohibited-act taxonomy,
  versioning scheme).
- **Recommended action:** Implement the classifier service in the AI trust zone with no
  authoritative write capability, wire `UPL_CLASSIFIER_VERSION` into the release gate, and make the
  gate deny by default when the classifier or its audit trail is unreachable. Add the corpora
  evaluation to CI as a release-blocking check.
- **Status:** Not started
- **Notes for future engineers:** Content Safety is not a substitute for this classifier — the
  Configuration Contract wiki page says so explicitly. They address different risks.

### 5.6 — Implement retention and erasure orchestration

- **Priority:** P1
- **Description:** Step 7 of the data flow requires scheduled lifecycle orchestration that deletes
  case content under the approved retention policy and records content-free, verifiable deletion
  evidence, withholding the receipt until active copies, versions, indexes, links, and applicable
  key material are all verified. None of it is implemented.
- **Dependencies:** 5.2, 5.3; `REVIEW.md` **R-11** (ratified retention contract).
- **Recommended action:** Add a Durable Functions orchestration that sweeps SQL, Cosmos projections,
  blob current versions and prior versions, temporary stores, delivery links, and search indexes,
  verifies each deletion, writes content-free evidence to the audit account, and only then issues
  the receipt. Pair it with the tests in item 3.5.
- **Status:** Not started
- **Notes for future engineers:** "Applicable key material" matters when per-case keys are used —
  coordinate with the CMK design in item 2.1 before deciding whether crypto-shredding is part of the
  erasure path.

### 5.7 — Implement short-lived revocable delivery links

- **Priority:** P1
- **Description:** The design delivers approved packages through a short-lived, revocable link.
  Nothing in the Core API or the storage configuration implements issuance, expiry, or revocation,
  and shared-key access is disabled on every storage account, so classic SAS issuance is not
  available.
- **Dependencies:** 1.3, 5.1.
- **Recommended action:** Issue user-delegation SAS tokens through the core managed identity with a
  short lifetime, record every issuance in the audit trail, and implement revocation — either by
  rotating the user-delegation key or by fronting delivery with an authorized API endpoint. Ensure
  the erasure sweep in item 5.6 invalidates outstanding links.
- **Status:** Not started
- **Notes for future engineers:** `allowSharedKeyAccess: false` is set on all four storage accounts,
  which is deliberate. Do not re-enable it to make SAS issuance easier — use user-delegation SAS.

---

## Phase 6 — Documentation improvements

### 6.1 — Publish the staged wiki pages and remove `wiki/`

- **Priority:** P1
- **Description:** Nine wiki pages are written and staged in `wiki/` — Home, Azure Deployment Plan,
  Architecture Overview, Environments and Release Path, Configuration Contract, Security and Data
  Protection, Pilot Policy and Compliance Gates, Azure Component Research Record, and Documentation
  Standards. They are staged rather than published because the automation that prepared them has no
  GitHub Wiki write access. Until they are published, the repository holds a documentation directory
  that the documentation model does not permit.
- **Dependencies:** `REVIEW.md` **R-17** (wiki write access).
- **Recommended action:** Clone `https://github.com/HybridCloudWorks/PEN-lapluma_infra.wiki.git`,
  copy the contents of `wiki/` into it, push, verify all nine pages render and that every
  cross-link resolves, then delete `wiki/` from this repository and update the README documentation
  table.
- **Status:** Not started
- **Notes for future engineers:** GitHub derives wiki page titles from filenames, so
  `Architecture-Overview.md` becomes "Architecture Overview" and the relative links in the pages
  (`[Architecture Overview](Architecture-Overview)`) resolve correctly. Do not rename the files.

### 6.2 — Author the operational runbooks

- **Priority:** P2
- **Description:** Incident response, on-call procedure, restore drill, and deletion drill runbooks
  are real-user pilot prerequisites. None exist.
- **Dependencies:** 3.6, 6.1; `REVIEW.md` **R-04** (operations and on-call owner).
- **Recommended action:** Write the runbooks as wiki pages once the operations owner is named and
  `staging` exists to validate them against. Link them from the wiki Home page.
- **Status:** Not started
- **Notes for future engineers:** Runbooks belong in the wiki, never in the repository — see the
  Documentation Standards wiki page.

### 6.3 — Record architecture decision records

- **Priority:** P3
- **Description:** Several foundational decisions are documented as conclusions with no recorded
  alternatives: AZD with Bicep over Terraform, three separate Container Apps environments over one
  with internal isolation, SQL as authoritative with Cosmos as rebuildable projections, Service Bus
  Premium over Standard, and Managed HSM over Key Vault-managed keys. A future engineer cannot tell
  what was rejected or why.
- **Dependencies:** 6.1.
- **Recommended action:** Write one short ADR per decision as a wiki page, capturing the context,
  the options considered, the decision, and its consequences. Link them from the Architecture
  Overview page.
- **Status:** Not started
- **Notes for future engineers:** The Azure Component Research Record wiki page holds the research
  that informed several of these; use it as the starting evidence rather than re-researching.

### 6.4 — Write the troubleshooting guide

- **Priority:** P3
- **Description:** No troubleshooting documentation exists. It cannot usefully be written before a
  `dev` environment produces real failure modes.
- **Dependencies:** 1.1 – 1.4, 6.1.
- **Recommended action:** Once `dev` is provisioned, collect the actual failure modes — private DNS
  resolution failures, RBAC propagation delays, Container Apps revision failures, Durable task hub
  conflicts — and write them up as a wiki page.
- **Status:** Not started
- **Notes for future engineers:** Wait for real failures. A speculative troubleshooting guide is
  worse than none, because it sends engineers down paths that do not apply.
