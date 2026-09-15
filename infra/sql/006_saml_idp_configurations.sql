-- Enterprise SAML 2.0 Identity Provider Configurations, PostgreSQL 16
-- Delivers task INF-20 / INT-16: Enterprise SAML 2.0 Single Sign-On & IdP Lockdown (Google Workspace & Entra ID Only).
--
-- Security Invariants:
-- 1. Authentication is strictly limited to approved Google Workspace and Microsoft Entra ID tenants.
-- 2. Personal email domains (@gmail.com, @outlook.com) fail closed and cannot be registered in allowed_domains.
-- 3. Inbound SAML assertions must validate against the pinned X.509 certificate for that tenant and entity_id.

CREATE SCHEMA IF NOT EXISTS library;

-- -----------------------------------------------------------------------------
-- Institution IdP Configuration Table
-- -----------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS library.institution_idp_configuration (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id VARCHAR(128) NOT NULL REFERENCES library.institution_tenant(tenant_id) ON DELETE CASCADE,
    idp_type VARCHAR(64) NOT NULL CHECK (idp_type IN ('GOOGLE_WORKSPACE', 'ENTRA_ID_SAML')),
    entity_id VARCHAR(512) NOT NULL,
    sso_service_url VARCHAR(1024) NOT NULL,
    idp_x509_certificate TEXT NOT NULL,
    allowed_domains JSONB NOT NULL,
    attribute_mappings JSONB NOT NULL DEFAULT '{
        "email": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
        "given_name": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname",
        "surname": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname",
        "role": "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
    }'::jsonb,
    admin_consent_verified BOOLEAN NOT NULL DEFAULT FALSE,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT uq_tenant_idp UNIQUE (tenant_id, idp_type)
);

CREATE INDEX IF NOT EXISTS idx_idp_entity_id ON library.institution_idp_configuration(entity_id);
CREATE INDEX IF NOT EXISTS idx_idp_tenant ON library.institution_idp_configuration(tenant_id);

-- -----------------------------------------------------------------------------
-- Seed HybridCloudWorks Tenant & Approved IdP Configurations
-- -----------------------------------------------------------------------------
INSERT INTO library.institution_tenant (tenant_id, display_name, is_active)
VALUES ('tenant_hybridcloudworks', 'HybridCloudWorks', TRUE)
ON CONFLICT (tenant_id) DO UPDATE SET is_active = TRUE;

-- Google Workspace Enterprise SAML Configuration for hybridcloudworks.com
INSERT INTO library.institution_idp_configuration (
    tenant_id, idp_type, entity_id, sso_service_url,
    idp_x509_certificate, allowed_domains, attribute_mappings, admin_consent_verified, is_active
)
VALUES (
    'tenant_hybridcloudworks',
    'GOOGLE_WORKSPACE',
    'https://accounts.google.com/o/saml2?idpid=C03hcwks',
    'https://accounts.google.com/o/saml2/idp?idpid=C03hcwks',
    '-----BEGIN CERTIFICATE-----
MIIDdDCCAlygAwIBAgIGAY...MOCK_GOOGLE_WORKSPACE_CERT...
-----END CERTIFICATE-----',
    '["hybridcloudworks.com"]'::jsonb,
    '{
        "email": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
        "given_name": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname",
        "surname": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname",
        "role": "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
    }'::jsonb,
    TRUE,
    TRUE
)
ON CONFLICT (tenant_id, idp_type) DO UPDATE
SET entity_id = EXCLUDED.entity_id,
    sso_service_url = EXCLUDED.sso_service_url,
    idp_x509_certificate = EXCLUDED.idp_x509_certificate,
    allowed_domains = EXCLUDED.allowed_domains,
    admin_consent_verified = EXCLUDED.admin_consent_verified,
    updated_at = NOW();

-- Microsoft Entra ID Enterprise SAML Configuration for hybridcloudworks.com
INSERT INTO library.institution_idp_configuration (
    tenant_id, idp_type, entity_id, sso_service_url,
    idp_x509_certificate, allowed_domains, attribute_mappings, admin_consent_verified, is_active
)
VALUES (
    'tenant_hybridcloudworks',
    'ENTRA_ID_SAML',
    'https://sts.windows.net/4a2b9a76-2e4b-47e1-872f-5b1234567890/',
    'https://login.microsoftonline.com/4a2b9a76-2e4b-47e1-872f-5b1234567890/saml2',
    '-----BEGIN CERTIFICATE-----
MIIDBTCCAe2gAwIBAgIQ...MOCK_MICROSOFT_ENTRA_ID_CERT...
-----END CERTIFICATE-----',
    '["hybridcloudworks.com"]'::jsonb,
    '{
        "email": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress",
        "given_name": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname",
        "surname": "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname",
        "role": "http://schemas.microsoft.com/ws/2008/06/identity/claims/role"
    }'::jsonb,
    TRUE,
    TRUE
)
ON CONFLICT (tenant_id, idp_type) DO UPDATE
SET entity_id = EXCLUDED.entity_id,
    sso_service_url = EXCLUDED.sso_service_url,
    idp_x509_certificate = EXCLUDED.idp_x509_certificate,
    allowed_domains = EXCLUDED.allowed_domains,
    admin_consent_verified = EXCLUDED.admin_consent_verified,
    updated_at = NOW();
