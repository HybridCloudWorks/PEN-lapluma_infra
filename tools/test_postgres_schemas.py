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


if __name__ == "__main__":
    unittest.main()
