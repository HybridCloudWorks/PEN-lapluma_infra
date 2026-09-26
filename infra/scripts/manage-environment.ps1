<#
.SYNOPSIS
    Manages power state (pause/resume/status) for LaPluma Staging Environment on GCP.

.DESCRIPTION
    Pausing the environment stops the billable Cloud SQL PostgreSQL instance
    while keeping all data and disk intact, reducing hourly run rate to ~$0.05/day.
    Cloud Run services automatically scale to zero when idle.

.PARAMETER Action
    pause  - Stops the Cloud SQL instance to eliminate compute billing.
    resume - Starts the Cloud SQL instance and verifies readiness.
    status - Inspects current state of Cloud SQL, Cloud Run, and API Gateway.

.EXAMPLE
    .\manage-environment.ps1 -Action status
    .\manage-environment.ps1 -Action pause
    .\manage-environment.ps1 -Action resume
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet("pause", "resume", "status")]
    [string]$Action,

    [Parameter(Mandatory = $false)]
    [string]$ProjectId = "project-27d49bcd-4a1d-4d7a-8a7",

    [Parameter(Mandatory = $false)]
    [string]$SqlInstance = "lp-postgres-staging-9f5d4205",

    [Parameter(Mandatory = $false)]
    [string]$Region = "us-central1"
)

$ErrorActionPreference = "Stop"

function Get-SqlStatus {
    try {
        $json = gcloud sql instances describe $SqlInstance --project=$ProjectId --format="json" 2>$null | ConvertFrom-Json
        return @{
            State = $json.state
            ActivationPolicy = $json.settings.activationPolicy
            IpAddress = ($json.ipAddresses | Where-Object { $_.type -eq "PRIMARY" }).ipAddress
            PrivateIp = ($json.ipAddresses | Where-Object { $_.type -eq "PRIVATE" }).ipAddress
        }
    } catch {
        Write-Error "Failed to query Cloud SQL instance: $_"
        return $null
    }
}

function Get-CloudRunStatus {
    $services = @("lp-core-api-staging", "lp-wf-api-staging", "lp-proc-worker-staging")
    $results = @()
    foreach ($svc in $services) {
        $ready = gcloud run services describe $svc --region=$Region --project=$ProjectId --format="value(status.conditions[0].status)" 2>$null
        $results += [PSCustomObject]@{
            Service = $svc
            Ready = $ready
        }
    }
    return $results
}

switch ($Action) {
    "status" {
        Write-Host "=================================================" -ForegroundColor Cyan
        Write-Host " LaPluma Staging Environment Status ($ProjectId)" -ForegroundColor Cyan
        Write-Host "=================================================" -ForegroundColor Cyan

        $sql = Get-SqlStatus
        if ($sql) {
            $stateColor = if ($sql.State -eq "RUNNABLE") { "Green" } else { "Yellow" }
            Write-Host "Cloud SQL Instance:    $SqlInstance"
            Write-Host "  State:               $($sql.State)" -ForegroundColor $stateColor
            Write-Host "  Activation Policy:   $($sql.ActivationPolicy)"
            Write-Host "  Primary IP:          $($sql.IpAddress)"
            Write-Host "  Private IP:          $($sql.PrivateIp)"
        }

        Write-Host "`nCloud Run Services (Auto-scale to Zero):"
        $runServices = Get-CloudRunStatus
        foreach ($r in $runServices) {
            $sColor = if ($r.Ready -eq "True") { "Green" } else { "Red" }
            Write-Host "  $($r.Service): Ready=$($r.Ready)" -ForegroundColor $sColor
        }

        Write-Host "`nAPI Gateway:"
        $gwHost = gcloud api-gateway gateways describe lp-gateway-staging --location=$Region --project=$ProjectId --format="value(defaultHostname)" 2>$null
        if ($gwHost) {
            Write-Host "  Hostname: https://$gwHost" -ForegroundColor Green
        }
        Write-Host "=================================================" -ForegroundColor Cyan
    }

    "pause" {
        Write-Host "Pausing staging environment..." -ForegroundColor Yellow
        $sql = Get-SqlStatus
        if ($sql.ActivationPolicy -eq "NEVER" -and $sql.State -ne "RUNNABLE") {
            Write-Host "Cloud SQL is already paused ($($sql.State))." -ForegroundColor Green
            return
        }

        Write-Host "Setting Cloud SQL activation-policy to NEVER..." -ForegroundColor Yellow
        gcloud sql instances patch $SqlInstance --activation-policy=NEVER --project=$ProjectId --quiet

        Write-Host "Verifying shutdown..."
        Start-Sleep -Seconds 5
        $sql = Get-SqlStatus
        Write-Host "Cloud SQL State is now: $($sql.State) (Policy: $($sql.ActivationPolicy))" -ForegroundColor Green
        Write-Host "`n[Cost Savings]: Compute charges have stopped. Current cost is ~$0.05/day (SSD storage only)." -ForegroundColor Cyan
    }

    "resume" {
        Write-Host "Resuming staging environment..." -ForegroundColor Yellow
        Write-Host "Setting Cloud SQL activation-policy to ALWAYS..." -ForegroundColor Yellow
        gcloud sql instances patch $SqlInstance --activation-policy=ALWAYS --project=$ProjectId --quiet

        Write-Host "Waiting for database to reach RUNNABLE state..." -ForegroundColor Yellow
        $maxAttempts = 30
        $attempt = 0
        do {
            Start-Sleep -Seconds 10
            $attempt++
            $sql = Get-SqlStatus
            Write-Host "[$attempt/$maxAttempts] Current state: $($sql.State)..."
        } while ($sql.State -ne "RUNNABLE" -and $attempt -lt $maxAttempts)

        if ($sql.State -eq "RUNNABLE") {
            Write-Host "Cloud SQL is fully operational and ready!" -ForegroundColor Green
        } else {
            Write-Warning "Cloud SQL state is $($sql.State). Please check Google Cloud Console."
        }
    }
}
