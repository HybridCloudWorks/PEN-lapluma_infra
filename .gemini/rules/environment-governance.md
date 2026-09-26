# Rule: Environment Power & Cost Governance Protocol

## Context
LaPluma Staging Environment runs in GCP (`project-27d49bcd-4a1d-4d7a-8a7`) under strict cost governance (ADR-019, <$100/mo).
Cloud SQL PostgreSQL (`lp-postgres-staging-9f5d4205`) can be paused when idle to reduce the run rate to ~$0.05/day.

## Agent Protocol

### 1. Pre-Execution Gate (Before E2E / Live Cloud Testing):
* **ALWAYS** check the environment status before running tests against live GCP staging:
  ```powershell
  .\infra\scripts\manage-environment.ps1 -Action status
  ```
* If Cloud SQL state is `STOPPED`:
  * Automatically resume the database:
    ```powershell
    .\infra\scripts\manage-environment.ps1 -Action resume
    ```
  * Verify readiness before sending API traffic.

### 2. Post-Execution Gate (After E2E / Testing Sessions Conclude):
* When the user's active testing or task phase is complete, **ALWAYS** prompt or pause the environment:
  ```powershell
  .\infra\scripts\manage-environment.ps1 -Action pause
  ```
* Verify state returns to `STOPPED` and confirm cost savings to the user.

### 3. Tooling Reference:
* Management Script: `infra/scripts/manage-environment.ps1` (`-Action status|pause|resume`)
* GitHub Actions: `.github/workflows/staging-power.yml`
* Runbook: `docs/runbooks/environment-power-and-cost-management.md`
