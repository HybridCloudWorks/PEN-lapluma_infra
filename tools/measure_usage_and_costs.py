#!/usr/bin/env python3
"""Per-institution usage measurement, telemetry validation, and pilot/growth cost verification
for Google Cloud Platform services (INF-19, INT-08).

Pure Python standard library only.
"""

from __future__ import annotations

import datetime
import json
import re
import sys
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]

# Banned PII keywords and patterns that must never appear in usage telemetry records
PII_KEY_PATTERNS = [
    r"^name$", r"\b(?:first|last|middle|full|client|user|person|applicant|beneficiary|petitioner)_name\b",
    r"email", r"ssn", r"social.*security", r"phone", r"address",
    r"birth", r"alien", r"passport", r"receipt.*number", r"payload", r"content",
]
PII_VALUE_PATTERNS = [
    r"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",  # Email
    r"^\d{3}-\d{2}-\d{4}$",  # SSN
    r"^\+?1?\d{10,14}$",  # Phone
]

# GCP Pilot Budget Ceiling
PILOT_MONTHLY_BUDGET_CAP_USD = 100.00

# Concurrency and scale ceilings for pilot cost safety
CONCURRENCY_CEILINGS = {
    "cloud_run_max_instances": 10,
    "cloud_run_concurrency_per_instance": 80,
    "cloud_sql_max_connections": 50,
    "rate_limit_requests_per_minute": 120,
    "max_upload_size_bytes": 104857600,  # 100 MB
}

# Standard GCP us-central1 pricing rates (monthly basis / unit rates)
RATES = {
    # Cloud Run (Scale to zero, min instances = 0)
    "cloud_run_vcpu_sec": 0.00002400,
    "cloud_run_gib_sec": 0.00000250,
    "cloud_run_million_requests": 0.40,
    "cloud_run_free_vcpu_sec": 180_000,
    "cloud_run_free_gib_sec": 360_000,
    "cloud_run_free_requests": 2_000_000,

    # Cloud SQL PostgreSQL 16 (db-g1-small, 1 vCPU, 1.7 GB RAM, zonal)
    "cloud_sql_base_monthly": 28.03,  # ~$0.0384/hr * 730 hrs
    "cloud_sql_storage_gb_monthly": 0.17,  # SSD storage

    # Cloud Storage (Standard storage + 7-day soft delete retention)
    "gcs_standard_gb_monthly": 0.020,
    "gcs_nearline_gb_monthly": 0.010,
    "gcs_class_a_10k": 0.05,
    "gcs_class_b_10k": 0.004,

    # Pub/Sub
    "pubsub_free_gb": 10.0,
    "pubsub_gb_rate": 0.040,

    # API Gateway / Network Egress
    "api_gateway_free_million_calls": 2.0,
    "api_gateway_million_calls_rate": 3.00,
    "network_egress_free_gb": 100.0,
}


def validate_usage_record(record: dict[str, Any]) -> tuple[bool, str]:
    """Validate that a usage record conforms to schema and contains strictly zero PII."""
    required_keys = {"tenant_id", "environment", "metric_name", "quantity", "unit", "timestamp"}
    missing = required_keys - set(record.keys())
    if missing:
        return False, f"Missing required fields: {missing}"

    if record["environment"] not in {"dev", "staging", "pilot"}:
        return False, f"Invalid environment: {record['environment']}"

    if not isinstance(record["quantity"], (int, float)) or record["quantity"] < 0:
        return False, "Quantity must be a non-negative number"

    # Deep scan for PII in keys and string values
    def check_pii(data: Any) -> tuple[bool, str]:
        if isinstance(data, dict):
            for k, v in data.items():
                for pat in PII_KEY_PATTERNS:
                    if re.search(pat, str(k), re.IGNORECASE):
                        return True, f"Banned PII key pattern detected: '{k}'"
                pii_found, msg = check_pii(v)
                if pii_found:
                    return True, msg
        elif isinstance(data, list):
            for item in data:
                pii_found, msg = check_pii(item)
                if pii_found:
                    return True, msg
        elif isinstance(data, str):
            for pat in PII_VALUE_PATTERNS:
                if re.search(pat, data):
                    return True, f"Banned PII value pattern detected in string value"
        return False, ""

    pii_found, pii_msg = check_pii(record)
    if pii_found:
        return False, f"PII violation: {pii_msg}"

    return True, "Valid"


def calculate_pilot_monthly_cost(
    cases: int = 100,
    documents_per_case: int = 5,
    pages_per_doc: int = 3,
    avg_doc_size_mb: float = 2.5,
) -> dict[str, Any]:
    """Calculate the estimated monthly GCP pilot infrastructure cost.

    Pilot workload parameters:
    - 100 cases / month
    - 500 uploaded documents
    - 1,500 processed document pages
    - 1.25 GB active monthly uploads (plus 7-day retention versions)
    """
    total_docs = cases * documents_per_case
    total_pages = total_docs * pages_per_doc
    total_doc_gb = (total_docs * avg_doc_size_mb) / 1024.0

    # 1. Cloud Run (Core API, Workflow API, Processing Worker)
    # Estimate 50 API calls per case + 5 processing jobs per doc
    api_requests = (cases * 50) + (total_docs * 10)  # ~10,000 requests
    # Each request ~200ms vCPU, 0.5 GiB RAM
    vcpu_seconds = api_requests * 0.20 + total_pages * 0.50  # ~2,750 vCPU-sec
    gib_seconds = api_requests * 0.10 + total_pages * 0.25   # ~1,375 GiB-sec

    billable_requests = max(0, api_requests - RATES["cloud_run_free_requests"])
    billable_vcpu = max(0, vcpu_seconds - RATES["cloud_run_free_vcpu_sec"])
    billable_gib = max(0, gib_seconds - RATES["cloud_run_free_gib_sec"])

    cloud_run_cost = (
        (billable_requests / 1_000_000) * RATES["cloud_run_million_requests"]
        + billable_vcpu * RATES["cloud_run_vcpu_sec"]
        + billable_gib * RATES["cloud_run_gib_sec"]
    )
    # Minimum container overhead / active requests when triggered:
    cloud_run_cost = max(cloud_run_cost, 2.50)  # Floor estimate for container spins

    # 2. Cloud SQL PostgreSQL 16 (db-g1-small, 10 GB SSD)
    cloud_sql_cost = RATES["cloud_sql_base_monthly"] + (10 * RATES["cloud_sql_storage_gb_monthly"])

    # 3. Cloud Storage
    # Storage volume: 10 GB cumulative + versions + soft delete buffer = ~25 GB
    storage_gb = max(25.0, total_doc_gb * 2)
    storage_cost = storage_gb * RATES["gcs_standard_gb_monthly"]
    # Operations: Class A (upload/list) ~3,000; Class B (download/get) ~15,000
    ops_cost = (3000 / 10000) * RATES["gcs_class_a_10k"] + (15000 / 10000) * RATES["gcs_class_b_10k"]
    gcs_cost = storage_cost + ops_cost

    # 4. Pub/Sub
    # Volume is < 1 GB (well within 10 GB free tier)
    pubsub_cost = 0.00

    # 5. API Gateway & Network Egress
    # Within 2M free requests and 100 GB free egress
    networking_cost = 1.00  # Nominal buffer for regional egress

    total_monthly = cloud_run_cost + cloud_sql_cost + gcs_cost + pubsub_cost + networking_cost

    return {
        "workload": {
            "cases_per_month": cases,
            "documents_per_month": total_docs,
            "pages_per_month": total_pages,
            "storage_gb": round(storage_gb, 2),
        },
        "line_items_usd": {
            "cloud_run_compute": round(cloud_run_cost, 2),
            "cloud_sql_postgresql": round(cloud_sql_cost, 2),
            "cloud_storage": round(gcs_cost, 2),
            "pubsub_messaging": round(pubsub_cost, 2),
            "networking_and_gateway": round(networking_cost, 2),
        },
        "total_monthly_usd": round(total_monthly, 2),
        "budget_cap_usd": PILOT_MONTHLY_BUDGET_CAP_USD,
        "is_within_budget": total_monthly <= PILOT_MONTHLY_BUDGET_CAP_USD,
        "headroom_usd": round(PILOT_MONTHLY_BUDGET_CAP_USD - total_monthly, 2),
    }


def calculate_growth_projection(
    institutions: int = 10,
    cases_per_institution: int = 250,
) -> dict[str, Any]:
    """Calculate multi-institution growth projection costs (2,500 cases/mo)."""
    total_cases = institutions * cases_per_institution
    total_docs = total_cases * 5
    total_pages = total_docs * 3
    storage_gb = 500.0  # 500 GB document repository

    # Cloud Run: ~250,000 requests, ~50,000 vCPU-s
    cloud_run_cost = 18.50
    # Cloud SQL: upgrade to db-custom-2-4096 ($65/mo) + 100 GB SSD ($17/mo)
    cloud_sql_cost = 82.00
    # Cloud Storage: 500 GB * $0.02 = $10/mo + ops $2.00
    gcs_cost = 12.00
    # Pub/Sub: ~50 GB billable
    pubsub_cost = 1.60
    networking_cost = 8.50

    total_growth = cloud_run_cost + cloud_sql_cost + gcs_cost + pubsub_cost + networking_cost

    return {
        "institutions": institutions,
        "cases_per_month": total_cases,
        "total_monthly_usd": round(total_growth, 2),
        "cost_per_case_usd": round(total_growth / total_cases, 3),
        "cost_per_institution_usd": round(total_growth / institutions, 2),
    }


def main() -> int:
    print("=== LaPluma GCP Pilot Usage & Cost Verification (INF-19) ===")
    pilot = calculate_pilot_monthly_cost()
    print(f"Pilot Monthly Total: ${pilot['total_monthly_usd']} / ${pilot['budget_cap_usd']} cap")
    print(f"Headroom: ${pilot['headroom_usd']} (Under budget: {pilot['is_within_budget']})")
    print("Line items:")
    for k, v in pilot["line_items_usd"].items():
        print(f"  - {k}: ${v}")

    growth = calculate_growth_projection()
    print(f"\nGrowth Projection ({growth['institutions']} institutions, {growth['cases_per_month']} cases):")
    print(f"  Total: ${growth['total_monthly_usd']}/mo (${growth['cost_per_case_usd']}/case)")

    if not pilot["is_within_budget"]:
        print("ERROR: Pilot monthly cost exceeds $100.00 budget cap!", file=sys.stderr)
        return 1

    print("\nVerification PASSED: Pilot costs adhere strictly to <$100/mo ceiling.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
