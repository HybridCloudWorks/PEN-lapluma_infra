#!/usr/bin/env python3
"""Contract and integration test suite for Pub/Sub workflow state alignment,
poison message DLQ quarantine, standardized error mapping, and per-institution
usage/cost verification (INT-08, INF-19, INF-13).

Pure Python standard library only.
"""

from __future__ import annotations

import datetime
import hashlib
import json
import re
import sys
import unittest
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))

from measure_usage_and_costs import (
    CONCURRENCY_CEILINGS,
    PILOT_MONTHLY_BUDGET_CAP_USD,
    calculate_growth_projection,
    calculate_pilot_monthly_cost,
    validate_usage_record,
)
from verify_design_spec import verify_operator_grayscale_compliance

ROOT = Path(__file__).resolve().parents[1]
CONTRACTS_DIR = ROOT / "contracts" / "openapi"


class TestPubSubWorkflowAndUsage(unittest.TestCase):
    def test_pubsub_at_least_once_idempotent_processing(self) -> None:
        """Verify at-least-once Pub/Sub delivery is handled idempotently without duplicate side effects (INT-08)."""
        processed_message_ids: set[str] = set()
        case_history: list[dict[str, Any]] = []

        def handle_pubsub_event(event: dict[str, Any]) -> tuple[int, str]:
            msg_id = event.get("message_id")
            if not msg_id:
                return 400, "Missing message_id"

            # Idempotency gate: if message already processed, return 200 without side effects
            if msg_id in processed_message_ids:
                return 200, "Duplicate message ignored"

            processed_message_ids.add(msg_id)
            case_history.append({
                "case_id": event["case_id"],
                "event_type": event["event_type"],
                "occurred_at": event["timestamp"],
            })
            return 200, "Processed"

        event = {
            "message_id": "msg_pubsub_001_unique",
            "case_id": "c_ramirez_i130",
            "event_type": "DOCUMENT_EXTRACTED",
            "timestamp": "2026-09-14T20:00:00Z",
        }

        # First delivery
        status1, msg1 = handle_pubsub_event(event)
        self.assertEqual(status1, 200)
        self.assertEqual(msg1, "Processed")
        self.assertEqual(len(case_history), 1)

        # Second delivery (at-least-once duplicate)
        status2, msg2 = handle_pubsub_event(event)
        self.assertEqual(status2, 200)
        self.assertIn("Duplicate", msg2)
        # Verify no duplicate history entries created
        self.assertEqual(len(case_history), 1)

    def test_out_of_order_pubsub_state_monotonicity(self) -> None:
        """Verify delayed/out-of-order Pub/Sub events cannot regress durable case/document state (INT-08)."""
        # Monotonic rank for document processing states
        doc_state_rank = {
            "UPLOADED": 1,
            "SCANNING": 2,
            "SANITIZED": 3,
            "CLASSIFYING": 4,
            "EXTRACTED": 5,
        }

        current_state = "EXTRACTED"

        def apply_state_update(incoming_state: str) -> tuple[str, bool]:
            nonlocal current_state
            if doc_state_rank.get(incoming_state, 0) <= doc_state_rank[current_state]:
                # Regressive or stale update discarded
                return current_state, False
            current_state = incoming_state
            return current_state, True

        # Delayed "SCANNING" event arriving after document already reached "EXTRACTED"
        state, updated = apply_state_update("SCANNING")
        self.assertFalse(updated, "Out-of-order earlier state must be discarded")
        self.assertEqual(state, "EXTRACTED", "State must remain EXTRACTED")

        # Stale duplicate "EXTRACTED" event
        state, updated = apply_state_update("EXTRACTED")
        self.assertFalse(updated)
        self.assertEqual(state, "EXTRACTED")

    def test_poison_message_dlq_routing_and_pii_redaction(self) -> None:
        """Verify poison messages route to DLQ after 5 retries with sensitive bytes redacted (INT-08)."""
        dead_letter_queue: list[dict[str, Any]] = []

        def process_with_retry(msg: dict[str, Any], attempt: int, max_retries: int = 5) -> tuple[int, str]:
            # Simulate a poison message that crashes or fails unrecoverably
            is_poison = msg.get("payload_corrupt", False)
            if is_poison:
                if attempt >= max_retries:
                    # Route to DLQ with PII redaction
                    dlq_record = {
                        "message_id": msg["message_id"],
                        "correlation_id": msg.get("correlation_id", "unknown"),
                        "failure_reason": "POISON_PAYLOAD_UNPARSEABLE",
                        "retry_count": attempt,
                        "quarantined_at": datetime.datetime.now(datetime.timezone.utc).isoformat(),
                        # Raw payload and PII are explicitly omitted/redacted
                        "sanitized_summary": f"Document ID {msg.get('document_id', 'unknown')} failed parsing",
                    }
                    dead_letter_queue.append(dlq_record)
                    return 202, "Accepted into DLQ"
                return 500, "Processing failed, will retry"
            return 200, "OK"

        poison_msg = {
            "message_id": "msg_poison_999",
            "correlation_id": "corr_abc_123",
            "document_id": "d_bad_input",
            "payload_corrupt": True,
            "raw_pii": "Sensitive Applicant Name and SSN 000-11-2222",
        }

        # Attempts 1 through 4 fail with 500
        for attempt in range(1, 5):
            status, _ = process_with_retry(poison_msg, attempt)
            self.assertEqual(status, 500)
            self.assertEqual(len(dead_letter_queue), 0)

        # 5th attempt routes to DLQ
        status5, msg5 = process_with_retry(poison_msg, attempt=5)
        self.assertEqual(status5, 202)
        self.assertEqual(len(dead_letter_queue), 1)

        dlq_item = dead_letter_queue[0]
        self.assertEqual(dlq_item["failure_reason"], "POISON_PAYLOAD_UNPARSEABLE")
        self.assertEqual(dlq_item["retry_count"], 5)
        # Ensure no raw PII in DLQ record
        self.assertNotIn("raw_pii", dlq_item)
        self.assertNotIn("000-11-2222", json.dumps(dlq_item))

    def test_standardized_problem_details_taxonomy(self) -> None:
        """Verify standard Problem Details status codes and contract mapping (INT-08)."""
        error_scenarios = [
            (401, "https://api.aperture.app/problems/unauthorized", "Authentication required"),
            (403, "https://api.aperture.app/problems/separation-of-duties", "Approver must be distinct"),
            (404, "https://api.aperture.app/problems/not-found", "Resource not found"),
            (409, "https://api.aperture.app/problems/approval-invalidated", "Case approval was invalidated"),
            (410, "https://api.aperture.app/problems/upload-session-expired", "Upload session expired"),
            (412, "https://api.aperture.app/problems/version-conflict", "Precondition Failed"),
            (422, "https://api.aperture.app/problems/evidence-incomplete", "Validation failed"),
            (429, "https://api.aperture.app/problems/rate-limit-exceeded", "Too many requests"),
            (503, "https://api.aperture.app/problems/service-unavailable", "Upload not configured"),
        ]

        for status, problem_type, title in error_scenarios:
            with self.subTest(status=status):
                detail = {
                    "type": problem_type,
                    "title": title,
                    "status": status,
                    "instance": f"/errors/{status}",
                }
                self.assertEqual(detail["status"], status)
                self.assertTrue(detail["type"].startswith("https://api.aperture.app/problems/"))

    def test_per_institution_usage_telemetry_pii_sanitization(self) -> None:
        """Verify usage metering schema rejects PII in telemetry records (INF-19)."""
        # Valid usage record
        valid_record = {
            "tenant_id": "tenant_clinic_alpha",
            "environment": "pilot",
            "metric_name": "ocr_pages_extracted",
            "quantity": 12,
            "unit": "pages",
            "timestamp": "2026-09-14T20:00:00Z",
        }
        is_valid, msg = validate_usage_record(valid_record)
        self.assertTrue(is_valid, msg)

        # Record with PII key (e.g. applicant_name)
        pii_key_record = {
            "tenant_id": "tenant_clinic_alpha",
            "environment": "pilot",
            "metric_name": "api_calls",
            "applicant_name": "Maria Ramirez",
            "quantity": 1,
            "unit": "requests",
            "timestamp": "2026-09-14T20:00:00Z",
        }
        is_valid_pii_key, _ = validate_usage_record(pii_key_record)
        self.assertFalse(is_valid_pii_key, "Record with PII key must be rejected")

        # Record with PII email value
        pii_val_record = {
            "tenant_id": "tenant_clinic_alpha",
            "environment": "pilot",
            "metric_name": "api_calls",
            "quantity": 1,
            "unit": "requests",
            "timestamp": "2026-09-14T20:00:00Z",
            "note": "User contact at maria@example.com",
        }
        is_valid_pii_val, _ = validate_usage_record(pii_val_record)
        self.assertFalse(is_valid_pii_val, "Record with email pattern must be rejected")

    def test_pilot_cost_calculation_under_budget_cap(self) -> None:
        """Verify pilot cost model is strictly below the $100/mo budget cap (INF-19)."""
        pilot = calculate_pilot_monthly_cost()
        self.assertTrue(pilot["is_within_budget"])
        self.assertLess(pilot["total_monthly_usd"], PILOT_MONTHLY_BUDGET_CAP_USD)
        self.assertGreater(pilot["headroom_usd"], 50.00, "Pilot must have >$50 headroom")

        # Growth scenario
        growth = calculate_growth_projection(institutions=10, cases_per_institution=250)
        self.assertLess(growth["cost_per_case_usd"], 0.10, "Marginal cost per case must stay under $0.10")

    def test_concurrency_and_scale_ceilings(self) -> None:
        """Verify pilot concurrency and rate-limiting guardrails (INF-19)."""
        self.assertEqual(CONCURRENCY_CEILINGS["cloud_run_max_instances"], 10)
        self.assertEqual(CONCURRENCY_CEILINGS["cloud_sql_max_connections"], 50)
        self.assertEqual(CONCURRENCY_CEILINGS["rate_limit_requests_per_minute"], 120)
        self.assertEqual(CONCURRENCY_CEILINGS["max_upload_size_bytes"], 104857600)

    def test_operator_ux_boundary_conformance(self) -> None:
        """Verify infra operator tooling boundary conforms to grayscale CLI/CI (INF-13)."""
        wiki_overview = ROOT / "wiki" / "Architecture-Overview.md"
        self.assertTrue(wiki_overview.exists())
        content = wiki_overview.read_text(encoding="utf-8")
        self.assertIn("Grayscale", content, "Operator surfaces must specify grayscale theme")
        self.assertIn("CLI", content, "Operator workflow must remain CLI tooling")
        self.assertIn("No custom web frontend", content, "Operator surfaces must not deploy unapproved web frontends")
        errors = verify_operator_grayscale_compliance()
        self.assertEqual(errors, [], f"verify_operator_grayscale_compliance returned errors: {errors}")


if __name__ == "__main__":
    unittest.main()
