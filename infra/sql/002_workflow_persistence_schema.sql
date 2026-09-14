-- Workflow Persistence Schema, PostgreSQL 16
-- Delivers task INF-02 (and foundation for INF-10): Durable workflow persistence contracts.
--
-- Incorporates:
--   - Tenant isolation & per-person household trust boundaries (ADR-007)
--   - Case Blueprint & Collection revision pinning (immutable against edition drift)
--   - Human review & confirmation gate (fail-closed, approval invalidation)
--   - Scoped direct document upload grants
--   - Transactional outbox pattern for reliable Pub/Sub dispatch

CREATE SCHEMA IF NOT EXISTS workflow;

-- -----------------------------------------------------------------------------
-- Client Folders
-- -----------------------------------------------------------------------------
CREATE TABLE workflow.client_folder
(
    id              VARCHAR(64)  NOT NULL PRIMARY KEY,
    tenant_id       VARCHAR(64)  NOT NULL REFERENCES library.institution_tenant (tenant_id),
    display_label   VARCHAR(256) NOT NULL,
    idempotency_key VARCHAR(128) NULL UNIQUE,
    payload_hash    CHAR(64)     NULL,
    created_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at      TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_folder_tenant ON workflow.client_folder (tenant_id);
CREATE INDEX idx_folder_idempotency ON workflow.client_folder (idempotency_key) WHERE idempotency_key IS NOT NULL;

-- -----------------------------------------------------------------------------
-- Folder Persons (Per-Person Trust Boundary - ADR-007)
-- -----------------------------------------------------------------------------
CREATE TABLE workflow.folder_person
(
    id                   VARCHAR(64)  NOT NULL,
    folder_id            VARCHAR(64)  NOT NULL REFERENCES workflow.client_folder (id) ON DELETE CASCADE,
    display_label        VARCHAR(256) NOT NULL,
    is_minor             BOOLEAN      NOT NULL DEFAULT FALSE,
    holds_own_credential BOOLEAN      NOT NULL DEFAULT FALSE,
    created_at           TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT pk_folder_person PRIMARY KEY (folder_id, id),
    CONSTRAINT ck_minor_no_credential CHECK (NOT (is_minor AND holds_own_credential))
);

-- -----------------------------------------------------------------------------
-- Case Workspaces
-- -----------------------------------------------------------------------------
CREATE TABLE workflow.case_workspace
(
    id                         VARCHAR(64)  NOT NULL PRIMARY KEY,
    folder_id                  VARCHAR(64)  NOT NULL REFERENCES workflow.client_folder (id) ON DELETE CASCADE,
    tenant_id                  VARCHAR(64)  NOT NULL REFERENCES library.institution_tenant (tenant_id),
    collection_namespace       VARCHAR(64)  NOT NULL,
    collection_id              VARCHAR(64)  NOT NULL,
    pinned_collection_revision INT          NOT NULL,
    state                      VARCHAR(32)  NOT NULL DEFAULT 'INTAKE',
    preparer_user_id           VARCHAR(64)  NULL,
    reviewer_user_id           VARCHAR(64)  NULL,
    approver_user_id           VARCHAR(64)  NULL,
    created_at                 TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at                 TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT ck_case_state 
        CHECK (state IN ('INTAKE', 'IN_PROGRESS', 'REVIEW_READY', 'APPROVED', 'GENERATED', 'QUARANTINED')),

    CONSTRAINT fk_case_collection
        FOREIGN KEY (collection_namespace, collection_id, pinned_collection_revision)
        REFERENCES library.document_collection (namespace, collection_id, revision)
);

CREATE INDEX idx_case_folder ON workflow.case_workspace (folder_id);
CREATE INDEX idx_case_tenant ON workflow.case_workspace (tenant_id);

-- -----------------------------------------------------------------------------
-- Case Pinned Blueprints (Immutable protection against edition drift)
-- -----------------------------------------------------------------------------
CREATE TABLE workflow.case_pinned_blueprint
(
    case_id                    VARCHAR(64) NOT NULL REFERENCES workflow.case_workspace (id) ON DELETE CASCADE,
    blueprint_namespace        VARCHAR(64) NOT NULL,
    blueprint_id               VARCHAR(64) NOT NULL,
    pinned_blueprint_revision  INT         NOT NULL,
    drift_detected             BOOLEAN     NOT NULL DEFAULT FALSE,
    drift_quarantined_at       TIMESTAMPTZ NULL,

    CONSTRAINT pk_case_pinned_blueprint 
        PRIMARY KEY (case_id, blueprint_namespace, blueprint_id),

    CONSTRAINT fk_pinned_blueprint 
        FOREIGN KEY (blueprint_namespace, blueprint_id, pinned_blueprint_revision)
        REFERENCES library.document_blueprint (namespace, blueprint_id, revision)
);

-- -----------------------------------------------------------------------------
-- Case Field Values (Canonical values, confirmations, and provenance)
-- -----------------------------------------------------------------------------
CREATE TABLE workflow.case_field_value
(
    case_id               VARCHAR(64)   NOT NULL REFERENCES workflow.case_workspace (id) ON DELETE CASCADE,
    canonical_path        VARCHAR(256)  NOT NULL, -- e.g. 'applicant.name.first'
    attributed_person_id  VARCHAR(64)   NULL,
    raw_value             TEXT          NULL,
    confirmed_value       TEXT          NULL,
    is_human_confirmed    BOOLEAN       NOT NULL DEFAULT FALSE,
    confirmed_by_user_id  VARCHAR(64)   NULL,
    confirmed_at          TIMESTAMPTZ   NULL,
    confidence_score      NUMERIC(4, 3) NULL,
    source_kind           VARCHAR(32)   NOT NULL DEFAULT 'MANUAL_ENTRY',
    source_document_id    VARCHAR(64)   NULL,
    created_at            TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at            TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT pk_case_field_value PRIMARY KEY (case_id, canonical_path),

    CONSTRAINT ck_source_kind 
        CHECK (source_kind IN ('DOCUMENT_OCR', 'QUESTIONNAIRE', 'MANUAL_ENTRY', 'STUB', 'AI_PROPOSAL'))
);

CREATE INDEX idx_field_value_person ON workflow.case_field_value (case_id, attributed_person_id);

-- -----------------------------------------------------------------------------
-- Case Approvals (Audit trail & invalidation)
-- -----------------------------------------------------------------------------
CREATE TABLE workflow.case_approval
(
    id                   UUID        NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    case_id              VARCHAR(64) NOT NULL REFERENCES workflow.case_workspace (id) ON DELETE CASCADE,
    approved_by_user_id  VARCHAR(64) NOT NULL,
    approval_kind        VARCHAR(32) NOT NULL,
    approved_at          TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    is_invalidated       BOOLEAN     NOT NULL DEFAULT FALSE,
    invalidated_at       TIMESTAMPTZ NULL,
    invalidation_reason  TEXT        NULL,

    CONSTRAINT ck_approval_kind 
        CHECK (approval_kind IN ('PREPARER_CONFIRMATION', 'APPLICANT_REVIEW', 'FINAL_SIGN_OFF'))
);

CREATE INDEX idx_approval_case ON workflow.case_approval (case_id, is_invalidated);

-- -----------------------------------------------------------------------------
-- Upload Sessions (Direct GCS upload grants)
-- -----------------------------------------------------------------------------
CREATE TABLE workflow.upload_session
(
    id                 VARCHAR(64)   NOT NULL PRIMARY KEY,
    case_id            VARCHAR(64)   NULL REFERENCES workflow.case_workspace (id) ON DELETE SET NULL,
    folder_id          VARCHAR(64)   NOT NULL REFERENCES workflow.client_folder (id) ON DELETE CASCADE,
    tenant_id          VARCHAR(64)   NOT NULL REFERENCES library.institution_tenant (tenant_id),
    subject_person_id  VARCHAR(64)   NULL,
    original_name      VARCHAR(256)  NOT NULL,
    declared_mime_type VARCHAR(128)  NOT NULL,
    size_bytes         BIGINT        NOT NULL,
    content_sha256     CHAR(64)      NULL,
    storage_path       VARCHAR(1024) NOT NULL,
    expires_at         TIMESTAMPTZ   NOT NULL,
    state              VARCHAR(32)   NOT NULL DEFAULT 'CREATED',
    created_at         TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT ck_upload_state 
        CHECK (state IN ('CREATED', 'UPLOADED', 'VERIFIED', 'PROCESSED', 'FAILED', 'EXPIRED'))
);

CREATE INDEX idx_upload_folder ON workflow.upload_session (folder_id);

-- -----------------------------------------------------------------------------
-- Transactional Outbox (Reliable Pub/Sub Event Dispatch)
-- -----------------------------------------------------------------------------
CREATE TABLE workflow.outbox_event
(
    id             UUID        NOT NULL PRIMARY KEY DEFAULT gen_random_uuid(),
    aggregate_type VARCHAR(64) NOT NULL,
    aggregate_id   VARCHAR(64) NOT NULL,
    event_type     VARCHAR(64) NOT NULL,
    payload        JSONB       NOT NULL,
    published      BOOLEAN     NOT NULL DEFAULT FALSE,
    published_at   TIMESTAMPTZ NULL,
    created_at     TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX idx_outbox_unpublished 
    ON workflow.outbox_event (created_at) WHERE (NOT published);
