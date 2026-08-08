# Review — human-resolvable blockers

Items on this page cannot be resolved by an engineer without external input: a decision, an
approval, a credential, or an access grant. Anything an engineer can solve independently belongs in
`TODO.md`.

Every item states the problem, why it blocks progress, the required owner, the required action, the
impact if it stays unresolved, its references, and the recommended next step.

## Index

| ID | Blocker | Required owner |
|----|---------|----------------|
| [R-01](#r-01--azure-tenant-and-subscription-identity-are-unconfirmed) | Azure tenant and subscription identity are unconfirmed | Platform owner |
| [R-02](#r-02--subscription-authorization-for-real-pilot-pii-and-cost-bearing-services) | Subscription authorization for real pilot PII and cost-bearing services | Subscription owner |
| [R-03](#r-03--east-us-2-capability-quota-and-cost-approval) | East US 2 capability, quota, and cost approval | Platform owner and finance owner |
| [R-04](#r-04--named-approval-owners-are-undefined) | Named approval owners are undefined | Executive sponsor |
| [R-05](#r-05--resource-naming-tagging-and-cost-center-standard) | Resource naming, tagging, and cost-center standard | Governance owner |
| [R-06](#r-06--entra-application-registrations-and-api-audience) | Entra application registrations and API audience | Identity owner |
| [R-07](#r-07--public-hostname-dns-and-tls-certificate-ownership) | Public hostname, DNS, and TLS certificate ownership | Platform owner |
| [R-08](#r-08--ios-bundle-identifier-and-app-attest-environment) | iOS bundle identifier and App Attest environment | Mobile owner and security owner |
| [R-09](#r-09--network-address-plan-private-dns-model-and-approved-egress) | Network address plan, private DNS model, and approved egress | Network owner and security owner |
| [R-10](#r-10--sql-administrator-group-and-managed-hsm-key-governance) | SQL administrator group and Managed HSM key governance | Data owner and CISO |
| [R-11](#r-11--retention-erasure-and-backup-policy-alignment) | Retention, erasure, and backup policy alignment | Privacy owner |
| [R-12](#r-12--document-intelligence-and-generative-ai-approvals) | Document Intelligence and generative AI approvals | AI owner and RAI owner |
| [R-13](#r-13--upl-classifier-release-gate-ownership) | UPL classifier release-gate ownership | Compliance owner |
| [R-14](#r-14--official-form-artifact-activation-approvals) | Official form artifact activation approvals | Catalog owner and compliance owner |
| [R-15](#r-15--independent-penetration-test-authorization) | Independent penetration test authorization | Security owner |
| [R-16](#r-16--catalog-acquisition-schedule-decision) | Catalog acquisition schedule decision | Catalog operations owner |
| [R-17](#r-17--github-wiki-write-access-for-documentation-publication) | GitHub Wiki write access for documentation publication | Repository administrator |

---

### R-01 — Azure tenant and subscription identity are unconfirmed

**Problem.** The Azure tenant and subscription that will host the pilot have never been stated. No
display name and no GUID exist for either.

**Why it blocks progress.** Item 1 and item 2 of the hard context gate. Nothing Azure-connected may
be created — no AZD environment, no preflight, no role assignment, no resource-provider
registration, no provisioning, no deployment — until both are recorded and explicitly confirmed. The
values must never be inferred from CLI context or subscription defaults.

**Required owner.** Platform owner.

**Required action.** Provide the Azure tenant display name and tenant ID, and the Azure subscription
display name and subscription ID, and confirm both explicitly in writing.

**Impact if unresolved.** The entire deployment path stays closed. `dev` cannot be created, so no
integration testing, no preflight, and no cost measurement can occur.

**References.** `AZURE_TENANT_ID` and `AZURE_SUBSCRIPTION_ID` on the Configuration Contract wiki
page; the hard context gate on the Azure Deployment Plan wiki page.

**Recommended next step.** Platform owner supplies both pairs; an engineer records them in the AZD
environment (never in Git) and confirms `az account show` matches the stated display names before
any further Azure command runs.

---

### R-02 — Subscription authorization for real pilot PII and cost-bearing services

**Problem.** No one has confirmed that the target subscription is authorized to hold real pilot PII
and to incur cost-bearing resources.

**Why it blocks progress.** Item 3 of the hard context gate. The pilot data plane is classified
production-sensitive PII, and the planning baseline includes Managed HSM, Premium Service Bus, and
API Management, all of which bill continuously.

**Required owner.** Subscription owner, with privacy owner concurrence.

**Required action.** Confirm in writing that the subscription is approved for production-sensitive
PII under US-only residency and for cost-bearing service creation.

**Impact if unresolved.** Deployment cannot proceed. Provisioning into an unauthorized subscription
would be a privacy and financial governance incident.

**References.** Requirements table on the Azure Deployment Plan wiki page; `PILOT_DATA_RESIDENCY` on
the Pilot Policy and Compliance Gates wiki page. Depends on **R-01**.

**Recommended next step.** Obtain written authorization referencing the specific subscription ID
confirmed in R-01, and attach it to the deployment approval record.

---

### R-03 — East US 2 capability, quota, and cost approval

**Problem.** `eastus2` is approved in principle only. No one has verified that it supports every
selected service and SKU, the required quota, the required private-networking features, and the
Document Intelligence models the pilot uses. No estimated monthly cost exists for `dev`, `staging`,
or `pilot`.

**Why it blocks progress.** Item 4 of the hard context gate. The planning baseline includes API
Management Standard v2, Service Bus Premium, Managed HSM `Standard_B1`, and Document Intelligence
Standard — the last two are significant recurring cost and quota commitments.

**Required owner.** Platform owner for capability and quota; finance owner for cost.

**Required action.** Produce a region capability and quota verification record covering every
service in the service mapping, and an estimated monthly cost for each of the three environments
including Managed HSM and API Management. Obtain budget approval.

**Impact if unresolved.** Preflight may pass and provisioning still fail on quota, or succeed and
produce unbudgeted spend. Neither is acceptable for a supervised pilot.

**References.** Service mapping on the Architecture Overview wiki page; region note on the Azure
Component Research Record wiki page. Depends on **R-01**.

**Recommended next step.** Run the region and quota check against the confirmed subscription, record
the result, and route the cost estimate to the finance owner for sign-off.

---

### R-04 — Named approval owners are undefined

**Problem.** No named person or group holds deployment authority, security approval, privacy
approval, compliance approval, or operations approval. No security review board decision, DPIA,
counsel boundary approval, incident plan, or restore and deletion drill evidence exists.

**Why it blocks progress.** Item 5 of the hard context gate. Protected-environment approval, the
real-data gates, and the expansion checkpoint all require a named accountable owner. Without one,
the gates cannot be exercised even if every technical control is complete.

**Required owner.** Executive sponsor, to designate the individual owners.

**Required action.** Name the deployment approver or approval group, the security owner, the privacy
owner, the compliance owner, the operations and on-call owner, and the finance owner. Record their
Entra group object IDs for `AZURE_SECURITY_GROUP_OBJECT_ID`,
`AZURE_OPERATIONS_GROUP_OBJECT_ID`, `AZURE_PRIVACY_GROUP_OBJECT_ID`, and
`AZURE_DEPLOYMENT_PRINCIPAL_OBJECT_ID`.

**Impact if unresolved.** Every other blocker on this page is unassignable. `pilot` can never be
created, because there is nobody authorized to approve it.

**References.** Configuration Contract wiki page, Azure context table; real-user pilot prerequisites
on the Pilot Policy and Compliance Gates wiki page.

**Recommended next step.** Executive sponsor circulates a one-page ownership matrix; the platform
owner then creates the corresponding Entra groups and supplies their object IDs.

---

### R-05 — Resource naming, tagging, and cost-center standard

**Problem.** No approved value exists for `AZURE_RESOURCE_NAME_PREFIX`, `AZURE_RESOURCE_OWNER`,
`AZURE_RESOURCE_TAGS`, or `AZURE_COST_CENTER`.

**Why it blocks progress.** The prefix is a required Bicep parameter constrained to 3–12 lowercase
characters, and it participates in globally unique resource names. Owner and cost-center values are
required tags on every resource.

**Required owner.** Governance owner, with the finance owner for the cost center.

**Required action.** Approve a naming prefix, the accountable owner string, the full required tag
set, and the cost-center identifier.

**Impact if unresolved.** Provisioning cannot run — the parameter file has no values to substitute —
and any resources created without correct tags would be untraceable for cost and governance
reporting.

**References.** Azure context table on the Configuration Contract wiki page; resource tagging on the
Environments and Release Path wiki page.

**Recommended next step.** Governance owner approves the prefix against the organization's naming
standard; an engineer then validates global uniqueness for the storage, Key Vault, Managed HSM, and
Cosmos names the prefix would produce.

---

### R-06 — Entra application registrations and API audience

**Problem.** No Entra registrations exist for the applicant API or the staff API, and no verified
application URI has been chosen as the token audience. It is also unconfirmed whether the workforce
tenant is the same tenant that holds the Azure resources.

**Why it blocks progress.** APIM cannot validate a token without a registered audience, and the
identity and policy boundary in the Core API cannot be implemented against an undefined issuer.
Staff access additionally requires phishing-resistant MFA and Conditional Access policy decisions.

**Required owner.** Identity owner.

**Required action.** Create or nominate the applicant public-client and API registrations and the
staff API registration, approve the registration design, publish the verified application URI to use
as `ENTRA_API_AUDIENCE`, and confirm `ENTRA_STAFF_TENANT_ID`.

**Impact if unresolved.** The Edge trust zone cannot be built, so the Core API cannot be exposed
safely and the iOS client has nothing to authenticate against.

**References.** Identity and public interface table on the Configuration Contract wiki page; Edge
zone on the Architecture Overview wiki page. Related engineering work: `TODO.md` item **2.3**.

**Recommended next step.** Identity owner drafts the registration design for security review, then
provisions the registrations in the tenant confirmed in R-01.

---

### R-07 — Public hostname, DNS, and TLS certificate ownership

**Problem.** No approved public hostname exists for API Management, and DNS and certificate
ownership have not been proven. `APIM_PUBLISHER_NAME` and `APIM_PUBLISHER_EMAIL` are also unset.

**Why it blocks progress.** APIM cannot be provisioned with a custom domain without a hostname whose
DNS zone and certificate the organization demonstrably controls. The publisher email must be a
monitored role mailbox, not a personal address.

**Required owner.** Platform owner for the hostname and DNS; product owner for the publisher name;
operations owner for the role mailbox.

**Required action.** Approve the HTTPS hostname, prove DNS zone control, arrange certificate
issuance and renewal, and supply the publisher name and role mailbox.

**Impact if unresolved.** The public ingress cannot be created, so no environment can serve the iOS
client, and the associated-domains configuration on the mobile side cannot be finalized.

**References.** Identity and public interface table on the Configuration Contract wiki page.
Related to **R-08**.

**Recommended next step.** Platform owner confirms which DNS zone will host the API name and whether
certificates come from an existing organizational issuance process.

---

### R-08 — iOS bundle identifier and App Attest environment

**Problem.** `IOS_BUNDLE_IDENTIFIER` and `IOS_APP_ATTEST_ENVIRONMENT` are unset.

**Why it blocks progress.** App Attest verification, associated domains, and the APIM client-policy
rules all key off the bundle identifier. The pilot must use the approved production attestation
policy, which is a security decision rather than a build setting.

**Required owner.** Mobile owner for the identifier; security owner for the attestation environment
per deployment environment.

**Required action.** Supply the reverse-DNS bundle identifier and state which attestation
environment (`development` or `production`) applies to `dev`, `staging`, and `pilot`.

**Impact if unresolved.** Client attestation cannot be enforced at the Edge, which weakens the only
control that distinguishes a genuine app instance from an arbitrary HTTPS client.

**References.** Identity and public interface table on the Configuration Contract wiki page.
Related to **R-07**.

**Recommended next step.** Mobile owner confirms the identifier already in use by the iOS
repository; security owner rules on the per-environment attestation policy.

---

### R-09 — Network address plan, private DNS model, and approved egress

**Problem.** The VNet and subnet CIDR values in the configuration contract are proposals only
(`10.42.0.0/16` and its five subnets). DNS ownership and the private-DNS linking model are
undecided, and no approved egress destination list or enforcement mechanism exists.

**Why it blocks progress.** The address plan must not collide with existing organizational address
space, and private DNS linking determines whether private endpoints resolve at all. The processing
zone's deny-by-default egress posture needs an approved allowlist and a mechanism — an Azure
Firewall, a UDR, or an equivalent — before it can be enforced beyond the current NSG rule.

**Required owner.** Network owner for addressing and DNS; security owner for egress.

**Required action.** Approve the CIDR plan against the organization's address registry, decide the
private-DNS zone ownership and linking model, and approve the egress destination list with the
enforcement mechanism.

**Impact if unresolved.** Private endpoints and private DNS cannot be modeled, which means every
data and AI service — all of which have public network access disabled — would be unreachable.
This is the single largest technical dependency in the foundation.

**References.** Decision areas on the Security and Data Protection wiki page; foundation inputs on
the Configuration Contract wiki page. Blocks `TODO.md` items **1.1** and **2.2**.

**Recommended next step.** Network owner reviews the proposed `10.42.0.0/16` plan against the
existing address registry and either ratifies it or supplies replacement prefixes.

---

### R-10 — SQL administrator group and Managed HSM key governance

**Problem.** No Entra group has been nominated as the Azure SQL administrator, and Managed HSM has
no approved administrator set, key hierarchy, backup and restore procedure, quota, or monthly cost
approval. Customer-managed-key coverage and the key-rotation policy are also undecided.

**Why it blocks progress.** `AZURE_SQL_ENTRA_ADMIN_OBJECT_ID`,
`AZURE_SQL_ENTRA_ADMIN_DISPLAY_NAME`, and `AZURE_HSM_INITIAL_ADMIN_OBJECT_ID` are required Bicep
parameters. A Managed HSM pool bootstrapped with the wrong administrator set cannot be corrected
without a destructive recovery procedure, and purge protection is enabled.

**Required owner.** Data owner and platform owner for SQL; CISO for HSM administration and the key
hierarchy; finance owner for the HSM cost.

**Required action.** Nominate the SQL administrator group, decide whether HSM administration is held
by a group or granted through PIM, approve the key hierarchy and rotation policy, define the backup
and restore procedure, and approve the recurring cost.

**Impact if unresolved.** Customer-managed encryption — a stated pilot prerequisite — cannot be
implemented, and provisioning cannot run at all because the parameters are required.

**References.** Foundation inputs on the Configuration Contract wiki page; decision areas on the
Security and Data Protection wiki page. Blocks `TODO.md` item **2.1**.

**Recommended next step.** CISO decides the HSM administration model first, since it determines
whether an Entra group object ID or a PIM-eligible principal is supplied.

---

### R-11 — Retention, erasure, and backup policy alignment

**Problem.** `CASE_CONTENT_RETENTION_TRIGGER` is unresolved. The erasure SLA (proposed 30 days),
backup expiry (proposed 12 months), audit metadata retention (proposed 7 years), security log
retention (proposed 12 months), and the per-purpose blob soft-delete and version-purge windows are
all proposals. Blob soft delete is currently hard-coded to 7 days and Log Analytics retention to 365
days in the Bicep.

**Why it blocks progress.** The deletion receipt promised in the data-flow design cannot be issued
until there is one consistent retention contract across SQL, Cosmos, Blob versions, projections,
temporary stores, delivery links, logs, backups, and key material. Whatever is implemented must
match what participants are told.

**Required owner.** Privacy owner, with the data owner and compliance owner.

**Required action.** Approve a single retention and erasure contract covering all storage classes,
confirm it matches the participant notice, and state the per-purpose blob lifecycle windows.

**Impact if unresolved.** Real pilot data cannot be accepted. An erasure implementation built
against unratified numbers would have to be redone and could make the participant notice inaccurate.

**References.** Retention and erasure targets on the Pilot Policy and Compliance Gates wiki page;
hard-coded baselines on the Configuration Contract wiki page. Blocks `TODO.md` items **3.5** and
**4.3**.

**Recommended next step.** Privacy owner and data owner reconcile the proposed values into one
table, then confirm the drafted participant notice states the same numbers.

---

### R-12 — Document Intelligence and generative AI approvals

**Problem.** No approved Document Intelligence model IDs or pinned service API version exist.
Whether any generative feature — Azure OpenAI, Content Safety — enters Alpha 0.2 is undecided, and
`MODIFIED_ABUSE_MONITORING_STATUS` has no recorded outcome.

**Why it blocks progress.** The processing adapter cannot be implemented without a pinned API
version and an approved model set, and it must never fall back to an API key. Model IDs must be
source-document classes, never government form IDs used as extractor classes.

**Required owner.** AI owner and RAI owner, with privacy owner for abuse-monitoring status.

**Required action.** Approve the source-document model IDs and the pinned API version. Decide
whether any generative path is in scope for Alpha 0.2 and, if so, approve the OpenAI resource,
pinned deployment names, and Content Safety resource. Record the modified abuse monitoring outcome
with an evidence reference.

**Impact if unresolved.** The document-processing zone stays a health-check skeleton, so the pilot's
core extraction capability cannot be built or tested.

**References.** Document and AI configuration on the Configuration Contract wiki page; Document
Intelligence note on the Azure Component Research Record wiki page. Blocks `TODO.md` item **5.3**.

**Recommended next step.** AI owner pins the API version and model set for the identity-document
Path A, which is non-generative and can proceed independently of any OpenAI decision.

---

### R-13 — UPL classifier release-gate ownership

**Problem.** `UPL_CLASSIFIER_VERSION` has no immutable release identifier, and no owner is named for
the development and held-out corpora the gate is evaluated against.

**Why it blocks progress.** The unauthorized-practice-of-law gate must pass with zero escapes per
prohibited act and per supported language, and it must fail closed when the classifier or its audit
trail is unavailable. Neither the corpora nor the escape criteria can be defined by engineering.

**Required owner.** Compliance owner, with the RAI owner.

**Required action.** Name the corpora owner, define the prohibited-act taxonomy and supported
languages, and establish the versioning scheme for classifier releases.

**Impact if unresolved.** No release can pass the UPL gate, so no edition can move beyond
`CATALOG_ONLY`, and the pilot cannot serve real users.

**References.** UPL release gate on the Pilot Policy and Compliance Gates wiki page. Blocks
`TODO.md` item **5.5**.

**Recommended next step.** Compliance owner confirms the prohibited-act taxonomy and supported
language list so the corpora can be assembled.

---

### R-14 — Official form artifact activation approvals

**Problem.** I-130, I-485, and DS-11 have proposed artifact and fill modes but no verified official
artifact, encoding, hash, edition, or two-person-approved field map. DS-11 additionally needs a
round-trip fidelity confirmation and carries a no-electronic-signature constraint.

**Why it blocks progress.** Priority is not activation. Every edition stays fail-closed until its
source, encoding, field map, and approvals are verified, and the field map specifically requires
two-person approval.

**Required owner.** Catalog owner and compliance owner.

**Required action.** For each form, verify and record the official artifact URL, its SHA-256, its
encoding (AcroForm, XFA, or flat), the edition date, and a two-person-approved field-map version.
Confirm the FAFSA boundary stays `EXTERNAL_WORKFLOW` / `REFERENCE_ONLY` with no portal automation
and no credential handling.

**Impact if unresolved.** The package worker has nothing to fill, so the pilot's delivery path
cannot be exercised end to end.

**References.** Alpha 0.2 catalog scope and pilot policy baselines on the Pilot Policy and
Compliance Gates wiki page. Blocks `TODO.md` item **5.1**.

**Recommended next step.** Catalog owner downloads the current official artifacts, records their
hashes and encodings, and routes the derived field maps for two-person approval.

---

### R-15 — Independent penetration test authorization

**Problem.** No independent penetration test has been scoped, authorized, or budgeted.

**Why it blocks progress.** A real-user pilot prerequisite is an independent penetration test with
all high findings closed. It cannot be scoped meaningfully until a `staging` environment exists,
but authorization and budget have long lead times.

**Required owner.** Security owner, with the finance owner for budget.

**Required action.** Select a testing vendor, agree the scope against the trust-zone model, obtain
Azure penetration-testing authorization where required, and budget remediation time.

**Impact if unresolved.** The pilot cannot launch even with every technical control complete.

**References.** Real-user pilot prerequisites on the Pilot Policy and Compliance Gates wiki page.
Depends on `staging` existing, which depends on **R-01** through **R-05**.

**Recommended next step.** Security owner starts vendor selection now so the engagement can begin as
soon as `staging` is provisioned.

---

### R-16 — Catalog acquisition schedule decision

**Problem.** `ACQUISITION_SCHEDULE` has no value. `src/functions/function_app.py` binds its timer
trigger to `%ACQUISITION_SCHEDULE%`, so the Functions host cannot start without one.

**Why it blocks progress.** The cadence is an operational policy question — how often the catalog
checks upstream authorities for edition drift — not an engineering default. Too frequent risks
upstream rate limiting; too infrequent risks serving a stale edition.

**Required owner.** Catalog operations owner.

**Required action.** Decide the acquisition cadence and supply it as a six-field NCRONTAB
expression, per environment if the cadence differs between `dev` and `pilot`.

**Impact if unresolved.** The Functions app cannot be deployed, so edition-drift detection — the
control that quarantines cases when an official form changes — is unavailable.

**References.** Foundation inputs on the Configuration Contract wiki page; catalog and edition
integrity on the Pilot Policy and Compliance Gates wiki page.

**Recommended next step.** Catalog operations owner proposes a cadence, and an engineer confirms it
against the publication frequency of the four Alpha 0.2 authorities.

---

### R-17 — GitHub Wiki write access for documentation publication

**Problem.** Nine wiki pages are written and staged in the repository's `wiki/` directory, but they
cannot be published. The wiki Git remote rejects writes from the automation used to prepare them.

**Why it blocks progress.** The documentation model designates the GitHub Wiki as the destination
for all long-form documentation. Until the pages are published, the repository holds a `wiki/`
staging directory that is itself a deviation from the model, and the wiki's only page is the default
`Home` stub.

**Required owner.** Repository administrator.

**Required action.** Either publish the staged pages by pushing them to
`https://github.com/HybridCloudWorks/PEN-lapluma_infra.wiki.git`, or grant wiki write access to the
account or automation that will maintain them.

**Impact if unresolved.** Long-form documentation stays in a staging directory rather than its
authoritative destination, which is exactly the documentation sprawl this model exists to remove.

**References.** Documentation Standards wiki page; `TODO.md` item **6.1**.

**Recommended next step.** A maintainer clones the wiki repository, copies the contents of `wiki/`
into it, pushes, verifies the nine pages render with working cross-links, and then removes `wiki/`
from this repository.
