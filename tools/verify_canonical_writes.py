"""
Verification script for Canonical Case Writes, Section Commits, Conflicts & Approval Invalidation (INT-04).

Validates:
1. Schema Invariants: workflow.case_field_value, workflow.case_approval, and workflow.outbox_event tables.
2. Workflow API Invariants: BaseRevision/If-Match optimistic concurrency, 412 version-conflict, 409 idempotency conflict.
3. Postgres Workflow Source Invariants: Atomic transaction upserting canonical values, invalidating approvals, and writing to outbox.
4. Fixture Source Invariants: In-memory revision checking, idempotency replay/conflict detection, and review reopening.
5. Test Coverage: CanonicalCaseWritesTests covers happy path, stale revision 412, idempotency replay, idempotency conflict 409, and If-Match header override.
"""
from __future__ import annotations

import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
SQL_SCHEMA = ROOT / "infra" / "sql" / "002_workflow_persistence_schema.sql"
PROGRAM_CS = ROOT / "src" / "workflow-api" / "Program.cs"
PG_SOURCE_CS = ROOT / "src" / "workflow-api" / "PostgresWorkflowSource.cs"
FIXTURE_SOURCE_CS = ROOT / "src" / "workflow-api" / "WorkflowFixtureSource.cs"
MODELS_CS = ROOT / "src" / "workflow-api" / "WorkflowModels.cs"
TESTS_CS = ROOT / "src" / "workflow-api.tests" / "CanonicalCaseWritesTests.cs"


def check_schema_invariants() -> list[str]:
    errors: list[str] = []
    if not SQL_SCHEMA.exists():
        return [f"Missing SQL schema file at {SQL_SCHEMA}"]

    text = SQL_SCHEMA.read_text(encoding="utf-8")

    if "CREATE TABLE workflow.case_field_value" not in text:
        errors.append("Missing workflow.case_field_value table definition")
    if "is_human_confirmed" not in text:
        errors.append("workflow.case_field_value missing is_human_confirmed column")
    if "source_kind" not in text or "'MANUAL_ENTRY'" not in text:
        errors.append("workflow.case_field_value missing source_kind with MANUAL_ENTRY support")
    if "PRIMARY KEY (case_id, canonical_path)" not in text:
        errors.append("workflow.case_field_value missing composite primary key (case_id, canonical_path)")

    if "CREATE TABLE workflow.case_approval" not in text:
        errors.append("Missing workflow.case_approval table definition")
    if "is_invalidated" not in text or "invalidation_reason" not in text:
        errors.append("workflow.case_approval missing invalidation tracking columns")

    if "CREATE TABLE workflow.outbox_event" not in text:
        errors.append("Missing workflow.outbox_event table definition")

    return errors


def check_workflow_api_invariants() -> list[str]:
    errors: list[str] = []
    if not PROGRAM_CS.exists():
        return [f"Missing Program.cs at {PROGRAM_CS}"]

    text = PROGRAM_CS.read_text(encoding="utf-8")

    if "/cases/{caseId}/sections/{sectionId}/commit" not in text:
        errors.append("Program.cs missing section commit endpoint mapping")
    if "If-Match" not in text:
        errors.append("Program.cs missing If-Match header handling for optimistic concurrency")
    if "version-conflict" not in text or "412" not in text:
        errors.append("Program.cs missing 412 version-conflict problem response")
    if "idempotency-key-conflict" not in text or "409" not in text:
        errors.append("Program.cs missing idempotency-key-conflict 409 response")

    return errors


def check_workflow_sources() -> list[str]:
    errors: list[str] = []
    if not PG_SOURCE_CS.exists():
        errors.append(f"Missing PostgresWorkflowSource.cs at {PG_SOURCE_CS}")
    else:
        pg_text = PG_SOURCE_CS.read_text(encoding="utf-8")
        if "CommitSectionAsync" not in pg_text:
            errors.append("PostgresWorkflowSource missing CommitSectionAsync implementation")
        if "workflow.case_field_value" not in pg_text:
            errors.append("PostgresWorkflowSource does not persist to workflow.case_field_value")
        if "is_invalidated = TRUE" not in pg_text:
            errors.append("PostgresWorkflowSource does not invalidate approvals on material change")
        if "SECTION_COMMITTED" not in pg_text:
            errors.append("PostgresWorkflowSource does not emit SECTION_COMMITTED outbox event")
        if "VersionConflict" not in pg_text:
            errors.append("PostgresWorkflowSource missing VersionConflict status handling")

    if not FIXTURE_SOURCE_CS.exists():
        errors.append(f"Missing WorkflowFixtureSource.cs at {FIXTURE_SOURCE_CS}")
    else:
        fix_text = FIXTURE_SOURCE_CS.read_text(encoding="utf-8")
        if "CommitSectionAsync" not in fix_text:
            errors.append("WorkflowFixtureSource missing CommitSectionAsync implementation")
        if "VersionConflict" not in fix_text:
            errors.append("WorkflowFixtureSource missing VersionConflict check")
        if "Conflict" not in fix_text:
            errors.append("WorkflowFixtureSource missing Conflict check for mutated idempotency payload")
        if "reopen" not in fix_text or "invalidate" not in fix_text:
            errors.append("WorkflowFixtureSource missing review reopening / approval invalidation logic")

    if not MODELS_CS.exists():
        errors.append(f"Missing WorkflowModels.cs at {MODELS_CS}")
    else:
        models_text = MODELS_CS.read_text(encoding="utf-8")
        if "record SectionCommit" not in models_text:
            errors.append("WorkflowModels missing SectionCommit record")
        if "ReopenedReview" not in models_text or "InvalidatedApproval" not in models_text:
            errors.append("WorkflowModels SectionCommit missing ReopenedReview or InvalidatedApproval")

    return errors


def check_tests_coverage() -> list[str]:
    errors: list[str] = []
    if not TESTS_CS.exists():
        return [f"Missing CanonicalCaseWritesTests.cs at {TESTS_CS}"]

    text = TESTS_CS.read_text(encoding="utf-8")
    required_tests = [
        "CommitSection_ValidRevision_SucceedsAndIncrementsRevision",
        "CommitSection_StaleRevision_Returns412VersionConflict",
        "CommitSection_IdempotencyReplay_ReturnsSameResult",
        "CommitSection_IdempotencyConflict_Returns409",
        "CommitSection_IfMatchHeader_OverridesBaseRevision",
    ]
    for test in required_tests:
        if test not in text:
            errors.append(f"CanonicalCaseWritesTests missing required test: {test}")

    return errors


def main() -> int:
    failures: list[str] = [
        *check_schema_invariants(),
        *check_workflow_api_invariants(),
        *check_workflow_sources(),
        *check_tests_coverage(),
    ]
    if failures:
        for failure in failures:
            print(f"ERROR: {failure}", file=sys.stderr)
        return 1
    print("Canonical case writes validation passed (INT-04).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
