-- ==============================================================================
-- LaPluma Workflow Relational Schema (PostgreSQL 16 / ADR-019)
-- Migration: 001_workflow_schema_postgres.sql
-- Conforms to: contracts/openapi/workforce-workflow.yaml & INT-05/ADR-019
-- ==============================================================================

CREATE SCHEMA IF NOT EXISTS workflow;
CREATE EXTENSION IF NOT EXISTS "pgcrypto";

-- ------------------------------------------------------------------------------
-- Primary Case Folder Aggregate
-- ------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS workflow.case_folder
(
    id          UUID         NOT NULL DEFAULT gen_random_uuid() CONSTRAINT pk_case_folder PRIMARY KEY,
    tenant_id   VARCHAR(64)  NOT NULL,
    client_name VARCHAR(256) NOT NULL,
    category    VARCHAR(64)  NOT NULL,
    status      VARCHAR(32)  NOT NULL DEFAULT 'INTAKE'
        CONSTRAINT ck_case_folder_status CHECK (status IN
            ('INTAKE', 'IN_REVIEW', 'BLOCKED', 'READY_FOR_FILING', 'SUBMITTED', 'ARCHIVED')),
    created_at  TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at  TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_case_folder_tenant
    ON workflow.case_folder (tenant_id, status);

-- ------------------------------------------------------------------------------
-- Scoped Uploads and Storage Lifecycle (INT-05 / ADR-019)
-- ------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS workflow.document_upload
(
    id           UUID         NOT NULL DEFAULT gen_random_uuid() CONSTRAINT pk_document_upload PRIMARY KEY,
    case_id      UUID         NOT NULL
        CONSTRAINT fk_document_upload_case REFERENCES workflow.case_folder (id) ON DELETE CASCADE,
    session_id   VARCHAR(64)  NOT NULL CONSTRAINT uq_document_upload_session UNIQUE,
    file_name    VARCHAR(512) NOT NULL,
    byte_size    BIGINT       NOT NULL
        CONSTRAINT ck_document_upload_size CHECK (byte_size > 0 AND byte_size <= 104857600), -- 100 MB max
    content_type VARCHAR(128) NOT NULL,
    sha256       CHAR(64)     NOT NULL
        CONSTRAINT ck_document_upload_sha256 CHECK (length(sha256) = 64),
    status       VARCHAR(32)  NOT NULL DEFAULT 'PENDING_UPLOAD'
        CONSTRAINT ck_document_upload_status CHECK (status IN
            ('PENDING_UPLOAD', 'QUARANTINED', 'VERIFIED', 'PROMOTED', 'REJECTED')),
    storage_uri  TEXT         NULL,
    uploaded_at  TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP,
    promoted_at  TIMESTAMPTZ  NULL
);

CREATE INDEX IF NOT EXISTS idx_document_upload_case
    ON workflow.document_upload (case_id);

-- ------------------------------------------------------------------------------
-- Append-Only Review Ledger and Human Attribution Audit Trail
-- ------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS workflow.review_ledger
(
    id               UUID          NOT NULL DEFAULT gen_random_uuid() CONSTRAINT pk_review_ledger PRIMARY KEY,
    case_id          UUID          NOT NULL
        CONSTRAINT fk_review_ledger_case REFERENCES workflow.case_folder (id) ON DELETE CASCADE,
    entry_kind       VARCHAR(64)   NOT NULL
        CONSTRAINT ck_review_ledger_kind CHECK (entry_kind IN
            ('INITIAL_INTAKE', 'EXTRACTION_PROPOSAL', 'HUMAN_CONFIRMATION', 'CORRECTION', 'APPROVAL_INVALIDATION')),
    field_key        VARCHAR(128)  NOT NULL,
    proposed_value   TEXT          NULL,
    confirmed_value  TEXT          NULL,
    confidence_score NUMERIC(5, 4) NULL
        CONSTRAINT ck_review_ledger_confidence CHECK (confidence_score IS NULL OR (confidence_score >= 0.0000 AND confidence_score <= 1.0000)),
    author_id        VARCHAR(128)  NOT NULL,
    author_role      VARCHAR(64)   NOT NULL
        CONSTRAINT ck_review_ledger_role CHECK (author_role IN
            ('SYSTEM_EXTRACTOR', 'ACCREDITED_REPRESENTATIVE', 'APPLICANT', 'SUPERVISING_ATTORNEY')),
    recorded_at      TIMESTAMPTZ   NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_review_ledger_case_field
    ON workflow.review_ledger (case_id, field_key);

-- ------------------------------------------------------------------------------
-- Generated AcroForm Filing Packages and Short-Lived Scoped Download Grants
-- ------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS workflow.package_generation
(
    id                   UUID         NOT NULL DEFAULT gen_random_uuid() CONSTRAINT pk_package_generation PRIMARY KEY,
    case_id              UUID         NOT NULL
        CONSTRAINT fk_package_generation_case REFERENCES workflow.case_folder (id) ON DELETE CASCADE,
    package_code         VARCHAR(64)  NOT NULL,
    content_sha256       CHAR(64)     NOT NULL
        CONSTRAINT ck_package_generation_sha256 CHECK (length(content_sha256) = 64),
    download_grant_token VARCHAR(256) NOT NULL CONSTRAINT uq_package_generation_token UNIQUE,
    grant_expires_at     TIMESTAMPTZ  NOT NULL,
    generated_at         TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_package_generation_lookup
    ON workflow.package_generation (case_id, package_code);
