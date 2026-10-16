#Requires -Version 7.2
<#
.SYNOPSIS
    One-time, owner-run setup of the identity .github/workflows/deploy-gcp.yml deploys with, for one
    LaPluma environment. Run it with -WhatIf first.

.DESCRIPTION
    Every step reads the live state first and changes only what differs, so a rerun changes nothing
    and says so. -WhatIf reads and reports, and changes nothing. The script never runs Terraform,
    never deploys, and never sets GitHub variables: it prints the `gh variable set` commands for you.

    For -Environment it creates or validates:

    1. APIs: iam, iamcredentials (the auth action impersonates the deployer through it), sts, run,
       artifactregistry.

    2. The GitHub Actions workload identity pool and provider. The provider is pinned to this
       repository's immutable repository_id and repository_owner_id (from `gh api`) and to
       refs/heads/main, with the attribute mapping and condition that Terraform declares in
       infra/terraform/modules/iam and bootstrap/project-factory. Writing the identical values means
       the next plan shows no change to the provider.
       In staging and pilot the pool, provider and deployer account belong to Terraform (module.iam),
       so the script updates them in place but refuses to create them: apply Terraform first. In dev
       they came from bootstrap/project-factory, which has no state in use, so missing ones are created.

    3. The deployer service account (lp-deployer-<env>), and roles/iam.workloadIdentityUser on it for
       this repository's identities in the pool. A binding by repository name (what Terraform grants)
       or by repository_id is accepted; if neither exists, the repository_id one is added.

    4. Least-privilege roles for exactly what deploy-gcp.yml runs:
         docker push <region>-docker.pkg.dev/<project>/lapluma-services-<env>/...
           roles/artifactregistry.writer on that repository only.
           https://docs.cloud.google.com/artifact-registry/docs/docker/pushing-and-pulling
         gcloud run deploy lp-core-api-<env> / lp-wf-api-<env> / lp-proc-worker-<env>
           roles/run.developer on each of those three services only (not the project).
           roles/iam.serviceAccountUser on the runtime service account each service runs as, and on
           nothing else. A revision always runs as its service's identity, so deploying one needs
           iam.serviceAccounts.actAs on it even when the deploy does not name it.
           https://docs.cloud.google.com/run/docs/deploying#required_roles
           https://docs.cloud.google.com/run/docs/reference/iam/roles
         roles/run.viewer on the project: read-only. gcloud reads the new revision and its operation
           while it waits for the rollout, which a service-level grant may not cover.
           staging-power.yml's status action relies on it too.
           https://docs.cloud.google.com/run/docs/reference/iam/roles
       Artifact Registry Reader, which the deploy page also lists, is contained in Writer.

    5. Lists roles the deployer holds beyond these, without removing any, and prints the repository
       variables deploy-gcp.yml reads for the environment.

    Workload identity federation for GitHub Actions, including why the numeric *_id claims are used
    rather than names (a name can be claimed again after a rename or deletion):
    https://docs.cloud.google.com/iam/docs/workload-identity-federation-with-deployment-pipelines

.PARAMETER Environment
    dev, staging or pilot.

.PARAMETER ProjectId
    The environment's GCP project ID (not its number). Staging: project-27d49bcd-4a1d-4d7a-8a7.

.PARAMETER GcloudAccount
    gcloud account to run as, when it is not the active one. It needs to administer IAM in the
    project (roles/owner, or roles/iam.workloadIdentityPoolAdmin, roles/iam.serviceAccountAdmin,
    roles/resourcemanager.projectIamAdmin, roles/run.admin, roles/artifactregistry.admin and
    roles/serviceusage.serviceUsageAdmin).

.EXAMPLE
    ./infra/scripts/setup-deploy-identity.ps1 -Environment staging -ProjectId project-27d49bcd-4a1d-4d7a-8a7 -WhatIf
    ./infra/scripts/setup-deploy-identity.ps1 -Environment staging -ProjectId project-27d49bcd-4a1d-4d7a-8a7
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'staging', 'pilot')]
    [string]$Environment,

    [Parameter(Mandatory)]
    [ValidatePattern('^[a-z][a-z0-9-]{4,28}[a-z0-9]$')]
    [string]$ProjectId,

    [ValidatePattern('^[a-z]+-[a-z]+[0-9]+$')]
    [string]$Region = 'us-central1',

    [ValidatePattern('^[A-Za-z0-9-]+/[A-Za-z0-9._-]+$')]
    [string]$Repository = 'HybridCloudWorks/PEN-lapluma_infra',

    # Read from `gh api repos/<Repository>` when omitted.
    [ValidatePattern('^[0-9]+$')]
    [string]$RepositoryId,

    [ValidatePattern('^[0-9]+$')]
    [string]$RepositoryOwnerId,

    # Defaults: dev uses bootstrap/project-factory's names, staging and pilot modules/iam's.
    [ValidatePattern('^[a-z0-9-]{4,32}$')]
    [string]$PoolId,

    [ValidatePattern('^[a-z0-9-]{4,32}$')]
    [string]$ProviderId,

    [string]$GcloudAccount
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:CLOUDSDK_CORE_DISABLE_PROMPTS = '1'
Set-StrictMode -Version 3.0

$Cmdlet = $PSCmdlet
$IsWhatIf = [bool]$WhatIfPreference
$Issuer = 'https://token.actions.githubusercontent.com'
$Branch = 'main'   # Terraform's condition says refs/heads/main; the two must match.

$TerraformOwned = $Environment -ne 'dev'
if (-not $PoolId) { $PoolId = if ($TerraformOwned) { "gh-pool-$Environment" } else { "pool-github-$Environment" } }
if (-not $ProviderId) { $ProviderId = if ($TerraformOwned) { "gh-provider-$Environment" } else { 'provider-github' } }
$OwnerRoot = if ($TerraformOwned) { 'infra/terraform/modules/iam (module.iam)' } else { 'infra/terraform/bootstrap/project-factory' }
$DeployerAccountId = "lp-deployer-$Environment"
$Deployer = "$DeployerAccountId@$ProjectId.iam.gserviceaccount.com"
$DeployerMember = "serviceAccount:$Deployer"
$ArtifactRepository = "lapluma-services-$Environment"
# The three services deploy-gcp.yml deploys, and the runtime account modules/iam gives each.
$Services = [ordered]@{
    "lp-core-api-$Environment"    = "lp-core-api-$Environment@$ProjectId.iam.gserviceaccount.com"
    "lp-wf-api-$Environment"      = "lp-wf-api-$Environment@$ProjectId.iam.gserviceaccount.com"
    "lp-proc-worker-$Environment" = "lp-proc-worker-$Environment@$ProjectId.iam.gserviceaccount.com"
}
$RequiredApis = @('iam.googleapis.com', 'iamcredentials.googleapis.com', 'sts.googleapis.com', 'run.googleapis.com', 'artifactregistry.googleapis.com')

$Results = [System.Collections.Generic.List[object]]::new()
$Problems = [System.Collections.Generic.List[string]]::new()

# ---------------------------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------------------------
function Write-Section([string]$Title) { Write-Host ''; Write-Host "=== $Title ===" -ForegroundColor Cyan }
function Write-Info([string]$Message) { Write-Host "  $Message" }
function Write-Note([string]$Message) { Write-Host "  [!] $Message" -ForegroundColor Yellow }

function Add-Result([string]$Item, [string]$Status, [string]$Detail = '') {
    $Results.Add([pscustomobject]@{ Item = $Item; Status = $Status; Detail = $Detail })
    $color = switch -Regex ($Status) { '^(ok|kept)$' { 'Green' } '^(created|updated|granted|enabled)$' { 'Cyan' } '^(would|broader|review)' { 'Yellow' } default { 'Red' } }
    Write-Host ("  [{0}] {1}{2}" -f $Status, $Item, $(if ($Detail) { ": $Detail" } else { '' })) -ForegroundColor $color
}

function Add-Problem([string]$Item, [string]$Message) {
    $Problems.Add("${Item}: $Message")
    Add-Result $Item 'PROBLEM' $Message
}

# ---------------------------------------------------------------------------------------------
# Native commands
# ---------------------------------------------------------------------------------------------
$NativeExe = @{}
function Resolve-NativeExe([string]$Name) {
    # The Cloud SDK also ships gcloud.ps1, which PowerShell prefers over gcloud.cmd and which
    # inherits -WhatIf. Always run the real executable.
    $found = @(Get-Command $Name -CommandType Application -All -ErrorAction SilentlyContinue |
            Where-Object { -not $IsWindows -or $_.Extension -in @('.exe', '.cmd', '.bat') }) | Select-Object -First 1
    if ($found) { return $found.Source }
    return $null
}

function Invoke-Native {
    param([Parameter(Mandatory)][string]$Exe, [string[]]$Arguments = @(), [switch]$AllowFailure)
    $WhatIfPreference = $false   # changes are gated by ShouldProcess, never by a child process
    if (-not $NativeExe.ContainsKey($Exe)) { $NativeExe[$Exe] = Resolve-NativeExe $Exe }
    if (-not $NativeExe[$Exe]) { throw "$Exe is not installed or not on PATH." }
    $all = & $NativeExe[$Exe] @Arguments 2>&1
    $code = $LASTEXITCODE
    $out = @($all | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    $err = @($all | Where-Object { $_ -is [System.Management.Automation.ErrorRecord] } | ForEach-Object { "$_" })
    if ($code -ne 0 -and -not $AllowFailure) {
        $tail = (@($err + $out) | Where-Object { $_.Trim() } | Select-Object -Last 6) -join [Environment]::NewLine
        throw "$Exe $(@($Arguments | Select-Object -First 5) -join ' ') failed (exit $code).$([Environment]::NewLine)$tail"
    }
    return [pscustomobject]@{ ExitCode = $code; Out = ($out -join "`n"); Err = ($err -join "`n") }
}

function Invoke-Gcloud([string[]]$Arguments, [switch]$AllowFailure) {
    $extra = @('--quiet')
    if ($GcloudAccount) { $extra += "--account=$GcloudAccount" }
    return Invoke-Native -Exe 'gcloud' -Arguments (@($Arguments) + $extra) -AllowFailure:$AllowFailure
}

function Get-GcloudJson([string[]]$Arguments) {
    # $null when the resource does not exist; the caller decides what that means. Any other failure
    # (permission denied, an API that is off, a bad flag) stops the script with gcloud's message,
    # rather than being mistaken for "missing" and answered with a create.
    $r = Invoke-Gcloud -Arguments (@($Arguments) + '--format=json') -AllowFailure
    if ($r.ExitCode -eq 0) {
        if (-not $r.Out.Trim()) { return $null }
        return ($r.Out | ConvertFrom-Json -Depth 64)
    }
    $message = "$($r.Err)`n$($r.Out)"
    if ($message -match 'NOT_FOUND|not found|Cannot find|does not exist') { return $null }
    $tail = (@($message -split "`n" | Where-Object { $_.Trim() }) | Select-Object -Last 6) -join [Environment]::NewLine
    throw "gcloud $(@($Arguments | Select-Object -First 5) -join ' ') failed (exit $($r.ExitCode)).$([Environment]::NewLine)$tail"
}

function Get-Prop($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $p = $Object.PSObject.Properties[$Name]
    if ($p) { return $p.Value }
    return $null
}

function Test-Binding($Policy, [string]$Role, [string]$Member) {
    foreach ($b in @(Get-Prop $Policy 'bindings')) {
        if ($null -eq $b -or $b.role -ne $Role -or (Get-Prop $b 'condition')) { continue }
        if (@(Get-Prop $b 'members') -ccontains $Member) { return $true }
    }
    return $false
}

function Get-MemberRole($Policy, [string]$Member) {
    return @(@(Get-Prop $Policy 'bindings') | Where-Object { $_ -and (@(Get-Prop $_ 'members') -ccontains $Member) } | ForEach-Object { $_.role })
}

function Grant-Role {
    # Adds one binding unless it is already there. $AddArguments is the gcloud add-iam-policy-binding
    # command without --member and --role.
    param([string]$Item, $Policy, [string]$Role, [string]$Member, [string[]]$AddArguments)
    if (Test-Binding $Policy $Role $Member) { Add-Result $Item 'ok' "$Role already granted"; return }
    if ($Cmdlet.ShouldProcess($Item, "Grant $Role to $Member")) {
        Invoke-Gcloud (@($AddArguments) + "--member=$Member" + "--role=$Role") | Out-Null
        Add-Result $Item 'granted' $Role
    }
    else { Add-Result $Item 'would grant' $Role }
}

# ---------------------------------------------------------------------------------------------
# What the provider must say: identical to Terraform's attribute_mapping and attribute_condition.
# ---------------------------------------------------------------------------------------------
function Get-TerraformDefault([string]$File, [string]$Variable) {
    $path = Join-Path $PSScriptRoot "../terraform/$File"
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $m = [regex]::Match((Get-Content -LiteralPath $path -Raw), "(?s)variable\s+""$Variable""\s*\{.*?default\s*=\s*""([^""]*)""")
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

$DesiredMapping = [ordered]@{
    'google.subject'                = 'assertion.sub'
    'attribute.actor'               = 'assertion.actor'
    'attribute.repository'          = 'assertion.repository'
    'attribute.repository_id'       = 'assertion.repository_id'
    'attribute.repository_owner_id' = 'assertion.repository_owner_id'
    'attribute.ref'                 = 'assertion.ref'
}

function Test-ProviderMapping($Live) {
    if ($null -eq $Live) { return $false }
    $names = @($Live.PSObject.Properties | ForEach-Object { $_.Name })
    if ($names.Count -ne $DesiredMapping.Count) { return $false }
    foreach ($key in $DesiredMapping.Keys) {
        if ((Get-Prop $Live $key) -cne $DesiredMapping[$key]) { return $false }
    }
    return $true
}

# =============================================================================================
# Main
# =============================================================================================
$exitCode = 0
try {
    Write-Host ("LaPluma deploy identity | environment {0} | project {1} | region {2} | {3}" -f $Environment, $ProjectId, $Region, $(if ($IsWhatIf) { 'WHATIF: no changes' } else { 'LIVE' })) -ForegroundColor White

    # --- Preflight ------------------------------------------------------------------------
    Write-Section 'Preflight'
    if (-not (Resolve-NativeExe 'gcloud')) { throw 'gcloud is not on PATH.' }
    $haveGh = [bool](Resolve-NativeExe 'gh')

    if (-not $RepositoryId -or -not $RepositoryOwnerId) {
        if (-not $haveGh) { throw 'gh is not on PATH; pass -RepositoryId and -RepositoryOwnerId (gh api repos/<owner>/<repo> --jq ''.id, .owner.id'').' }
        $ids = @((Invoke-Native gh @('api', "repos/$Repository", '--jq', '.id, .owner.id')).Out -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($ids.Count -ne 2 -or $ids[0] -notmatch '^[0-9]+$' -or $ids[1] -notmatch '^[0-9]+$') { throw "Could not read the numeric IDs of $Repository from gh api." }
        if (-not $RepositoryId) { $RepositoryId = $ids[0] }
        if (-not $RepositoryOwnerId) { $RepositoryOwnerId = $ids[1] }
    }
    Add-Result "GitHub $Repository" 'ok' "repository_id $RepositoryId, repository_owner_id $RepositoryOwnerId"

    $tfFile = if ($TerraformOwned) { 'modules/iam/variables.tf' } else { 'bootstrap/project-factory/variables.tf' }
    foreach ($pair in @(@('github_repository_id', $RepositoryId), @('github_repository_owner_id', $RepositoryOwnerId))) {
        $declared = Get-TerraformDefault $tfFile $pair[0]
        if ($null -eq $declared) { Write-Note "Could not read $($pair[0]) from infra/terraform/$tfFile next to this script; not compared." }
        elseif ($declared -ne $pair[1]) { Add-Problem "Terraform $($pair[0])" "infra/terraform/$tfFile declares $declared, GitHub says $($pair[1]). Fix the module first, or the next plan reverts the provider." }
    }
    $DesiredCondition = "assertion.repository_id == '$RepositoryId' && assertion.repository_owner_id == '$RepositoryOwnerId' && assertion.ref == 'refs/heads/$Branch'"

    $project = Get-GcloudJson @('projects', 'describe', $ProjectId)
    if (-not $project) { throw "Cannot read project $ProjectId. Check the ID and your gcloud login$(if ($GcloudAccount) { " ($GcloudAccount)" })." }
    if ($project.lifecycleState -ne 'ACTIVE') { throw "Project $ProjectId is $($project.lifecycleState)." }
    $ProjectNumber = [string]$project.projectNumber
    Add-Result "project $ProjectId" 'ok' "number $ProjectNumber, ACTIVE"
    $PoolName = "projects/$ProjectNumber/locations/global/workloadIdentityPools/$PoolId"
    $ProviderName = "$PoolName/providers/$ProviderId"
    if ($Problems.Count) { throw "$($Problems.Count) problem(s) in preflight; nothing has been changed." }

    # --- 1. APIs ----------------------------------------------------------------------------
    Write-Section '1. APIs'
    $enabled = @((Invoke-Gcloud @('services', 'list', '--enabled', "--project=$ProjectId", '--format=value(config.name)')).Out -split "`n" | ForEach-Object { $_.Trim() })
    $missingApis = @($RequiredApis | Where-Object { $_ -notin $enabled })
    foreach ($api in $RequiredApis) { if ($api -in $enabled) { Add-Result $api 'ok' 'enabled' } }
    if ($missingApis) {
        if ($Cmdlet.ShouldProcess($ProjectId, "Enable $($missingApis -join ', ')")) {
            Invoke-Gcloud (@('services', 'enable') + $missingApis + "--project=$ProjectId") | Out-Null
            foreach ($api in $missingApis) { Add-Result $api 'enabled' }
        }
        else { foreach ($api in $missingApis) { Add-Result $api 'would enable' } }
    }

    # --- 2. Workload identity pool and provider ---------------------------------------------
    Write-Section "2. Workload identity provider (declared in $OwnerRoot)"
    $pool = Get-GcloudJson @('iam', 'workload-identity-pools', 'describe', $PoolId, '--location=global', "--project=$ProjectId")
    $poolReady = $false
    if (-not $pool) {
        if ($TerraformOwned) { Add-Problem "pool $PoolId" "not found. Terraform declares it ($OwnerRoot); apply Terraform for $Environment first, then rerun." }
        elseif ($Cmdlet.ShouldProcess($PoolName, 'Create workload identity pool')) {
            Invoke-Gcloud @('iam', 'workload-identity-pools', 'create', $PoolId, '--location=global', "--project=$ProjectId",
                "--display-name=GitHub Actions Pool - $Environment", '--description=OIDC federation identity pool for GitHub Actions CI/CD') | Out-Null
            Add-Result "pool $PoolId" 'created'
            $poolReady = $true
        }
        else { Add-Result "pool $PoolId" 'would create' }
    }
    elseif ((Get-Prop $pool 'state') -ne 'ACTIVE' -or (Get-Prop $pool 'disabled')) {
        Add-Problem "pool $PoolId" "state $(Get-Prop $pool 'state'), disabled=$(Get-Prop $pool 'disabled'). Restore or enable it first (gcloud iam workload-identity-pools undelete / update --no-disabled)."
    }
    else { Add-Result "pool $PoolId" 'ok' 'ACTIVE'; $poolReady = $true }

    $mappingArg = ($DesiredMapping.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }) -join ','
    if ($poolReady -or -not $pool) {
        $provider = if ($pool) { Get-GcloudJson @('iam', 'workload-identity-pools', 'providers', 'describe', $ProviderId, '--location=global', "--workload-identity-pool=$PoolId", "--project=$ProjectId") } else { $null }
        if (-not $provider) {
            if ($TerraformOwned) { if ($pool) { Add-Problem "provider $ProviderId" "not found. Terraform declares it ($OwnerRoot); apply Terraform first, then rerun." } }
            elseif ($Cmdlet.ShouldProcess($ProviderName, "Create OIDC provider for $Issuer, condition: $DesiredCondition")) {
                Invoke-Gcloud @('iam', 'workload-identity-pools', 'providers', 'create-oidc', $ProviderId, '--location=global', "--workload-identity-pool=$PoolId",
                    "--project=$ProjectId", '--display-name=GitHub Actions Provider', "--issuer-uri=$Issuer",
                    "--attribute-mapping=$mappingArg", "--attribute-condition=$DesiredCondition") | Out-Null
                Add-Result "provider $ProviderId" 'created' $DesiredCondition
            }
            else { Add-Result "provider $ProviderId" 'would create' $DesiredCondition }
        }
        elseif ((Get-Prop $provider 'state') -ne 'ACTIVE' -or (Get-Prop $provider 'disabled')) {
            Add-Problem "provider $ProviderId" "state $(Get-Prop $provider 'state'), disabled=$(Get-Prop $provider 'disabled'). Restore or enable it first."
        }
        else {
            $oidc = Get-Prop $provider 'oidc'
            $liveIssuer = Get-Prop $oidc 'issuerUri'
            $audiences = @(Get-Prop $oidc 'allowedAudiences' | Where-Object { $_ })
            $liveCondition = [string](Get-Prop $provider 'attributeCondition')
            $mappingOk = Test-ProviderMapping (Get-Prop $provider 'attributeMapping')
            $conditionOk = $liveCondition -ceq $DesiredCondition
            $issuerOk = $liveIssuer -eq $Issuer
            if ($audiences) { Write-Note "Provider has allowedAudiences ($($audiences -join ', ')); google-github-actions/auth sends the provider's default audience. Left as is." }
            if ($mappingOk -and $conditionOk -and $issuerOk) {
                Add-Result "provider $ProviderId" 'ok' 'pinned to repository_id, repository_owner_id and main'
            }
            else {
                $what = @()
                if (-not $issuerOk) { $what += "issuer $liveIssuer -> $Issuer" }
                if (-not $mappingOk) { $what += 'attribute mapping (adds repository_id, repository_owner_id)' }
                if (-not $conditionOk) { $what += "condition '$liveCondition' -> '$DesiredCondition'" }
                $update = @('iam', 'workload-identity-pools', 'providers', 'update-oidc', $ProviderId, '--location=global', "--workload-identity-pool=$PoolId",
                    "--project=$ProjectId", "--attribute-mapping=$mappingArg", "--attribute-condition=$DesiredCondition")
                if (-not $issuerOk) { $update += "--issuer-uri=$Issuer" }
                if ($Cmdlet.ShouldProcess($ProviderName, "Update $($what -join '; ')")) {
                    Invoke-Gcloud $update | Out-Null
                    Add-Result "provider $ProviderId" 'updated' ($what -join '; ')
                }
                else { Add-Result "provider $ProviderId" 'would update' ($what -join '; ') }
                if ($TerraformOwned) { Write-Info 'This matches what Terraform now declares, so the next plan shows no change to the provider.' }
            }
        }
    }

    # --- 3. Deployer service account --------------------------------------------------------
    Write-Section "3. Deployer service account $Deployer"
    $sa = Get-GcloudJson @('iam', 'service-accounts', 'describe', $Deployer, "--project=$ProjectId")
    $saReady = $false
    if (-not $sa) {
        if ($TerraformOwned) { Add-Problem $Deployer "not found. Terraform declares it ($OwnerRoot); apply Terraform first, then rerun." }
        elseif ($Cmdlet.ShouldProcess($Deployer, 'Create service account')) {
            Invoke-Gcloud @('iam', 'service-accounts', 'create', $DeployerAccountId, "--project=$ProjectId",
                "--display-name=LaPluma Deployment SA ($Environment)", '--description=CI/CD deployment runner authenticated via GitHub OIDC federation') | Out-Null
            Add-Result $Deployer 'created'
            $saReady = $true
        }
        else { Add-Result $Deployer 'would create' }
    }
    elseif (Get-Prop $sa 'disabled') { Add-Problem $Deployer 'is disabled. Enable it first: gcloud iam service-accounts enable.' }
    else { Add-Result $Deployer 'ok' 'exists'; $saReady = $true }

    $byName = "principalSet://iam.googleapis.com/$PoolName/attribute.repository/$Repository"
    $byId = "principalSet://iam.googleapis.com/$PoolName/attribute.repository_id/$RepositoryId"
    if ($saReady) {
        $saPolicy = if ($sa) { Get-GcloudJson @('iam', 'service-accounts', 'get-iam-policy', $Deployer, "--project=$ProjectId") } else { $null }
        $wifRole = 'roles/iam.workloadIdentityUser'
        if ((Test-Binding $saPolicy $wifRole $byName) -or (Test-Binding $saPolicy $wifRole $byId)) {
            Add-Result "$wifRole on $DeployerAccountId" 'ok' "granted to $Repository's identities in $PoolId"
        }
        else {
            Grant-Role "$wifRole on $DeployerAccountId" $saPolicy $wifRole $byId @('iam', 'service-accounts', 'add-iam-policy-binding', $Deployer, "--project=$ProjectId", '--condition=None')
        }
        $others = @(@(Get-Prop $saPolicy 'bindings') | Where-Object { $_ -and $_.role -eq $wifRole } | ForEach-Object { @(Get-Prop $_ 'members') } |
                Where-Object { $_ -cne $byName -and $_ -cne $byId })
        foreach ($m in $others) { Write-Note "$DeployerAccountId can also be impersonated by $m. Remove it if it is not intended." }
    }

    # --- 4. Least-privilege roles for deploy-gcp.yml ----------------------------------------
    Write-Section '4. Roles for build, push and deploy'
    if (-not $saReady -and -not $IsWhatIf) {
        Add-Problem 'roles' "skipped: $Deployer does not exist yet."
    }
    else {
        # 4a. Push images: Artifact Registry Writer on the one repository the workflow pushes to.
        $repo = Get-GcloudJson @('artifacts', 'repositories', 'describe', $ArtifactRepository, "--location=$Region", "--project=$ProjectId")
        if (-not $repo) { Add-Problem "repository $ArtifactRepository" "not found in $Region. $(if ($TerraformOwned) { 'Terraform creates it (module.compute).' } else { 'bootstrap/project-factory creates it.' })" }
        else {
            $repoPolicy = Get-GcloudJson @('artifacts', 'repositories', 'get-iam-policy', $ArtifactRepository, "--location=$Region", "--project=$ProjectId")
            Grant-Role "repository $ArtifactRepository" $repoPolicy 'roles/artifactregistry.writer' $DeployerMember @('artifacts', 'repositories', 'add-iam-policy-binding', $ArtifactRepository, "--location=$Region", "--project=$ProjectId")
        }

        # 4b. Deploy: Cloud Run Developer on each service, Service Account User on its runtime account.
        $defaultCompute = "$ProjectNumber-compute@developer.gserviceaccount.com"
        $runtimeAccounts = [ordered]@{}
        foreach ($svc in $Services.Keys) {
            $service = Get-GcloudJson @('run', 'services', 'describe', $svc, "--region=$Region", "--project=$ProjectId")
            if (-not $service) {
                Add-Problem "service $svc" "not found in $Region. Terraform creates it (module.compute); rerun this script once it exists."
                continue
            }
            $runtime = [string](Get-Prop (Get-Prop (Get-Prop (Get-Prop $service 'spec') 'template') 'spec') 'serviceAccountName')
            if (-not $runtime) { $runtime = $defaultCompute }
            if ($runtime -ne $Services[$svc]) {
                Write-Note "$svc runs as $runtime, not $($Services[$svc]) as modules/iam declares$(if ($runtime -eq $defaultCompute) { ' (the default compute account, usually Editor: deploying it means the deployer can act with that account''s access)' })."
            }
            $runtimeAccounts[$runtime] = $true
            $servicePolicy = Get-GcloudJson @('run', 'services', 'get-iam-policy', $svc, "--region=$Region", "--project=$ProjectId")
            Grant-Role "service $svc" $servicePolicy 'roles/run.developer' $DeployerMember @('run', 'services', 'add-iam-policy-binding', $svc, "--region=$Region", "--project=$ProjectId")
        }
        foreach ($runtime in $runtimeAccounts.Keys) {
            $runtimePolicy = Get-GcloudJson @('iam', 'service-accounts', 'get-iam-policy', $runtime, "--project=$ProjectId")
            if ($null -eq $runtimePolicy) {
                Add-Problem "runtime account $runtime" 'not found.'
                continue
            }
            Grant-Role "runtime account $runtime" $runtimePolicy 'roles/iam.serviceAccountUser' $DeployerMember @('iam', 'service-accounts', 'add-iam-policy-binding', $runtime, "--project=$ProjectId", '--condition=None')
        }

        # 4c. Read-only project access so gcloud can follow the rollout.
        $projectPolicy = Get-GcloudJson @('projects', 'get-iam-policy', $ProjectId)
        Grant-Role "project $ProjectId" $projectPolicy 'roles/run.viewer' $DeployerMember @('projects', 'add-iam-policy-binding', $ProjectId, '--condition=None')

        # --- 5. Roles beyond what the workflow needs (reported, never removed) -------------------
        Write-Section '5. Other project roles held by the deployer'
        $needed = @('roles/run.viewer')
        $usedElsewhere = @{
            'roles/cloudsql.admin'    = 'used by staging-power.yml (infra/scripts/manage-environment.ps1 pause/resume)'
            'roles/apigateway.viewer' = 'used by staging-power.yml status'
        }
        $broader = @{
            'roles/owner'                   = 'far beyond deploying'
            'roles/editor'                  = 'far beyond deploying'
            'roles/run.admin'               = 'can also change Cloud Run IAM; the service-level run.developer grants above cover the deploy'
            'roles/run.developer'           = 'project-wide; the service-level grants above cover the three services'
            'roles/iam.serviceAccountUser'  = 'project-wide: can act as every service account in the project, not only the runtime ones'
            'roles/artifactregistry.writer' = 'project-wide; the repository-level grant above covers the push'
            'roles/artifactregistry.admin'  = 'project-wide; the repository-level writer grant above covers the push'
        }
        $extra = @(Get-MemberRole $projectPolicy $DeployerMember | Where-Object { $_ -notin $needed } | Sort-Object -Unique)
        if (-not $extra) { Add-Result 'extra roles' 'ok' 'none' }
        foreach ($role in $extra) {
            if ($usedElsewhere.ContainsKey($role)) { Add-Result "project role $role" 'kept' $usedElsewhere[$role] }
            elseif ($broader.ContainsKey($role)) {
                Add-Result "project role $role" 'broader' $broader[$role]
                Write-Info "    Once a deploy has succeeded without it: gcloud projects remove-iam-policy-binding $ProjectId --member=$DeployerMember --role=$role --condition=None"
            }
            else { Add-Result "project role $role" 'review' 'not needed by deploy-gcp.yml' }
        }
    }

    # --- Repository variables ---------------------------------------------------------------
    Write-Section 'Repository variables for deploy-gcp.yml (run these yourself)'
    $suffix = $Environment.ToUpperInvariant()
    $variables = [ordered]@{
        "GCP_PROJECT_$suffix"      = $ProjectId
        "GCP_REGION_$suffix"       = $Region
        "GCP_WIF_PROVIDER_$suffix" = $ProviderName
        "GCP_DEPLOYER_SA_$suffix"  = $Deployer
    }
    foreach ($name in $variables.Keys) {
        $state = ''
        if ($haveGh) {
            $current = Invoke-Native gh @('variable', 'get', $name, '--repo', $Repository) -AllowFailure
            $state = if ($current.ExitCode -ne 0) { '   # not set (or gh cannot read it)' } elseif ($current.Out.Trim() -eq $variables[$name]) { '   # already set to this value' } else { "   # currently '$($current.Out.Trim())'" }
        }
        Write-Host ("  gh variable set {0} --repo {1} --body ""{2}""{3}" -f $name, $Repository, $variables[$name], $state)
    }
    Write-Info 'deploy-gcp.yml stops before authenticating, naming the variable, while any of these is missing.'

    if ($Problems.Count) { $exitCode = 1 }
}
catch {
    $exitCode = 1
    Write-Host ''
    Write-Host "STOPPED: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Write-Section 'Summary'
    $Results | Format-Table Status, Item, Detail -AutoSize -Wrap | Out-Host
    if ($Problems.Count) { Write-Host "$($Problems.Count) problem(s); see above." -ForegroundColor Red }
    if ($IsWhatIf) { Write-Host 'WhatIf: nothing was changed.' -ForegroundColor Yellow }
}
exit $exitCode
