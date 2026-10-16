# Runbook: Session Tokens and SAML Identity Providers

How the LaPluma APIs authenticate callers, what each environment must be configured with, how to
provision the signing key and identity-provider certificates in Secret Manager, and the order in
which to bring an environment back online after the authentication fix (INF-21 to INF-24).

> **Staging and dev are offline.** Cloud SQL is stopped and Cloud Run ingress is set to internal
> while the authentication fix is rolled out. Keep them that way until every step in
> [section 4](#4-bringing-staging-and-dev-back-online) is done, in order.

---

## 1. How authentication works

1. A clinic user starts at `GET /auth/saml/login?domain=<domain>` and is redirected to the
   institution's Google Workspace or Microsoft Entra ID SAML app.
2. The IdP posts the response to `POST /auth/saml/acs/<tenantId>`. The core API accepts it only if
   the Assertion's XML signature verifies against the certificate pinned for that tenant's IdP,
   and the Assertion is current, addressed to this service provider and this consumer URL, and
   not seen before.
3. The core API returns `accessToken`: an RS256 JWT, valid for one hour, carrying `sub`, `email`,
   `tenant_id`, `role`, and `jti`, signed with the environment's session signing key. The header's
   `kid` is the key's RFC 7638 thumbprint.
4. API Gateway requires that token on every route except health and readiness,
   `/.well-known/jwks.json`, and the three SAML endpoints. It validates issuer, audience, expiry,
   and signature against `<issuer>/.well-known/jwks.json`, which the core API serves.
5. The gateway calls Cloud Run with its own identity and passes the caller's header on as
   `X-Forwarded-Authorization`. Both APIs validate the token again with the standard JWT bearer
   handler. The core API checks it against its own key; the workflow API fetches the key set from
   `Authentication__JwksUri` and caches it.

There is no other credential. No fixed test token, no unsigned session format, and no JWT accepted
without signature validation exists in any environment. The test suites authenticate through a test
scheme registered only inside the test projects.

## 2. Configuration

### Service settings

| Setting (environment variable) | Service | Comes from | Notes |
|--------------------------------|---------|------------|-------|
| `Authentication__Issuer` | both | Terraform `session_token_issuer` | The environment's public API base URL. Must equal the gateway's `x-google-issuer`. |
| `Authentication__Audience` | both | Terraform `session_token_audience` | Must equal the gateway's `x-google-audiences`. |
| `Authentication__JwksUri` | workflow | Terraform, derived | `<issuer>/.well-known/jwks.json` unless `session_token_jwks_uri` overrides it. HTTPS only. |
| `SessionTokens__SigningKeyPem` | core | Secret Manager `lp-session-signing-key-<env>` | Unencrypted PEM RSA private key (PKCS#8 or PKCS#1), at least 2048 bits. |
| `Saml__IdentityProvidersJson` | core | Secret Manager `lp-saml-idps-<env>` | JSON array described in [section 3.2](#32-saml-identity-providers). |
| `Saml__SpEntityId` | core | Terraform, derived | `<issuer>/auth/saml/metadata`. |
| `Saml__SpAcsBaseUrl` | core | Terraform, derived | `<issuer>/auth/saml/acs`; the consumer URL for a tenant is this plus `/<tenantId>`. |

A missing value fails closed: with no issuer and audience every protected route returns 403, with
no signing key the consumer service returns 503 and nothing validates, and with no identity
providers every sign-in is refused. A value that is present but unusable (a malformed key, a key
under 2048 bits, a certificate that does not parse, a non-HTTPS key-set address) stops the service
at startup, so the new revision never takes traffic.

### Terraform variables (each environment root)

| Variable | Required | Default | Meaning |
|----------|----------|---------|---------|
| `session_token_issuer` | yes | none | `https://<API Gateway hostname>` (`terraform output gateway_url`) or the custom domain in front of it, no trailing slash. |
| `session_token_audience` | no | `lapluma-api-<env>` | Token audience shared by the gateway and both APIs. |
| `session_token_jwks_uri` | no | empty | Override for the key-set address; leave empty to use `<issuer>/.well-known/jwks.json`. |

These replace `oidc_issuer` and `oidc_audience`. Remove those from the staging HCP Terraform
workspace (`lapluma-staging`) and from any local `terraform.tfvars`.

The two secrets are not Terraform inputs and Terraform does not create them, so key material never
enters state. Terraform grants the core API service account `roles/secretmanager.secretAccessor` on
each and mounts their `latest` version; `terraform apply` fails while either secret is missing.

### Deploy workflow variables

`deploy-gcp.yml` takes its target from repository variables (**Settings > Secrets and variables >
Actions > Variables**), never from the workflow file, and stops before authenticating, naming any
that is missing:

| Environment | Project | Region | Workload identity provider | Deployer service account |
|-------------|---------|--------|----------------------------|--------------------------|
| dev | `GCP_PROJECT_DEV` | `GCP_REGION_DEV` | `GCP_WIF_PROVIDER_DEV` | `GCP_DEPLOYER_SA_DEV` |
| staging | `GCP_PROJECT_STAGING` | `GCP_REGION_STAGING` | `GCP_WIF_PROVIDER_STAGING` | `GCP_DEPLOYER_SA_STAGING` |
| pilot | `GCP_PROJECT_PILOT` | `GCP_REGION_PILOT` | `GCP_WIF_PROVIDER_PILOT` | `GCP_DEPLOYER_SA_PILOT` |

The provider value is `projects/<project number>/locations/global/workloadIdentityPools/<pool>/providers/<provider>`.
Staging runs in project `project-27d49bcd-4a1d-4d7a-8a7`, not `lapluma-staging-gcp`, which is
pending deletion.

`infra/scripts/setup-deploy-identity.ps1` prepares an environment once and prints the four
`gh variable set` commands. It is idempotent and supports `-WhatIf`. It:

- pins the GitHub provider to this repository's immutable `repository_id` and `repository_owner_id`
  and to `refs/heads/main`, with the same attribute mapping and condition `modules/iam` declares, so
  the next plan shows no change to it. A deploy dispatched from any other branch is refused;
- grants the deployer `roles/artifactregistry.writer` on `lapluma-services-<env>` only,
  `roles/run.developer` on the three services only, `roles/iam.serviceAccountUser` on their runtime
  accounts only, and read-only `roles/run.viewer` on the project;
- lists any broader roles the deployer holds without removing them. `staging-power.yml` uses the same
  account, so `roles/cloudsql.admin` and `roles/apigateway.viewer` stay.

In staging and pilot the pool, provider and deployer account belong to Terraform (`module.iam`): the
script updates them in place but will not create them.

### Cloud SQL power state

Terraform does not manage whether the instance runs (`lifecycle.ignore_changes` on
`settings[0].activation_policy` in `modules/database`), so `terraform apply` neither starts nor stops
it. `infra/scripts/manage-environment.ps1` (or `staging-power.yml`) does. Staging stays stopped until
INF-27 is fixed: the instance has a public IPv4 address and does not enforce SSL.

## 3. Provisioning the secrets

Do this from a trusted workstation or Cloud Shell, once per environment. Never commit the files,
paste them into a ticket or chat, or leave them on disk afterwards.

```bash
ENV=staging                 # or dev, pilot
PROJECT=<gcp-project-id>
```

### 3.1 Session signing key

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out session-signing-key.pem
gcloud secrets create "lp-session-signing-key-${ENV}" --project="$PROJECT" --replication-policy=automatic
gcloud secrets versions add "lp-session-signing-key-${ENV}" --project="$PROJECT" --data-file=session-signing-key.pem
shred -u session-signing-key.pem
```

Use a different key in every environment.

### 3.2 SAML identity providers

Register LaPluma with each institution's IdP first. The service-provider values are:

| Field | Value |
|-------|-------|
| Entity ID (Audience) | `<issuer>/auth/saml/metadata` |
| ACS URL (Recipient) | `<issuer>/auth/saml/acs/<tenantId>` |
| NameID | Email address |
| Signing | Sign the **assertion**, RSA-SHA256 |

Google Workspace signs the assertion by default. In Entra ID, set the SAML signing option to *Sign
SAML assertion* (or *Sign SAML response and assertion*) with SHA-256. Responses signed with SHA-1, or
with only the response signed, are refused.

Then write the IdP list as a JSON array, one object per tenant and IdP:

```json
[
  {
    "tenantId": "tenant_hybridcloudworks",
    "idpType": "GoogleWorkspace",
    "entityId": "<IdP entity ID>",
    "ssoServiceUrl": "<IdP single sign-on URL>",
    "signingCertificates": ["<IdP signing certificate: PEM, or the Base64 DER from IdP metadata>"],
    "allowedDomains": ["hybridcloudworks.com"],
    "attributeMappings": {
      "email": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
      "role": "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
    },
    "adminConsentVerified": true,
    "isActive": true
  }
]
```

- `idpType` is `GoogleWorkspace` or `EntraIdSaml`.
- Google Workspace: Admin console, **Apps > Web and mobile apps >** the SAML app, **Download
  metadata** gives the entity ID, SSO URL, and certificate.
- Entra ID: **Enterprise applications >** the app **> Single sign-on**. *Microsoft Entra
  Identifier* is the entity ID, *Login URL* the SSO URL, and *Certificate (Base64)* the certificate.
- Use the SSO URL exactly as the metadata gives it. Google's carries `?idpid=...`; the core API adds
  `SAMLRequest` to that query with `&` and sends the request with the HTTP-Redirect binding
  (raw DEFLATE, then Base64, then URL encoding).
- `signingCertificates` may hold two certificates during a rollover.
- If no institution is ready yet, store `[]`. Sign-in is then disabled and everything else still
  requires a token.

```bash
gcloud secrets create "lp-saml-idps-${ENV}" --project="$PROJECT" --replication-policy=automatic
gcloud secrets versions add "lp-saml-idps-${ENV}" --project="$PROJECT" --data-file=saml-idps.json
shred -u saml-idps.json
```

## 4. Bringing staging and dev back online

Each step depends on the one before it. In particular, `terraform apply` restores Cloud Run ingress
to `INGRESS_TRAFFIC_ALL` (the declared state), so it must not run until the new images are deployed.

1. **Merge** the pull request once CI is green.
2. **Create both secrets** for the environment ([section 3](#3-provisioning-the-secrets)).
3. **Set the Terraform variables.** Staging: in the `lapluma-staging` workspace set
   `session_token_issuer = https://lp-gateway-staging-am9yq93d.uc.gateway.dev` and remove
   `oidc_issuer` and `oidc_audience`. Dev: set `session_token_issuer` in `terraform.tfvars` (see
   `terraform.tfvars.example`).
4. **Deploy the new images** with the *Deploy to GCP Cloud Run* workflow for the environment,
   dispatched from `main`. The first time, prepare the deploy identity and set the four variables
   ([Deploy workflow variables](#deploy-workflow-variables)); for staging:
   ```powershell
   ./infra/scripts/setup-deploy-identity.ps1 -Environment staging -ProjectId project-27d49bcd-4a1d-4d7a-8a7 -WhatIf
   ./infra/scripts/setup-deploy-identity.ps1 -Environment staging -ProjectId project-27d49bcd-4a1d-4d7a-8a7
   # then run the four gh variable set commands it prints
   ```
   Ingress is still internal, so nothing becomes reachable. Confirm the serving revision runs the
   new image:
   ```bash
   gcloud run services describe "lp-core-api-${ENV}" --project="$PROJECT" --region=us-central1 \
     --format='value(status.latestReadyRevisionName,spec.template.spec.containers[0].image)'
   ```
   Repeat for `lp-wf-api-${ENV}`.
5. **Plan, review, apply.** `terraform plan` should show only: environment and secret changes on
   `lp-core-api-<env>` and `lp-wf-api-<env>`; ingress returning to `INGRESS_TRAFFIC_ALL` on both;
   two `google_secret_manager_secret_iam_member` bindings; a new API config with
   `securityDefinitions`; and the gateway moving to it. The image reference may also move from the
   deploy workflow's commit tag back to `:latest` (staging, the same image) or to the placeholder
   image (dev, whose root does not pin service images). If `setup-deploy-identity.ps1` has not been
   run for the environment, the GitHub provider's attribute mapping and condition also update to the
   repository-ID pin. The Cloud SQL instance must not appear: Terraform ignores its activation
   policy. Stop on anything else. Then
   `terraform apply`. **Dev only:** re-run the deploy workflow afterwards to replace the
   placeholder image.
6. **Verify from outside**, against `https://<gateway-hostname>`:

   | Request | Expected |
   |---------|----------|
   | `GET /catalog/healthz` | 200 |
   | `GET /.well-known/jwks.json` | 200, one RSA key, no private fields |
   | `GET /v1/catalog/packages` with no token | 401 |
   | `GET /v1/catalog/packages` with `Authorization: Bearer test-caller` | 401 |
   | SAML sign-in, then `GET /v1/session` with the returned `accessToken` | 200 (only if an IdP is configured) |

   `tools/live_test_gateway.py` needs a real session token in `LAPLUMA_AUTH_TOKEN`.
7. **Cloud SQL last.** Both APIs still serve fixtures (`Catalog__Source` and `Workflow__Source` are
   `fixture`), so nothing on the request path needs the database, and neither the checks above nor
   `terraform apply` start it ([Cloud SQL power state](#cloud-sql-power-state)). Start it only when
   it is needed, and preferably after INF-27 lands:
   `infra/scripts/manage-environment.ps1 -Action resume` for staging, or
   `gcloud sql instances patch <instance> --project="$PROJECT" --activation-policy=ALWAYS`.

## 5. Rotation

### Session signing key

1. Generate a new key and add it as a new version of `lp-session-signing-key-<env>`
   ([section 3.1](#31-session-signing-key)).
2. Start a new core API revision so `latest` is re-read: re-run the deploy workflow.
3. Disable the old version: `gcloud secrets versions disable <version> --secret="lp-session-signing-key-${ENV}" --project="$PROJECT"`.

Sessions signed with the old key stop validating immediately; they last at most an hour, and users
sign in again. API Gateway caches the key set for up to five minutes, so new sessions can be refused
at the edge for a few minutes after the switch. Rotate in a quiet window, and at once if the key may
have been exposed.

### IdP certificate

Add the new certificate to that IdP's `signingCertificates` next to the old one, add the JSON as a
new secret version, and start a new core API revision. After the IdP has switched to the new
certificate, remove the old one the same way.
