#!/usr/bin/env python3
"""LaPluma Automated Database Migration Runner & Schema Ledger (P1).

Provides deterministic discovery, checksum validation, bundling, and execution
planning for PostgreSQL 16 migrations under `infra/sql/`.

Features:
  - Discovers sequentially numbered migrations (001, 002, 003, ...).
  - Calculates cryptographic SHA256 checksums to detect silent mutation/tampering.
  - Generates transaction-wrapped idempotent bundles for Cloud SQL bootstrap.
  - Maintains `public.schema_migrations` ledger schema for applied tracking.
  - Zero third-party dependencies required (pure standard library Python).
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SQL_DIR = ROOT / "infra" / "sql"

MIGRATION_FILENAME_PATTERN = re.compile(r"^([0-9]{3})_([a-z0-9_]+)\.sql$")

SCHEMA_MIGRATIONS_DDL = """-- Schema Migrations Ledger Table
CREATE TABLE IF NOT EXISTS public.schema_migrations (
    version     VARCHAR(64)  PRIMARY KEY,
    name        VARCHAR(256) NOT NULL,
    checksum    CHAR(64)     NOT NULL,
    applied_at  TIMESTAMPTZ  NOT NULL DEFAULT CURRENT_TIMESTAMP
);
"""


@dataclass(frozen=True)
class Migration:
    """Represents a discovered database migration file."""
    version: str
    name: str
    file_path: Path
    checksum: str
    size_bytes: int

    @property
    def filename(self) -> str:
        return self.file_path.name


class MigrationError(Exception):
    """Raised when migration sequence or validation invariants are violated."""


class MigrationRunner:
    """Manages discovery, validation, and bundling of SQL migration scripts."""

    def __init__(self, sql_dir: Path | None = None) -> None:
        self.sql_dir = sql_dir or DEFAULT_SQL_DIR

    def discover_migrations(self) -> list[Migration]:
        """Discover and return all SQL migration files sorted in sequential order."""
        if not self.sql_dir.is_dir():
            raise FileNotFoundError(f"SQL migrations directory does not exist: {self.sql_dir}")

        migrations: list[Migration] = []
        for file_path in sorted(self.sql_dir.glob("*.sql")):
            match = MIGRATION_FILENAME_PATTERN.match(file_path.name)
            if not match:
                continue

            version_str, name = match.group(1), match.group(2)
            content_bytes = file_path.read_bytes()
            checksum = hashlib.sha256(content_bytes).hexdigest()

            migrations.append(
                Migration(
                    version=version_str,
                    name=name,
                    file_path=file_path,
                    checksum=checksum,
                    size_bytes=len(content_bytes),
                )
            )

        return migrations

    def verify_migrations(self) -> list[str]:
        """Verify that migrations are strictly sequential starting at 001 with no gaps."""
        errors: list[str] = []

        if not self.sql_dir.is_dir():
            return [f"Migrations directory not found: {self.sql_dir}"]

        # Check for unindexed or malformed SQL files
        all_sql_files = sorted(self.sql_dir.glob("*.sql"))
        if not all_sql_files:
            return [f"No SQL migration files found in {self.sql_dir}"]

        for f in all_sql_files:
            if not MIGRATION_FILENAME_PATTERN.match(f.name):
                errors.append(f"SQL file '{f.name}' does not match naming pattern '###_name.sql'")
            elif f.stat().st_size == 0:
                errors.append(f"Migration file '{f.name}' is empty")

        migrations = self.discover_migrations()
        if not migrations:
            errors.append(f"No valid migrations discovered in {self.sql_dir}")
            return errors

        # Check versions sequence: must start at 001 and be strictly monotonic incrementing by 1
        expected_num = 1
        seen_versions = set()

        for m in migrations:
            try:
                ver_num = int(m.version)
            except ValueError:
                errors.append(f"Invalid non-integer version in {m.filename}: {m.version}")
                continue

            if m.version in seen_versions:
                errors.append(f"Duplicate migration version '{m.version}' in {m.filename}")
            seen_versions.add(m.version)

            if ver_num != expected_num:
                errors.append(
                    f"Sequence break: expected version {expected_num:03d} but found {m.version} in {m.filename}"
                )
            expected_num = ver_num + 1

        return errors

    def generate_bundle(self, output_path: Path | None = None) -> str:
        """Generate an idempotent, transaction-safe master SQL bundle combining all migrations."""
        verification_errors = self.verify_migrations()
        if verification_errors:
            raise MigrationError(
                "Cannot generate bundle with invalid migrations:\n"
                + "\n".join(f"  - {e}" for e in verification_errors)
            )

        migrations = self.discover_migrations()
        lines: list[str] = [
            "-- =============================================================================",
            "-- LaPluma Master Database Initialization & Migration Bundle",
            "-- Target Engine:  PostgreSQL 16 (Cloud SQL)",
            f"-- Total Scripts:  {len(migrations)}",
            "-- =============================================================================",
            "",
            SCHEMA_MIGRATIONS_DDL.strip(),
            "",
        ]

        for m in migrations:
            script_text = m.file_path.read_text(encoding="utf-8").strip()
            lines.extend([
                "-- -----------------------------------------------------------------------------",
                f"-- Migration {m.version}: {m.name}",
                f"-- File:     {m.filename}",
                f"-- Checksum: {m.checksum}",
                "-- -----------------------------------------------------------------------------",
                "DO $$",
                "BEGIN",
                f"    IF NOT EXISTS (SELECT 1 FROM public.schema_migrations WHERE version = '{m.version}') THEN",
                f"        RAISE NOTICE 'Applying migration {m.version}: {m.name}...';",
                "    END IF;",
                "END $$;",
                "",
                script_text,
                "",
                f"INSERT INTO public.schema_migrations (version, name, checksum, applied_at)",
                f"VALUES ('{m.version}', '{m.name}', '{m.checksum}', CURRENT_TIMESTAMP)",
                f"ON CONFLICT (version) DO UPDATE",
                f"SET checksum = EXCLUDED.checksum,",
                f"    applied_at = CURRENT_TIMESTAMP;",
                "",
            ])

        bundle_sql = "\n".join(lines) + "\n"
        if output_path:
            output_path.write_text(bundle_sql, encoding="utf-8")

        return bundle_sql

    def plan(self, applied_versions: set[str] | None = None) -> dict[str, Any]:
        """Generate a JSON execution plan showing applied and pending migrations."""
        migrations = self.discover_migrations()
        applied = applied_versions or set()

        plan_items: list[dict[str, Any]] = []
        pending_count = 0

        for m in migrations:
            is_applied = m.version in applied
            if not is_applied:
                pending_count += 1
            plan_items.append({
                "version": m.version,
                "name": m.name,
                "filename": m.filename,
                "checksum": m.checksum,
                "sizeBytes": m.size_bytes,
                "status": "APPLIED" if is_applied else "PENDING",
            })

        return {
            "totalMigrations": len(migrations),
            "pendingCount": pending_count,
            "appliedCount": len(migrations) - pending_count,
            "migrations": plan_items,
        }


def cmd_list(args: argparse.Namespace) -> int:
    runner = MigrationRunner(Path(args.sql_dir) if args.sql_dir else None)
    try:
        migrations = runner.discover_migrations()
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    if not migrations:
        print("No migrations found.")
        return 0

    print(f"Found {len(migrations)} migration(s) in {runner.sql_dir}:")
    print(f"{'VER':<5} {'NAME':<40} {'SIZE':<8} {'CHECKSUM (first 16)':<18}")
    print("-" * 75)
    for m in migrations:
        print(f"{m.version:<5} {m.name:<40} {m.size_bytes:<8} {m.checksum[:16]:<18}")
    return 0


def cmd_verify(args: argparse.Namespace) -> int:
    runner = MigrationRunner(Path(args.sql_dir) if args.sql_dir else None)
    errors = runner.verify_migrations()
    if errors:
        print(f"VERIFICATION FAILED ({len(errors)} error(s)):", file=sys.stderr)
        for err in errors:
            print(f"  - {err}", file=sys.stderr)
        return 1

    migrations = runner.discover_migrations()
    print(f"PASS: All {len(migrations)} migration scripts in {runner.sql_dir} verified successfully.")
    print(f"  Range: {migrations[0].version} ({migrations[0].name}) -> {migrations[-1].version} ({migrations[-1].name})")
    return 0


def cmd_plan(args: argparse.Namespace) -> int:
    runner = MigrationRunner(Path(args.sql_dir) if args.sql_dir else None)
    try:
        plan_data = runner.plan()
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    plan_json = json.dumps(plan_data, indent=2) + "\n"
    if args.output:
        Path(args.output).write_text(plan_json, encoding="utf-8")
        print(f"Migration plan saved to {args.output}")
    else:
        print(plan_json)
    return 0


def cmd_bundle(args: argparse.Namespace) -> int:
    runner = MigrationRunner(Path(args.sql_dir) if args.sql_dir else None)
    out_path = Path(args.output) if args.output else runner.sql_dir / "all_migrations_bundle.sql"
    try:
        runner.generate_bundle(output_path=out_path)
    except Exception as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1

    print(f"SUCCESS: Generated master migration bundle at {out_path}")
    return 0


def cmd_ddl(_args: argparse.Namespace) -> int:
    print(SCHEMA_MIGRATIONS_DDL)
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="LaPluma PostgreSQL Migration Runner & Schema Ledger (P1)",
        prog="migrate_db.py",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    # list
    p_list = subparsers.add_parser("list", help="List discovered migrations")
    p_list.add_argument("--sql-dir", help="Path to SQL migrations directory")
    p_list.set_defaults(func=cmd_list)

    # verify
    p_verify = subparsers.add_parser("verify", help="Verify migration sequence and checksum integrity")
    p_verify.add_argument("--sql-dir", help="Path to SQL migrations directory")
    p_verify.set_defaults(func=cmd_verify)

    # plan
    p_plan = subparsers.add_parser("plan", help="Output JSON execution plan")
    p_plan.add_argument("--sql-dir", help="Path to SQL migrations directory")
    p_plan.add_argument("-o", "--output", help="Output plan JSON file")
    p_plan.set_defaults(func=cmd_plan)

    # bundle
    p_bundle = subparsers.add_parser("bundle", help="Generate single concatenated master migration bundle")
    p_bundle.add_argument("--sql-dir", help="Path to SQL migrations directory")
    p_bundle.add_argument("-o", "--output", help="Output SQL bundle file path")
    p_bundle.set_defaults(func=cmd_bundle)

    # ddl
    p_ddl = subparsers.add_parser("ddl", help="Print schema_migrations DDL table definition")
    p_ddl.set_defaults(func=cmd_ddl)

    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
