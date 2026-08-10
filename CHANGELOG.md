# Changelog

Completed work only. Planned work lives in `TODO.md`; blockers awaiting a human decision live in
`REVIEW.md`.

## Unreleased

### Changed

- Moved the Core API from .NET 9 to **.NET 10**. `LaPluma.CoreApi.csproj` targets `net10.0`, the
  Dockerfile builds on `dotnet/sdk:10.0` and runs on `dotnet/aspnet:10.0`, and CI installs the
  10.0.x SDK. .NET 9 is a Standard Term Support release whose support window has closed; .NET 10 is
  the current Long Term Support release, which suits a pilot that must stay patchable. The README,
  `TODO.md` item 5.1, and the Architecture Overview and Azure Deployment Plan wiki pages were
  updated to match, so the stated stack and the built stack agree.
- Pinned all three container base images by digest: `mcr.microsoft.com/dotnet/sdk:10.0` and
  `mcr.microsoft.com/dotnet/aspnet:10.0` in `src/core-api/Dockerfile`, and `python:3.12-slim` in
  `src/document-processing/Dockerfile`. Two builds of the same commit now resolve the same base
  layers. Each pin keeps its tag alongside the digest for readability; the digest is what resolves.
- Removed the `main` and `codex/**` branch filter from the foundation validation workflow's `push`
  trigger, so every branch gets push validation rather than only two prefixes.
- Consolidated repository documentation into a single model: `README.md` for repository purpose,
  `CHANGELOG.md` for completed work, `REVIEW.md` for human-resolvable blockers, `TODO.md` for the
  engineering work queue, and the GitHub Wiki for everything else.
- Rewrote `README.md` so it covers only repository purpose, layout, requirements, quick start,
  configuration overview, conventions, and navigation.

### Added

- Repository governance files, all but one of them. `.github/dependabot.yml` watches NuGet, pip,
  Docker, and GitHub Actions. The last two are load-bearing rather than optional here: every
  container base image is pinned by digest and every action by commit SHA, so until now nothing
  proposed those bumps and the pins went stale silently. `.github/pull_request_template.md` is a
  short checklist of the invariants a change is actually read against — no credentials or applicant
  identifiers, content-free telemetry, no processing-zone database route, no automated activation,
  `enableProvisioning` still `false`, and new dependencies pinned. `.github/SECURITY.md` routes
  reports to GitHub's private advisory flow and names the guarantee classes worth reporting, while
  scoping out the gaps `REVIEW.md` and `TODO.md` already track. `.github/CONTRIBUTING.md` covers
  where work is tracked, the commands CI runs, and the two conventions that are easy to miss —
  verify a fix by breaking it, and record a finding rather than absorbing it into the current
  change.

  These four sit under `.github/` rather than the repository root. GitHub reads `SECURITY.md` and
  `CONTRIBUTING.md` from either location, and `.github/` keeps the root at the four markdown files
  the Documentation Standards allow.

- Apache License 2.0, in a `LICENSE` byte-identical to the canonical text, with the copyright notice
  in `NOTICE` as the Apache Software Foundation's own guidance directs. A public repository with no
  licence grants nothing while looking merely unfinished; this states the terms.

### Fixed

- The acquisition orchestration publishes. `publish_acquisition_proposals` returned metadata and
  sent nothing; the Durable activity now carries an identity-based Service Bus output binding to
  the `catalog-acquisition` queue, and the function app is configured with the namespace and its
  managed identity rather than a connection string — the namespace sets `disableLocalAuth`, so a
  shared-access key would be refused even if one were configured.

  The publisher takes a `send` seam. The activity passes the output binding, the tests pass a
  recorder, and passing nothing keeps the offline path deterministic. That is not a convenience:
  without it the module could not be tested at all without the runtime it is deferred behind, which
  is the same reason the orchestration's shape is asserted by parsing `function_app.py` with `ast`.

  Three invariants are enforced rather than described. A proposal carrying a key outside
  `PUBLISHABLE_KEYS` is refused rather than published — the message lands on a queue another
  component reads, so an extra key is a contract change made by accident, and an applicant
  identifier arriving there would be published rather than merely logged. Only `PROPOSED` items may
  be published, so nothing that already claims an outcome can route around the two-person approval.
  A send failure propagates rather than being swallowed, so the orchestrator's bounded retry sees
  it and the accepted count never describes a publish that did not happen.

  The binding collects into a list and sets it once, because a Functions output binding is written
  when the function returns: either the contract check raises and nothing is set, or every message
  goes together. There is no partial publish to observe.
- Every resource that can emit diagnostics now routes them to the Log Analytics workspace. Not one
  resource in the network, security, messaging, data, or compute modules had a `diagnosticSettings`
  child: no SQL security log, no Key Vault access log, no Managed HSM audit trail, no Service Bus
  operational log, and no NSG rule evaluation reached the workspace that was built to receive them.
  Nineteen declarations, expanding to twenty-five settings once the storage loops unroll.

  `allLogs` rather than an enumerated category list, deliberately. A list has to be revised whenever
  Azure adds a category, and the failure mode of a stale list is silence — the category never
  arrives and nothing says so. Where a type genuinely differs, it is stated: storage accounts emit
  metrics only and their blob services carry the access logs, and network security groups emit no
  metrics at all.

  The settings are written out per resource rather than looped over an array of symbols, because a
  diagnostic setting's scope has to be resolvable at the start of the deployment and a resource
  symbol is not. That is a Bicep constraint, not a stylistic choice, and the comment says so.
- A check that a resource which can emit diagnostics has somewhere to emit them. Adding a storage
  account, a vault, an environment, or a namespace without a diagnostic setting now fails, which is
  how the gap it found on its first run was caught: the function host's storage account had metrics
  wired but its blob service — where access to the deployment container actually shows up — had
  nothing. The rule only counts a `scope:` inside a `diagnosticSettings` declaration, so a role
  assignment scoped to the same resource does not satisfy it; that case is mutation-tested, because
  it is the way the rule would otherwise have passed while checking nothing.
- The Core API is no longer anonymous. Every catalog endpoint accepted every request; the design
  terminates JWT validation at the API Management edge, and a service whose only protection is an
  upstream gateway fails open the moment anything reaches it directly — which, inside the core
  subnet, plenty can. `src/core-api/CatalogAuthentication.cs` adds bearer validation and a policy
  applied to the `/v1/catalog` group, so a route added later inherits it instead of having to
  remember it. `/health` and `/ready` stay anonymous: an orchestrator holds no token, and a probe
  that needed one would report the identity provider's health rather than this service's.

  It **fails closed**. With no audience and issuer configured there is nothing to validate a token
  against, so the policy denies outright rather than falling back to accepting whatever arrives.
  Without that explicit deny the service would still reject unsigned tokens, but any scheme
  registered later — a test handler, a developer's convenience shim — would sail straight through. A
  test asserts that an authenticated caller is still refused by an unconfigured deployment.

  Adding the lock broke 27 of the 49 existing tests, which is the evidence that it is real: the 22
  that survived were the health, readiness, and contract tests that never touched the catalog. Those
  27 now authenticate through a test scheme, because they are about catalog behaviour rather than
  about the lock; the lock has its own tests.

  One bug surfaced while writing them. `UseStatusCodePages` inspects the response on the way out, so
  it only sees what was produced *below* it. Registered above authentication, the 401 travelled
  outward past it and reached the client as a bare status code — every other failure carrying a
  problem document and that one not. The middleware order is now explicit and commented, and the
  test that caught it asserts the 401 body.
- Network security groups carry rules. Four of the five had none at all, which left the AI zone with
  unrestricted outbound internet access; each now carries a baseline internet deny. The processing
  group additionally denies the `Sql` and `AzureCosmosDB` service tags and the private-endpoint
  subnet prefix, closing code review finding **F-04**: an NSG carries an implicit
  `AllowVnetOutBound` at priority 65000 and every private endpoint sits inside this VNet, so the
  existing `DenyInternetEgress` never covered a processing replica reaching the database endpoints —
  that traffic is intra-VNet and the internet rule does not apply to it. Rule structure is authored
  here; the destination addresses for an allowlist are `REVIEW.md` **R-09** and are not invented.
- The audit container carries a time-based immutability policy, with the window parameterized and
  defaulting to seven years pending **R-11**, and `allowProtectedAppendWrites` so evidence appended
  over time stays protected. Only the audit container: the other three hold working material that
  retention and erasure policy has to be able to remove, and a policy there would collide with the
  erasure obligation rather than support it.

  The policy is created **unlocked**, and there is deliberately no parameter offering to lock it.
  Locking is not a declarative property — ARM exposes it as an explicit action on the policy — so a
  `lock: true` in the template would read like a guarantee and enforce nothing. It is an
  irreversible out-of-band step for `staging` and `pilot` once R-11 ratifies the period, and never
  for `dev`.
- The foundation can now function once provisioning is unlocked. Four P0 gaps closed together,
  because each was load-bearing for the next.

  **Private endpoints and private DNS.** Every data service set `publicNetworkAccess: 'Disabled'`
  while no `privateEndpoints` or `privateDnsZones` resource existed anywhere, and the
  `snet-private-endpoints` subnet was created and left empty — provisioning would have produced a
  set of services nothing could reach. `infra/modules/privatelink.bicep` creates twelve endpoints
  and twelve zones with their virtual-network links. It is data-driven rather than one block per
  service: the blocks would differ only in three strings, and a copied block is where a wrong
  `groupId` or a zone that does not match its endpoint hides. Zones for services that do not exist
  yet — Document Intelligence, under **R-12** — are created and linked anyway, since a zone is inert
  until an endpoint registers a record in it.

  **The workload hosting layer.** `azure.yaml` declared three services with nowhere to deploy them
  and four delegated subnets with no consumers. `infra/modules/compute.bicep` adds three Container
  Apps managed environments — core, processing, and AI, each bound to its own subnet, because an
  environment is the logging and networking boundary and sharing one would collapse the trust-zone
  split the subnets exist to express — plus the Core API app with liveness and readiness probes
  wired to the endpoints that mean what they say, a queue-driven processing worker, a Flex
  Consumption function app pinned to Python 3.13, and a Premium container registry with
  managed-identity pull and no admin user. Each app carries the `azd-service-name` tag that binds it
  to its `azure.yaml` service. The function host gets its own storage account: the four data
  accounts hold case material under a retention obligation, and host bookkeeping does not belong
  beside it.

  **Role assignments.** Four managed identities existed with no assignment anywhere, while every
  service had local authentication and shared keys disabled — no workload could read or write
  anything. `infra/modules/rbac.bicep` adds thirteen assignments plus one Cosmos data-plane
  assignment, every one scoped to a single resource rather than to the resource group, because a
  group-scoped assignment would silently hand the processing zone the SQL and Cosmos access the
  design exists to deny it. The processing zone gets blob **reader** on quarantine only and receiver
  on one queue only. The AI zone gets nothing at all.

  **The Application Insights ingestion deadlock.** Ingestion and query were both disabled with no
  Azure Monitor Private Link Scope, so workloads could not send telemetry and operators could not
  read it. The scope now exists with both access modes set to `PrivateOnly`, the workspace and the
  component are scoped to it, and it is reached through a private endpoint resolving across the four
  zones Azure Monitor needs. Only with that in place did the Log Analytics workspace's own public
  ingestion and query get disabled — doing it earlier would have extended the same deadlock.
- Three checks so those gaps cannot reopen, each verified by reproducing the failure it prevents:
  a resource that disables public network access must have a private endpoint wired to it, and is
  checked in both directions — a new locked-down service fails until an endpoint exists, and an
  endpoint removed from the wiring fails too; the AI zone must hold no data-plane role, asserted
  against the RBAC module rather than trusted to a comment; and the function host's Python version
  joins the set that has to agree with the image, CI, and the documentation.
- Moved the repository to **Python 3.13** and took the two `azure-functions` bumps that depended on
  it: `azure-functions` to `>=2.2.0,<3` and `azure-functions-durable` to `>=1.7.0,<2`. The 2.x line
  requires Python `>=3.13` and the 1.x line caps at `<3.13` — the ranges are disjoint, so the pin
  could not move without the interpreter. The version now reads 3.13 in the worker image, the CI
  `setup-python` step, the worker docstring, the README, and both wiki pages, which is every place
  that states it. 3.13 rather than 3.14 because it is the minimum that unblocks the SDK; whether the
  Azure Functions runtime offers 3.13 on the chosen plan and region could not be confirmed from here.
  The function host now pins it in `infra/modules/compute.bicep`, so that is where the assumption
  lives; confirm it before provisioning.
- Three guardrails so those failures cannot return, each verified by reproducing the failure it
  prevents:
  - CI resolves `src/functions/requirements.txt` against the interpreter it tests on. Nothing
    installed that file before — not a test, not a build — so an unsatisfiable pin was invisible.
    That is not hypothetical: the incompatibility above arrived as an automated update whose checks
    all passed, and was found by reading package metadata by hand. The step fails on 3.12 and passes
    on 3.13, which is the check doing its job.
  - The validator requires one Python version across all six places that state it. Bumping only the
    image — the exact single-file change that was proposed — is now rejected, naming both sides of
    the disagreement, as is bumping only CI or only the documentation.
  - Dependabot no longer proposes interpreter minor or major bumps for the worker image. Digest and
    patch refreshes still come through, which is the security-relevant half; a version jump is a
    repository-wide decision that cannot be correct as a one-file change.
- Every third-party GitHub Action moved to its current major, in one change rather than five:
  `actions/checkout` to 7.0.1, `actions/setup-python` to 7.0.0, `actions/setup-dotnet` to 6.0.0, and
  all four `github/codeql-action` references to 4.37.6. The runner had begun forcing the previous
  `checkout` and `codeql-action` pins onto Node 24 with a deprecation warning, so these were already
  running on a runtime they do not target.

  Combining them was not tidiness. Dependabot raises one pull request per action, and for
  `github/codeql-action` that is one per sub-action — so its `init` and `analyze` proposals each
  moved half of a set that has to move together, and neither touched the two `upload-sarif`
  references at all. Both failed CI with `CodeQL job status was configuration error`, which names
  neither the cause nor the fix.
- The foundation validator now checks how workflow actions are pinned, so that failure cannot
  recur. Every third-party action must be pinned to a full commit SHA, and every
  `github/codeql-action` sub-action must be pinned to the *same* SHA. Repository-local composite
  actions are exempt: they are read from the checked-out tree, so there is no ref to pin. Both rules
  were verified by reproducing the failures — Dependabot's `init`-only change, an `upload-sarif` left
  on the previous SHA, and a tag pin in place of a SHA are each rejected, naming the offending
  action, while the unmutated tree passes.
- Policy-bearing baselines are parameters instead of literals, so `dev` and `pilot` can differ
  without editing Bicep. Twenty-two values moved: Log Analytics retention, blob and container soft
  delete, Key Vault and Managed HSM soft delete, the SQL SKU with its capacity, minimum capacity and
  auto-pause delay, the Cosmos autoscale ceiling, both zone-redundancy flags, the audit and default
  storage redundancy, the Managed HSM SKU, Service Bus capacity and partitions, and the messaging
  duplicate-detection window, lock duration, delivery count, and both TTLs. Every default reproduces
  exactly the literal it replaced — this made the baselines adjustable, it did not adjust them, and
  a check asserts each default against the value it came from.

  AZD substitutes into `infra/main.parameters.json` textually and that file must stay valid JSON,
  because `tools/validate_foundation.py` parses it. Every substituted value therefore arrives as a
  string whatever it represents. The parameters are declared as strings at that boundary and
  converted once in `infra/main.bicep`; the modules take properly typed parameters carrying the
  range and allowed-value constraints, so an out-of-range value is rejected naming the parameter.
  Each was probed: 5000 retention days, a one-day Key Vault window, 100 RU/s, a Service Bus capacity
  of 3, and an invented storage SKU are all refused, while a valid override compiles.

  The four parameter groups are named for the decision that gates their values — retention, capacity,
  resilience, messaging — rather than for the module that consumes them, so what unblocks a change is
  visible from the parameter list. The values themselves remain pending `REVIEW.md` **R-03** and
  **R-11** and `TODO.md` item 3.2, which is now a decision about numbers rather than a code change.

  Five values deliberately stayed literals, recorded with reasons on the Configuration Contract wiki
  page: blob versioning, Key Vault and HSM purge protection, Cosmos `Session` consistency, the
  hierarchical partition key, and the shared-key, local-auth, public-access, and TLS settings.
  Making those adjustable would turn a guarantee into an option.
- A push to a branch with an open pull request no longer runs every workflow twice. Both workflows
  trigger on an unfiltered `pull_request` and an unfiltered `push`, so one commit produced four
  workflow runs and sixteen check runs where eight carry the same signal — double the CodeQL and
  Trivy minutes on every push. A `guard` job now asks whether the pushed commit already has an
  **open** pull request and, if so, skips the rest of the workflow; the `pull_request` run covers
  that commit. The shared decision lives in `.github/actions/duplicate-run-guard`, because its
  correctness rests on two details that are easy to get wrong when copied. Filtering on the open
  state is what keeps a push to the default branch running: after a merge, GitHub still associates
  the commit with the pull request that introduced it, now closed, and treating that as coverage
  would stop refreshing the CodeQL baseline the security tab reads from. Failing open is what keeps
  a transient API error costing a duplicate run rather than a commit that silently went
  unvalidated. `dependency-review` is deliberately not gated on the guard — it runs only on
  `pull_request`, which is never the duplicate, and depending on the guard would let a guard failure
  suppress it.

  A `concurrency` group keyed on the commit SHA is the more obvious answer and is the wrong one: it
  puts both runs in one group, so the second to start cancels the first, and which one starts first
  is a race. Roughly half the time the push run would cancel the `pull_request` run, taking
  `dependency-review` with it and reporting "cancelled" on the pull request's checks.
- Bicep parameters, naming, and API versions are consistent, and the failures they permitted now
  fail at parameter validation instead of mid-deployment. Every parameter is environment-substituted
  by AZD, and an unset variable substitutes to the empty string, which ARM treats as supplied — so
  an empty string silently overrode a default rather than falling back to it. Each string parameter,
  including all five subnet prefixes, now carries a minimum length. `environmentName` gained length
  bounds and is lowercased where it composes a resource name, because it flows into
  `sql-${name}-${suffix}` and Azure SQL server names permit only lowercase letters, digits, and
  hyphens: `AZURE_ENV_NAME=Dev` previously failed after the resource group and network had already
  deployed. `subnetPrefixes` is a user-defined type rather than an untyped `object`, so a misspelled
  key is a compile error naming the key instead of a failure deep inside the network module. The
  Service Bus namespace was the only globally scoped resource named without a `uniqueString` suffix
  and now matches its Key Vault, Cosmos, SQL, and storage siblings, so deployment no longer depends
  on whether anyone else has already taken the plain name it resolved to. The module's `name`
  parameter still feeds that suffix — it is kept out of the literal stem so an environment name
  legal in Bicep but illegal in a global DNS label cannot reach one, the same reasoning
  `data.bicep` applies to its own `compactName`. Which of that file's two conventions should win
  repository-wide is `REVIEW.md` **R-05**. Both queues set `deadLetteringOnMessageExpiration`
  with no TTL, which meant the default was effectively infinite and the dead-letter policy could
  never fire; they now carry an explicit seven-day window pending `REVIEW.md` **R-11**. The SQL
  database and the Cosmos database and container are tagged — the remaining untagged resources are
  ARM proxy types whose definitions have no `tags` property at all, which was confirmed rather than
  assumed. Service Bus and SQL moved off preview API versions to GA, leaving no preview pin anywhere
  in `infra/`. `infra/main.bicep` no longer names a symbolic resource `resourceGroup`, shadowing the
  built-in function.
- Runtime application settings are inventoried and checked. `ACQUISITION_SCHEDULE`,
  `DURABLE_TASK_HUB_NAME`, and `PORT` are consumed by the services but appeared in no
  machine-checked inventory — the existing `.env.example` parity rule covers only the Bicep
  parameter half of the configuration contract, and two of the three are required for the Functions
  host to start at all. They now have their own section in `.env.example`: every `%NAME%` binding in
  `src/functions` must be declared there, and every name declared there must appear in `src/`, so
  neither an unbound setting nor a stale entry survives. The presence check is a literal search
  rather than a scan for `os.environ`, because a value read through an indirection would otherwise
  be reported as unused.
- The processing worker's health listener no longer holds threads open indefinitely, disclose the
  interpreter version, or answer one endpoint in two content types. Requests now time out,
  `ThreadingHTTPServer` shuts down on SIGTERM so container stop lets in-flight probes finish, the
  `Server` header is the service name rather than `BaseHTTP/0.6 Python/3.12.x`, and an unknown path
  returns a JSON 404 instead of an HTML error page. That 404 carries a real document rather than
  an empty payload, because `Content-Type: application/json` with nothing after it is a
  contradiction that breaks any client parsing every response unconditionally; the body uses the
  same envelope as a successful probe and never echoes the requested path. `PORT` is validated
  rather than passed straight to `int()`, which previously crashed at startup with an unhandled
  `ValueError` on a non-numeric value.
- The prohibited-input rule reports a malformed contract instead of dying on one. Every shape it
  reads is type-checked first: a path item, a `parameters` value, and each declaration within it.
  A non-object entry previously raised `AttributeError` or `TypeError` out of the collector, so a
  rule whose entire purpose is to surface a contract violation ended CI on a traceback rather
  than the ERROR line the log is scanned for. A `parameters` value set to an object was worse
  than a crash — it was iterated as its keys, collecting nothing while looking like a clean pass.
- The prohibited-input rule now sees every parameter declaration. It collected names from operation
  objects only, so a `parameters` list declared at path-item level — a documented OpenAPI construct
  that applies to every operation beneath it — was invisible, and a `caseId` declared that way
  walked past the rule that exists to keep person, case, and eligibility identifiers out of the
  catalog API. A `$ref` parameter has no `name`, so the old code raised `KeyError` and CI failed
  with a traceback rather than a diagnosis; such declarations are now rejected, because a gate that
  cannot read a declaration must not pass it. Request bodies are rejected outright, since no catalog
  operation declares one.
- Structural drift in the contract now produces a diagnosis rather than a traceback: the remaining
  direct dictionary indexing in the validator is guarded. Each check owns its failures instead of
  appending to a module-level global, so a check can run twice, or alone, without inheriting
  another's results — which is why these functions previously had no unit tests.
- Request logging no longer emits the URL. ASP.NET Core's `Hosting.Diagnostics` logs the full
  request URL including the query string at `Information`, on by default, which the content-free
  telemetry constraint does not allow. That category and `Microsoft.AspNetCore.Routing` are raised
  to `Warning`; the service logs every rejection itself with a correlation identifier and no request
  content, so the signal worth keeping is kept. A test asserts that no log from *any* category
  carries a path, query value, or route value — measured by capturing at `Trace`, so removing the
  filters makes the suppressed logging reappear and fails the test.
- The acquisition sweep is a singleton and reports what the publisher accepted. The timer trigger
  called `start_new` with no instance ID, so Durable Functions minted a fresh GUID per firing and a
  sweep running longer than the schedule interval — or a `use_monitor` catch-up landing on a normal
  firing — started a second sweep proposing the same editions to the same downstream. It now uses a
  fixed instance ID and skips the start when a run is already `Running`, `Pending`, or
  `ContinuedAsNew`. The publish activity is retried on transient failure; the proposal activity
  deliberately is not, because a scope-drift rejection is deterministic and must fail closed on the
  first attempt. The orchestration result now carries `acceptedProposalCount` and `published` rather
  than discarding them, so a publisher accepting three of four proposals cannot report success for
  all four.
- The Core API's `correlationId` is now findable. It was a fresh `Guid.NewGuid()` per response,
  written nowhere and derived from nothing, so the identifier a user reported to support could not
  be located in any log or trace. It is now derived from the ambient W3C trace identifier — sixteen
  bytes, exactly a GUID, so the contract's `format: uuid` is unchanged while the value is something
  that exists in the trace backend — and each problem is logged once at construction. The service
  previously performed no logging at all. The log records the problem type, status, and correlation
  identifier only; a test asserts these logs carry no path, query string, or route value.
- `/ready` reports readiness rather than a literal. It answered from a constant and never resolved
  `CatalogRepository`, so it could not detect the one way the catalog actually breaks: the fixture
  is built in a static constructor that throws on an unrecognised form number, which makes every
  `/v1/catalog/*` route return 500 while a literal probe stays green. Readiness now resolves the
  repository and returns 503 when it cannot be built, with `/health` left as pure liveness so an
  orchestrator does not restart a process that is running correctly.
- The secret scan matches an Azure storage connection string in any key ordering. Connection
  strings are unordered key/value pairs, and the rule required one specific ordering, so the same
  credential written any other way passed the scan. Added rules for a bare storage account key and
  for a shared-access signature — the shapes that matter most in a repository whose invariant is
  that `allowSharedKeyAccess` is false everywhere and access is managed-identity only.
- The Core API build context is an allowlist. `src/core-api/Dockerfile` does `COPY . ./` while
  `.dockerignore` excluded only build output, so a developer's local `appsettings.Development.json`,
  `.env`, or certificate sitting in that directory would have been copied into a build layer. The
  file now denies everything and re-includes only `*.cs` and `*.csproj`, so new source files are
  picked up automatically and anything else has to be allowed deliberately.
- The acquisition contract rejects an unknown key instead of ignoring it.
  `propose_acquisition_batch` read two keys with `.get()` and ignored everything else, while its
  sibling in the processing zone computed an exact key-set difference and had a test proving an
  injected `approve` key fails closed — two contract modules taking opposite positions on the same
  question at the same kind of boundary. The request is now checked as a whole shape. This matters
  beyond consistency: the dict is the Durable Functions orchestration input, which is persisted to
  the task hub and replayed, so a personal or case field reaching this function would be written to
  durable storage. `host.json`'s `traceInputsAndOutputs: false` suppresses tracing, not history.
  `requestedAt` is required and must parse as ISO 8601, and a test reads the timer trigger's
  `client_input` keys out of `function_app.py` so the caller and the contract cannot drift apart.
- The processing zone's request contract is validated rather than prefix-matched. The blob URIs
  that bound which single object the isolated worker may touch were checked only for an `https://`
  prefix, which accepted a URI with no host at all, an arbitrary external host, a shared-access
  signature smuggled through the query string, and two spellings of one object differing by a
  trailing slash — defeating the create-only staging guarantee. They are now parsed: HTTPS only,
  pinned to the Azure Blob host suffix, no embedded credentials, no query or fragment, a
  container-and-blob path, and the normalised forms compared for distinctness.
- Anchored value proposals validate their polygon rather than counting it. The previous check
  counted coordinates while its message promised a "non-degenerate polygon", so a zero-area polygon
  and a tuple of eight strings both passed. Coordinates must now be finite, non-negative numbers
  enclosing a non-zero extent, and `AnchoredValueProposal` validates in `__post_init__`, so an
  invalid proposal cannot be constructed at all — previously the "all proposals require human
  confirmation" invariant depended on every caller remembering to call `validate()`.
- The `sha256` error message no longer states a rule the code does not apply: the value is
  normalised to lowercase on ingest, so the message says so instead of demanding lowercase input.
- Core API error responses now conform to the published contract. Every problem document is served
  as `application/problem+json` rather than `application/json`, and its `status` is derived from the
  same value as the HTTP status so the two cannot disagree. A malformed `editionDate` previously
  failed framework route binding, which returns a plain-text diagnostic in Development and a bare
  empty 400 in Production — the error a client saw depended on the environment. The route now binds
  the value as a string and parses it, so the response is a problem document everywhere, and
  `contracts/catalog.openapi.json` declares the 400 the route can actually return.
- Catalog codes are validated against the patterns the contract declares. A malformed
  `categoryCode` previously returned 200 with an empty list, indistinguishable from a category that
  legitimately has no packages; it now returns 400. With inputs validated, the lookups compare
  ordinally rather than case-insensitively, matching the contract's uppercase-only declaration.
- `CatalogRepository` asserts package-code uniqueness once at startup instead of throwing from
  `SingleOrDefault` on a request, and the service version is a single constant pinned by test to
  `info.version` in the contract rather than a literal repeated per endpoint.
- Removed the global `JsonStringEnumConverter`. Type-level attributes take precedence over a
  globally registered converter, so it never applied to any existing enum while making a new enum
  added without those attributes look handled — it would have serialised in PascalCase.
- The catalog fixture parser now rejects a form declared more than once. Classifications are keyed
  by form number, so a second declaration of the same form silently replaced the first and hid
  whatever it said.
- The catalog priority and fill-mode rules in `tools/validate_foundation.py` now bind each
  classification to the form it is declared on. They previously asserted that a literal such as
  `FormArtifactKind.ExternalWorkflow` appeared somewhere in `CatalogRepository.cs`, which passed
  just as happily when the classifications were swapped between forms — the validator reported
  success on a tree where FAFSA had become an automatically fillable official PDF. The
  retired-form check now covers `src/functions/acquisition_contract.py` as well as the catalog
  fixture; it previously read only the fixture, so restoring `N-400` or `I-765` to the acquisition
  scope passed unnoticed.
- A `FormPackage` with no forms now derives `UNAVAILABLE` rather than `PILOT`. The derivation is a
  chain of `Any` calls expressing "a package is only as activated as its weakest form"; for an
  empty list every one of them is false and the expression fell through to the most permissive
  state, the one that permits case creation.
- The Log Analytics workspace now sets `features.disableLocalAuth: true`. Application Insights
  already disabled local auth, but the workspace that stores what it ingests did not, so the
  telemetry and audit store still accepted the legacy workspace-key ingestion path.
- Split `test_unanchored_or_unconfirmed_proposal_fails_closed`, which violated two invariants at
  once and asserted only that some `ValueError` was raised. The polygon check short-circuited, so
  the human-confirmation invariant it was named for could be deleted outright with the suite still
  green. Each invariant now has its own test, matched on message.
- The validator's secret scan no longer walks generated output. It previously read every file under
  the repository root except its own source, so compiling the validator wrote a `.pyc` containing its
  own pattern literals and the next run failed as though a storage connection string had leaked.
  `__pycache__`, `bin`, `obj`, `.venv`, and `node_modules` are now skipped, matching `.gitignore`.
  The scan's own source stays excluded — it defines the patterns as literals and would match itself.

### Added

- `src/core-api.tests`, a xUnit project holding the Core API to its published contract. Twenty-nine
  tests run against the real request pipeline through `WebApplicationFactory<Program>`, so routing,
  parameter binding, serialization, and status codes are exercised rather than handler bodies:
  the catalog hierarchy, package list with taxonomy and activation-state filters, package detail,
  the fail-closed schema lookup, both 404 problem documents, and every branch of the
  `activationState` parse. Alongside them, unit tests pin the package activation derivation —
  including that a package with no forms is `UNAVAILABLE` — and assert that the C# enum wire names
  match the enum values published in `contracts/catalog.openapi.json`, so the two copies of each
  enum in this repository cannot drift apart unnoticed. The project sets `TreatWarningsAsErrors`,
  matching the project under test, and CI runs `dotnet test`.
- `.github/workflows/security-scanning.yml`, running CodeQL for C# and Python, dependency review on
  pull requests, a Trivy scan of both container images, and a Trivy scan of the ARM template the
  Bicep compiles to, plus a weekly schedule so a newly published advisory surfaces without waiting
  for the next commit. Every action is pinned by commit SHA. Both Trivy jobs report to code scanning
  without failing the build until their baseline is triaged; that threshold, and the secret-scanning
  and push-protection repository settings, remain open under `TODO.md` item **2.5**.
- `bicepconfig.json`, setting twenty security-relevant linter rules to `error`, including
  `no-hardcoded-location`, `secure-parameter-default`, and `no-unused-params`. The Bicep CI step now
  fails on any diagnostic, so a rule left at warning level cannot pass silently. CI pins the Bicep
  CLI to `v0.46.1`, because `az bicep` otherwise installs whatever is current and an upstream release
  could change the rule set or the diagnostic format under a step that now treats any diagnostic as a
  build failure.
- `tools/test_validate_foundation.py`, covering the secret scan: every pattern is still detected in
  source, generated directories and binary suffixes are skipped, a file merely named like a skipped
  directory is still scanned, and the repository tree itself is clean. Wired into CI beside the
  existing contract tests.
- `.env.example`, listing every environment variable `infra/main.parameters.json` substitutes, with
  no values and a short comment each. `tools/validate_foundation.py` now fails if the file drifts
  from the parameter file in either direction, or if any variable is committed carrying a value.
- `CHANGELOG.md`, `REVIEW.md`, and `TODO.md`.
- Nine GitHub Wiki pages staged under `wiki/`: an index plus the architecture, deployment plan,
  environment and release path, configuration contract, security policy, pilot and compliance gates,
  Azure research record, and documentation standards migrated out of `.azure/plan.md` and
  `IMPLEMENTATION_LEDGER.md`.

### Removed

- `.azure/plan.md`. Documentation does not belong in a dot-prefixed configuration folder, and
  `.azure/` is AZD's own environment directory. Its content was validated against the repository and
  split across `CHANGELOG.md`, `REVIEW.md`, `TODO.md`, and the staged wiki pages.
- `IMPLEMENTATION_LEDGER.md`. Its configuration contract moved to the Configuration Contract wiki
  page, its pilot policy values to the Pilot Policy and Compliance Gates wiki page, its
  secret-exclusion policy to the Security and Data Protection wiki page, and its missing decisions
  and approvals to `REVIEW.md`.

## lapluma-infra-0.0 — 2026-08-02

Initial placeholder-only contract, service, and infrastructure scaffold for the supervised pilot.
Nothing in this release was deployed, and no Azure environment was created.

### Added

- **Contracts.** `contracts/catalog.openapi.json`, an OpenAPI 3.1.0 catalog contract covering
  `/health`, `/ready`, the catalog hierarchy and package endpoints, and an authority-aware
  form-edition schema endpoint. `contracts/catalog-package-compatibility.json` pins the
  `lapluma-app-0.2` package identity and composition handshake shared with the iOS repository.
- **AZD configuration.** `azure.yaml` declaring the `core-api`, `processing-worker`, and
  `acquisition-functions` services.
- **Infrastructure.** `infra/main.bicep`, a subscription-scope entrypoint, with `network`,
  `observability`, `security`, `messaging`, and `data` modules under `infra/modules/`, plus the
  secret-free, environment-substituted `infra/main.parameters.json`.
- **Core API.** `src/core-api`, a .NET 9 minimal API exposing health, readiness, catalog hierarchy,
  catalog package list and detail, and form-edition schema endpoints over an in-memory fixture.
- **Processing worker.** `src/document-processing`, a Python 3.12 health surface for the isolated
  worker zone, with queue and Document Intelligence adapters deliberately absent.
- **Orchestration.** `src/functions`, a Durable Functions catalog-acquisition proposal skeleton
  separating timer, client, orchestrator, and activity roles. It creates proposals only; it does not
  download, activate, or mutate an official-form edition.
- **Validation tooling.** `tools/validate_foundation.py`, a dependency-free validator covering the
  OpenAPI contract shape, the Alpha 0.2 priority forms and fill modes, the Azure provisioning
  interlock, and repository-wide secret absence.
- **CI.** `.github/workflows/foundation-validation.yml`, running the validator, the Python contract
  tests, a .NET build, Bicep compilation, and both container builds, with all actions pinned by
  commit SHA.

### Security

- Provisioning safety interlock: `enableProvisioning` defaults to `false`, is restricted to `false`
  by the Bicep `@allowed` list, pinned to `false` in the parameter file, and guards every resource
  group and module. Enabling it requires a reviewed code change, not a parameter change.
- Azure SQL configured for Entra-only authentication with a TLS 1.2 floor, public network access
  disabled, and outbound network access restricted.
- Cosmos DB, Service Bus, and Application Insights configured with local authentication disabled.
- All four storage accounts configured with shared-key access disabled, blob public access disabled,
  OAuth as the default, a TLS 1.2 floor, and public network access disabled, separated by
  `quarantine`, `documents`, `packages`, and `audit` purpose.
- Key Vault configured with RBAC authorization, soft delete, purge protection, a deny-by-default
  network ACL with no bypass, and public network access disabled. Managed HSM configured with purge
  protection, a deny-by-default network ACL, and public network access disabled.
- Four separate user-assigned managed identities generated for the core, processing, AI, and
  functions workloads.
- Per-zone network security groups, with an explicit `DenyInternetEgress` outbound rule on the
  processing subnet.
- Non-root users in both container images, and health-endpoint logging suppressed in the processing
  worker so no path, query string, document ID, or free text is emitted.
- Repository-wide scanning for private keys, storage connection strings, JWT-like tokens, and
  concrete tenant or subscription ID assignments, enforced in CI.

### Validation evidence

Recorded 2026-08-02 and re-confirmed on 2026-08-07 where the toolchain was available.

| Check | Command | Result | Date |
|-------|---------|--------|------|
| Foundation contract and interlock scan | `python3 tools/validate_foundation.py` | Passed | 2026-08-07 |
| Python contract tests | `python3 -m unittest discover` for `src/document-processing` and `src/functions` | 5 passed | 2026-08-07 |
| Python source compilation | `python3 -m py_compile` | Passed | 2026-08-02 |
| JSON and YAML syntax | Local Python parsers | Passed | 2026-08-02 |
| Whitespace validation | `git diff --check` | Passed | 2026-08-02 |
| Bicep compilation | Bicep CLI v0.46.1, independent agent review | Passed with no diagnostics | 2026-08-02 |
| Bicep compilation | `az bicep build --file infra/main.bicep`, foundation validation workflow | Passed | 2026-08-07 |
| .NET core API build | `dotnet build --configuration Release`, foundation validation workflow | Passed | 2026-08-07 |
| Non-root container images | `docker build` for `src/core-api` and `src/document-processing`, foundation validation workflow | Both passed | 2026-08-07 |

Every check above has now been observed passing. The .NET and container builds — pending since
generation because no local .NET SDK or Docker daemon was available — were confirmed by run
[31149584806](https://github.com/HybridCloudWorks/PEN-lapluma_infra/actions/runs/31149584806), in
which all nine workflow steps succeeded.

Azure subscription preflight remains blocked by design until the context gate in `REVIEW.md` closes.
