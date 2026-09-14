-- Library Access Views and Multi-Tenant Isolation Contracts, PostgreSQL 16
-- Delivers task INF-18: Customer Collections and Library Access Controls.
--
-- Security Invariants:
-- 1. Resolve effective access server-side from tenant membership and assignments.
-- 2. Client tenant selection is NEVER accepted as authorization.
-- 3. Shared Blueprints (e.g., 'uscis', 'official') are immutable and multi-tenant.
-- 4. Private Blueprints have explicit tenant ownership and cannot be enumerated or accessed by other tenants.

CREATE SCHEMA IF NOT EXISTS library;

-- -----------------------------------------------------------------------------
-- Effective Collections View
-- Resolves all published collections actively assigned to a specific tenant.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE VIEW library.v_effective_tenant_collections AS
SELECT 
    tca.tenant_id,
    dc.namespace,
    dc.collection_id,
    dc.revision,
    dc.title,
    dc.description,
    dc.workflow_settings,
    dc.publication_state,
    dc.is_latest,
    dc.created_at,
    tca.assigned_at
FROM library.tenant_collection_assignment tca
JOIN library.document_collection dc
  ON tca.collection_namespace = dc.namespace
 AND tca.collection_id = dc.collection_id
 AND tca.collection_revision = dc.revision
WHERE tca.is_active = TRUE
  AND dc.publication_state = 'PUBLISHED';

-- -----------------------------------------------------------------------------
-- Effective Blueprints View
-- Resolves all blueprints authorized for a given tenant:
-- (a) Pinned members of actively assigned collections, OR
-- (b) Shared official published blueprints, OR
-- (c) Private blueprints owned by the tenant (namespace = tenant_id), OR
-- (d) Private blueprints explicitly granted to the tenant via tenant_blueprint_grant.
-- -----------------------------------------------------------------------------
CREATE OR REPLACE VIEW library.v_effective_tenant_blueprints AS
WITH authorized_blueprints AS (
    -- Member of an assigned collection
    SELECT 
        tca.tenant_id,
        cbm.blueprint_namespace AS namespace,
        cbm.blueprint_id,
        cbm.blueprint_revision AS revision
    FROM library.tenant_collection_assignment tca
    JOIN library.collection_blueprint_member cbm
      ON tca.collection_namespace = cbm.collection_namespace
     AND tca.collection_id = cbm.collection_id
     AND tca.collection_revision = cbm.collection_revision
    WHERE tca.is_active = TRUE

    UNION

    -- Shared official blueprints (shared official namespace)
    SELECT 
        it.tenant_id,
        db.namespace,
        db.blueprint_id,
        db.revision
    FROM library.institution_tenant it
    CROSS JOIN library.document_blueprint db
    WHERE it.is_active = TRUE
      AND db.namespace IN ('uscis', 'official', 'dos', 'irs')
      AND db.publication_state = 'PUBLISHED'

    UNION

    -- Private blueprints owned by the tenant
    SELECT 
        db.namespace AS tenant_id,
        db.namespace,
        db.blueprint_id,
        db.revision
    FROM library.document_blueprint db
    WHERE db.publication_state = 'PUBLISHED'
      AND db.namespace NOT IN ('uscis', 'official', 'dos', 'irs')

    UNION

    -- Explicitly granted private blueprints
    SELECT 
        tbg.tenant_id,
        tbg.blueprint_namespace AS namespace,
        tbg.blueprint_id,
        db.revision
    FROM library.tenant_blueprint_grant tbg
    JOIN library.document_blueprint db
      ON tbg.blueprint_namespace = db.namespace
     AND tbg.blueprint_id = db.blueprint_id
    WHERE db.publication_state = 'PUBLISHED'
)
SELECT DISTINCT
    ab.tenant_id,
    db.namespace,
    db.blueprint_id,
    db.revision,
    db.title,
    db.issuer,
    db.official_edition_date,
    db.preparation_mode,
    db.artifact_type,
    db.source_url,
    db.source_sha256,
    db.fields_schema,
    db.validation_rules,
    db.evidence_requirements,
    db.publication_state,
    db.is_latest,
    db.created_at
FROM authorized_blueprints ab
JOIN library.document_blueprint db
  ON ab.namespace = db.namespace
 AND ab.blueprint_id = db.blueprint_id
 AND ab.revision = db.revision;

-- -----------------------------------------------------------------------------
-- Seed Data for Multi-Tenant Verification & Synthetic Testing
-- -----------------------------------------------------------------------------
INSERT INTO library.institution_tenant (tenant_id, display_name, is_active)
VALUES 
    ('tenant_clinic_alpha', 'East Bay Community Legal Clinic', TRUE),
    ('tenant_firm_beta', 'Sterling Immigration Partners', TRUE)
ON CONFLICT (tenant_id) DO NOTHING;

-- 1. Shared Official Blueprint (USCIS I-130)
INSERT INTO library.document_blueprint (
    namespace, blueprint_id, revision, title, issuer, official_edition_date,
    preparation_mode, artifact_type, source_url, source_sha256,
    publication_state, is_latest
)
VALUES (
    'uscis', 'i-130', 1, 'Petition for Alien Relative', 'U.S. Citizenship and Immigration Services',
    '2024-04-01', 'FILLABLE_PDF', 'OFFICIAL_PDF',
    'https://www.uscis.gov/sites/default/files/document/forms/i-130.pdf',
    'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855',
    'PUBLISHED', TRUE
)
ON CONFLICT (namespace, blueprint_id, revision) DO NOTHING;

-- 2. Private Blueprint for Clinic Alpha (Intake Questionnaire)
INSERT INTO library.document_blueprint (
    namespace, blueprint_id, revision, title, issuer, official_edition_date,
    preparation_mode, artifact_type, source_url, source_sha256,
    publication_state, is_latest
)
VALUES (
    'tenant_clinic_alpha', 'intake', 1, 'Clinic Client Intake Sheet', 'East Bay Community Legal Clinic',
    '2026-01-01', 'STATIC_ASSISTED', 'AUTHORED_TEMPLATE',
    'https://partner.lapluma.io/templates/intake-2026.pdf',
    '01ba4719c80b6fe911b091a7c05124b64eeece964e09c058ef8f9805daca546b',
    'PUBLISHED', TRUE
)
ON CONFLICT (namespace, blueprint_id, revision) DO NOTHING;

-- 3. Private Blueprint for Firm Beta (Retainer Agreement)
INSERT INTO library.document_blueprint (
    namespace, blueprint_id, revision, title, issuer, official_edition_date,
    preparation_mode, artifact_type, source_url, source_sha256,
    publication_state, is_latest
)
VALUES (
    'tenant_firm_beta', 'special_retainer', 1, 'Immigration Legal Retainer', 'Sterling Immigration Partners',
    '2026-01-01', 'STATIC_ASSISTED', 'AUTHORED_TEMPLATE',
    'https://sterling.example.com/retainer-v1.pdf',
    'cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce',
    'PUBLISHED', TRUE
)
ON CONFLICT (namespace, blueprint_id, revision) DO NOTHING;

-- 4. Shared Official Collection (Family Reunification)
INSERT INTO library.document_collection (
    namespace, collection_id, revision, title, description,
    workflow_settings, publication_state, is_latest
)
VALUES (
    'official', 'family_reunification', 1, 'Family Reunification Package',
    'Standard family-based immigration workflow containing USCIS Form I-130.',
    '{"theme": "official-standard", "displayOrder": 10}'::jsonb,
    'PUBLISHED', TRUE
)
ON CONFLICT (namespace, collection_id, revision) DO NOTHING;

-- Member for Official Collection: uscis.i-130:1
INSERT INTO library.collection_blueprint_member (
    collection_namespace, collection_id, collection_revision,
    blueprint_namespace, blueprint_id, blueprint_revision,
    sort_order, is_required
)
VALUES (
    'official', 'family_reunification', 1,
    'uscis', 'i-130', 1,
    1, TRUE
)
ON CONFLICT (collection_namespace, collection_id, collection_revision, blueprint_namespace, blueprint_id) DO NOTHING;

-- 5. Private Collection for Clinic Alpha
INSERT INTO library.document_collection (
    namespace, collection_id, revision, title, description,
    workflow_settings, publication_state, is_latest
)
VALUES (
    'tenant_clinic_alpha', 'clinic_intake_pkg', 1, 'East Bay Legal Clinic Intake & Petition',
    'Custom clinic intake package linking private intake sheet with official I-130.',
    '{"theme": "clinic-pastel", "displayOrder": 1}'::jsonb,
    'PUBLISHED', TRUE
)
ON CONFLICT (namespace, collection_id, revision) DO NOTHING;

-- Members for Clinic Alpha Collection: private intake sheet + uscis.i-130
INSERT INTO library.collection_blueprint_member (
    collection_namespace, collection_id, collection_revision,
    blueprint_namespace, blueprint_id, blueprint_revision,
    sort_order, is_required
)
VALUES 
    ('tenant_clinic_alpha', 'clinic_intake_pkg', 1, 'tenant_clinic_alpha', 'intake', 1, 1, TRUE),
    ('tenant_clinic_alpha', 'clinic_intake_pkg', 1, 'uscis', 'i-130', 1, 2, TRUE)
ON CONFLICT (collection_namespace, collection_id, collection_revision, blueprint_namespace, blueprint_id) DO NOTHING;

-- 6. Assign Collections to Tenants
-- Both Clinic Alpha and Firm Beta get the official Family Reunification collection
INSERT INTO library.tenant_collection_assignment (
    tenant_id, collection_namespace, collection_id, collection_revision, is_active
)
VALUES 
    ('tenant_clinic_alpha', 'official', 'family_reunification', 1, TRUE),
    ('tenant_firm_beta', 'official', 'family_reunification', 1, TRUE),
    ('tenant_clinic_alpha', 'tenant_clinic_alpha', 'clinic_intake_pkg', 1, TRUE)
ON CONFLICT (tenant_id, collection_namespace, collection_id, collection_revision) DO NOTHING;
