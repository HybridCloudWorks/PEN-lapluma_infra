#!/usr/bin/env python3
"""Unit and contract tests for LaPluma Migration Runner & Schema Ledger (P1)."""

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

from migrate_db import (
    MigrationError,
    MigrationRunner,
    SCHEMA_MIGRATIONS_DDL,
    main,
)


class TestMigrationRunner(unittest.TestCase):
    """Test suite for migration discovery, verification, plan generation, and bundling."""

    def setUp(self) -> None:
        self.runner = MigrationRunner()

    def test_discover_migrations_in_repo(self) -> None:
        migrations = self.runner.discover_migrations()
        self.assertGreaterEqual(len(migrations), 5)
        self.assertEqual(migrations[0].version, "001")
        self.assertEqual(migrations[0].name, "document_library_schema")
        self.assertEqual(migrations[1].version, "002")
        self.assertEqual(migrations[2].version, "003")
        self.assertEqual(migrations[3].version, "004")
        self.assertEqual(migrations[4].version, "005")

    def test_checksum_reproducibility(self) -> None:
        migrations = self.runner.discover_migrations()
        for m in migrations:
            self.assertEqual(len(m.checksum), 64)
            self.assertTrue(all(c in "0123456789abcdef" for c in m.checksum))

    def test_verify_passes_on_repo_migrations(self) -> None:
        errors = self.runner.verify_migrations()
        self.assertEqual(errors, [], f"Expected no errors on repo migrations, got: {errors}")

    def test_verify_detects_sequence_gap(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td)
            (p / "001_initial.sql").write_text("SELECT 1;", encoding="utf-8")
            (p / "003_skipped.sql").write_text("SELECT 3;", encoding="utf-8")

            temp_runner = MigrationRunner(sql_dir=p)
            errors = temp_runner.verify_migrations()
            self.assertTrue(
                any("Sequence break" in e and "002" in e for e in errors),
                f"Expected sequence gap error, got {errors}",
            )

    def test_verify_detects_non_001_start(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td)
            (p / "002_initial.sql").write_text("SELECT 2;", encoding="utf-8")

            temp_runner = MigrationRunner(sql_dir=p)
            errors = temp_runner.verify_migrations()
            self.assertTrue(
                any("Sequence break" in e and "001" in e for e in errors),
                f"Expected start version error, got {errors}",
            )

    def test_verify_detects_duplicate_version(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td)
            (p / "001_first.sql").write_text("SELECT 1;", encoding="utf-8")
            (p / "001_duplicate.sql").write_text("SELECT 2;", encoding="utf-8")

            temp_runner = MigrationRunner(sql_dir=p)
            errors = temp_runner.verify_migrations()
            self.assertTrue(
                any("Duplicate migration version" in e for e in errors),
                f"Expected duplicate version error, got {errors}",
            )

    def test_verify_detects_malformed_filename(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td)
            (p / "001_first.sql").write_text("SELECT 1;", encoding="utf-8")
            (p / "bad_name.sql").write_text("SELECT 2;", encoding="utf-8")

            temp_runner = MigrationRunner(sql_dir=p)
            errors = temp_runner.verify_migrations()
            self.assertTrue(
                any("does not match naming pattern" in e for e in errors),
                f"Expected malformed filename error, got {errors}",
            )

    def test_verify_detects_empty_file(self) -> None:
        with tempfile.TemporaryDirectory() as td:
            p = Path(td)
            (p / "001_first.sql").write_text("", encoding="utf-8")

            temp_runner = MigrationRunner(sql_dir=p)
            errors = temp_runner.verify_migrations()
            self.assertTrue(
                any("is empty" in e for e in errors),
                f"Expected empty file error, got {errors}",
            )

    def test_generate_bundle_contains_all_scripts(self) -> None:
        bundle = self.runner.generate_bundle()
        self.assertIn("public.schema_migrations", bundle)
        self.assertIn("Migration 001: document_library_schema", bundle)
        self.assertIn("Migration 005: database_roles_and_permissions", bundle)
        self.assertIn("INSERT INTO public.schema_migrations", bundle)

    def test_plan_output(self) -> None:
        plan_data = self.runner.plan(applied_versions={"001", "002"})
        self.assertEqual(plan_data["totalMigrations"], 5)
        self.assertEqual(plan_data["appliedCount"], 2)
        self.assertEqual(plan_data["pendingCount"], 3)
        self.assertEqual(plan_data["migrations"][0]["status"], "APPLIED")
        self.assertEqual(plan_data["migrations"][1]["status"], "APPLIED")
        self.assertEqual(plan_data["migrations"][2]["status"], "PENDING")

    def test_cli_commands(self) -> None:
        self.assertEqual(main(["verify"]), 0)
        self.assertEqual(main(["list"]), 0)
        self.assertEqual(main(["ddl"]), 0)

        with tempfile.NamedTemporaryFile("w", suffix=".json", delete=False) as tf:
            plan_path = Path(tf.name)
        try:
            self.assertEqual(main(["plan", "-o", str(plan_path)]), 0)
            data = json.loads(plan_path.read_text(encoding="utf-8"))
            self.assertEqual(data["totalMigrations"], 5)
        finally:
            plan_path.unlink(missing_ok=True)

        with tempfile.NamedTemporaryFile("w", suffix=".sql", delete=False) as tf:
            bundle_path = Path(tf.name)
        try:
            self.assertEqual(main(["bundle", "-o", str(bundle_path)]), 0)
            self.assertTrue(bundle_path.stat().st_size > 0)
        finally:
            bundle_path.unlink(missing_ok=True)


if __name__ == "__main__":
    unittest.main()
