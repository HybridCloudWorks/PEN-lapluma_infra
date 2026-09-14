-- Database Roles and Permissions Schema, PostgreSQL 16
-- Delivers task INT-02: Map application identity and authorization to GCP.
--
-- Implements separation of duty across database principals:
--   1. lapluma_app_core: Core API application principal.
--      - Granted SELECT on library schemas and views.
--      - REVOKE ALL on workflow schema (zero case/client/workflow access).
--   2. lapluma_app_workflow: Workflow API application principal.
--      - Granted SELECT, INSERT, UPDATE, DELETE on workflow schema.
--      - Granted SELECT on library schema (read-only for pinned blueprint/collection resolution).
--      - REVOKE INSERT, UPDATE, DELETE on library schema (cannot mutate library definitions).
--   3. lapluma_library_admin: Managed CI / CLI library operator principal.
--      - Granted SELECT, INSERT, UPDATE on library schema for blueprint publication and tenant assignment.
--      - REVOKE ALL on workflow schema (operators NEVER have case access).
--   4. lapluma_worker: Processing worker principal.
--      - STRICTLY ISOLATED: Denied direct database connection entirely.

DO $$
BEGIN
    -- Core API Application Role
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'lapluma_app_core') THEN
        CREATE ROLE lapluma_app_core WITH LOGIN NOINHERIT;
    END IF;

    -- Workflow API Application Role
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'lapluma_app_workflow') THEN
        CREATE ROLE lapluma_app_workflow WITH LOGIN NOINHERIT;
    END IF;

    -- Library Administrator / Publisher Role (CLI & CI runner)
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'lapluma_library_admin') THEN
        CREATE ROLE lapluma_library_admin WITH LOGIN NOINHERIT;
    END IF;
END
$$;

-- -----------------------------------------------------------------------------
-- Core API Permissions (Read-only library catalog, ZERO workflow access)
-- -----------------------------------------------------------------------------
GRANT USAGE ON SCHEMA library TO lapluma_app_core;
GRANT SELECT ON ALL TABLES IN SCHEMA library TO lapluma_app_core;
GRANT SELECT ON ALL SEQUENCES IN SCHEMA library TO lapluma_app_core;

-- Explicitly revoke all access from Core API to workflow tables
REVOKE ALL ON SCHEMA workflow FROM lapluma_app_core;
REVOKE ALL ON ALL TABLES IN SCHEMA workflow FROM lapluma_app_core;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA workflow FROM lapluma_app_core;

-- -----------------------------------------------------------------------------
-- Workflow API Permissions (Full workflow CRUD, read-only library)
-- -----------------------------------------------------------------------------
GRANT USAGE ON SCHEMA workflow TO lapluma_app_workflow;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA workflow TO lapluma_app_workflow;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA workflow TO lapluma_app_workflow;

-- Workflow API may read published blueprints and collections for pinned resolution
GRANT USAGE ON SCHEMA library TO lapluma_app_workflow;
GRANT SELECT ON ALL TABLES IN SCHEMA library TO lapluma_app_workflow;
REVOKE INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA library FROM lapluma_app_workflow;

-- -----------------------------------------------------------------------------
-- Library Admin Permissions (Blueprint publishing, ZERO workflow access)
-- -----------------------------------------------------------------------------
GRANT USAGE ON SCHEMA library TO lapluma_library_admin;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA library TO lapluma_library_admin;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA library TO lapluma_library_admin;

-- Library operators have zero access to cases, clients, or evidence
REVOKE ALL ON SCHEMA workflow FROM lapluma_library_admin;
REVOKE ALL ON ALL TABLES IN SCHEMA workflow FROM lapluma_library_admin;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA workflow FROM lapluma_library_admin;

-- -----------------------------------------------------------------------------
-- Default Privileges for Future Tables
-- -----------------------------------------------------------------------------
ALTER DEFAULT PRIVILEGES IN SCHEMA library GRANT SELECT ON TABLES TO lapluma_app_core;
ALTER DEFAULT PRIVILEGES IN SCHEMA library GRANT SELECT ON TABLES TO lapluma_app_workflow;
ALTER DEFAULT PRIVILEGES IN SCHEMA library GRANT SELECT, INSERT, UPDATE ON TABLES TO lapluma_library_admin;
ALTER DEFAULT PRIVILEGES IN SCHEMA workflow GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO lapluma_app_workflow;
ALTER DEFAULT PRIVILEGES IN SCHEMA workflow REVOKE ALL ON TABLES FROM lapluma_app_core;
ALTER DEFAULT PRIVILEGES IN SCHEMA workflow REVOKE ALL ON TABLES FROM lapluma_library_admin;
