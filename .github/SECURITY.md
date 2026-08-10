# Security policy

## Reporting a vulnerability

Report privately through this repository's **Security → Report a vulnerability** tab, which opens a
private advisory visible only to repository administrators.

Do not open a public issue, and do not include real applicant data, document content, credentials,
or tokens in a report. A description of the flaw and the steps to reach it is enough; if a
reproduction genuinely needs a payload, say so and it can be arranged privately.

There is no service in production. This repository holds the infrastructure and service scaffold for
a supervised pilot that has not been provisioned — `enableProvisioning` is restricted to `false` —
so a report here concerns the design and the code, not a live system.

## Scope

In scope: anything in `infra/`, `src/`, `contracts/`, `tools/`, and `.github/`.

Particularly wanted, because these are the guarantees the design rests on rather than ordinary bugs:

- A path by which a credential, key, token, connection string, document content, or applicant
  identifier could enter source control, a build layer, a Bicep output, or a log line.
- Telemetry that carries case content — a path, query string, route value, or document identifier
  rather than a bare correlation identifier.
- A route from the processing zone to SQL or Cosmos, or an authoritative write reachable from the
  AI zone.
- Any automated path that activates, approves, signs, or files rather than proposing.

Out of scope: the unbuilt controls already recorded in [`REVIEW.md`](../REVIEW.md) and
[`TODO.md`](../TODO.md). Those are known and tracked; a report restating one adds nothing. Findings
about the *tracking* — a gap neither file records — are very much in scope.

## Response

Reports are acknowledged and triaged by the repository administrators. Named security, privacy, and
compliance owners do not exist yet; designating them is `REVIEW.md` **R-04**, and until it is
resolved there is no committed response time to quote.

The threat model and the constraints these rules come from are on the wiki's **Security and Data
Protection** page.
