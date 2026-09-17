-- ==============================================================================
-- LaPluma Catalog Relational Schema (PostgreSQL 16 / ADR-019)
-- Migration: 001_catalog_schema_postgres.sql
-- Conforms to: contracts/catalog.openapi.json
-- ==============================================================================

CREATE SCHEMA IF NOT EXISTS catalog;

-- ------------------------------------------------------------------------------
-- Top-level catalog category grouping
-- ------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS catalog.category
(
    code        VARCHAR(64)  NOT NULL CONSTRAINT pk_category PRIMARY KEY,
    title       VARCHAR(256) NOT NULL,
    sort_order  INT          NOT NULL
);

-- ------------------------------------------------------------------------------
-- Subcategory grouping within a primary category
-- ------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS catalog.subcategory
(
    code          VARCHAR(64)  NOT NULL CONSTRAINT pk_subcategory PRIMARY KEY,
    category_code VARCHAR(64)  NOT NULL
        CONSTRAINT fk_subcategory_category REFERENCES catalog.category (code),
    title         VARCHAR(256) NOT NULL,
    sort_order    INT          NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_subcategory_category_code
    ON catalog.subcategory (category_code);

-- ------------------------------------------------------------------------------
-- Official filing package definition
-- ------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS catalog.package
(
    package_code          VARCHAR(64)  NOT NULL CONSTRAINT pk_package PRIMARY KEY,
    title                 VARCHAR(256) NOT NULL,
    category_code         VARCHAR(64)  NOT NULL
        CONSTRAINT fk_package_category REFERENCES catalog.category (code),
    subcategory_code      VARCHAR(64)  NOT NULL
        CONSTRAINT fk_package_subcategory REFERENCES catalog.subcategory (code),
    agency                VARCHAR(256) NOT NULL,
    agency_category_label VARCHAR(256) NULL,
    fee_usd_cents         INT          NULL CONSTRAINT ck_package_fee_positive CHECK (fee_usd_cents IS NULL OR fee_usd_cents >= 0),
    fee_citation_url      TEXT         NULL
        CONSTRAINT ck_package_fee_citation_url CHECK (fee_citation_url IS NULL OR fee_citation_url LIKE 'https://%'),
    source_url            TEXT         NOT NULL
        CONSTRAINT ck_package_source_url CHECK (source_url LIKE 'https://%'),
    last_verified         TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_package_category_code
    ON catalog.package (category_code);
CREATE INDEX IF NOT EXISTS idx_package_subcategory_code
    ON catalog.package (subcategory_code);

-- ------------------------------------------------------------------------------
-- Form definitions pinned to an official filing package
-- ------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS catalog.form
(
    package_code         VARCHAR(64)  NOT NULL
        CONSTRAINT fk_form_package REFERENCES catalog.package (package_code),
    form_number          VARCHAR(64)  NOT NULL,
    title                VARCHAR(256) NOT NULL,
    edition_date         DATE         NOT NULL,
    encoding             VARCHAR(32)  NOT NULL
        CONSTRAINT ck_form_encoding CHECK (encoding IN ('ACROFORM', 'XFA', 'FLAT')),
    page_count           INT          NOT NULL
        CONSTRAINT ck_form_page_count CHECK (page_count >= 0),
    artifact_kind        VARCHAR(32)  NOT NULL
        CONSTRAINT ck_form_artifact_kind CHECK (artifact_kind IN
            ('OFFICIAL_PDF', 'EXTERNAL_WORKFLOW', 'PROPRIETARY_FORM', 'AUTHORED_TEMPLATE')),
    fill_capability      VARCHAR(32)  NOT NULL
        CONSTRAINT ck_form_fill_capability CHECK (fill_capability IN
            ('AUTOMATIC_FILL', 'ASSISTED_PREPARATION', 'REFERENCE_ONLY')),
    activation_state     VARCHAR(32)  NOT NULL
        CONSTRAINT ck_form_activation_state CHECK (activation_state IN
            ('UNAVAILABLE', 'CATALOG_ONLY', 'ASSISTED', 'PILOT')),
    source_page_url      TEXT         NULL
        CONSTRAINT ck_form_source_page_url CHECK (source_page_url IS NULL OR source_page_url LIKE 'https://%'),
    artifact_url         TEXT         NULL
        CONSTRAINT ck_form_artifact_url CHECK (artifact_url IS NULL OR artifact_url LIKE 'https://%'),
    official_domain      VARCHAR(256) NULL,
    sha256               CHAR(64)     NULL
        CONSTRAINT ck_form_sha256 CHECK (sha256 IS NULL OR length(sha256) = 64),
    source_last_verified TIMESTAMPTZ  NULL,
    sort_order           INT          NOT NULL,
    CONSTRAINT pk_form PRIMARY KEY (package_code, form_number)
);

CREATE INDEX IF NOT EXISTS idx_form_form_number
    ON catalog.form (form_number);

-- ------------------------------------------------------------------------------
-- Extracted schemas for field mapping and verification
-- ------------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS catalog.extracted_schema
(
    authority      VARCHAR(256) NOT NULL,
    form_id        VARCHAR(64)  NOT NULL,
    edition_date   DATE         NOT NULL,
    schema_version VARCHAR(32)  NOT NULL,
    approved_at    TIMESTAMPTZ  NOT NULL,
    fields_json    JSONB        NOT NULL,
    CONSTRAINT pk_extracted_schema PRIMARY KEY (authority, form_id, edition_date, schema_version)
);

CREATE INDEX IF NOT EXISTS idx_extracted_schema_lookup
    ON catalog.extracted_schema (form_id, edition_date);
