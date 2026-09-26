# Runbook: Staging Environment Power & Cost Management

This runbook establishes operational procedures and automated governance for the **LaPluma Staging Environment** on Google Cloud Platform (`project-27d49bcd-4a1d-4d7a-8a7`).

---

## 1. Cost Governance Overview

Under **ADR-019 (Lean GCP Architecture)**, staging operating costs must remain strictly under **$100/mo**.

* **Microservices (Cloud Run):** Configured with `min_instance_count = 0` (scales to zero when idle = **$0.00**).
* **Storage, Pub/Sub, API Gateway, Artifact Registry:** Free tier coverage = **$0.00**.
* **Cloud SQL (`lp-postgres-staging-9f5d4205`):**
  * When **Running:** `~$27.25 / month` (~$0.93 / day).
  * When **Paused:** `~$1.70 / month` (~$0.05 / day, SSD storage only).
  * Pausing when not actively testing saves ~94% of infrastructure costs.

---

## 2. Power Management Operations

### CLI Management Script: `infra/scripts/manage-environment.ps1`

Run from the repository root:

```powershell
# 1. Check current live status (Cloud SQL, Cloud Run, API Gateway)
.\infra\scripts\manage-environment.ps1 -Action status

# 2. Resume before E2E testing (starts Cloud SQL and polls until RUNNABLE)
.\infra\scripts\manage-environment.ps1 -Action resume

# 3. Pause after E2E testing (stops Cloud SQL compute billing)
.\infra\scripts\manage-environment.ps1 -Action pause
```

### GitHub Actions Workflow: `.github/workflows/staging-power.yml`
* Trigger manually from GitHub UI: **Actions** -> **Staging Environment Power Management** -> **Run workflow**.
* Input dropdown: `status`, `pause`, `resume`.
* Authenticates via keyless OIDC Workload Identity Federation (`gh-pool-staging`).

---

## 3. Pre-Flight Verification & E2E Testing Protocol

### Before Running Live E2E Tests:
1. Always run `./infra/scripts/manage-environment.ps1 -Action status`.
2. If Cloud SQL is `STOPPED`, run `./infra/scripts/manage-environment.ps1 -Action resume`.
3. Verify API Gateway health:
   ```powershell
   Invoke-RestMethod -Uri "https://lp-gateway-staging-am9yq93d.uc.gateway.dev/auth/saml/metadata"
   ```
4. Execute test suite.

### After Concluding Development / Testing:
1. Prompt or execute `./infra/scripts/manage-environment.ps1 -Action pause`.
2. Verify state is `STOPPED`.
