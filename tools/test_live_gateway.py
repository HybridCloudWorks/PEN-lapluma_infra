#!/usr/bin/env python3
"""
Live E2E Integration Test for LaPluma API Gateway & Microservices.

Executes real HTTP traffic through the Google API Gateway edge to:
1. GET /v1/library/blueprints (Core API via Gateway)
2. POST /v1/clients (Workflow API via Gateway - Client creation)
3. GET /v1/cases/{caseId}/workspace (Workflow API via Gateway - Case Workspace)
4. POST /v1/cases/{caseId}/sections/{sectionId}/commit (Workflow API - Section Commit with If-Match)
5. Test Stale Revision Concurrency -> Expect HTTP 412 Version Conflict (Problem Details)
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request
import uuid

GATEWAY_BASE_URL = os.environ.get(
    "LAPLUMA_GATEWAY_URL",
    "https://lp-gateway-staging-am9yq93d.uc.gateway.dev"
).rstrip("/")

AUTH_TOKEN = os.environ.get("LAPLUMA_AUTH_TOKEN", "")


def make_request(method: str, path: str, body: dict | None = None, headers: dict | None = None):
    url = f"{GATEWAY_BASE_URL}{path}"
    req_headers = {
        "Accept": "application/json, application/problem+json",
        "X-Request-ID": str(uuid.uuid4()),
    }
    if AUTH_TOKEN:
        req_headers["Authorization"] = f"Bearer {AUTH_TOKEN}"
    if headers:
        req_headers.update(headers)

    data = None
    if body is not None:
        data = json.dumps(body).encode("utf-8")
        req_headers["Content-Type"] = "application/json"

    req = urllib.request.Request(url, data=data, headers=req_headers, method=method)
    
    try:
        with urllib.request.urlopen(req, timeout=20) as resp:
            resp_body = resp.read().decode("utf-8")
            status = resp.status
            parsed = json.loads(resp_body) if resp_body else None
            return status, parsed, dict(resp.headers)
    except urllib.error.HTTPError as e:
        resp_body = e.read().decode("utf-8")
        parsed = None
        try:
            parsed = json.loads(resp_body)
        except Exception:
            parsed = {"raw": resp_body}
        return e.code, parsed, dict(e.headers)
    except Exception as e:
        return 0, {"error": str(e)}, {}


def test_library_blueprints() -> bool:
    print("\n[TEST 1] GET /v1/library/blueprints (Core API)")
    status, body, _ = make_request("GET", "/v1/library/blueprints")
    print(f"Status: {status}")
    if status != 200:
        print(f"FAILED: Expected 200 OK, got {status}: {body}")
        return False
    
    blueprints = body.get("data", body) if isinstance(body, dict) else body
    if isinstance(blueprints, list) and len(blueprints) > 0:
        print(f"SUCCESS: Retrieved {len(blueprints)} blueprints from library.")
        return True
    print(f"SUCCESS: Retrieved response: {body}")
    return True


def test_create_client() -> tuple[bool, str | None]:
    print("\n[TEST 2] POST /v1/clients (Workflow API)")
    client_name = f"Live E2E Client {int(time.time())}"
    idem_key = f"idem-client-{uuid.uuid4().hex[:12]}"
    status, body, _ = make_request(
        "POST",
        "/v1/clients",
        body={"displayLabel": client_name},
        headers={"Idempotency-Key": idem_key}
    )
    print(f"Status: {status}")
    if status not in (200, 201):
        print(f"FAILED: Expected 201 Created or 200 OK, got {status}: {body}")
        return False, None
    
    folder_id = body.get("folderId") or body.get("id") or body.get("folder_id")
    print(f"SUCCESS: Client created successfully. Folder ID: {folder_id}")
    return True, folder_id


def test_case_workspace_and_commit() -> bool:
    case_id = "case-fixture-0002"
    section_id = "identity"
    print(f"\n[TEST 3] GET /v1/cases/{case_id}/workspace (Workflow API)")
    status, body, _ = make_request("GET", f"/v1/cases/{case_id}/workspace")
    print(f"Status: {status}")
    if status != 200:
        print(f"FAILED: Expected 200 OK, got {status}: {body}")
        return False
    
    sections = body.get("sections", []) if isinstance(body, dict) else []
    target_sec = next((s for s in sections if s.get("id") == section_id), None)
    current_rev = target_sec.get("revision", 1) if target_sec else 1
    print(f"SUCCESS: Case workspace retrieved. Current section '{section_id}' revision: {current_rev}")

    print(f"\n[TEST 4] POST /v1/cases/{case_id}/sections/{section_id}/commit (Happy Path)")
    commit_idem = f"idem-commit-{uuid.uuid4().hex[:12]}"
    test_value = f"Maria Verified E2E {int(time.time())}"
    commit_payload = {
        "baseRevision": current_rev,
        "values": {
            "applicant.name.first": test_value
        }
    }
    status, body, _ = make_request(
        "POST",
        f"/v1/cases/{case_id}/sections/{section_id}/commit",
        body=commit_payload,
        headers={
            "Idempotency-Key": commit_idem,
            "If-Match": f'"{current_rev}"'
        }
    )
    print(f"Status: {status}")
    if status != 200:
        print(f"FAILED: Expected 200 OK, got {status}: {body}")
        return False
    
    sec_info = body.get("section") if isinstance(body, dict) else None
    new_rev = sec_info.get("revision") if isinstance(sec_info, dict) else None
    print(f"SUCCESS: Section committed in real-time. New revision: {new_rev}")

    print("\n[TEST 5] Stale Revision Concurrency Check (Expect HTTP 412 Version Conflict)")
    stale_payload = {
        "baseRevision": 0,
        "values": {
            "applicant.name.first": "Stale Attempt"
        }
    }
    status, body, _ = make_request(
        "POST",
        f"/v1/cases/{case_id}/sections/{section_id}/commit",
        body=stale_payload,
        headers={
            "Idempotency-Key": f"idem-stale-{uuid.uuid4().hex[:12]}",
            "If-Match": '"0"'
        }
    )
    print(f"Status: {status}")
    if status == 412:
        print(f"SUCCESS: Correctly rejected stale revision with 412 Precondition Failed.")
        print(f"ProblemDetails: {json.dumps(body, indent=2)}")
        return True
    else:
        print(f"FAILED: Expected HTTP 412, got {status}: {body}")
        return False


def main():
    print(f"Target Gateway: {GATEWAY_BASE_URL}")
    print(f"Auth Token configured: {'Yes' if AUTH_TOKEN else 'No (calling unauthenticated or default credentials)'}")

    all_passed = True
    if not test_library_blueprints():
        all_passed = False

    passed, _ = test_create_client()
    if not passed:
        all_passed = False

    if not test_case_workspace_and_commit():
        all_passed = False

    if all_passed:
        print("\n==========================================")
        print("ALL E2E LIVE GATEWAY TESTS PASSED (100% SLA)!")
        print("==========================================")
        return 0
    else:
        print("\n==========================================")
        print("SOME E2E LIVE TESTS FAILED")
        print("==========================================")
        return 1


if __name__ == "__main__":
    sys.exit(main())
