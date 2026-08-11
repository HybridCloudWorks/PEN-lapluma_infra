# Pilot policy and compliance gates

## Alpha 0.2 catalog scope

The Alpha 0.2 priority catalog is exactly **I-130, I-485, DS-11, and FAFSA**. Their artifact and
fill modes remain explicit: official PDF versus online application, and automatic, assisted, or
reference-only.

**Priority does not imply activation.** Every edition remains fail-closed until its source,
encoding, field map or external-workflow boundary, and approvals are verified.

The package composition is pinned in `contracts/catalog-package-compatibility.json` under
`contractVersion: lapluma-app-0.2` and enforced by `tools/validate_foundation.py`:

| Package code | Forms |
|--------------|-------|
| `FAMILY_I130` | I-130, I-130A |
| `ADJUSTMENT_I485_I864` | I-485, I-864 |
| `PASSPORT_DS11` | DS-11 |
| `FINANCIAL_AID_FAFSA` | FAFSA |

The validator also asserts that the earlier priority forms N-400 and I-765 have not leaked back into
the Alpha 0.2 fixture, that FAFSA stays an external workflow that is reference-only, and that the
placeholder catalog activates no pilot edition.

## Pilot policy baselines

| Value | Approved baseline | Owner | Change rule |
|-------|-------------------|-------|-------------|
| `PILOT_INITIAL_CASE_TARGET` | `40` | Product and compliance | Expansion requires checkpoint evidence |
| `PILOT_MAX_ENROLLED_USERS` | `999` | Product and security | Server-enforced; an increase requires a new approval cycle |
| `PILOT_DATA_RESIDENCY` | `US` | Privacy | A new geography requires a new data plane and a new legal review |
| `PILOT_PRIORITY_FORMS` | I-130; I-485; DS-11; FAFSA | Product and compliance | Priority is not activation; every edition remains fail-closed pending source and workflow approval |
| `PILOT_I130_ARTIFACT_MODE` | Proposed `OFFICIAL_PDF` / `AUTOMATIC_FILL` | Catalog and compliance | Activation requires a verified official artifact, encoding, hash, edition, and two-person field-map approval |
| `PILOT_I485_ARTIFACT_MODE` | Proposed `OFFICIAL_PDF` / `AUTOMATIC_FILL` | Catalog and compliance | Activation requires a verified official artifact, encoding, hash, edition, and two-person field-map approval |
| `PILOT_DS11_ARTIFACT_MODE` | Proposed `OFFICIAL_PDF` / `AUTOMATIC_FILL` | Catalog and compliance | No electronic signature; verify the current official artifact encoding and round-trip fidelity before activation |
| `PILOT_FAFSA_ARTIFACT_MODE` | `EXTERNAL_WORKFLOW` / `REFERENCE_ONLY` | Catalog and compliance | No portal automation, no credential handling, and no claim that LaPluma files FAFSA |
| `PILOT_AUTOMATED_FILING_ENABLED` | `false` | Compliance | Architectural invariant |
| `PILOT_AUTOMATED_APPROVAL_ENABLED` | `false` | Compliance | Architectural invariant |
| `PILOT_ELECTRONIC_SIGNATURE_ENABLED` | `false` | Compliance | Wet-ink points only until form-specific counsel approval |
| `PILOT_REALTIME_VOICE_ENABLED` | `false` by default | Compliance and RAI | Enable only after the latency, consent, retention, and 100% post-hoc review gates pass |

## Retention and erasure targets

Ratified. These are settings now, not proposals.

**The ordering rule is the contract.** Every window that extends the life of case content must be
*strictly* shorter than the erasure SLA. A soft-deleted blob is still recoverable, which means it is
still retained: if the recovery window reaches the SLA, the deletion receipt is false at the moment
it is issued. Equality is not close enough, because the clocks start at different moments.

Two classes are exempt, for the same reason in both cases — they hold no case content. Audit
metadata is content-free and pseudonymized on erasure, so it is the evidence that erasure happened
rather than a surviving copy of what was erased. Key material is not case content either.

`tools/validate_foundation.py` enforces the ordering rule against the Bicep defaults, so a later
change that widens a window past the SLA fails the build rather than quietly breaking the promise.

| Value | Ratified | Owner | Notes |
|-------|----------|-------|-------|
| `CASE_CONTENT_RETENTION_TRIGGER` | Case closure, or 180 days of participant inactivity on an open case, whichever comes first; content deleted 18 months after the clock starts | Privacy and data | The inactivity limb is what stops a case nobody closes from being retained forever |
| `ACCOUNT_ERASURE_ACTIVE_DATA_SLA_DAYS` | `30` | Privacy | The ceiling every content-bearing window below is measured against |
| `BACKUP_EXPIRY_MAX_MONTHS` | `12` | Privacy and data | Must be stated truthfully to participants |
| `AUDIT_METADATA_RETENTION_YEARS` | `7` (2555 days) | Compliance and privacy | Content-free and pseudonymized on erasure. Exempt from the ordering rule |
| `SECURITY_LOG_RETENTION_MONTHS` | `12` (365-day workspace retention) | Security and privacy | Logs must contain no case content |
| Blob soft delete | `7` days | Privacy and data | Recovery window for operator error, not retention |
| Container soft delete | `7` days | Privacy and data | |
| Blob version purge | `7` days | Privacy and data | **Corrected from the proposed 30.** See below |
| Key Vault / Managed HSM soft delete | `90` days | Security | Exempt: key material is not case content |
| Queue / topic message TTL | `P7D` / `P14D` | Data | A message carries a reference, not content |

### The one value that changed on ratification

Blob version purge was proposed at 30 days and is set to 7. The proposal paired it with a 30-day
erasure SLA, which reads fine until the clocks are lined up: versioning is enabled, so deleting a
blob creates a version, and that version's 30 days begin *after* however long the deletion itself
took. The total runs past the SLA rather than inside it. The ordering-rule check rejected the pair
on its first run, which is the check doing its job on the very contract it was written to defend.

Seven days matches the soft-delete window, so the recovery story is one number rather than two:
content is recoverable for a week, and after that it is gone.

The lifecycle policy is a backstop for versions produced by ordinary overwrites. Erasure
orchestration must purge versions explicitly rather than waiting for it, because lifecycle
management runs once a day at Azure's discretion and that is not a schedule an erasure promise can
rest on.

## Operational gates

### Catalog and edition integrity

Catalog and form versions are identified by form ID plus edition date, official source URL, source
SHA-256, encoding, and a two-person-approved field-map version. Edition drift quarantines the
affected cases.

The OpenAPI contract enforces authority-aware edition identity (`authority`, `formID`,
`editionDate`), HTTPS-only source and artifact URLs, and package activation derived from child forms
rather than declared on the package.

### UPL release gate

The unauthorized-practice-of-law release gate must pass its development and held-out corpora with
zero escapes per prohibited act and per supported language. Classifier or audit unavailability fails
closed.

### Invariant tests

Cross-tenant, cross-folder, person-boundary, and agent-no-write invariant tests must pass on every
build. A generated-package mismatch blocks delivery.

### Erasure and retention testing

Production account erasure and case-retention sweeps must be integration-tested across SQL, Cosmos,
Blob versions, search and projections, temporary stores, delivery links, logs, and backups and key
policy.

### Real-user pilot prerequisites

The initial real-user pilot requires:

- approved privacy, consent, and retention materials;
- partner and reviewer authorization;
- outside-counsel and Compliance gates;
- an independent penetration test with all high findings closed;
- incident-response and on-call readiness;
- restore and deletion drills;
- physical-device end-to-end validation.

### Expansion checkpoint

Expansion beyond the initial supervised cohort requires a documented CPO, CTO, CISO, and Compliance
checkpoint, adequate human-review capacity, no unresolved Sev-1 incident, and an enforced
server-side maximum of 999 enrolled users.

## Related pages

- [Security and Data Protection](Security-and-Data-Protection)
- [Environments and Release Path](Environments-and-Release-Path)
- [Architecture Overview](Architecture-Overview)
