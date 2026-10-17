# Runbook: Session Tokens and SAML Identity Providers

How the LaPluma APIs authenticate callers, what each environment must be configured with, how to
provision the signing key and identity-provider certificates in Secret Manager, and the order in
which to bring an environment back online after the authentication fix (INF-21 to INF-24).

> **Staging and dev are offline.** Cloud SQL is stopped and Cloud Run ingress is set to internal
> while the authentication fix is rolled out. Keep them that way until every step in
> [section 4](#4-bringing-staging-and-dev-back-online) is done, in order.

---

## 1. How authentication works

1. The app starts at `GET /auth/saml/login` with the user's `domain`, its `redirect_uri`, a PKCE
   `code_challenge` (S256), and its own `state` ([section 1.1](#11-native-app-sign-in-contract)).
   The core API redirects to the institution's Google Workspace or Microsoft Entra ID SAML app
   with the AuthnRequest and a signed `RelayState`.
2. The IdP posts the response to `POST /auth/saml/acs/<tenantId>`. The core API first checks the
   `RelayState`, then accepts the Assertion only if its XML signature verifies against the
   certificate pinned for that tenant's IdP, and the Assertion is current, addressed to this
   service provider and this consumer URL, answers this sign-in's AuthnRequest (`InResponseTo`),
   and has not been seen before.
3. The core API answers `302` to `redirect_uri?code=<code>&state=<state>`. The app posts the code
   and its PKCE verifier to `POST /auth/token` and receives `accessToken`: an RS256 JWT, valid for
   one hour, carrying `sub`, `email`, `tenant_id`, `role`, and `jti`, signed with the
   environment's session signing key. The header's `kid` is the key's RFC 7638 thumbprint. The
   session token never appears in a URL.
4. API Gateway requires that token on every route except health and readiness,
   `/.well-known/jwks.json`, the three SAML endpoints, and `/auth/token`. It validates issuer,
   audience, expiry, and signature against `<issuer>/.well-known/jwks.json`, which the core API
   serves.
5. The gateway calls Cloud Run with its own identity and passes the caller's header on as
   `X-Forwarded-Authorization`. Both APIs validate the token again with the standard JWT bearer
   handler. The core API checks it against its own key; the workflow API fetches the key set from
   `Authentication__JwksUri` and caches it.

There is no other credential. No fixed test token, no unsigned session format, and no JWT accepted
without signature validation exists in any environment. The test suites authenticate through a test
scheme registered only inside the test projects.

### 1.1 Native-app sign-in contract

The iOS app signs in with `ASWebAuthenticationSession` (INT-22). Every step is anonymous at the
gateway; the code is useless without the verifier, which never leaves the device.

| Step | Request | Response |
|------|---------|----------|
| Start | `GET /auth/saml/login?domain=&provider=&redirect_uri=&code_challenge=&code_challenge_method=S256&state=` | `302` to the IdP with `SAMLRequest` and `RelayState`. `400` (problem JSON, no redirect) if any parameter is invalid; `503` with no signing key. |
| IdP answer | `POST /auth/saml/acs/{tenantId}` with `SAMLResponse` and `RelayState` (form) | `302` to `redirect_uri?code=<code>&state=<state>`. A rejected assertion: `302` to `redirect_uri?error=access_denied&error_description=<problem code>&state=<state>`. A missing, altered, expired, or other-tenant `RelayState`: `400` problem JSON, and the assertion is not consumed. |
| Exchange | `POST /auth/token` with `grant_type=authorization_code`, `code`, `code_verifier`, `redirect_uri` (form, or JSON `code`, `codeVerifier`, `redirectUri`) | `200` with `accessToken`, `tokenType`, `expiresIn` (3600), `tenantId`, `userId`, `email`, `roles`. `400` problem JSON on any failure. Responses carry `Cache-Control: no-store`. |

Rules the core API enforces:

| Item | Rule |
|------|------|
| `redirect_uri` | Exact, case-sensitive match against `SignIn__RedirectUris__<n>`. Default: `lapluma://auth/callback`, the app's custom scheme. A universal link (an `https` URL) is added as another entry once the app declares `applinks`; entries with a query, fragment, user info, or an `http`, `javascript`, `data`, or `file` scheme stop the service at startup. |
| `code_challenge` | `code_challenge_method=S256` only; the challenge is the 43-character base64url SHA-256 of the verifier. The verifier is 43 to 128 unreserved characters (RFC 7636). |
| `state` | 16 to 64 unreserved characters, echoed back unchanged. The app rejects a callback whose state is not the one it sent. |
| `RelayState` | `base64url(payload).base64url(HMAC-SHA256)`, about 190 characters. The payload holds the challenge, redirect URI, state, and a 10-minute expiry; the MAC also covers the tenant. The AuthnRequest ID is derived from the MAC, so `InResponseTo` ties the assertion to this sign-in without storing anything. IdP-initiated sign-in (no `RelayState`) is refused. |
| Code | `lpc1.` + AES-256-GCM ciphertext over subject, email, tenant, roles, challenge, redirect URI, a random `jti`, and a 60-second expiry. Opaque to the app and to anyone who sees the URL. |
| Keys | The `RelayState` MAC key and the code key are derived from the session signing key with HKDF-SHA256 under different labels. Nothing extra to provision, and every instance derives the same keys, so a sign-in started on one Cloud Run instance finishes on any other. |

Replay handling:

- **Assertions:** single-use per instance (an `issuer|ID` cache until `NotOnOrAfter`), as before.
- **Codes:** single-use per instance. Each instance remembers the `jti` of every code it redeemed
  until the code expires. The cache is in memory and not shared, so within the 60-second lifetime
  one code could be redeemed once on each running instance (at most 2 in dev and staging, 5 in
  pilot), and only by a caller that also holds the PKCE verifier. A failed exchange (wrong
  verifier or redirect URI) does not use the code up.
- **Session tokens:** one hour, no refresh token. The app renews by signing in again when a call
  returns `401` or the token is within a minute of expiry; with the IdP's single-sign-on cookie in
  place that needs no password, only the system's "wants to use … to sign in" confirmation.

Rotating the session signing key ([section 5](#5-rotation)) also invalidates sign-ins in progress
(at most 10 minutes old) and unredeemed codes; users sign in again.

In the Entra ID enterprise app, leave **Relay State** and **Sign on URL** under *Basic SAML
Configuration* empty. Both describe IdP-initiated or portal-launched sign-in, which this contract
refuses: a My Apps tile would land on a `400` page instead of the app.

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
| `SignIn__RedirectUris__<n>` | core | Not set (code default) | Allowed app callbacks ([section 1.1](#11-native-app-sign-in-contract)). Unset means `lapluma://auth/callback` only; setting any entry replaces the default. |

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
   | `GET /auth/saml/login?domain=<domain>` with no PKCE parameters | 400 `redirect-uri-not-allowed` |
   | `POST /auth/token` with no body | 400 |
   | SAML sign-in from the app (or the manual procedure below), then `GET /v1/session` with the `accessToken` | 200 (only if an IdP is configured) |

   `tools/live_test_gateway.py` needs a real session token in `LAPLUMA_AUTH_TOKEN`. Without the app,
   get one by hand from a workstation browser:

   ```bash
   GW=https://<gateway-hostname>
   VERIFIER=$(openssl rand -base64 48 | tr '+/' '-_' | tr -d '=\n' | cut -c1-64)
   CHALLENGE=$(printf '%s' "$VERIFIER" | openssl dgst -binary -sha256 | openssl base64 -A | tr '+/' '-_' | tr -d '=')
   STATE=$(openssl rand -hex 16)
   echo "$GW/auth/saml/login?domain=<domain>&provider=entra&redirect_uri=lapluma%3A%2F%2Fauth%2Fcallback&code_challenge=$CHALLENGE&code_challenge_method=S256&state=$STATE"
   ```

   Open the printed URL and sign in. The browser stops at a `lapluma://auth/callback?code=…` address
   it cannot open; copy the `code` value from the address bar (or the developer tools network tab)
   within 60 seconds and exchange it:

   ```bash
   curl -sS -X POST "$GW/auth/token" \
     --data-urlencode grant_type=authorization_code \
     --data-urlencode code='<code>' \
     --data-urlencode code_verifier="$VERIFIER" \
     --data-urlencode redirect_uri='lapluma://auth/callback'
   ```

   The response's `accessToken` is valid for one hour. Do not paste it into a ticket or chat.
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
sign in again. Sign-ins in progress and unredeemed sign-in codes fail the same way, because their
keys are derived from the signing key. API Gateway caches the key set for up to five minutes, so new sessions can be refused
at the edge for a few minutes after the switch. Rotate in a quiet window, and at once if the key may
have been exposed.

### IdP certificate

Add the new certificate to that IdP's `signingCertificates` next to the old one, add the JSON as a
new secret version, and start a new core API revision. After the IdP has switched to the new
certificate, remove the old one the same way.

## 6. App Review and TestFlight reviewer account

Apple's reviewers sign in the same way a clinic user does: SAML through the **LaPluma Staging**
enterprise application in the hybridcloudworks.com Entra ID tenant, against the staging gateway.
There is no test token, bypass, or reviewer-only code path. The account's password lives only in
App Store Connect; never write it into this repository, a ticket, or chat.

### 6.1 Create the reviewer user (Entra admin center)

Needs at least **User Administrator** for steps 1 to 3 and **Cloud Application Administrator** (or
Application Administrator) for step 4.

1. **Entra ID > Users > All users > New user > Create new user.**
   - User principal name: `reviewer` @ `hybridcloudworks.com` (the domain must be in the tenant's
     `allowedDomains` in `lp-saml-idps-staging`; hybridcloudworks.com already is).
   - Display name: `LaPluma App Review`.
   - Password: let Entra generate it; you replace it in step 3.
   - Account enabled: yes. Do not assign any directory role and no Microsoft 365 license; SAML
     sign-in needs neither.
2. **Restrict what the account can reach.** Add it to a new security group, for example
   `LaPluma App Review`, and use that group for the assignment and the Conditional Access steps
   below. Nothing else in the tenant should grant this group access.
3. **Set a permanent password.** In a private browser window, sign in once at
   `https://myaccount.microsoft.com` as the reviewer, change the temporary password to a long
   random one, and keep it only in your password manager until step 6.2. If the tenant's password
   policy expires passwords, exempt this account
   (`Update-MgUser -UserId reviewer@hybridcloudworks.com -PasswordPolicies DisablePasswordExpiration`
   in Microsoft Graph PowerShell) so review does not fail on an expired password.
4. **Assign it to the app.** **Entra ID > Enterprise apps > LaPluma Staging > Users and groups >
   Add user/group**, select the `LaPluma App Review` group, and the role the reviewer should hold
   (with no app roles defined, the default role arrives without a role claim and the API treats the
   user as `preparer`). Keep **Properties > Assignment required** set to **Yes**, so the account
   can sign in to this app only.
5. **Multifactor authentication.** App Review cannot complete an MFA prompt on your phone, so the
   reviewer account must not get one when it signs in to LaPluma Staging:
   - **With Conditional Access (Entra ID P1):** exclude the `LaPluma App Review` group from the
     policies that require MFA, and add a policy that targets only that group, includes all
     resources except **LaPluma Staging**, and **Blocks access**. The account then reaches nothing
     but this app. Check **Sign-in logs** after the first review sign-in.
   - **With security defaults (no P1):** security defaults cannot exclude a user and require every
     account to register MFA. A reviewer account is not workable in that state; either move to
     Conditional Access as above or give App Review a different sign-in path before submitting.
6. **Test it end to end** from a TestFlight build on a device that has never signed in as you: tap
   **Sign in**, enter `reviewer@hybridcloudworks.com`, tap **Sign in with Microsoft Entra ID**,
   confirm the system's "wants to use … to sign in" prompt, enter the password, and answer **Yes**
   to **Stay signed in?** so the hourly session renewal needs no password. Then confirm the session
   survives past an hour of use.

To revoke review access at any time, disable the account (**Users > LaPluma App Review >
Edit properties > Account enabled: No**); its session tokens expire within the hour and renewal
fails at once.

### 6.2 Enter it in App Store Connect

- **App Store review:** **App Store Connect > Apps > LaPluma >** the version being submitted **>
  App Review Information > Sign-In Information**. Tick **Sign-in required**, enter
  `reviewer@hybridcloudworks.com` as **User name** and the password from 6.1 step 3 as
  **Password**, and paste `apps/ios/AppStore/review/reviewer-notes.en-US.txt` from PEN-lapluma_app
  into **Notes**.
- **External TestFlight:** **App Store Connect > Apps > LaPluma > TestFlight > Test Information >
  Beta App Review Information**. Tick **Sign-in required** and enter the same user name and
  password.

Rotate the password after each review cycle: change it in Entra, then update both App Store
Connect fields.
