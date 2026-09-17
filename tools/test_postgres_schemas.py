"""Validation suite for PostgreSQL 16 schema migrations under ADR-019."""

import pathlib
import re
import unittest

REPO_ROOT = pathlib.Path(__file__).resolve().parent.parent

CATALOG_MIGRATION = REPO_ROOT / "src" / "core-api" / "Sql" / "001_catalog_schema_postgres.sql"
WORKFLOW_MIGRATION = REPO_ROOT / "src" / "workflow-api" / "Sql" / "001_workflow_schema_postgres.sql"


class PostgresSchemaMigrationTests(unittest.TestCase):

    def test_catalog_migration_exists_and_validates(self) -> None:
        self.assertTrue(CATALOG_MIGRATION.is_file(), f"Missing {CATALOG_MIGRATION}")
        sql = CATALOG_MIGRATION.read_text(encoding="utf-8")

        # Disallow legacy T-SQL constructs
        self.assertNotRegex(sql, r"\bNVARCHAR\b", "PostgreSQL DDL must not use MSSQL NVARCHAR")
        self.assertNotRegex(sql, r"\bDATETIMEOFFSET\b", "PostgreSQL DDL must use TIMESTAMPTZ, not DATETIMEOFFSET")
        self.assertNotRegex(sql, r"^\s*GO\s*$", "PostgreSQL DDL must not contain MSSQL GO batch separators")
        self.assertNotRegex(sql, r"\bISJSON\b", "PostgreSQL DDL must use native JSONB, not MSSQL ISJSON()")

        # Verify key tables exist
        for table in ("catalog.category", "catalog.subcategory", "catalog.package", "catalog.form", "catalog.extracted_schema"):
            self.assertIn(table, sql.lower())

        # Verify parentheses are balanced
        self.assertEqual(sql.count("("), sql.count(")"))

    def test_workflow_migration_exists_and_validates(self) -> None:
        self.assertTrue(WORKFLOW_MIGRATION.is_file(), f"Missing {WORKFLOW_MIGRATION}")
        sql = WORKFLOW_MIGRATION.read_text(encoding="utf-8")

        # Disallow legacy T-SQL constructs
        self.assertNotRegex(sql, r"\bNVARCHAR\b", "PostgreSQL DDL must not use MSSQL NVARCHAR")
        self.assertNotRegex(sql, r"\bDATETIMEOFFSET\b", "PostgreSQL DDL must use TIMESTAMPTZ, not DATETIMEOFFSET")
        self.assertNotRegex(sql, r"^\s*GO\s*$", "PostgreSQL DDL must not contain MSSQL GO batch separators")

        # Verify key tables exist
        for table in ("workflow.case_folder", "workflow.document_upload", "workflow.review_ledger", "workflow.package_generation"):
            self.assertIn(table, sql.lower())

        # Verify UUID and TIMESTAMPTZ usage
        self.assertIn("gen_random_uuid()", sql)
        self.assertIn("timestamptz", sql.lower())

        # Verify parentheses are balanced
        self.assertEqual(sql.count("("), sql.count(")"))

    def test_migration_runner_dry_run(self) -> None:
        from tools.run_postgres_migrations import run_migrations
        result = run_migrations(dry_run=True)
        self.assertEqual(result, 0)

    def test_cloud_run_migration_job_declared_in_terraform(self) -> None:
        compute_tf = REPO_ROOT / "infra" / "terraform" / "modules" / "compute" / "main.tf"
        self.assertTrue(compute_tf.is_file(), f"Missing {compute_tf}")
        content = compute_tf.read_text(encoding="utf-8")

        self.assertIn('resource "google_cloud_run_v2_job" "db_migration"', content)
        self.assertIn('lp-db-migration-${var.environment}', content)
        self.assertIn('cloud_sql_instance', content)
        self.assertIn('POSTGRES_DB_CATALOG', content)
        self.assertIn('POSTGRES_DB_WORKFLOW', content)


if __name__ == "__main__":
    unittest.main()
