-- Document Library Schema, PostgreSQL 16
-- Delivers task INF-02: PostgreSQL Blueprint and Collection data contracts.
--
-- Domain Hierarchy:
--   Document Library -> Document Blueprints -> Document Collections
--
-- Blueprint identity: (namespace, blueprint_id, revision)
--   Official edition date is optional metadata, not identity.
--   Fields, validation rules, and evidence requirements are declarative JSONB.

CREATE SCHEMA IF NOT EXISTS library;

-- -----------------------------------------------------------------------------
-- Institution Tenants
-- -----------------------------------------------------------------------------
CREATE TABLE library.institution_tenant
(
    tenant_id    VARCHAR(64)  NOT NULL PRIMARY KEY,
    display_name VARCHAR(256) NOT NULL,
    is_active    BOOLEAN      NOT NULL DEFAULT TRUE,
    created_at   TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at   TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- -----------------------------------------------------------------------------
-- Document Blueprints (Immutable Revisions)
-- -----------------------------------------------------------------------------
CREATE TABLE library.document_blueprint
(
    namespace             VARCHAR(64)  NOT NULL, -- 'uscis', 'dos', 'irs', or tenant ID for private variants
    blueprint_id          VARCHAR(64)  NOT NULL, -- e.g. 'i-130', 'n-400', 'ds-11'
    revision              INT          NOT NULL, -- Immutable monotonic version integer: 1, 2, 3...
    title                 VARCHAR(256) NOT NULL,
    issuer                VARCHAR(256) NOT NULL, -- e.g. 'USCIS', 'U.S. Department of State'
    official_edition_date DATE         NULL,     -- Optional metadata; not all documents have official agency editions
    preparation_mode      VARCHAR(32)  NOT NULL,
    artifact_type         VARCHAR(32)  NOT NULL,
    source_url            VARCHAR(2048) NULL,
    source_sha256         CHAR(64)     NULL,
    fields_schema         JSONB        NOT NULL DEFAULT '{}'::jsonb,
    validation_rules      JSONB        NOT NULL DEFAULT '[]'::jsonb,
    evidence_requirements JSONB        NOT NULL DEFAULT '[]'::jsonb,
    publication_state     VARCHAR(32)  NOT NULL,
    reviewed_by           VARCHAR(256) NULL,
    reviewed_at           TIMESTAMPTZ  NULL,
    published_at          TIMESTAMPTZ  NULL,
    is_latest             BOOLEAN      NOT NULL DEFAULT FALSE,
    created_at            TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT pk_document_blueprint 
        PRIMARY KEY (namespace, blueprint_id, revision),

    CONSTRAINT ck_blueprint_prep_mode 
        CHECK (preparation_mode IN ('FILLABLE_PDF', 'STATIC_ASSISTED', 'EXTERNAL_REFERENCE')),

    CONSTRAINT ck_blueprint_artifact_type 
        CHECK (artifact_type IN ('OFFICIAL_PDF', 'XFA', 'FLAT', 'EXTERNAL_LINK', 'AUTHORED_TEMPLATE')),

    CONSTRAINT ck_blueprint_pub_state 
        CHECK (publication_state IN ('DRAFT', 'VALIDATED', 'IN_REVIEW', 'PUBLISHED', 'QUARANTINED', 'WITHDRAWN', 'ROLLED_BACK')),

    CONSTRAINT ck_blueprint_source_url 
        CHECK (source_url IS NULL OR source_url LIKE 'https://%')
);

CREATE INDEX idx_blueprint_lookup 
    ON library.document_blueprint (namespace, blueprint_id, is_latest);

CREATE INDEX idx_blueprint_fields_gin 
    ON library.document_blueprint USING GIN (fields_schema);

CREATE INDEX idx_blueprint_evidence_gin 
    ON library.document_blueprint USING GIN (evidence_requirements);

-- -----------------------------------------------------------------------------
-- Document Collections (Versioned Selections of Blueprints)
-- -----------------------------------------------------------------------------
CREATE TABLE library.document_collection
(
    namespace         VARCHAR(64)  NOT NULL, -- 'official' or tenant-specific namespace
    collection_id     VARCHAR(64)  NOT NULL, -- e.g. 'family_reunification_i130'
    revision          INT          NOT NULL, -- Immutable monotonic version integer: 1, 2, 3...
    title             VARCHAR(256) NOT NULL,
    description       TEXT         NULL,
    workflow_settings JSONB        NOT NULL DEFAULT '{}'::jsonb, -- presentation, order, instructions, branding
    publication_state VARCHAR(32)  NOT NULL,
    is_latest         BOOLEAN      NOT NULL DEFAULT FALSE,
    created_at        TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT pk_document_collection 
        PRIMARY KEY (namespace, collection_id, revision),

    CONSTRAINT ck_collection_pub_state 
        CHECK (publication_state IN ('DRAFT', 'VALIDATED', 'IN_REVIEW', 'PUBLISHED', 'WITHDRAWN', 'ROLLED_BACK'))
);

CREATE INDEX idx_collection_lookup 
    ON library.document_collection (namespace, collection_id, is_latest);

-- -----------------------------------------------------------------------------
-- Collection Members (Blueprint Reference and Ordering)
-- -----------------------------------------------------------------------------
CREATE TABLE library.collection_blueprint_member
(
    collection_namespace VARCHAR(64) NOT NULL,
    collection_id        VARCHAR(64) NOT NULL,
    collection_revision  INT         NOT NULL,
    blueprint_namespace  VARCHAR(64) NOT NULL,
    blueprint_id         VARCHAR(64) NOT NULL,
    blueprint_revision   INT         NOT NULL,
    sort_order           INT         NOT NULL DEFAULT 0,
    is_required          BOOLEAN     NOT NULL DEFAULT TRUE,

    CONSTRAINT pk_collection_blueprint_member 
        PRIMARY KEY (collection_namespace, collection_id, collection_revision, blueprint_namespace, blueprint_id),

    CONSTRAINT fk_member_collection 
        FOREIGN KEY (collection_namespace, collection_id, collection_revision)
        REFERENCES library.document_collection (namespace, collection_id, revision)
        ON DELETE CASCADE,

    CONSTRAINT fk_member_blueprint 
        FOREIGN KEY (blueprint_namespace, blueprint_id, blueprint_revision)
        REFERENCES library.document_blueprint (namespace, blueprint_id, revision)
);

-- -----------------------------------------------------------------------------
-- Tenant Collection Assignments
-- -----------------------------------------------------------------------------
CREATE TABLE library.tenant_collection_assignment
(
    tenant_id            VARCHAR(64) NOT NULL REFERENCES library.institution_tenant (tenant_id) ON DELETE CASCADE,
    collection_namespace VARCHAR(64) NOT NULL,
    collection_id        VARCHAR(64) NOT NULL,
    collection_revision  INT         NOT NULL,
    is_active            BOOLEAN     NOT NULL DEFAULT TRUE,
    assigned_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT pk_tenant_collection_assignment 
        PRIMARY KEY (tenant_id, collection_namespace, collection_id, collection_revision),

    CONSTRAINT fk_assignment_collection 
        FOREIGN KEY (collection_namespace, collection_id, collection_revision)
        REFERENCES library.document_collection (namespace, collection_id, revision)
);

-- -----------------------------------------------------------------------------
-- Tenant Private Blueprint Grants
-- -----------------------------------------------------------------------------
CREATE TABLE library.tenant_blueprint_grant
(
    tenant_id           VARCHAR(64) NOT NULL REFERENCES library.institution_tenant (tenant_id) ON DELETE CASCADE,
    blueprint_namespace VARCHAR(64) NOT NULL,
    blueprint_id        VARCHAR(64) NOT NULL,
    granted_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT pk_tenant_blueprint_grant 
        PRIMARY KEY (tenant_id, blueprint_namespace, blueprint_id)
);
