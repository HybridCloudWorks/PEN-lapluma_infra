-- Catalog schema, version 001.
--
-- The authoritative relational store behind ICatalogSource. Applied by a migration step that does
-- not exist yet: nothing has provisioned a database, so this file has never been executed. It is
-- reviewed source, not proven source — see TODO.md.
--
-- Shape follows contracts/catalog.openapi.json, which is the contract both this and the iOS client
-- are held to. Where the two could drift, the contract wins.

CREATE SCHEMA catalog;
GO

CREATE TABLE catalog.Category
(
    Code            NVARCHAR(64)  NOT NULL CONSTRAINT PK_Category PRIMARY KEY,
    Title           NVARCHAR(256) NOT NULL,
    SortOrder       INT           NOT NULL
);
GO

CREATE TABLE catalog.Subcategory
(
    Code            NVARCHAR(64)  NOT NULL CONSTRAINT PK_Subcategory PRIMARY KEY,
    CategoryCode    NVARCHAR(64)  NOT NULL
        CONSTRAINT FK_Subcategory_Category REFERENCES catalog.Category (Code),
    Title           NVARCHAR(256) NOT NULL,
    SortOrder       INT           NOT NULL
);
GO

CREATE TABLE catalog.Package
(
    PackageCode         NVARCHAR(64)   NOT NULL CONSTRAINT PK_Package PRIMARY KEY,
    Title               NVARCHAR(256)  NOT NULL,
    CategoryCode        NVARCHAR(64)   NOT NULL
        CONSTRAINT FK_Package_Category REFERENCES catalog.Category (Code),
    SubcategoryCode     NVARCHAR(64)   NOT NULL
        CONSTRAINT FK_Package_Subcategory REFERENCES catalog.Subcategory (Code),
    Agency              NVARCHAR(256)  NOT NULL,
    AgencyCategoryLabel NVARCHAR(256)  NULL,
    FeeUsdCents         INT            NULL,
    -- The contract requires https on both. Enforced here as well as in the API, because the API is
    -- not the only thing that will ever write this table.
    FeeCitationUrl      NVARCHAR(2048) NULL
        CONSTRAINT CK_Package_FeeCitationUrl CHECK (FeeCitationUrl IS NULL OR FeeCitationUrl LIKE 'https://%'),
    SourceUrl           NVARCHAR(2048) NOT NULL
        CONSTRAINT CK_Package_SourceUrl CHECK (SourceUrl LIKE 'https://%'),
    LastVerified        DATETIMEOFFSET NOT NULL
);
GO

-- Package activation is derived from the weakest form, never stored. Storing it would let the
-- stored value and the forms disagree, and the contract deliberately has no activationState on a
-- package for exactly that reason.
CREATE TABLE catalog.Form
(
    PackageCode      NVARCHAR(64)   NOT NULL
        CONSTRAINT FK_Form_Package REFERENCES catalog.Package (PackageCode),
    FormNumber       NVARCHAR(64)   NOT NULL,
    Title            NVARCHAR(256)  NOT NULL,
    EditionDate      DATE           NOT NULL,
    Encoding         NVARCHAR(32)   NOT NULL
        CONSTRAINT CK_Form_Encoding CHECK (Encoding IN ('ACROFORM', 'XFA', 'FLAT')),
    PageCount        INT            NOT NULL CONSTRAINT CK_Form_PageCount CHECK (PageCount >= 0),
    ArtifactKind     NVARCHAR(32)   NOT NULL
        CONSTRAINT CK_Form_ArtifactKind CHECK (ArtifactKind IN
            ('OFFICIAL_PDF', 'EXTERNAL_WORKFLOW', 'PROPRIETARY_FORM', 'AUTHORED_TEMPLATE')),
    FillCapability   NVARCHAR(32)   NOT NULL
        CONSTRAINT CK_Form_FillCapability CHECK (FillCapability IN
            ('AUTOMATIC_FILL', 'ASSISTED_PREPARATION', 'REFERENCE_ONLY')),
    ActivationState  NVARCHAR(32)   NOT NULL
        CONSTRAINT CK_Form_ActivationState CHECK (ActivationState IN
            ('UNAVAILABLE', 'CATALOG_ONLY', 'ASSISTED', 'PILOT')),
    SourcePageUrl    NVARCHAR(2048) NULL
        CONSTRAINT CK_Form_SourcePageUrl CHECK (SourcePageUrl IS NULL OR SourcePageUrl LIKE 'https://%'),
    ArtifactUrl      NVARCHAR(2048) NULL
        CONSTRAINT CK_Form_ArtifactUrl CHECK (ArtifactUrl IS NULL OR ArtifactUrl LIKE 'https://%'),
    OfficialDomain   NVARCHAR(256)  NULL,
    Sha256           CHAR(64)       NULL,
    SourceLastVerified DATETIMEOFFSET NULL,
    SortOrder        INT            NOT NULL,
    CONSTRAINT PK_Form PRIMARY KEY (PackageCode, FormNumber)
);
GO

-- Extracted schemas are edition-pinned and are not served until a field map has been approved by
-- two people. The table exists so the API's fail-closed 404 becomes a real absence rather than an
-- unimplemented branch; the approval gate is a separate item.
CREATE TABLE catalog.ExtractedSchema
(
    Authority      NVARCHAR(256) NOT NULL,
    FormId         NVARCHAR(64)  NOT NULL,
    EditionDate    DATE          NOT NULL,
    SchemaVersion  NVARCHAR(32)  NOT NULL,
    ApprovedAt     DATETIMEOFFSET NOT NULL,
    FieldsJson     NVARCHAR(MAX) NOT NULL
        CONSTRAINT CK_ExtractedSchema_FieldsJson CHECK (ISJSON(FieldsJson) = 1),
    CONSTRAINT PK_ExtractedSchema PRIMARY KEY (Authority, FormId, EditionDate, SchemaVersion)
);
GO

CREATE INDEX IX_Package_Ordering ON catalog.Package (CategoryCode, SubcategoryCode, PackageCode);
GO
CREATE INDEX IX_Form_Package ON catalog.Form (PackageCode, SortOrder);
GO
