# ADR 0007 — Workflow API as a second core-zone service

## Status

Accepted, partly gated. The scaffold is implemented in `src/workflow-api` and
`infra/modules/compute.bicep`; the tenant-session scheme it will eventually validate is gated on
`REVIEW.md` **R-06** and **R-20**, and the durable store on `TODO.md` **5.8**.

## Context

The iOS app codes against the LaPluma Workflow API (`contracts/openapi/workforce-workflow.yaml`):
session context, a client directory, case workspaces, canonical-section commits, evidence relays,
review and approval, and direct-to-storage document uploads. ADR-015 in the app repository assigns
contract and backend ownership to this repository, where until now the only API was the Core API —
a deliberately content-free, read-only catalog whose contract states that it accepts no person,
folder, case, document, eligibility, or free-text facts.

The workflow surface is the opposite: nearly every operation on it carries or reaches case content.
Something has to serve it, and where that something runs determines which data classification, blast
radius, and audit posture its process carries.

## Options considered

**Extend the Core API with the workflow routes.** One service, one deployment, one identity — the
cheapest shape, and the routes share the same auth pattern anyway. Rejected because it dissolves a
boundary both contracts state on purpose: the catalog is anonymous-shaped, cacheable, and safe to
expose broadly precisely because its process never holds case content, and its own invariant test
asserts catalog responses are independent of case data. One process serving both means one
compromise, one memory dump, one mis-scoped log statement spans both classifications. The
separation is between data classes, not between route prefixes.

**A new trust zone (fourth managed environment).** The heaviest shape: its own subnet, NSG,
identity, and environment, like processing and AI have. Rejected as a boundary without a threat
model to earn it. ADR-0002's zones exist for adversarial properties — processing parses hostile
bytes with no route to SQL; AI must hold no data-plane role. The workflow service is the same kind
of workload as the Core API: an authoritative, token-validating core service. Giving it a zone
would double the core network surface while enforcing nothing the identity layer does not already
enforce between two apps in one environment. What would change this: the relay surface. If the
anonymous `/relay/{token}` endpoints are ever served from this process rather than a dedicated
public edge, the unauthenticated-input threat model arrives and a separate zone (or a separate
relay service) stops being optional — which is one reason those endpoints are deliberately not
mapped at all today.

**A separate service in the core zone** — same environment, same per-zone identity, its own
process, image, and contract. Chosen.

Two subsidiary decisions ride along:

- **Authentication now.** The contract declares an opaque tenant-session bearer; no service exists
  to mint one (R-20). The service validates Entra JWTs fail-closed, exactly as the Core API does,
  and `tools/validate_foundation.py` pins the contract's declared scheme so the divergence stays
  visible rather than silently normalized.
- **Contract adoption by verbatim mirror.** The workflow contract is copied byte-identical from the
  app repository and pinned by SHA-256 (`WORKFORCE_WORKFLOW_SHA256`). A curated local variant was
  rejected: it would diverge from the document the Swift client is generated from on the first
  edit, which is the exact failure the four-versus-seven package drift already demonstrated.

## Decision

`src/workflow-api` is a second .NET service in the core managed environment, deployed as
`ca-<name>-workflow-api` with internal-only ingress behind the future APIM edge (TODO 1.1 publishes
both core apps). It reuses the per-zone core identity — the identity model is per trust zone, not
per app — and gains exactly one new data-plane grant: Storage Blob Data Contributor on the
quarantine account, which is what lets it mint write-only user-delegation upload SAS while
processing stays a quarantine Reader.

It implements the near-term contract slice (session, client directory, case workspace, upload
sessions) against an explicitly named in-memory fixture, answers every other authenticated
operation with a typed 501, and does not map the anonymous relay surface at all.

## Consequences

- The catalog process stays content-free; case content, when it arrives, lands in a process built
  for it from the first commit — content-free telemetry, single 404 for missing-or-unauthorized,
  idempotency on every mutation.
- Costs still being paid: `maxReplicas: 1` until the fixture and the idempotency replay map leave
  process memory (TODO 5.8); Entra-only callers until R-20's session service exists, so the iOS
  app cannot leave its stub on this surface yet; and a shared core identity means the two core
  apps are distinguishable in audit only by workload, not by principal — if that ever matters, a
  second core-zone identity is an additive change.
- The verbatim mirror means this repository cannot fix even a typo in the workflow contract
  unilaterally; changes route through the app repository and arrive as a deliberate revision
  adoption (R-19 owns the cross-repository pinning).

## References

- `contracts/openapi/workforce-workflow.yaml` and `contracts/openapi/documents-upload.yaml`
- `src/workflow-api/`, `infra/modules/compute.bicep`, `infra/modules/rbac.bicep`
- `tools/validate_foundation.py::validate_workflow_contract`
- `REVIEW.md` R-06, R-07, R-19, R-20 · `TODO.md` 1.1, 5.8, 5.9
- App repository: `ARCH-HANDOFF.md`, `docs/adr/ADR-015`
