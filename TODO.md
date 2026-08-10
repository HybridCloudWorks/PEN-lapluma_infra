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
| [1](#phase-1--critical-fixes) | Critical fixes: the edge zone is not modelled | 1.1 |
| [2](#phase-2--security-improvements) | Security improvements | 2.1 – 2.5 |
| [3](#phase-3--stability-improvements) | Stability, observability, and evidence | 3.1 – 3.5 |
| [4](#phase-4--technical-debt) | Technical debt and repository hygiene | 4.1 – 4.2 |
| [5](#phase-5--feature-enhancements) | Feature and service completion | 5.1 – 5.7 |
| [6](#phase-6--documentation-improvements) | Documentation | 6.1 – 6.4 |

---

## Phase 1 — Critical fixes

The private endpoints, hosting layer, role assignments, and Azure Monitor Private Link Scope are in
place. What remains is the edge, which cannot be modelled until an address range exists for it.

### 1.1 — Model the API Management edge

- **Priority:** P0
- **Description:** The Core API container app is created with `external: false` ingress, so nothing
  publishes it. That is correct for the current shape — an internet-facing authoritative API with no
  gateway in front of it would be worse — but it means the Edge zone does not exist yet, and no
  client can reach the service.
- **Dependencies:** `REVIEW.md` **R-09** is the hard one — APIM needs its own dedicated subnet and
  the address plan allocates five, none of them for the edge. Also **R-03** for the SKU cost,
  **R-06** for the Entra application registration and API audience, and **R-07** for the public
  hostname, DNS, and TLS certificate.
- **Recommended action:** Once R-09 allocates a range, add `apim` to the `SubnetPrefixes` type in
  `infra/main.bicep`, its variable to `.env.example` and `infra/main.parameters.json`, and a subnet
  to `infra/modules/network.bicep`. Then add an `apim` module publishing the Core API container app,
  validating the token audience R-06 settles, on the hostname R-07 settles.
- **Status:** Not started
- **Notes for future engineers:** APIM in internal VNet mode requires the Premium or Developer tier;
  the cheaper tiers cannot join a virtual network at all, which makes this an R-03 question before
  it is a networking one. `tools/validate_foundation.py` asserts that every resource disabling
  public network access has a private endpoint wired to it — APIM in internal mode has no
  `publicNetworkAccess` property and will not trip that rule, so do not read its silence as
  approval.

---

## Phase 2 — Security improvements

### 2.1 — Bind customer-managed keys from Managed HSM

- **Priority:** P1
- **Description:** `infra/modules/security.bicep` provisions a Managed HSM pool, but no storage
  account, SQL database, or Cosmos account references a customer-managed key. Customer-managed
  encryption is stated as a pilot prerequisite rather than later hardening, so the HSM currently
  costs money without protecting anything.
- **Dependencies:** `REVIEW.md` **R-10** (HSM administrators, key hierarchy, rotation policy, CMK
  coverage). The private endpoints and role assignments this used to wait on are in place.
- **Recommended action:** Once the key hierarchy is approved, create the keys, grant each service's
  system- or user-assigned identity the `Managed HSM Crypto Service Encryption User` role, and set
  the CMK properties on the four storage accounts, the SQL database transparent-data-encryption
  protector, and the Cosmos account. Implement the approved rotation policy and verify that key
  revocation renders data inaccessible as expected.
- **Status:** Not started
- **Notes for future engineers:** Managed HSM has purge protection enabled and a 90-day soft-delete
  retention. Bootstrapping the pool with the wrong `initialAdminObjectIds` is expensive to undo —
  confirm R-10 before the first provisioning run, not after.

### 2.2 — Implement the approved egress allowlist

- **Priority:** P1
- **Description:** The rule *structure* is in place. Every one of the five NSGs now carries a
  baseline `DenyInternetEgress`, where four of them previously carried nothing at all — the AI zone
  had unrestricted outbound internet access. The processing NSG additionally denies the `Sql` and
  `AzureCosmosDB` service tags and the private-endpoint subnet prefix, which closes code review
  finding **F-04**: an NSG carries an implicit `AllowVnetOutBound`, and every private endpoint sits
  inside this VNet, so the internet rule never covered that path. What remains is the mechanism and
  the destinations — there is still no route table, no firewall, and no DNS egress control.
- **Dependencies:** `REVIEW.md` **R-09** (approved egress destinations and enforcement mechanism).
- **Recommended action:** Implement the approved mechanism — an Azure Firewall with forced tunneling
  via UDR, or an equivalent — with an explicit allowlist, and punch the approved destinations
  through the baseline denies rather than widening them. Add a validation test that asserts a
  processing replica cannot resolve or reach an arbitrary external host.
- **Status:** Partially complete — rule structure and the F-04 denies are in; the allowlist and its
  enforcement mechanism remain.
- **Notes for future engineers:** Container Apps environments need platform-level egress for image
  pulls and control-plane traffic. Use the ACR private endpoint plus the documented required FQDNs;
  do not widen the allowlist to "all Azure services". The baseline deny sits at priority 4000 and
  the F-04 database denies at 3000–3020, so an allowlist has the whole range below 3000 to work in
  without editing what is already there.

### 2.3 — Lock the audit immutability policy

- **Priority:** P1
- **Description:** The audit container now carries a time-based immutability policy with a
  parameterized window, defaulting to seven years, and `allowProtectedAppendWrites` so evidence
  appended over time is still protected. It is created **unlocked**, and an unlocked policy can be
  shortened or removed — which is most of the protection missing.
- **Dependencies:** `REVIEW.md` **R-11** (audit metadata retention, proposed 7 years).
- **Recommended action:** Once R-11 ratifies the period, lock the policy in `staging` and `pilot`
  with `az storage container immutability-policy lock`. Keep `dev` unlocked permanently so test data
  can be cleaned up. Record the lock in the deployment runbook under 6.2.
- **Status:** Not started
- **Notes for future engineers:** **Locking is not a Bicep property, and there is deliberately no
  parameter offering to do it.** ARM exposes the lock as an explicit action on the policy resource,
  so a `lock: true` in the template would read like a guarantee and enforce nothing. It is an
  irreversible out-of-band step: a locked policy cannot be shortened or removed by an owner, by a
  subscription administrator, or by support. Extending it is the only permitted change. Do not run
  the lock command until R-11 has ratified the number.

### 2.4 — Add supply-chain and code scanning to CI

- **Priority:** P2
- **Description:** `.github/workflows/security-scanning.yml` now runs CodeQL for C# and Python,
  dependency review on pull requests, a Trivy image scan of both built images, and a Trivy scan of
  the ARM template the Bicep compiles to. Two parts of the original item remain. First, both Trivy
  scans run with `exit-code: '0'` — they publish findings to code scanning but cannot fail a build,
  because no one has triaged the base-image and template baseline yet. Second, GitHub secret
  scanning and push protection are repository settings, not workflow configuration, and are still
  off. The custom secret scan in `tools/validate_foundation.py` covers five specific patterns and is
  not a substitute for them.
- **Dependencies:** None. The repository is public, so CodeQL, dependency review, secret scanning,
  and code-scanning uploads are all available without an Advanced Security licence.
- **Recommended action:** Triage the first full scan results, then set a severity threshold that
  fails the build on both Trivy jobs — enforcing an untriaged baseline would only teach reviewers to
  ignore a red pipeline. Enable secret scanning and push protection in the repository settings.
  Consider whether the weekly schedule should also open an issue when a new advisory appears, since
  a scheduled run that only writes to the Security tab is easy to miss.
- **Status:** Partially complete — scanners run and report; enforcement and the settings toggles
  remain.
- **Notes for future engineers:** Enabling secret scanning is the part of this item that covers
  provider-issued credentials — Entra client secrets, GitHub tokens, and the like. A pattern for
  those was considered for `tools/validate_foundation.py` and deliberately not added: they have no
  distinctive enough shape to match without a false-positive rate that would train reviewers to
  ignore the scan, and GitHub's scanner uses partner-supplied signatures instead. The custom
  scanner covers what is specific to this repository's invariants — private keys, storage
  connection strings, bare account keys, shared-access signatures, and concrete tenant or
  subscription assignments — and is not a substitute for the platform feature.
  The SHA pinning across both workflows is deliberate. Do not relax
  it to tags for convenience. Trivy has no Bicep parser, which is why the infrastructure job
  compiles to ARM first and scans that; if the Bicep pin moves, the scanned artifact moves with it.
  The SARIF upload steps are skipped for pull requests from forks, which cannot be granted
  `security-events: write`. Trivy deliberately runs from a digest-pinned image rather than
  `aquasecurity/trivy-action`: that action's setup step downloads a release binary through an
  install script at run time, which is unpinned and was observed failing outright here. Scanning a
  `docker save` tarball rather than a running daemon keeps the Docker socket out of the scanner.

### 2.5 — Replace the Functions host shared-key auth default

- **Priority:** P2
- **Description:** Code review finding **F-17**. `src/functions/function_app.py` constructs
  `df.DFApp(http_auth_level=func.AuthLevel.FUNCTION)`, which authenticates callers with a function
  key — a long-lived shared secret carried in a query string or header. Every Azure resource in
  `infra/` implements the opposite posture: `disableLocalAuth` on Service Bus, Cosmos, and Log
  Analytics, `allowSharedKeyAccess: false` on all four storage accounts, RBAC-only Key Vault, and
  Entra-only SQL. No HTTP trigger exists yet, but this is the app-wide default that the next one
  inherits, and the Durable extension's built-in orchestration management endpoints already carry
  it.
- **Dependencies:** 1.1 (APIM must front the app first); `REVIEW.md` **R-07** for the hostname.
- **Recommended action:** Set `AuthLevel.ANONYMOUS` and terminate authentication at APIM with
  Entra, which is the topology the trust-zone model already describes. Record the decision as an
  ADR under 6.3, since it moves a security boundary. Decide separately whether the Durable HTTP
  management API should be disabled outright through `extensions.durableTask` in `host.json`.
- **Status:** Not started
- **Notes for future engineers:** Do not flip this in isolation. `ANONYMOUS` is only safe once
  network restriction and APIM fronting are both in place — land it with 1.1, not before.

---

## Phase 3 — Stability improvements

### 3.1 — Decide and implement resilience settings

- **Priority:** P1
- **Description:** SQL sets `zoneRedundant: false`, Cosmos sets `isZoneRedundant: false`, three of
  four storage accounts use `Standard_LRS`, and Service Bus Premium runs at capacity 1 with one
  messaging partition. These are cost-driven defaults, not measured decisions, and the SQL
  configuration also carries a 60-minute auto-pause that will produce cold-start latency on the
  authoritative store.
- **Dependencies:** `REVIEW.md` **R-03** (cost approval), **R-10** (SQL floor, maximum, zone
  redundancy, backup policy).
- **Recommended action:** Derive the resilience settings from an agreed SLO rather than from
  defaults, and document the resulting RTO and RPO on the Environments and Release Path wiki page.
  Confirm whether auto-pause is acceptable for a user-facing authoritative store. The
  parameterization this used to require is done: `sqlZoneRedundant`, `cosmosZoneRedundant`,
  `auditStorageSku`, `defaultStorageSku`, `serviceBusCapacity`, `serviceBusPartitions`, and
  `sqlAutoPauseMinutes` are all supplied per environment through `.env.example`, so this item is
  now a decision about values rather than a code change.
- **Status:** Not started
- **Notes for future engineers:** Log Analytics retention was parameterized before any diagnostic
  setting existed, so the 365-day default has never been weighed against real ingested volume. Now
  that every resource routes logs and metrics to the workspace, that number is measurable — take a
  reading before ratifying it, because retention on an empty workspace costs nothing and retention
  on a populated one is most of the observability bill.
  `GP_S_Gen5` with `minCapacity: 0.5` and `autoPauseDelay: 60` means
  the first request after an idle hour pays a resume penalty. For a ~40-case supervised pilot that
  may well be fine — but it should be a recorded decision, not an accident.

### 3.2 — Implement the invariant test suite

- **Priority:** P1
- **Description:** Cross-tenant, cross-folder, person-boundary, and agent-no-write invariant tests
  are required to pass on every build. Today the repository has five Python contract tests and no
  invariant tests at all.
- **Dependencies:** 5.2. The Core API's authorization boundary exists now, so a token-scoped
  test has something to assert against — supplying real tokens still needs `REVIEW.md` **R-06**.
- **Recommended action:** Build an integration test project that asserts, against a running `dev`
  environment: a token scoped to tenant A cannot read tenant B's data; a folder-scoped grant cannot
  traverse to a sibling folder; a person boundary cannot be crossed by any API path; and no AI or
  agent component can perform an authoritative write. Wire it into CI as a required check.
- **Status:** Not started
- **Notes for future engineers:** These are the tests that justify the trust-zone architecture. If
  they cannot be written against the implementation, the implementation has drifted from the design.

### 3.3 — Implement package round-trip fidelity verification

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

### 3.4 — Implement erasure and retention sweep integration tests

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

### 3.5 — Automate restore and deletion drills

- **Priority:** P2
- **Description:** Restore and deletion drill evidence is a real-user pilot prerequisite, and no
  drill procedure or automation exists.
- **Dependencies:** 3.4.
- **Recommended action:** Script a periodic drill against `staging` that restores SQL and Cosmos to
  a point in time, verifies data integrity, executes a deletion sweep, and writes content-free
  evidence to the audit account. Document the procedure as a runbook (item 6.2).
- **Status:** Not started
- **Notes for future engineers:** The drill evidence itself must be content-free and pseudonymized —
  it lands in the audit account, which is subject to the immutability policy from item 2.3.

---

## Phase 4 — Technical debt

### 4.1 — Add `CODEOWNERS` once the approval owners are named

- **Priority:** P2
- **Description:** The rest of the governance set landed — `.github/dependabot.yml`,
  `.github/pull_request_template.md`, `.github/SECURITY.md`, `.github/CONTRIBUTING.md`, and an
  Apache-2.0 `LICENSE` with `NOTICE`. `CODEOWNERS` did not, and it is the one that matters most:
  the design depends on two-person approval of field maps and on named security and privacy owners,
  and nothing enforces either today.
- **Dependencies:** `REVIEW.md` **R-04** (named owners). This is a hard block, not a soft one — see
  the notes.
- **Recommended action:** Once R-04 names the owners and their GitHub identities exist, add
  `.github/CODEOWNERS` requiring review from the platform owner for `infra/`, the security owner for
  `infra/modules/security.bicep`, and the catalog and compliance owners for `contracts/`. Then turn
  on "Require review from Code Owners" in branch protection, without which the file only requests
  review and never blocks a merge.
- **Status:** Not started
- **Notes for future engineers:** Do not fill this in with placeholder handles to make the file look
  complete. **GitHub silently ignores a `CODEOWNERS` entry naming a user or team that does not
  exist, or that lacks write access** — no error, no warning, and the file reads as though the
  control is in place while requesting review from nobody. That failure mode is the reason this item
  was left open rather than shipped with invented owners. At the time of writing the organisation
  has no teams, and the repository has two admins, so any entry must name real accounts. Verify
  after adding it by opening a pull request touching each guarded path and confirming the expected
  reviewer is actually requested.

### 4.2 — Confirm the Functions subnet delegation matches the hosting SKU

- **Priority:** P2
- **Description:** `infra/modules/network.bicep` delegates `snet-functions` to
  `Microsoft.App/environments`. That is correct for Flex Consumption, which is the stated preferred
  baseline, but wrong for Elastic Premium, which requires `Microsoft.Web/serverFarms`. The plan
  allows an approved equivalent if Flex Consumption features are unavailable in East US 2, so the
  delegation is only conditionally correct.
- **Dependencies:** `REVIEW.md` **R-03** (region capability verification).
- **Recommended action:** When the region verification confirms the available Functions hosting SKU,
  re-check the delegation and correct it if the SKU changed. Add a comment in the network module
  recording which SKU the delegation assumes.
- **Status:** Not started
- **Notes for future engineers:** Subnet delegation cannot be changed while resources occupy the
  subnet, so getting this right before the first provisioning run avoids a rebuild.

---

---

## Phase 5 — Feature enhancements

### 5.1 — Implement the package worker

- **Priority:** P1
- **Description:** The architecture lists a package and delivery service at `src/package-worker`
  responsible for deterministic PDF generation, verification, and delivery. The directory does not
  exist, and `azure.yaml` declares no such service. Without it, step 6 of the data flow — the entire
  output half of the product — has no implementation.
- **Dependencies:** 5.2; `REVIEW.md` **R-14** (verified artifacts and approved field maps).
- **Recommended action:** Create `src/package-worker` as a .NET 10 queue-driven worker or Container
  Apps Job. It fills an edition-pinned official form from a human-approved field ledger, round-trip
  verifies every mapped field (item 3.3), flattens the output where the artifact permits it, hashes
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
- **Dependencies:** None. The hosting layer and role assignments are in place.
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
- **Dependencies:** 2.2; `REVIEW.md` **R-12** (model IDs and pinned API version).
- **Recommended action:** Add a Service Bus receiver for `document-processing` that reads exactly
  one scoped message, a quarantine blob reader restricted to the single referenced object, a
  sanitization stage, a Document Intelligence client using the pinned API version over the private
  endpoint with no key fallback, and a create-only staging writer. The worker must retain no state
  between messages and must never hold a route to SQL or Cosmos.
- **Status:** Not started
- **Notes for future engineers:** `contracts.ProcessingRequest` already validates the blob URIs this
  zone may touch: HTTPS only, the Azure Blob host suffix, no credentials, no query string or
  fragment, and a container-and-blob path, with the normalised forms compared so input and output
  cannot be the same object spelled two ways. Read the object named by the validated URI — do not
  re-derive a URI from message fields, and do not relax the host pin to reach a non-Azure endpoint.
  The health handler deliberately suppresses all request logging so
  no path, query string, or document ID is emitted. Extend that discipline to the processing path —
  log correlation IDs, never content.

### 5.4 — Implement the acquisition Service Bus adapter

- **Priority:** P2
- **Description:** `publish_acquisition_proposals` in `src/functions/acquisition_contract.py` is a
  deliberate stub — the comment in `function_app.py` notes that the Service Bus adapter is deferred
  so local tests stay deterministic and the scaffold does not pretend to publish or activate
  anything. The orchestrator therefore returns metadata and publishes nothing.
- **Dependencies:** `REVIEW.md` **R-16** (`ACQUISITION_SCHEDULE`).
- **Recommended action:** Add an identity-based Service Bus output binding publishing to
  `catalog-acquisition`, keep the deterministic stub behind a test seam so the existing contract
  tests stay offline, and preserve the invariant that the function proposes and never activates.
- **Status:** Not started
- **Notes for future engineers:** The orchestrator returns `activatedEditionCount: 0`. That zero is
  an assertion about behaviour, not a placeholder — keep it, and add a test that fails if any code
  path can make it non-zero. `propose_acquisition_batch` now requires the exact key set
  `REQUIRED_REQUEST_KEYS`, and a test reads the timer trigger's `client_input` keys out of
  `function_app.py` to confirm the two agree. If this item adds a field to the orchestration input,
  add it to that constant in the same change or the test fails — which is the point, since the
  input is persisted to the task hub and replayed. The `domain-events` topic carries a 14-day TTL
  but no dead-letter policy, because `deadLetteringOnMessageExpiration` belongs to
  `Microsoft.ServiceBus/namespaces/topics/subscriptions` and no subscription is modelled yet — every
  subscription this item adds must set it, or messages that reach the TTL are discarded with no
  trace. Item 4.4 left the comment in `infra/modules/messaging.bicep` marking the spot.

### 5.5 — Implement the UPL classifier and its fail-closed gate

- **Priority:** P1
- **Description:** The unauthorized-practice-of-law release gate is a stated Alpha 0.2 requirement
  with a zero-escape threshold and fail-closed behaviour on classifier or audit unavailability. No
  classifier, no gate, and no corpora exist in this repository.
- **Dependencies:** `REVIEW.md` **R-13** (corpora ownership, prohibited-act taxonomy,
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
  the receipt. Pair it with the tests in item 3.4.
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
- **Dependencies:** 5.1.
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
- **Dependencies:** 3.5, 6.1; `REVIEW.md` **R-04** (operations and on-call owner).
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
- **Dependencies:** 1.1, 6.1.
- **Recommended action:** Once `dev` is provisioned, collect the actual failure modes — private DNS
  resolution failures, RBAC propagation delays, Container Apps revision failures, Durable task hub
  conflicts — and write them up as a wiki page.
- **Status:** Not started
- **Notes for future engineers:** Wait for real failures. A speculative troubleshooting guide is
  worse than none, because it sends engineers down paths that do not apply.
