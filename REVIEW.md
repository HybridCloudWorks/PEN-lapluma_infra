# Review — human-resolvable blockers

Items on this page cannot be resolved by an engineer without external input: a decision, an
approval, a credential, or an access grant. Anything an engineer can solve independently belongs in
`TODO.md`.

Every item states the problem, why it blocks progress, the required owner, the required action, the
impact if it stays unresolved, its references, and the recommended next step.

## How the proposals work

Most items below now carry a **Proposed answer** block. It is a concrete draft, written by
engineering, that the named owner can approve or reject in one reading. Its only purpose is to
remove the blank page: deciding "yes", "no", or "yes but change this number" is a far smaller task
than authoring a policy from nothing, and several of these items have sat unresolved because the
authoring was the hard part rather than the deciding.

A proposal carries no authority. Nothing in it is implemented, and an approval is what makes it
real. Where a proposal contradicts an organizational standard the author does not have, the standard
wins and the proposal is simply wrong — saying so is a valid and useful outcome.

Four items carry no proposal, and deliberately so. R-01, R-02, R-10's object IDs, and R-17 turn on
facts and grants that only their owner holds: a tenant ID, a subscription authorization, a group's
object ID, a repository permission. A plausible-looking invented GUID or hostname would be worse
than a blank, because a blank is obviously unanswered and an invention is not. The same rule applies
inside every proposal below — where a real identifier is needed, the proposal states its *shape* and
leaves the value to the owner.

## Index

| ID | Blocker | Required owner | Proposal |
|----|---------|----------------|----------|
| [R-01](#r-01--azure-tenant-and-subscription-identity-are-unconfirmed) | Azure tenant and subscription identity are unconfirmed | Platform owner | — |
| [R-02](#r-02--subscription-authorization-for-real-pilot-pii-and-cost-bearing-services) | Subscription authorization for real pilot PII and cost-bearing services | Subscription owner | — |
| [R-03](#r-03--east-us-2-capability-quota-and-cost-approval) | East US 2 capability, quota, and cost approval | Platform owner and finance owner | Drafted |
| [R-04](#r-04--named-approval-owners-are-undefined) | Named approval owners are undefined | Executive sponsor | Drafted |
| [R-05](#r-05--resource-naming-tagging-and-cost-center-standard) | Resource naming, tagging, and cost-center standard | Governance owner | Drafted |
| [R-06](#r-06--entra-application-registrations-and-api-audience) | Entra application registrations and API audience | Identity owner | Drafted |
| [R-07](#r-07--public-hostname-dns-and-tls-certificate-ownership) | Public hostname, DNS, and TLS certificate ownership | Platform owner | Drafted |
| [R-08](#r-08--ios-bundle-identifier-and-app-attest-environment) | iOS bundle identifier and App Attest environment | Mobile owner and security owner | Drafted |
| [R-09](#r-09--network-address-plan-private-dns-model-and-approved-egress) | Network address plan, private DNS model, and approved egress | Network owner and security owner | Drafted |
| [R-10](#r-10--sql-administrator-group-and-managed-hsm-key-governance) | SQL administrator group and Managed HSM key governance | Data owner and CISO | Drafted |
| [R-11](#r-11--retention-erasure-and-backup-policy-alignment) | Retention, erasure, and backup policy alignment | Privacy owner | Drafted |
| [R-12](#r-12--document-intelligence-and-generative-ai-approvals) | Document Intelligence and generative AI approvals | AI owner and RAI owner | Drafted |
| [R-13](#r-13--upl-classifier-release-gate-ownership) | UPL classifier release-gate ownership | Compliance owner | Drafted |
| [R-14](#r-14--official-form-artifact-activation-approvals) | Official form artifact activation approvals | Catalog owner and compliance owner | Drafted |
| [R-15](#r-15--independent-penetration-test-authorization) | Independent penetration test authorization | Security owner | Drafted |
| [R-16](#r-16--catalog-acquisition-schedule-decision) | Catalog acquisition schedule decision | Catalog operations owner | Drafted |
| [R-17](#r-17--github-wiki-write-access-for-documentation-publication) | GitHub Wiki write access for documentation publication | Repository administrator | — |

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

**Proposed answer.** None, and none is possible. A tenant ID and a subscription ID are facts held by
the platform owner, not judgements anyone can draft on their behalf. A GUID invented here to save
the owner some typing would be indistinguishable from a confirmed one, and the gate exists precisely
to stop a plausible value being mistaken for a confirmed value.

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

**Proposed answer.** None. This is an authorization, not a design question: the whole content of the
item is that a specific accountable person has said yes in writing, and nothing drafted here can
stand in for that. What can be drafted is the *scope* the authorization has to cover, so the request
is not made twice — it is the SKU ladder proposed under **R-03**, which enumerates every
continuously billing resource the subscription would carry.

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

**Proposed answer.** A per-environment SKU ladder, and a cost record whose numbers the finance owner
fills in rather than one this page invents.

The ladder, by environment:

| Resource | `dev` | `staging` | `pilot` | Note |
|----------|-------|-----------|---------|------|
| Azure SQL | `GP_S_Gen5`, 0.5–2 vCore, auto-pause 60 min | same, auto-pause 60 min | same, auto-pause disabled (`-1`) | Serverless auto-pause is the single largest saving in `dev`; disabling it in `pilot` removes cold-start latency from a participant-facing path |
| Cosmos DB | 1000 RU/s autoscale | 1000 | 1000, revisit on measured RU | Autoscale floor is 10% of the ceiling, so the ceiling is a cap, not a commitment |
| Service Bus | Premium, 1 MU, 1 partition | same | same | Standard cannot take a private endpoint, so the tier is fixed by the network posture rather than chosen |
| Container registry | Premium | Premium | Premium | Same reason: private endpoints require Premium |
| Managed HSM | **not provisioned** | `Standard_B1` | `Standard_B1` | See below |
| API Management | lowest v2 tier that supports the VNet integration the edge design needs | Standard v2 | Standard v2 | Which tier that is, is exactly what the capability check must establish |
| Storage | `Standard_LRS`, audit `Standard_ZRS` | same | audit `Standard_GZRS` | Only the audit account carries a retention obligation |

The Managed HSM line is the substantive proposal and the one most worth arguing with. A Managed HSM
pool bills continuously from activation whether or not a key is ever used, and `dev` exists to
exercise deployment mechanics, not key governance. The proposal is that `dev` uses the Key Vault
already in the template for its key material and provisions no HSM pool, and that `staging` and
`pilot` provision it. The cost of accepting this is real and should be stated plainly: `dev` then
stops being a faithful rehearsal of the HSM bootstrap, and the bootstrap is irreversible under purge
protection (**R-10**). The mitigation is that `staging` becomes the rehearsal, and `staging` is
where the bootstrap is practised before `pilot`. If the CISO would rather rehearse twice, reject
this line and accept the standing `dev` cost.

For the cost record itself, the proposal is a format rather than a figure. This page has no access
to a pricing sheet, and a monthly total invented here would be the single most quotable wrong number
in the repository. The record should list every row of the ladder above with its SKU, quantity, and
region, priced from the Azure pricing calculator for `eastus2` on a stated date, with the three
continuously billing lines — Managed HSM, API Management, Service Bus Premium — subtotalled
separately. Those three dominate a low-traffic environment and are the ones a budget conversation
is actually about.

For quota, the proposal is that the capability check covers, at minimum: Managed HSM availability
and pool quota, API Management v2 tier availability, Container Apps workload profile quota in each
of the three managed environments, Flex Consumption availability for the Functions plan, and the
Document Intelligence models named under **R-12**. A pass on the first four and a fail on the last
still blocks the pilot's core capability, so the model check is not an afterthought in this list.

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

**Proposed answer.** An ownership matrix of six roles, each held by an Entra **group** rather than a
named individual, with the groups created empty and populated afterwards.

| Role | Approves | Group name | Object ID recorded as |
|------|----------|------------|-----------------------|
| Deployment approver | Provisioning into `staging` and `pilot`; the expansion checkpoint | `sg-lapluma-deployment-approvers` | — (approval is recorded, not deployed) |
| Security owner | Security review board decision; penetration-test scope (**R-15**); egress list (**R-09**) | `sg-lapluma-security` | `AZURE_SECURITY_GROUP_OBJECT_ID` |
| Privacy owner | Retention and erasure contract (**R-11**); DPIA; participant notice | `sg-lapluma-privacy` | `AZURE_PRIVACY_GROUP_OBJECT_ID` |
| Compliance owner | UPL gate (**R-13**); form activation (**R-14**); counsel boundary | `sg-lapluma-compliance` | — |
| Operations and on-call owner | Incident plan; restore and deletion drills; the role mailbox in **R-07** | `sg-lapluma-operations` | `AZURE_OPERATIONS_GROUP_OBJECT_ID` |
| Finance owner | Cost approval (**R-03**); HSM recurring cost (**R-10**) | `sg-lapluma-finance` | — |

Groups rather than people, for two reasons that are worth stating because the alternative is
tempting. A named individual is a single point of failure for a gate that must be exercisable on the
day an incident happens, and an individual who leaves takes an approval authority with them silently
— the group survives the offboarding, the person does not.

`AZURE_DEPLOYMENT_PRINCIPAL_OBJECT_ID` is proposed to be a workload identity used by the deployment
pipeline, and explicitly **not** a member of any group above. The principal that performs a
deployment must not be a principal that approves one.

Two separations of duty are proposed as hard rules, and everything else is proposed as allowed
overlap for a pilot this size. First, no one may hold both deployment-approver membership and
authorship of the change being approved. Second, the security owner and the deployment approver must
be distinct groups with disjoint membership. Beyond those two, one person holding several of these
roles is proposed as acceptable — a pilot with six mandatory distinct humans is a pilot that stalls
on scheduling, and pretending otherwise is how ownership matrices become fiction.

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

**Proposed answer.** `AZURE_RESOURCE_NAME_PREFIX` = `lapluma`.

Seven lowercase alphanumeric characters, inside the 3–12 constraint, and already the fixed stem the
templates use for globally scoped names (`compactName` in `infra/modules/data.bicep`, and the
Service Bus namespace in `infra/modules/messaging.bicep`). Ratifying it means no code change;
choosing anything else means the stem and the prefix disagree, which is survivable but is a
diff nobody needs. The generated names it produces are of the form `st` + `lapluma` + a
three-letter purpose + a six-character `uniqueString` hash, which is 21 characters against the
storage account limit of 24 — the prefix has headroom, and a longer one would not.

`AZURE_RESOURCE_TAGS` is proposed as exactly four tags supplied by the operator, since
`infra/main.bicep` already merges five more of its own (`azd-env-name`, `system`, `release`,
`correlated-app-release`, `data-residency`):

| Tag | Value | Source |
|-----|-------|--------|
| `owner` | The accountable team or role name from `AZURE_RESOURCE_OWNER` | Governance owner |
| `cost-center` | `AZURE_COST_CENTER` | Finance owner |
| `data-classification` | `production-sensitive-pii` in `staging` and `pilot`; `synthetic` in `dev` | Privacy owner |
| `environment` | `dev`, `staging`, or `pilot` | Set per environment |

`data-classification` is the one to argue about. It is proposed as a tag rather than a comment
because it is the field a governance query filters on when someone asks which resources hold
regulated data, and a `dev` environment tagged `synthetic` is making a claim that **R-11**'s erasure
testing has to keep true.

`AZURE_RESOURCE_OWNER` is proposed to be a team or role name, never a person's name or address —
the same reasoning as **R-04**, and it also keeps a personal identifier out of resource metadata
that is visible to anyone with reader access.

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
zone on the Architecture Overview wiki page. The Core API's own JWT validation is built and fails
closed, so this item supplies the audience and issuer it validates against rather than gating
whether it validates at all; until then no catalog request can succeed. `TODO.md` item **1.1**
(the API Management edge) is the remaining engineering work.

**Proposed answer.** Three registrations, one audience, three scopes.

| Registration | Type | Purpose |
|--------------|------|---------|
| `lapluma-applicant-ios` | Public client, no secret, authorization code with PKCE | The iOS app. A public client cannot hold a secret, so no secret is issued for it |
| `lapluma-core-api` | API, exposing the scopes below | The audience. This registration's Application ID URI is `ENTRA_API_AUDIENCE` |
| `lapluma-staff-api` | API, workforce tenant | Staff surfaces. Separate registration so staff Conditional Access can be strict without affecting applicants |

`ENTRA_API_AUDIENCE` is proposed to be the Application ID URI of `lapluma-core-api`, in the form
`api://<verified-domain>/lapluma-core`, where `<verified-domain>` is a domain already verified in
the tenant confirmed under **R-01**. The default `api://<client-id>` form works and is proposed
against: a GUID audience is unreadable in a token trace and gives no signal when the wrong one is
configured, whereas a wrong domain-based audience is obvious on sight.

Three delegated scopes are proposed, matching the surfaces that exist or are planned:

- `catalog.read` — read the form catalog. This is what `src/core-api` serves today.
- `case.read` — read a case the caller owns.
- `case.write` — create or update a case the caller owns.

The Core API's `catalog-reader` policy currently requires only an authenticated caller. The proposal
is that it additionally require the `catalog.read` scope once these registrations exist, which is a
small change to `src/core-api/CatalogAuthentication.cs` and is deliberately not made in advance —
requiring a scope no issuer can mint would fail every request, and would do so through the same
fail-closed path that currently means "not yet configured", making the two states
indistinguishable.

`ENTRA_STAFF_TENANT_ID` is proposed to be the same tenant as the Azure resources. A separate
workforce tenant is defensible but adds cross-tenant token validation to a pilot that has no staff
surface built yet; if the organization already operates a separate workforce tenant, that fact wins
over this proposal.

Conditional Access on `lapluma-staff-api` is proposed as: phishing-resistant MFA required, no
exclusions, no legacy authentication, and compliant-device required. Applicant access via
`lapluma-applicant-ios` carries no device requirement — the equivalent control there is App Attest
(**R-08**), applied at the edge.

**Recommended next step.** Identity owner approves or amends the registration design above, then
provisions the three registrations in the tenant confirmed in R-01 and publishes the resulting
Application ID URI and issuer.

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

**Proposed answer.** One hostname per environment, in a zone the organization already controls, with
certificates issued into Key Vault rather than uploaded to API Management.

`APIM_PUBLIC_HOSTNAME` is proposed as `<environment>-api.<product-zone>` for `dev` and `staging`,
and `api.<product-zone>` for `pilot` — where `<product-zone>` is an existing organizational DNS
zone, not a new registration. Three distinct hostnames rather than one, because the iOS associated
domains file binds to a hostname: one shared hostname would mean a `dev` build and a `pilot` build
cannot both be installed, and the workaround for that is usually to weaken the binding.

Certificates are proposed to come from the organization's existing issuance process into an Azure
Key Vault certificate, referenced by API Management rather than uploaded to it. The difference
matters at renewal: an uploaded certificate expires silently and takes the public ingress with it,
whereas a Key Vault reference renews in place. If no organizational issuer exists, the fallback
proposal is an App Service Managed Certificate, with the caveat that it does not cover every
hostname shape and must be confirmed against the chosen name before it is relied on.

`APIM_PUBLISHER_NAME` is proposed to be the organization's display name as it should appear to a
developer reading the API surface — it is published, so it is a product decision rather than an
infrastructure one.

`APIM_PUBLISHER_EMAIL` is proposed to be a monitored distribution list owned by the operations group
from **R-04**, of the form `<team-alias>@<org-domain>`, with at least two subscribed recipients.
API Management sends certificate-expiry and quota notices to this address, which is the specific
reason a single person's mailbox fails: the notice that matters arrives while that person is on
leave.

**Recommended next step.** Platform owner confirms which existing DNS zone will host the API names
above, and whether the organization's certificate issuance process can target a Key Vault
certificate.

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

**Proposed answer.** One bundle identifier, and `production` attestation in both `staging` and
`pilot`.

`IOS_BUNDLE_IDENTIFIER` is proposed to be a single reverse-DNS identifier of the form
`<org-reverse-dns>.lapluma`, shared across all three environments, with the environments
distinguished by the hostname from **R-07** rather than by the identifier. Per-environment bundle
identifiers are the common alternative and are proposed against here for one reason: App Attest keys
are bound to the bundle identifier, so three identifiers means three attestation configurations to
keep in step, and the failure mode of a drifted one is that attestation silently stops being
enforced for that environment.

`IOS_APP_ATTEST_ENVIRONMENT` is proposed as:

| Environment | Value |
|-------------|-------|
| `dev` | `development` |
| `staging` | `production` |
| `pilot` | `production` |

`staging` is the load-bearing row. Attestation is the only control at the edge that distinguishes a
genuine app instance from an arbitrary HTTPS client, and a `staging` that attests against the
development environment does not exercise that control — it exercises a different one that happens
to be shaped like it. The independent penetration test under **R-15** runs against `staging`, so a
`staging` configured as `development` would return a clean result on a control the pilot does not
actually have.

**Recommended next step.** Mobile owner confirms the identifier already in use by the iOS
repository, which overrides the shape proposed above if it differs; security owner ratifies or
amends the per-environment attestation table.

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

**Impact if unresolved.** The private endpoints and private DNS zones are modeled now, against the
proposed `10.42.0.0/16` plan; ratifying or replacing that plan is what this item still decides, and
replacement prefixes mean redeploying the network. Two things remain blocked outright: the egress
allowlist and its enforcement mechanism, and the API Management edge, which needs a sixth subnet
the plan does not allocate.

**References.** Decision areas on the Security and Data Protection wiki page; foundation inputs on
the Configuration Contract wiki page. Blocks `TODO.md` items **1.1** (the edge subnet) and
**2.2** (the egress allowlist).

**Proposed answer.** Ratify `10.42.0.0/16` as proposed, add a sixth subnet for the edge, own the
private DNS zones in the workload resource group, and approve an egress list that is empty for three
of the five zones.

**Addressing.** The five proposed subnets stay as they are, and a sixth is added:

| Subnet | Prefix | Note |
|--------|--------|------|
| `snet-core` | `10.42.0.0/23` | Container Apps managed environment |
| `snet-processing` | `10.42.2.0/23` | Container Apps managed environment |
| `snet-ai` | `10.42.4.0/23` | Container Apps managed environment |
| `snet-functions` | `10.42.6.0/24` | Flex Consumption integration |
| `snet-private-endpoints` | `10.42.7.0/24` | Twelve endpoints today |
| `snet-apim` | `10.42.8.0/24` | **New.** API Management v2 VNet integration, delegated |

The sixth subnet is what unblocks `TODO.md` **1.1**. `10.42.8.0/24` is proposed rather than reusing
spare space inside an existing prefix because API Management v2 requires a delegated subnet, and a
delegated subnet cannot host anything else. `/16` leaves `10.42.9.0` upward unallocated, so a later
requirement does not force a renumbering.

**Private DNS.** Zones are proposed to live in the workload resource group and be linked to this
VNet only, with no auto-registration. The alternative — zones owned centrally in a hub subscription
and linked outward — is the better long-run model and is proposed against for now on the grounds
that no hub exists: adopting a hub-owned model before there is a hub means inventing a linking
process and an owner for it, and both would be fiction. If a hub arrives, the migration is to
recreate the zone links and delete the local zones, which is a single change and is worth naming now
so it is a planned move rather than a discovery.

**Egress.** The proposal is that three of the five zones have an approved destination list that is
**empty**, and that the deny rules already in `infra/modules/network.bicep` are the enforcement
mechanism — no Azure Firewall, no UDR.

| Zone | Approved egress |
|------|-----------------|
| Core | None. All dependencies are private endpoints, which are intra-VNet |
| Processing | None. Inputs arrive over Service Bus, outputs go to storage, both over private endpoints. This zone is defined by having no route out |
| AI | None initially. Model access is via private endpoint (**R-12**) |
| Private endpoints | None |
| Functions | The publication hosts of the four Alpha 0.2 authorities, TCP 443 only |

The functions row is the only non-empty one and needs a correction to the framing in this item.
`src/functions/acquisition_contract.py` performs **no** network fetch today — it proposes an
acquisition batch from a declared authority map. The egress requirement is therefore prospective,
arriving with the edition-drift work, not present. That matters for sequencing: the `DenyInternet`
rule at priority 4000 on the functions NSG will block the first upstream fetch that is written, and
it will do so at runtime rather than at review, so this list has to be approved before that work
starts rather than after it fails.

The specific hosts are deliberately not enumerated here. They are the hosts of the official source
URLs recorded under **R-14**, and writing a URL from memory is exactly how an acquisition sweep ends
up pointed at a plausible wrong address. The proposal is that the allowlist is *derived* from the
R-14 record, so the two cannot drift.

An Azure Firewall is proposed against for the pilot: with three zones needing no egress and one
needing four hosts, a firewall would add a continuously billing resource and a second policy surface
to enforce a list the NSGs can already express. If the AI zone later needs public model endpoints,
that is the point to revisit it, and this paragraph is where the reasoning to overturn lives.

**Recommended next step.** Network owner checks `10.42.0.0/16` and the six prefixes above against
the existing address registry and either ratifies them or supplies replacements; security owner
ratifies or amends the egress table, which is the part that unblocks `TODO.md` **2.2**.

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

**Proposed answer.** Group-held administration with no standing members, one key per purpose, and a
security-domain quorum recorded before any key exists. The object IDs themselves are not proposed —
they are facts, and this page does not invent GUIDs.

**SQL administration.** `AZURE_SQL_ENTRA_ADMIN_DISPLAY_NAME` = `sg-lapluma-sql-admins`, a group
created empty and populated only through PIM activation.
`AZURE_SQL_ENTRA_ADMIN_OBJECT_ID` is that group's object ID, supplied by the identity owner. The
server already sets `azureADOnlyAuthentication`, so this group is the sole administrative path;
standing membership would make it a permanent one.

**HSM administration.** `AZURE_HSM_INITIAL_ADMIN_OBJECT_ID` is proposed to be a **group**, never an
individual, and never the deployment principal from **R-04**. The bootstrap is irreversible under
purge protection, so the failure this avoids is concrete: an individual bootstrapped as sole
administrator who then leaves cannot be replaced without destructive recovery.

The security domain is the part most often deferred and should not be. The proposal is that it is
downloaded at activation under a quorum of **three**, with the three shares held by three distinct
people in the CISO's organization, stored separately from each other and from any Azure credential,
and that the quorum and the holders are recorded before the first key is created. A pool whose
security domain has one holder is one resignation away from unrecoverable.

**Key hierarchy.** One key per purpose, never one key reused:

| Key | Protects |
|-----|----------|
| `cmk-sql-tde` | Azure SQL transparent data encryption |
| `cmk-storage` | The four storage accounts |
| `cmk-cosmos` | Cosmos DB |

RSA-HSM, 3072 bits or higher, non-exportable. Separate keys because revoking one — the response to a
suspected compromise — should take down one store rather than the whole data plane, and a single
shared key makes that choice unavailable at the moment it is needed.

**Rotation.** Twelve months, automatic, with the previous version retained so existing ciphertext
stays readable. Rotation is proposed as automatic rather than a calendar task for the usual reason:
a manual annual task in a pilot is a task that happens once.

**Backup and restore.** Full pool backup to a storage account in the same subscription before every
key creation or rotation, and a documented restore rehearsal in `staging` before `pilot` accepts
real data. This is the same rehearsal that **R-03**'s proposal to skip the HSM in `dev` depends on,
so rejecting that proposal and rejecting this one together would leave the bootstrap unrehearsed.

**Scope of customer-managed keys.** Proposed as all three stores above. Log Analytics
customer-managed keys are proposed *out* of scope for Alpha 0.2 — the workspace holds content-free
telemetry by design, so the marginal protection is small against a linked-workspace configuration
that is awkward to reverse.

**Recommended next step.** CISO ratifies or amends the administration model, quorum, and key
hierarchy above; the identity owner then creates the two groups and supplies their object IDs.

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
the infrastructure baselines table on the Configuration Contract wiki page, whose retention rows
name this item as their gate. Those windows are parameters now, so resolving this sets values
rather than requiring a code change. Blocks `TODO.md` item **3.4**.

**Proposed answer.** One table, one ordering rule, and one trigger definition.

The **ordering rule** is the load-bearing part, and it is what makes this a contract rather than a
list of independently chosen numbers: *every window that extends the life of case content must be
strictly shorter than the erasure SLA.* A soft-delete window keeps deleted data recoverable, which
means it keeps it. If soft delete is 30 days and the erasure SLA is 30 days, the deletion receipt
issued on day 30 is false for the length of a rounding error — and if soft delete is longer, it is
false outright. Two classes are exempt because they hold no case content: audit metadata, which is
content-free and pseudonymized, and key material.

| Value | Proposed | Where it lands |
|-------|----------|----------------|
| `ACCOUNT_ERASURE_ACTIVE_DATA_SLA_DAYS` | `30` | The ceiling every row below is measured against |
| `CASE_CONTENT_RETENTION_TRIGGER` | See below | `TODO.md` **5.6** |
| `BACKUP_EXPIRY_MAX_MONTHS` | `12` | Backup policy |
| `AUDIT_METADATA_RETENTION_YEARS` | `7` | `LAPLUMA_AUDIT_IMMUTABILITY_DAYS` = `2555`, already the template default |
| `SECURITY_LOG_RETENTION_MONTHS` | `12` | `LAPLUMA_LOG_ANALYTICS_RETENTION_DAYS` = `365`, already the baseline |
| Blob soft delete | `7` days, all four accounts | `LAPLUMA_BLOB_SOFT_DELETE_DAYS` |
| Container soft delete | `7` days | `LAPLUMA_CONTAINER_SOFT_DELETE_DAYS` |
| Blob version purge | `30` days | Lifecycle rule; not yet modeled |
| Key Vault soft delete | `90` days | `LAPLUMA_KEY_VAULT_SOFT_DELETE_DAYS`, already the baseline |
| Managed HSM soft delete | `90` days | `LAPLUMA_HSM_SOFT_DELETE_DAYS`, already the baseline |
| Queue message TTL | `P7D` | Already the baseline; messages carry references, not content |
| Topic message TTL | `P14D` | Already the baseline |

Blob soft delete is proposed uniform at 7 days rather than per-purpose, which is a change of shape
from what this item asks for. Per-purpose windows are supportable — the Bicep would take an object
instead of an integer, one named change — but the case for them is weak: 7 days is a recovery
window for operator error, and an operator who has not noticed a wrong deletion within a week is not
going to notice it in three. Version purge at 30 days is where the longer tail sits, and it is still
inside the erasure SLA. If the privacy owner wants per-purpose windows anyway, the constraint that
survives is the ordering rule: no purpose may exceed 29 days.

`CASE_CONTENT_RETENTION_TRIGGER` is proposed as: **the retention clock starts at case closure, or at
180 days of participant inactivity on an open case, whichever comes first; content is deleted 18
months after the clock starts.** The inactivity limb is the part to scrutinise. Without it, a case
nobody ever closes is retained forever, which is the most common way a retention policy quietly
fails — and 180 days is a guess at the boundary between "a participant who is between steps" and "a
participant who has gone". This is the row where the privacy owner's judgement most changes the
answer, and it is proposed only so there is something specific to disagree with.

Two consequences worth stating before approval, because they are what the numbers cost. Seven-year
audit immutability means the audit container's WORM policy, once locked out of band, cannot be
shortened for seven years — `TODO.md` **2.3** carries that step, and it is irreversible. And 30-day
erasure across SQL, Cosmos, blob versions, projections, temporary stores, delivery links, logs, and
backups means the backup layer must support selective expiry within the SLA; a backup product that
only expires whole vaults on a 12-month schedule would make the 30-day promise unmeetable no matter
what the rest of the implementation does.

**Recommended next step.** Privacy owner and data owner ratify or amend the table above, confirm the
`CASE_CONTENT_RETENTION_TRIGGER` definition, and check the drafted participant notice states the
same numbers before either is published.

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

**Proposed answer.** No generative path in Alpha 0.2, two prebuilt models for Path A, and an API
version this page will not guess.

**Generative scope.** Proposed as **none** for Alpha 0.2: no Azure OpenAI resource, no Content
Safety resource, no generative deployment. `MODIFIED_ABUSE_MONITORING_STATUS` then records
`not applicable`, with the evidence reference being this decision. The reasoning is that every
generative feature in scope would sit behind the UPL gate (**R-13**), which cannot pass until its
corpora exist, so a generative path built now would be built to stay switched off. Deciding "no"
here is not a deferral — it removes a resource, an abuse-monitoring submission, and a set of
approvals from the critical path, and none of them come back cheaply if the decision drifts.

**Path A models.** `DOCUMENT_INTELLIGENCE_MODEL_IDS` proposed as `prebuilt-idDocument` and
`prebuilt-read`. Both are source-document classes — an identity document and a page of text —
satisfying the constraint that model IDs must never be government form IDs. `prebuilt-read` is
included because a document that fails identity-document classification still needs to be read
enough to be quarantined with a reason, and a quarantine with no reason is an unactionable one.

**API version.** Proposed as *the current GA version, recorded literally, never `latest`* — and the
string itself is deliberately left to the AI owner. This page cannot reach the service to confirm
which GA version is current, and a version pinned from memory is precisely the failure this item
exists to prevent: it would be a specific, confident, unverified value in a field whose whole
purpose is to be verified. The AI owner supplies it from the current service documentation at the
time of approval, and the capability check under **R-03** confirms that version and both models are
available in `eastus2`.

**Authentication.** Managed identity only, no key fallback, as the item already requires. Worth
restating as part of the proposal because `DOCUMENT_INTELLIGENCE_ENDPOINT` is a private endpoint
URI: if the endpoint is unreachable the adapter must fail, and a key fallback is the mechanism by
which "unreachable privately" quietly becomes "reachable publicly".

Approving the Path A half of this alone unblocks `TODO.md` **5.3** for the non-generative path,
which is the pilot's core extraction capability. The generative decision does not gate it.

**Recommended next step.** AI owner ratifies the two model IDs and supplies the current GA API
version string; RAI owner and privacy owner ratify the "no generative path in Alpha 0.2" decision
and record `MODIFIED_ABUSE_MONITORING_STATUS` as `not applicable`.

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

**Proposed answer.** Seven prohibited acts, English only for Alpha 0.2, an immutable version scheme,
and a held-out corpus the classifier's author cannot see.

**Prohibited-act taxonomy.** Seven acts, each phrased as something the system must not do:

| ID | Prohibited act |
|----|----------------|
| `UPL-1` | Selecting a form, filing, or legal pathway on the applicant's behalf |
| `UPL-2` | Advising on eligibility, or on the likelihood that a filing succeeds |
| `UPL-3` | Interpreting a legal term as applied to the applicant's particular facts |
| `UPL-4` | Recommending what to enter in a field whose answer is a legal characterization rather than a fact the applicant already knows |
| `UPL-5` | Drafting narrative legal argument, explanation, or justification |
| `UPL-6` | Predicting an outcome or a timeline in terms that a reader would take as a representation |
| `UPL-7` | Describing the system, or its operator, as counsel or as acting in place of counsel |

`UPL-4` is the one that does real work and the one most likely to be argued with. It is what
separates "you told us your date of birth, so this field is filled" from "based on what you have
described, you should answer *yes* here" — the second is a legal characterization wearing the
clothes of a form-filling convenience, and it is the failure mode a form assistant reaches naturally
rather than exceptionally.

**Supported languages.** English only for Alpha 0.2. The gate requires zero escapes *per language*,
so each added language is a full corpus, a full held-out set, and a full evaluation — adding Spanish
without funding that work does not extend coverage, it creates an untested surface that the gate
will report as passing because nothing evaluated it. If Spanish is required for the pilot
population, it should be approved here as scope with its corpus owner named, not assumed.

**Versioning.** `UPL_CLASSIFIER_VERSION` = `upl-<YYYY-MM-DD>-<7-char commit sha>`, immutable once
issued and never reused. Both halves earn their place: the date is what a human reads in an incident
review, and the sha is what makes the version resolvable back to the exact corpora and thresholds it
was evaluated against.

**Corpus ownership.** The compliance owner from **R-04** owns both corpora. The proposal is that
the **held-out corpus is not visible to whoever tunes the classifier** — not shared, not summarised,
not used to explain a failure. A held-out set that the author has seen has stopped being held out,
and the gate's guarantee degrades silently rather than failing, which is the worst available
outcome for a control whose stated property is zero escapes.

**Fail-closed definition.** Proposed as: classifier unavailable, classifier version unrecognized, or
audit trail unwritable each block the response, and each is reported as an availability failure
rather than as an allowed answer. The distinction matters because a fail-closed path that returns a
polite refusal is indistinguishable, from the outside, from a working classifier that found nothing
prohibited.

**Recommended next step.** Compliance owner ratifies or amends the taxonomy and the English-only
scope, and names the corpus owner; the versioning scheme and the visibility rule follow from that
approval.

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

**Proposed answer.** A record shape, an order to work in, and an activation ceiling that holds until
each record is complete.

**The record.** One per form edition, seven fields, all mandatory:

| Field | Note |
|-------|------|
| `authority` | `USCIS`, `U.S. Department of State`, or `Federal Student Aid` — already declared in `src/functions/acquisition_contract.py` |
| `formID` | `I-130`, `I-485`, `DS-11` |
| `editionDate` | From the artifact itself, not from the page that links to it |
| `sourceURL` | HTTPS, the authority's own host. Also the source of the **R-09** egress allowlist |
| `sourceSHA256` | Of the bytes downloaded, recorded at download time |
| `encoding` | `ACROFORM`, `XFA`, or `FLAT` — determined by inspection, not assumption |
| `fieldMapVersion` | Two-person approved, and the approval names both people |

None of these values are proposed here, and that is the point: a hash is a measurement, an edition
date is a fact printed on a document, and an encoding is discovered by opening the file. A plausible
value for any of them would defeat the control entirely, since the control *is* that the value was
observed.

**Order.** I-130 first, I-485 second, DS-11 last. I-130 is the simplest AcroForm case and proves the
record shape and the two-person approval flow on the least complicated artifact. DS-11 goes last
because it carries two extra obligations — round-trip fidelity confirmation, and a
no-electronic-signature constraint — so it is the form most likely to send the record shape back for
revision, and the cheapest time to discover that is after the shape has been proved twice.

**Activation ceiling.** All three stay `CATALOG_ONLY` until their record is complete, and the
proposal is that this is enforced rather than intended: the catalog's activation state is derived
from record completeness, so a form with a missing hash cannot be activated by someone editing a
column. Priority is not activation, and the way that principle usually fails is that a form becomes
important before its record is finished.

**FAFSA.** Proposed to stay `EXTERNAL_WORKFLOW` / `REFERENCE_ONLY` for the whole of Alpha 0.2, with
no portal automation, no credential handling, and no stored FSA ID material of any kind. This is
proposed as a scope boundary rather than a sequencing decision — not "not yet", but "not in this
edition" — because handling a federal student aid credential is a different risk posture from
filling a PDF, and it should be entered deliberately if it is ever entered at all.

**Recommended next step.** Catalog owner ratifies the record shape above, then downloads the current
official artifacts in the proposed order, records the seven fields for each, and routes the derived
field maps for two-person approval.

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

**Proposed answer.** Contract now, execute against `staging`, and buy the retest in the same
statement of work.

**Scope.** The engagement should be scoped against the trust-zone model rather than as a generic
application test, and should be given the specific claims the architecture makes — a test that
confirms what everyone already believes is worth less than one that tries to break the two
load-bearing assertions:

1. **The processing zone has no route to SQL or Cosmos.** Deny rules exist for this in
   `infra/modules/network.bicep`, including one covering the private-endpoint subnet directly,
   because service tags alone did not cover intra-VNet endpoint addresses. That gap was found by
   review; the test should assume there are more of its kind.
2. **The AI zone holds no authoritative data-plane role.** `tools/validate_foundation.py` asserts
   this against the templates, which proves what was declared, not what was deployed.

Beyond those, the proposed scope is: the API Management edge and its token validation (**R-06**),
App Attest enforcement (**R-08**), the applicant boundary — whether a caller can read a case that is
not theirs — and authenticated access to the Core API from inside the core subnet, since the Core
API's second lock exists specifically for that path.

**Out of scope**, proposed explicitly so it is not billed for: Azure platform infrastructure, which
Microsoft tests and which the customer is not authorized to test, and denial-of-service testing,
which is prohibited without separate arrangement and would prove nothing here.

**Timing.** Vendor selection and contracting start now; execution waits for `staging`. These have
different lead times and coupling them is what makes the test the thing that delays the pilot.

**Retest.** Proposed to be included in the original statement of work rather than bought after the
findings arrive. The prerequisite is *all high findings closed*, which means a retest is certain,
and a certain purchase negotiated after a deadline is visible is a purchase made from a weak
position.

**Environment note.** `staging` must be configured as `pilot` will be for the controls under test —
in particular the `production` App Attest environment proposed under **R-08**. A test against a
`staging` that attests as `development` returns a pass on a control the pilot does not have.

**Recommended next step.** Security owner ratifies the scope above and starts vendor selection now,
so the engagement can begin as soon as `staging` is provisioned; finance owner budgets the
engagement and the included retest together.

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

**Proposed answer.** Daily at 09:00 UTC in `staging` and `pilot`, weekly in `dev`.

| Environment | `ACQUISITION_SCHEDULE` | Meaning |
|-------------|------------------------|---------|
| `dev` | `0 0 6 * * 1` | Mondays, 06:00 UTC |
| `staging` | `0 0 9 * * *` | Daily, 09:00 UTC |
| `pilot` | `0 0 9 * * *` | Daily, 09:00 UTC |

Six fields, in NCRONTAB's `{second} {minute} {hour} {day} {month} {day-of-week}` order — the leading
seconds field is the one that makes a five-field cron expression pasted from elsewhere run at the
wrong time rather than fail.

Daily is proposed because the cadence's job is to bound how long a stale edition can be served, and
one business day is a defensible bound for a supervised pilot. Weekly would mean a form edition
could change on a Tuesday and be served until the following Monday, with cases filed against it in
between — and edition drift is what quarantines those cases, so the window is not merely a
freshness question. Hourly is available and proposed against: four authorities polled hourly is 96
requests a day for a set of documents that change a few times a year, which trades a real rate-limit
and politeness cost for a bound nobody needs.

09:00 UTC is early morning in the United States, which is deliberate. A sweep that detects drift
quarantines cases, and a quarantine is better discovered by an operator arriving at their desk than
by a participant mid-session.

`staging` matches `pilot` rather than saving the sweeps: an acquisition cadence that has never run
at pilot frequency has not been tested at pilot frequency, and this is the cheapest possible thing
to keep identical. `dev` differs because it exercises that the binding resolves and the timer fires,
not the cadence itself.

One correction to this item's framing, in the interest of not overstating the block: the timer
binding does require a value for the host to start, but `src/functions/acquisition_contract.py`
performs no upstream fetch today — it proposes an acquisition batch from a declared authority map.
So this decision unblocks deployment of the Functions app now, and becomes load-bearing for
drift detection when the fetch is implemented, at which point it also needs the functions-zone
egress list proposed under **R-09**.

**Recommended next step.** Catalog operations owner ratifies or amends the three values above; an
engineer confirms the chosen cadence against the publication frequency of the four Alpha 0.2
authorities once their source URLs are recorded under **R-14**.

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

**Proposed answer.** None to draft — this is a permission, not a decision, and no wording here
changes whether the wiki remote accepts a push. What can be reduced is the work the grant unlocks:
the nine pages are complete, cross-linked, and staged in `wiki/`, so publication is a clone, a copy,
and a push, with no authoring left. The only judgement involved is the second half of `TODO.md`
**6.1** — that `wiki/` is deleted from this repository once the pages render, since a staging
directory that outlives its purpose becomes a second copy that drifts.

**Recommended next step.** A maintainer clones the wiki repository, copies the contents of `wiki/`
into it, pushes, verifies the nine pages render with working cross-links, and then removes `wiki/`
from this repository.
