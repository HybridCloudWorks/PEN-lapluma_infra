# Operator Runbook: LaPluma-Managed Institution Onboarding

**Status:** Approved Standard Operating Procedure  
**Task Reference:** [INT-13](../../README.md)  
**ADR Reference:** [ADR-019](../../docs/adr/ADR-019-lean-gcp-document-library.md) (Lean Managed GCP Platform & Document Library Architecture)  
**Security Classification:** Confidential / Internal Operations  
**Last Updated:** 2026-09-14  

---

## 1. Overview & Operational Principles

LaPluma operates as a multi-tenant platform where institutional customers (such as legal aid clinics, university legal centers, and community immigrant advocacy organizations) are provisioned under strict, server-enforced tenant isolation.

### Key Governance Invariants:
1. **Zero Self-Approval:** Any onboarding configuration or collection assignment authored by an engineer must be reviewed and approved by an independent engineer (`reviewer != author`).
2. **Server-Derived Tenant Isolation:** Mobile and web clients never declare their own permissions; tenant authorization is derived solely from the authenticated principal's session in PostgreSQL (`library.institution_tenant` and `library.tenant_collection_assignment`).
3. **Immutable Blueprint Pointers:** Collections reference immutable blueprint revisions (`namespace/blueprint_id@revision`). Onboarding binds collections with pinned revisions, ensuring subsequent blueprint updates never alter existing in-flight cases.
4. **Clean Abort & Rollback Safety:** If any validation fails, onboarding aborts cleanly without leaving orphaned records, draft blueprints, or half-assigned collections. A verified `down.sql` rollback script is generated for every onboarding.

---

## 2. Pre-Onboarding Checklist

Before running the onboarding tooling, the operator must verify:
- [ ] Legal agreement / MOU executed with the institution.
- [ ] Institution tenant code chosen (e.g. `clinic-sf-01`), adhering to `^[a-z0-9_-]{2,64}$`.
- [ ] Primary institution contact email verified.
- [ ] Requested document collections verified (e.g. `official/family_reunification_i130@1`, `official/adjustment_of_status_i485@1`).
- [ ] Any requested custom institution-private blueprints validated and approved.
- [ ] Target GCP environment identified (`dev`, `staging`, or `pilot`).

---

## 3. Step-by-Step Operator Procedure

### Step 1: Prepare the Onboarding Manifest

Create an onboarding request manifest JSON file (e.g. `onboard-clinic-sf.json`):

```json
{
  "tenantId": "clinic-sf-01",
  "displayName": "San Francisco Immigrant Legal Clinic",
  "contactEmail": "admin@sfclinic.org",
  "author": "engineer_alice@lapluma.io",
  "reviewer": "engineer_bob@lapluma.io",
  "assignedCollections": [
    {
      "namespace": "official",
      "collectionId": "family-reunification-i130",
      "revision": 1
    },
    {
      "namespace": "official",
      "collectionId": "adjustment-of-status-i485",
      "revision": 1
    }
  ]
}
```

> [!IMPORTANT]
> The `author` and `reviewer` fields MUST contain distinct individuals. The onboarding CLI strictly rejects manifests where `author == reviewer`.

### Step 2: Validate Manifest & Blueprint Invariants

Run the validation command using `tools/onboard_institution.py`:

```bash
python tools/onboard_institution.py validate onboard-clinic-sf.json
```

Expected output:
```text
PASS: Onboarding manifest 'onboard-clinic-sf.json' passed all governance and safety invariants.
  Tenant:    clinic-sf-01 (San Francisco Immigrant Legal Clinic)
  Author:    engineer_alice@lapluma.io
  Reviewer:  engineer_bob@lapluma.io (Independent check passed)
  Collections (2):
    - official/family-reunification-i130@1
    - official/adjustment-of-status-i485@1
```

If validation fails, resolve the reported errors before proceeding.

### Step 3: Generate Execution Plan

Generate and inspect the formal onboarding plan:

```bash
python tools/onboard_institution.py plan onboard-clinic-sf.json -o onboard-clinic-sf.plan.json
```

Verify that all collections, revisions, and tenant details match the approved customer specification.

### Step 4: Generate Provisioning and Rollback SQL

Generate the transactional PostgreSQL migration scripts:

```bash
python tools/onboard_institution.py generate-sql onboard-clinic-sf.json --output-dir ./migrations/
```

This creates:
- `migrations/clinic-sf-01_onboard.up.sql`: Idempotent script wrapped in `BEGIN; ... COMMIT;` registering the tenant and assigning approved collections.
- `migrations/clinic-sf-01_onboard.down.sql`: Transactional rollback script that deactivates the tenant and removes assignments if needed.

### Step 5: Execute Provisioning in Target Database

Connect to the target PostgreSQL database using the authorized `lapluma_library_admin` role (never an app role with case access):

```bash
# Example execution via psql or Cloud SQL Auth Proxy:
psql "$DATABASE_URL" -f migrations/clinic-sf-01_onboard.up.sql
```

Verify that the transaction completed successfully.

### Step 6: Post-Onboarding Verification

1. Query tenant registration:
   ```sql
   SELECT tenant_id, display_name, is_active FROM library.institution_tenant WHERE tenant_id = 'clinic-sf-01';
   ```
2. Query effective tenant collections:
   ```sql
   SELECT * FROM library.v_tenant_effective_collections WHERE tenant_id = 'clinic-sf-01';
   ```
3. Verify that zero client or case tables in the `workflow` schema were accessed or altered.

---

## 4. Rollback & Incident Response

If onboarding must be aborted or reversed:
1. Apply the generated rollback script:
   ```bash
   psql "$DATABASE_URL" -f migrations/clinic-sf-01_onboard.down.sql
   ```
2. Confirm the tenant is marked `is_active = FALSE` and all collection assignments are removed.
3. Historical cases (if any were created in pilot) retain their pinned snapshots in the workflow schema.
