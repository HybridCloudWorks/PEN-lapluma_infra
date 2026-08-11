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
| [4](#phase-4--technical-debt) | Technical debt and repository hygiene | 4.1 |
| [5](#phase-5--feature-enhancements) | Feature and service completion | 5.1 – 5.7 |
| [6](#phase-6--documentation-improvements) | Documentation | 6.1 – 6.3 |

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
- **Dependencies:** The address range is no longer one of them. `snet-apim` exists at
  `10.42.8.0/24` with its own NSG, its prefix is wired through `SubnetPrefixes`, `.env.example` and
  `infra/main.parameters.json`, and `network.bicep` emits `apimSubnetId` for the module to consume.
  What remains is **R-03** for the SKU and its cost, **R-06** for the Entra registration and API
  audience, and **R-07** for the public hostname, DNS, and TLS certificate.
- **Recommended action:** Add an `apim` module publishing the Core API container app, validating the
  token audience R-06 settles, on the hostname R-07 settles. Set the subnet delegation at the same
  time: it is deliberately unset today because the v2 tiers integrate through a delegated subnet and
  the classic tiers in internal mode do not, so the right value depends on the tier R-03 picks. The
  subnet is empty, so setting it later costs nothing — unlike `snet-functions`, where the delegation
  had to be right before anything occupied it.
- **Status:** Partially unblocked — the subnet is reserved and wired; the resource needs three
  decisions.
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
  inside this VNet, so the internet rule never covered that path.

  The egress table is now ratified, and for four of the five zones it approves **no destination at
  all** — core, processing, AI, and private endpoints. For those four the existing deny *is* the
  approved posture rather than a placeholder for one, and no firewall is wanted: with four subnets
  needing nothing and one needing four hosts, a firewall would add a continuously billing resource
  and a second policy surface to express a list the NSGs already hold. The ratified table is on the
  Security and Data Protection wiki page.

  One zone remains: the functions zone is approved for the four Alpha 0.2 authority publication
  hosts on TCP 443. That row cannot be implemented as written, and the reason is worth knowing
  before someone tries. **NSG rules match IP prefixes and service tags, not hostnames.** The four
  authority hosts are CDN-backed, so their address ranges change and cannot be pinned in a rule.
- **Dependencies:** None for the four empty rows. The functions row needs either FQDN-capable
  filtering or an accepted change of mechanism, and it needs the source URLs `REVIEW.md` **R-14**
  records, since the allowlist derives from them rather than being written from memory.
- **Recommended action:** Nothing for the four empty zones — they are done. For the functions zone,
  wait: `src/functions/acquisition_contract.py` performs no upstream fetch today, so there is
  nothing to allow. When the edition-drift fetch is written, decide the mechanism then and land it
  with that work. The `DenyInternet` rule at priority 4000 will block the first fetch at runtime
  rather than at review, so that decision has to precede the code, not follow it. Add a validation
  test that asserts a processing replica cannot reach an arbitrary external host.
- **Status:** Complete for four of five zones. The functions row is deferred to the work that
  creates the need, with the FQDN constraint recorded above so it is not rediscovered.
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
- **Dependencies:** None remaining. The seven-year period is ratified; the audit row was the one
  value on that page whose consequence is irreversible, and it came back unchanged.
- **Recommended action:** Lock the policy in `staging` and `pilot` with
  `az storage container immutability-policy lock`, once those environments exist. Keep `dev`
  unlocked permanently so test data can be cleaned up. Record the lock in the operational runbooks.
- **Status:** Unblocked, waiting on an environment to lock it in.
- **Notes for future engineers:** **Locking is not a Bicep property, and there is deliberately no
  parameter offering to do it.** ARM exposes the lock as an explicit action on the policy resource,
  so a `lock: true` in the template would read like a guarantee and enforce nothing. It is an
  irreversible out-of-band step: a locked policy cannot be shortened or removed by an owner, by a
  subscription administrator, or by support. Extending it is the only permitted change. Do not run
  the lock command anywhere the number might still move, and never in `dev`.

### 2.4 — Add supply-chain and code scanning to CI

- **Priority:** P2
- **Description:** `.github/workflows/security-scanning.yml` runs CodeQL for C# and Python,
  dependency review on pull requests, a Trivy image scan of both built images, and a Trivy scan of
  the ARM template the Bicep compiles to. Both Trivy jobs now **enforce**: each scans once to JSON,
  converts that to SARIF and uploads it, and then fails the build on a CRITICAL or HIGH finding.
  What remains is not workflow configuration — GitHub secret scanning and push protection are
  repository settings, and the custom secret scan in `tools/validate_foundation.py` covers five
  specific patterns and is not a substitute for them.
- **Dependencies:** `REVIEW.md` **R-18** for the repository settings. The repository is public, so
  CodeQL, dependency review, secret scanning, and code-scanning uploads are all available without
  an Advanced Security licence — the blocker is administrative access, not licensing.
- **Recommended action:** Nothing further in this repository. Enabling secret scanning and push
  protection is R-18.
- **Status:** Complete for everything workflow configuration can do. Enforcement landed; the
  settings toggles moved to R-18 because no engineer can set them.
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

  Two things about the enforcement shape are load-bearing. The scan itself still exits 0 and the
  gate is a separate `trivy convert` step placed **after** the SARIF upload, so a failing build
  still publishes its findings to the Security tab and prints them as a table in the job log — a
  gate that suppresses the report it gates on leaves a reviewer with a red check and nowhere to
  look. And the image scans keep `--ignore-unfixed`: a CVE with no published fix is not something
  this repository can act on, and failing on one would teach reviewers that red means "wait for
  upstream". What survives that filter is a base-image bump, which is actionable. The
  infrastructure scan has no equivalent filter, deliberately — every misconfiguration Trivy reports
  against the compiled ARM is one this repository wrote.

  The original item also asked whether the weekly scheduled run should open an issue when a new
  advisory appears. It should not, and enforcement is why: a scheduled run that only wrote to the
  Security tab was easy to miss, but a scheduled run that *fails* is notified to the repository
  owner by GitHub already. Adding an issue-opening job would duplicate that notification and add a
  `issues: write` permission to a security workflow for no gain.

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
  ADR — the Architecture Decision Records wiki page carries the format — since it moves a security
  boundary. Decide separately whether the Durable HTTP
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

### 5.2 — Exercise the SQL catalog against a real database

- **Priority:** P1
- **Description:** `ICatalogSource` has two implementations. `CatalogRepository` is the in-memory
  fixture, now opt-in; `SqlCatalogSource` reads the authoritative store and is the default. The
  schema is in `src/core-api/Sql/001_catalog_schema.sql`, and `CatalogProjectionWriter` writes the
  rebuildable Cosmos views. **None of the SQL or Cosmos code has ever executed.** No environment has
  been provisioned, so the queries compile and are reviewed and that is the entire assurance behind
  them. There is also no migration runner: nothing applies the schema file.
- **Dependencies:** A provisioned `dev` environment, which is gated on the approvals in `REVIEW.md`.
- **Recommended action:** Apply the schema, seed it from the fixture, and write integration tests
  that run the four `ICatalogSource` methods against a real database and compare their output to
  the fixture's — the contract is identical, so any difference is a defect in the SQL path. Add a
  migration runner, and wire the projection writer to a rebuild command. Then run the same tests
  against Cosmos.
- **Status:** Not started
- **Notes for future engineers:** The first thing to check is the reader's column ordinals in
  `LoadPackagesAsync`: they are positional, and a column added to the `SELECT` in the wrong place
  shifts every one after it without any compile error. The wire-name mapping is derived from the
  enums' own `JsonStringEnumMemberName` attributes rather than restated, so the database, the JSON
  contract, and C# cannot disagree — keep it that way. `tools/validate_foundation.py` still reads
  `CatalogRepository.cs` as text for the Alpha 0.2 priority forms, so the fixture stays where it is.

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

### 5.4 — Add a dead-letter policy to every `domain-events` subscription

- **Priority:** P2
- **Description:** The acquisition adapter publishes to the `catalog-acquisition` queue and that
  path is complete. The `domain-events` topic still has no subscriber, and it carries a 14-day TTL
  with no dead-letter policy — because `deadLetteringOnMessageExpiration` belongs to
  `Microsoft.ServiceBus/namespaces/topics/subscriptions`, and `SBTopicProperties` rejects it. A
  message that reaches the TTL today would be discarded with no trace, if anything were subscribed.
- **Dependencies:** None. It becomes real the moment a subscription exists.
- **Recommended action:** Nothing to do until a subscriber exists. The requirement is now enforced
  rather than remembered: `validate_subscriptions_dead_letter` in `tools/validate_foundation.py`
  fails any subscription declared without `deadLetteringOnMessageExpiration: true`, so the first one
  added cannot omit it.
- **Status:** Guarded, pending a subscriber. The rule that was the substance of this item exists;
  the subscription it applies to does not, and creating one belongs to whichever item introduces a
  projection worker.
- **Notes for future engineers:** `infra/modules/messaging.bicep` carries a comment on the topic
  marking the spot. The queues already set the flag, so the pattern to copy is directly above. The
  validator rule matches nothing today, deliberately — do not delete it as dead code, because the
  moment it has something to match is the moment it earns its place.

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
- **Description:** Fifteen wiki pages are written and staged in `wiki/` — the original nine (Home,
  Azure Deployment Plan, Architecture Overview, Environments and Release Path, Configuration
  Contract, Security and Data Protection, Pilot Policy and Compliance Gates, Azure Component
  Research Record, Documentation Standards), plus the five architecture decision records and their
  index, plus the four operational runbooks and their index. They are staged rather than published
  because the automation that prepared them has no GitHub Wiki write access. Until they are
  published, the repository holds a documentation directory that the documentation model does not
  permit.
- **Dependencies:** `REVIEW.md` **R-17** (wiki write access).
- **Recommended action:** Clone `https://github.com/HybridCloudWorks/PEN-lapluma_infra.wiki.git`,
  copy the contents of `wiki/` into it, push, verify all fifteen pages render and that every
  cross-link resolves, then delete `wiki/` from this repository and update the README documentation
  table.
- **Status:** Blocked. The block is confirmed rather than assumed: cloning the wiki remote succeeds
  and pushing returns HTTP 403, because the wiki is a separate repository from the perspective of
  access control and is not in the authorized set. Adding it as a source fails too — GitHub does not
  expose `<repo>.wiki` as a grantable repository. This will not clear by retrying.
- **Notes for future engineers:** GitHub derives wiki page titles from filenames, so
  `Architecture-Overview.md` becomes "Architecture Overview" and the relative links in the pages
  (`[Architecture Overview](Architecture-Overview)`) resolve correctly. Do not rename the files.
  This applies to the new pages too: `ADR-0001-AZD-and-Bicep-over-Terraform.md` renders as
  "ADR 0001 AZD and Bicep over Terraform", and the cross-links between records are written against
  those derived titles.

### 6.2 — Author the operational runbooks

- **Priority:** P2
- **Description:** Incident response, on-call procedure, restore drill, and deletion drill runbooks
  are real-user pilot prerequisites. All four are now **drafted** and staged in `wiki/`, together
  with an index page. What remains is validation: no step in any of them has been executed, because
  no environment exists to execute it against.
- **Dependencies:** 3.5, 6.1; `REVIEW.md` **R-04** (operations and on-call owner), **R-11** (the
  deletion drill's pass criteria are the retention numbers).
- **Recommended action:** Once `staging` exists, execute each runbook by hand and correct it against
  what actually happened. The restore and deletion drills are the two that will change most —
  every command in them is written against the resource shapes declared in `infra/`, not against a
  deployed resource, and every timing figure is an intention rather than a measurement. Then fill in
  the role names from R-04 and the response targets the operations owner agrees.
- **Status:** Drafted, unvalidated. Each page carries a banner saying so.
- **Notes for future engineers:** Runbooks belong in the wiki, never in the repository — see the
  Documentation Standards wiki page. Resist the urge to tidy away the "never executed" banners
  before the drills have actually been run: a runbook that looks authoritative and has never been
  tested is worse than one that admits what it is, because someone will follow it under pressure.

### 6.3 — Write the troubleshooting guide

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
