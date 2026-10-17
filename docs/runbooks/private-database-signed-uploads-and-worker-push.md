# Runbook: Private Cloud SQL, Signed Uploads, and Worker Push (INF-25 to INF-28)

How to apply the four P0 fixes to staging (`project-27d49bcd-4a1d-4d7a-8a7`, HCP Terraform
workspace `lapluma-staging`), what the plan must show, the order that keeps the database
unreachable from the internet the whole time, and how to verify each fix from outside.

> **The Cloud SQL instance is stopped on purpose** (`lp-postgres-staging-9f5d4205`, activation
> policy `NEVER`) because it still has a public IPv4 address and accepts plaintext. Do not start it
> before [section 5](#5-apply-with-the-instance-still-stopped). Terraform does not manage its power
> state, so neither the plan nor the apply starts it.

---

## 1. What changes

| Item | Change | Where |
|------|--------|-------|
| INF-25 | Upload URLs are real V4 signed URLs. The Cloud Storage library builds the canonical request; the RSA-SHA256 signature comes from the IAM Credentials `signBlob` API, signed by the workflow API's own service account. The workflow API now gets the quarantine bucket and the signer account from Terraform, so staging issues upload URLs instead of answering 503. | `src/workflow-api/GoogleCloudStorageUploadUrlIssuer.cs`, `IamCredentialsBlobSigner.cs`; `modules/iam`, `modules/compute` |
| INF-26 | Every workflow query is filtered by the caller's tenant, read from the session token's `tenant_id` claim and nowhere else; a token without exactly one valid `tenant_id` gets 403. Client creation uses the caller's tenant instead of `tenant_clinic_alpha`. | `src/workflow-api/WorkflowTenant.cs`, `PostgresWorkflowSource.cs`, `WorkflowFixtureSource.cs` |
| INF-27 | Cloud SQL has no public IPv4 address (`ipv4_enabled = false`) and refuses unencrypted connections (`ssl_mode = "ENCRYPTED_ONLY"`). It keeps its private address on `lp-vpc-staging` through the existing Private Service Access range `lp-sql-range-staging`. The core API, the workflow API and the migration job get Direct VPC egress (`lp-snet-staging`, `PRIVATE_RANGES_ONLY`); the migration job connects to the private IP with `PGSSLMODE=require` instead of the `/cloudsql` socket, which needs a public address. Both APIs force `SslMode=Require` on any PostgreSQL connection string and refuse `Disable` or `Allow` at startup. | `modules/database`, `modules/compute`; `PostgresConnectionSecurity.cs` in both APIs |
| INF-28 | The push subscription `lp-process-worker-sub-staging` delivers to `<worker URL>/pubsub` with an OIDC token for `lp-ps-invoker-staging`, the worker's only `run.invoker`. The quarantine bucket publishes `OBJECT_FINALIZE` events to `lp-document-uploaded-staging`. | `environments/staging/main.tf`, `modules/messaging`, `modules/compute` |

**INF-28 keeps the worker's ingress internal.** The board item asked for `INGRESS_TRAFFIC_ALL`
because Pub/Sub push comes from outside the VPC. Cloud Run now counts a Pub/Sub push subscription
in the same project, delivering to the service's default `run.app` URL, as an internal source
([Cloud Run ingress](https://cloud.google.com/run/docs/securing/ingress)), so the push reaches an
`INGRESS_TRAFFIC_INTERNAL_ONLY` service and opening ingress would only add exposure. What actually
blocked delivery in staging was that the subscription had no push configuration and the worker had
no invoker. If [verification](#inf-28-worker-push) shows deliveries refused at ingress, set
`ingress = "INGRESS_TRAFFIC_ALL"` on `processing_worker` in `modules/compute/main.tf`; the
`run.invoker` binding still limits invocation to `lp-ps-invoker-staging`.

The worker does not validate the OIDC token itself. Cloud Run does, before the request reaches the
container: signature, expiry, and audience (the service URL), then `run.invoker` for the token's
service account.

## 2. Before you start

```bash
PROJECT=project-27d49bcd-4a1d-4d7a-8a7
REGION=us-central1
INSTANCE=lp-postgres-staging-9f5d4205

gcloud sql instances describe "$INSTANCE" --project="$PROJECT" \
  --format='value(state,settings.activationPolicy,settings.ipConfiguration.ipv4Enabled,settings.ipConfiguration.sslMode,settings.ipConfiguration.authorizedNetworks.len())'
```

Expect `STOPPED  NEVER  True  ALLOW_UNENCRYPTED_AND_ENCRYPTED  0`. Stop if the instance is running
or if any authorized network is listed: someone has changed it, and the order below assumes
neither.

The VPC, the subnet, and Private Service Access already exist and do not change:
`lp-vpc-staging`, `lp-snet-staging` (`10.10.0.0/24`, large enough for Direct VPC egress, which
needs `/26` or more), `lp-sql-range-staging` (`10.247.80.0/20`), and the peering to
`servicenetworking.googleapis.com`. The instance's private address is `10.247.80.3`.

## 3. Merge, then deploy the new images

1. Merge the pull request once CI is green.
2. Run *Deploy to GCP Cloud Run* for `staging` from `main`. Either order with the apply is safe,
   but deploying first means the workflow API that receives the bucket setting is the one that
   signs correctly. Until the apply, upload issuing stays fail-closed (503). `gcloud run deploy`
   keeps the VPC and environment settings Terraform adds later.

## 4. Plan and review

Start an HCP run on the merged commit (auto-apply is off). The plan must contain exactly these
twelve changes, **5 to add, 7 to change, 1 to destroy**. The last two rows are the INT-22 sign-in
contract ([Session Tokens and SAML Identity Providers](session-tokens-and-saml-identity-providers.md)),
which lands in the same merge. The API config is replaced create-before-destroy, so the one destroy
is the old config, removed only after the gateway has moved off it. If the INT-22 gateway change was
already applied, those two rows are absent and the plan reads 4 to add, 6 to change, 0 to destroy.

| Address | Action | What changes |
|---------|--------|--------------|
| `module.iam.google_service_account_iam_member.workflow_api_self_token_creator` | create | `roles/iam.serviceAccountTokenCreator` for `lp-wf-api-staging` on its own account |
| `module.database.google_sql_database_instance.postgres` | update | `settings.ip_configuration`: `ipv4_enabled` `true -> false`, `ssl_mode` `ALLOW_UNENCRYPTED_AND_ENCRYPTED -> ENCRYPTED_ONLY`. Nothing else, and no `activation_policy` |
| `module.compute.google_cloud_run_v2_service.core_api` | update | `template.vpc_access` added |
| `module.compute.google_cloud_run_v2_service.workflow_api` | update | `template.vpc_access` added; `Workflow__GcsQuarantineBucket` and `Workflow__GcsSignerServiceAccount` added |
| `module.compute.google_cloud_run_v2_service.processing_worker` | update | No real change: see below |
| `module.compute.google_cloud_run_v2_job.db_migration` | update | `vpc_access` added; `cloudsql` volume and mount removed; `PGHOST`, `PGPORT`, `PGSSLMODE` added |
| `module.compute.google_cloud_run_v2_service_iam_member.processing_worker_pubsub_invoker[0]` | create | `roles/run.invoker` on `lp-proc-worker-staging` for `lp-ps-invoker-staging` |
| `module.messaging.google_pubsub_subscription.processing_subscription` | update | `push_config` added: endpoint `<worker URL>/pubsub`, OIDC token for `lp-ps-invoker-staging`, audience `<worker URL>` |
| `module.messaging.google_pubsub_topic_iam_member.gcs_publisher[0]` | create | `roles/pubsub.publisher` on `lp-document-uploaded-staging` for the Cloud Storage service agent |
| `module.messaging.google_storage_notification.quarantine_notification[0]` | create | `OBJECT_FINALIZE` on `lp-quarantine-staging-<project>` to `lp-document-uploaded-staging` |
| `module.gateway.google_api_gateway_api_config.api_cfg` | replace (create, then delete) | INT-22: new `lp-cfg-staging-…` config adding `POST /auth/token` with `security: []`, the PKCE login parameters, and the ACS `302`/`400`/`503` responses |
| `module.gateway.google_api_gateway_gateway.gw` | update | INT-22: `api_config` moves to the new config |

All three Cloud Run services also show a service-level `scaling` block with zero counts being
removed (`manual_instance_count = 0`, `min_instance_count = 0`). google provider 6.50 reports it on
every plan and it changes nothing; it is the only diff on `processing_worker`. If the deploy in
step 3 used a commit tag, the three image references may also move back to `:latest` (the same
image).

Stop on anything else, and in particular on any change to `activation_policy`, to authorized
networks, or to the network, subnet, or Private Service Access resources.

## 5. Apply, with the instance still stopped

Apply the reviewed run. Then check the instance:

```bash
gcloud sql instances describe "$INSTANCE" --project="$PROJECT" \
  --format='value(state,settings.ipConfiguration.ipv4Enabled,settings.ipConfiguration.sslMode)'
```

- **`STOPPED  False  ENCRYPTED_ONLY`**: Cloud SQL accepted the change while stopped. The instance
  never runs with a public address again. Go to [section 7](#7-verify).
- **The apply failed on `google_sql_database_instance.postgres` only**, with an error that the
  instance is not running or not in a state to accept the request, and the instance still reads
  `STOPPED  True  ALLOW_UNENCRYPTED_AND_ENCRYPTED`: Cloud SQL applies most edits only to a running
  instance. Nothing about the instance changed, everything else is applied, and it is still
  unreachable. Continue with section 6.

## 6. Only if section 5 failed on the instance: one start, patched at once

The instance has to run for the change to apply. It must not run with a public address longer than
the patch takes, and never with an authorized network.

1. Re-run the check in [section 2](#2-before-you-start) and confirm zero authorized networks.
   With none, the public address accepts no direct connection: the only way in is the Cloud SQL Auth
   Proxy or a Cloud SQL connector, which require `roles/cloudsql.client` (held only by
   `lp-core-api-staging` and `lp-wf-api-staging`) and the database password, and always use TLS.
   Both APIs serve fixtures and open no database connection.
2. Start it and remove the address in one call, so it comes up without one if Cloud SQL accepts the
   combination:
   ```bash
   gcloud sql instances patch "$INSTANCE" --project="$PROJECT" \
     --activation-policy=ALWAYS --no-assign-ip --ssl-mode=ENCRYPTED_ONLY
   ```
   If Cloud SQL refuses to combine them, run the same command with only
   `--activation-policy=ALWAYS`, and as soon as it returns, run it again with only
   `--no-assign-ip --ssl-mode=ENCRYPTED_ONLY`.
3. Run a new plan. It must show no change to the instance (Terraform and Cloud SQL now agree), and
   nothing but the provider's `scaling` noise elsewhere.
4. If the database is not needed yet, stop it again:
   `./infra/scripts/manage-environment.ps1 -Action pause`.

## 7. Verify

### INF-27: Cloud SQL

```bash
gcloud sql instances describe "$INSTANCE" --project="$PROJECT" \
  --format='yaml(ipAddresses,settings.ipConfiguration.ipv4Enabled,settings.ipConfiguration.sslMode)'
```

Expect the `PRIVATE` address `10.247.80.3` and no `PRIMARY` (public) address; `ipv4Enabled:
false`; `sslMode: ENCRYPTED_ONLY`.

Optional, with the instance running: prove from inside the VPC that plaintext is refused and TLS is
accepted, using the migration job (it has Direct VPC egress and the `postgres:16-alpine` client):

```bash
# Plaintext: refused by the server for lack of encryption.
gcloud run jobs execute lp-db-migration-staging --project="$PROJECT" --region="$REGION" --wait \
  --args=psql,-c,"select 1" \
  --update-env-vars=PGUSER=lapluma_app,PGDATABASE=lapluma_workflow,PGSSLMODE=disable
# TLS: gets past the handshake and stops at authentication, because no password is supplied.
gcloud run jobs execute lp-db-migration-staging --project="$PROJECT" --region="$REGION" --wait \
  --args=psql,-c,"select 1" \
  --update-env-vars=PGUSER=lapluma_app,PGDATABASE=lapluma_workflow
gcloud logging read 'resource.type="cloud_run_job" AND resource.labels.job_name="lp-db-migration-staging"' \
  --project="$PROJECT" --freshness=15m --limit=20 --format='value(textPayload)'
```

The first run should log a `pg_hba.conf` rejection mentioning no encryption; the second
`fe_sendauth: no password supplied`. Both executions exit non-zero; that is expected.

### INF-28: worker push

```bash
gcloud pubsub subscriptions describe lp-process-worker-sub-staging --project="$PROJECT" --format='yaml(pushConfig)'
gcloud run services get-iam-policy lp-proc-worker-staging --project="$PROJECT" --region="$REGION" --format=json
WORKER_URL=$(gcloud run services describe lp-proc-worker-staging --project="$PROJECT" --region="$REGION" --format='value(status.url)')
curl -s -o /dev/null -w '%{http_code}\n' -X POST "$WORKER_URL/pubsub" -H 'Content-Type: application/json' -d '{}'
curl -s -o /dev/null -w '%{http_code}\n' -X POST "$WORKER_URL/pubsub" -H 'Content-Type: application/json' -d '{}' \
  -H "Authorization: Bearer $(gcloud auth print-identity-token)"
```

| Check | Expected |
|-------|----------|
| `pushConfig` | `pushEndpoint: <worker URL>/pubsub`, `oidcToken.serviceAccountEmail: lp-ps-invoker-staging@...`, `oidcToken.audience: <worker URL>` |
| IAM policy | `roles/run.invoker` for `lp-ps-invoker-staging` only (the deployer's `roles/run.developer` stays); no `allUsers` or `allAuthenticatedUsers` |
| Unauthenticated `POST /pubsub` from the internet | 404: internal ingress refuses it before IAM. With `INGRESS_TRAFFIC_ALL` it would be 403 |
| Same request with your own identity token | 404 for the same reason; with `INGRESS_TRAFFIC_ALL`, 403, because you are not the invoker |

Then send one synthetic event through the subscription and confirm the worker answered it:

```bash
gcloud pubsub topics publish lp-document-uploaded-staging --project="$PROJECT" \
  --message='{"name":"verify/inf-28.pdf","bucket":"verify","contentType":"application/pdf","size":"1"}'
gcloud logging read 'resource.type="cloud_run_revision" AND resource.labels.service_name="lp-proc-worker-staging" AND httpRequest.requestUrl:"/pubsub"' \
  --project="$PROJECT" --freshness=10m --limit=5 --format='value(httpRequest.status)'
```

Expect `200`. If the worker logs nothing and the subscription's undelivered message count rises,
the push is being refused before the worker: apply the ingress fallback in
[section 1](#1-what-changes).

### INF-25: signed upload

This needs a session token (`accessToken` from a SAML sign-in; see
[Session Tokens and SAML Identity Providers](session-tokens-and-saml-identity-providers.md)).

```bash
GATEWAY=https://lp-gateway-staging-am9yq93d.uc.gateway.dev
TOKEN=<accessToken>
FILE=verify.pdf
curl -s -X POST "$GATEWAY/v1/documents/upload-sessions" \
  -H "Authorization: Bearer $TOKEN" -H "Idempotency-Key: $(uuidgen)" -H 'Content-Type: application/json' \
  -d "{\"folderId\":\"folder-fixture-0001\",\"originalName\":\"verify.pdf\",\"sizeBytes\":$(stat -c %s "$FILE"),\"contentSha256\":\"$(sha256sum "$FILE" | cut -d' ' -f1)\"}" \
  > session.json
UPLOAD_URL=$(jq -r .uploadUrl session.json)
curl -s -o /dev/null -w '%{http_code}\n' -X PUT --upload-file "$FILE" "$UPLOAD_URL"                                # 200
curl -s -o /dev/null -w '%{http_code}\n' -X PUT --upload-file "$FILE" "$(echo "$UPLOAD_URL" | sed 's#?#-tampered?#')" # 403
gcloud storage ls "gs://lp-quarantine-staging-$PROJECT/"
```

The first `PUT` returns 200 and the object appears in the bucket. Any change to the URL (another
object name, a longer `X-Goog-Expires`) returns 403 `SignatureDoesNotMatch`; so does the same URL
after 15 minutes. The upload also fires the bucket notification, so the worker logs one more
`POST /pubsub` with status 200: INF-25 and INF-28 together, end to end.

A `503` with `urn:lapluma:problem:upload-signing-unavailable` right after the apply means the new
`signBlob` grant has not propagated yet; IAM changes can take a few minutes. Retry. If it persists,
the workflow API log says which status the IAM Credentials API returned.

### INF-26: tenant scoping

CI proves it against PostgreSQL 16 with the repository's own migrations
(`PostgresTenantIsolationTests`). In staging the workflow API still serves fixtures (INF-30), where
clients a tenant creates are listed only to that tenant. A session token without `tenant_id` gets
403 on every `/v1` route.

## 8. Rolling back

- **INF-25, INF-26, INF-28:** revert the pull request and apply. Reverting removes the push
  configuration, the bucket notification, and the two new IAM bindings.
- **INF-27:** do not give the instance its public address back. If something needs to reach the
  database, it does so over the VPC: give it Direct VPC egress on `lp-snet-staging` like the
  services here.

## 9. For the restore script's plan review

`Get-PlanReview` in `lapluma-restore.ps1` compares plan addresses and actions with a list. For this
change the list is:

```powershell
$allowed = [ordered]@{
    'module.iam.google_service_account_iam_member.workflow_api_self_token_creator'              = @('create')
    'module.database.google_sql_database_instance.postgres'                                     = @('update')   # settings.ip_configuration only
    'module.compute.google_cloud_run_v2_service.core_api'                                       = @('update')
    'module.compute.google_cloud_run_v2_service.workflow_api'                                   = @('update')
    'module.compute.google_cloud_run_v2_service.processing_worker'                              = @('update')   # provider's service-level scaling noise
    'module.compute.google_cloud_run_v2_job.db_migration'                                       = @('update')
    'module.compute.google_cloud_run_v2_service_iam_member.processing_worker_pubsub_invoker[0]' = @('create')
    'module.messaging.google_pubsub_subscription.processing_subscription'                       = @('update')
    'module.messaging.google_pubsub_topic_iam_member.gcs_publisher[0]'                          = @('create')
    'module.messaging.google_storage_notification.quarantine_notification[0]'                   = @('create')
    'module.gateway.google_api_gateway_api_config.api_cfg'                                      = @('create,delete')   # INT-22, create before destroy
    'module.gateway.google_api_gateway_gateway.gw'                                              = @('update')          # INT-22, moves to the new config
}
$required = @(
    'module.database.google_sql_database_instance.postgres',
    'module.iam.google_service_account_iam_member.workflow_api_self_token_creator',
    'module.compute.google_cloud_run_v2_service_iam_member.processing_worker_pubsub_invoker[0]',
    'module.messaging.google_pubsub_subscription.processing_subscription',
    'module.gateway.google_api_gateway_api_config.api_cfg'
)
```

The existing activation-policy check still applies and must still find no change. For the instance,
also require that the only differences between `change.before.settings[0]` and
`change.after.settings[0]` are `ip_configuration[0].ipv4_enabled` (`true` to `false`) and
`ip_configuration[0].ssl_mode` (to `ENCRYPTED_ONLY`).
