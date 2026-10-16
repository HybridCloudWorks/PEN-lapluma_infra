-- Remove the unused SAML IdP configuration table and its placeholder rows, PostgreSQL 16
-- Follow-up to PR #141 (INF-21 to INF-24).
--
-- Migration 006 created library.institution_idp_configuration and seeded two rows whose
-- certificates are placeholders (MOCK_GOOGLE_WORKSPACE_CERT, MOCK_MICROSOFT_ENTRA_ID_CERT). Nothing
-- reads the table: the core API loads identity providers only from Saml__IdentityProvidersJson
-- (Secret Manager secret lp-saml-idps-<env>; see
-- docs/runbooks/session-tokens-and-saml-identity-providers.md), and it refuses a placeholder
-- certificate at startup. A table of trust anchors that no code consults invites someone to "fix"
-- sign-in by editing it, so it is dropped rather than emptied.
--
-- 006 is not edited, because tools/migrate_db.py records a checksum for every migration. The bundle
-- replays every script on each run, so 006 recreates the table and this script drops it again; the
-- end state is the same. The tenant row 006 seeds (tenant_hybridcloudworks) is still used and stays.
--
-- No CASCADE: if anything has come to depend on the table, this fails instead of dropping it too.

DROP TABLE IF EXISTS library.institution_idp_configuration;
