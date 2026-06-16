#Requires -Modules Az.Accounts, Az.Websites, Az.Functions
<#
.SYNOPSIS
    Build, deploy and test the func-eldk-webhook Azure Function App.

.USAGE
    # Build zip from source + deploy + test
    .\Deploy-FuncEldk.ps1 -Action BuildAndDeploy

    # Build zip only (no deploy)
    .\Deploy-FuncEldk.ps1 -Action Build

    # Deploy existing zip
    .\Deploy-FuncEldk.ps1 -Action Deploy

    # Test all endpoints
    .\Deploy-FuncEldk.ps1 -Action Test

    # Stream live logs
    .\Deploy-FuncEldk.ps1 -Action Logs

    # Check which functions are registered
    .\Deploy-FuncEldk.ps1 -Action Status

.SOURCE FOLDER STRUCTURE
    func-eldk-source\
        host.json
        profile.ps1
        requirements.psd1
        health-probe\
            function.json
            run.ps1
        webhook-customer\
            function.json
            run.ps1
        webhook-contact\
            function.json
            run.ps1
        webhook-syncorders\
            function.json
            run.ps1
#>

param(
    [ValidateSet("Build", "BuildAndDeploy", "Deploy", "Test", "Logs", "Status")]
    [string]$Action = "BuildAndDeploy",

    [string]$SourceDir = "$PSScriptRoot\func-eldk-source",
    [string]$ZipPath   = "$PSScriptRoot\func-eldk-automation.zip"
)

# ─── Config (operator config via env vars — no IDs committed) ───────────────────
# Subscription id is an Azure identifier; supply it at runtime, do not hard-code it.
$SubscriptionId    = $env:ELDK_FUNC_SUBSCRIPTION_ID
if ([string]::IsNullOrWhiteSpace($SubscriptionId)) {
    throw "Set `$env:ELDK_FUNC_SUBSCRIPTION_ID to the target Azure subscription id (operator config)."
}
$ResourceGroup     = if ($env:ELDK_FUNC_RESOURCE_GROUP) { $env:ELDK_FUNC_RESOURCE_GROUP } else { "rg-eldk-automation" }
$FunctionAppName   = if ($env:ELDK_FUNC_APP_NAME)       { $env:ELDK_FUNC_APP_NAME }       else { "func-eldk-webhook" }
# The Function App's public host + Kudu (SCM) host are the generated *.azurewebsites.net
# names. Supply via env; the defaults derive from the app name (override if your slot uses
# a generated suffix).
$FunctionAppHost   = if ($env:ELDK_FUNC_HOST)     { $env:ELDK_FUNC_HOST }     else { "https://$FunctionAppName.azurewebsites.net" }
$ScmHost           = if ($env:ELDK_FUNC_SCM_HOST) { $env:ELDK_FUNC_SCM_HOST } else { "https://$FunctionAppName.scm.azurewebsites.net" }
$ExpectedFunctions = @("health-probe", "webhook-customer", "webhook-contact", "webhook-syncorders")

$RequiredSourceFiles = @(
    "host.json",
    "profile.ps1",
    "health-probe\function.json",
    "health-probe\run.ps1",
    "webhook-customer\function.json",
    "webhook-customer\run.ps1",
    "webhook-contact\function.json",
    "webhook-contact\run.ps1",
    "webhook-syncorders\function.json",
    "webhook-syncorders\run.ps1"
)

# ─── Helpers ──────────────────────────────────────────────────────────────────
function Write-Step { param([string]$m) Write-Host ""; Write-Host "── $m" -ForegroundColor Cyan }
function Write-OK   { param([string]$m) Write-Host "  [OK]   $m" -ForegroundColor Green }
function Write-Warn { param([string]$m) Write-Host "  [WARN] $m" -ForegroundColor Yellow }
function Write-Fail { param([string]$m) Write-Host "  [FAIL] $m" -ForegroundColor Red }

function Get-PublishCredentials {
    $profile = Get-AzWebAppPublishingProfile `
        -ResourceGroupName $ResourceGroup `
        -Name              $FunctionAppName `
        -ErrorAction       Stop
    [xml]$xml   = $profile
    $user       = $xml.publishData.publishProfile[0].userName
    $pass       = $xml.publishData.publishProfile[0].userPWD
    $base64     = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("${user}:${pass}"))
    return @{ Authorization = "Basic $base64" }
}

# ─── Login ────────────────────────────────────────────────────────────────────
function Invoke-Login {
    Write-Step "Checking Azure login..."
    $ctx = Get-AzContext -ErrorAction SilentlyContinue
    if (-not $ctx -or $ctx.Subscription.Id -ne $SubscriptionId) {
        Write-Warn "Not logged in — connecting..."
        Connect-AzAccount -Subscription $SubscriptionId | Out-Null
    } else {
        Write-OK "Logged in: $($ctx.Account.Id)"
        Write-OK "Subscription: $($ctx.Subscription.Name)"
    }
    Set-AzContext -SubscriptionId $SubscriptionId | Out-Null
}

# ─── Build ────────────────────────────────────────────────────────────────────
function Invoke-Build {
    Write-Step "Building zip from source..."
    Write-Host "  Source : $SourceDir" -ForegroundColor Gray
    Write-Host "  Output : $ZipPath"  -ForegroundColor Gray

    if (-not (Test-Path $SourceDir)) {
        Write-Fail "Source directory not found: $SourceDir"
        return $false
    }

    # Validate all required files exist
    $missing = $RequiredSourceFiles | Where-Object {
        -not (Test-Path (Join-Path $SourceDir $_))
    }
    if ($missing.Count -gt 0) {
        Write-Fail "Missing source files:"
        $missing | ForEach-Object { Write-Host "    - $_" -ForegroundColor Red }
        return $false
    }

    # Remove old zip
    if (Test-Path $ZipPath) {
        Remove-Item $ZipPath -Force
    }

    # Zip contents of source folder (not the folder itself)
    Compress-Archive -Path "$SourceDir\*" -DestinationPath $ZipPath -Force

    $zipSizeKB = [math]::Round((Get-Item $ZipPath).Length / 1KB, 1)
    Write-OK "Zip built: $ZipPath ($zipSizeKB KB)"

    # Show contents
    Write-Host "  Contents:" -ForegroundColor Gray
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    $zip.Entries | ForEach-Object { Write-Host "    $($_.FullName)" -ForegroundColor Gray }
    $zip.Dispose()

    return $true
}

# ─── Deploy ───────────────────────────────────────────────────────────────────
function Invoke-Deploy {
    Write-Step "Deploying to $FunctionAppName..."

    if (-not (Test-Path $ZipPath)) {
        Write-Fail "Zip not found: $ZipPath"
        Write-Host "  Run with -Action Build first." -ForegroundColor Yellow
        return
    }

    $headers = Get-PublishCredentials

    Write-Host "  Uploading via Kudu zip deploy API..." -ForegroundColor Gray
    try {
        Invoke-RestMethod `
            -Uri         "$ScmHost/api/zipdeploy?isAsync=false" `
            -Method      POST `
            -Headers     $headers `
            -InFile      $ZipPath `
            -ContentType "application/zip" `
            -TimeoutSec  180 `
            -ErrorAction Stop | Out-Null

        Write-OK "Deployment complete."
    } catch {
        Write-Fail "Deployment failed: $($_.Exception.Message)"
        if ($_.Exception.Response) {
            try {
                $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                Write-Host "  Response: $($reader.ReadToEnd())" -ForegroundColor Red
            } catch {}
        }
        return
    }

    Write-Host "  Waiting 20s for functions to register..." -ForegroundColor Gray
    Start-Sleep -Seconds 20
    Invoke-Status
}

# ─── Status ───────────────────────────────────────────────────────────────────
function Invoke-Status {
    Write-Step "Checking registered functions..."
    try {
        $functions = Get-AzFunctionAppFunction `
            -ResourceGroupName $ResourceGroup `
            -Name              $FunctionAppName `
            -ErrorAction       Stop

        if ($functions.Count -eq 0) {
            Write-Fail "No functions registered — check deployment logs."
            return
        }

        Write-OK "$($functions.Count) function(s) found:"
        foreach ($fn in $functions) {
            $color = if ($fn.IsDisabled) { "Red" } else { "Green" }
            $state = if ($fn.IsDisabled) { "DISABLED" } else { "Active" }
            Write-Host "    [$state] $($fn.Name)" -ForegroundColor $color
        }

        $names = $functions | Select-Object -ExpandProperty Name
        $ExpectedFunctions | Where-Object { $names -notcontains $_ } | ForEach-Object {
            Write-Warn "Missing: $_"
        }
    } catch {
        Write-Fail "Could not list functions: $($_.Exception.Message)"
    }
}

# ─── Test ─────────────────────────────────────────────────────────────────────
function Invoke-Test {
    Write-Step "Testing all endpoints..."

    # Health
    Write-Host ""; Write-Host "  [1/3] GET /api/health" -ForegroundColor Cyan
    try {
        $r = Invoke-RestMethod -Uri "$FunctionAppHost/api/health" -Method GET -TimeoutSec 30
        Write-OK "Response: $r"
    } catch { Write-Fail "$($_.Exception.Message)" }

    # Customer
    Write-Host ""; Write-Host "  [2/3] POST /api/webshop/customer" -ForegroundColor Cyan
    $customerBody = @{
        name          = "Test Function Customer ApS"
        currency      = "DKK"
        customerGroup = @{ customerGroupNumber = 1 }
        paymentTerms  = @{ paymentTermsNumber = 1 }
        vatZone       = @{ vatZoneNumber = 1 }
        email         = "test-function@example.com"
    } | ConvertTo-Json -Depth 10
    try {
        $r = Invoke-RestMethod -Uri "$FunctionAppHost/api/webshop/customer" -Method POST -ContentType "application/json" -Body $customerBody -TimeoutSec 120
        Write-OK "CustomerNumber: $($r.customerNumber) — $($r.name)"
    } catch { Write-Fail "HTTP $($_.Exception.Response.StatusCode.value__): $($_.Exception.Message)" }

    # Contact
    Write-Host ""; Write-Host "  [3/3] POST /api/webshop/contact" -ForegroundColor Cyan
    $contactBody = @{
        customerNumber = 1
        contact        = @{ name = "Test Contact Function"; email = "testcontact-function@example.com"; phone = "+45 12345678" }
    } | ConvertTo-Json -Depth 10
    try {
        $r = Invoke-RestMethod -Uri "$FunctionAppHost/api/webshop/contact" -Method POST -ContentType "application/json" -Body $contactBody -TimeoutSec 120
        Write-OK "ContactNumber: $($r.customerContactNumber) — $($r.name)"
    } catch { Write-Fail "HTTP $($_.Exception.Response.StatusCode.value__): $($_.Exception.Message)" }
}

# ─── Logs ─────────────────────────────────────────────────────────────────────
function Invoke-Logs {
    Write-Step "Streaming live logs (Ctrl+C to stop)..."
    $headers = Get-PublishCredentials
    Write-Host "  Connecting..." -ForegroundColor Gray
    Write-Host "  Tip: trigger a webhook in another window to see output" -ForegroundColor Yellow
    Write-Host ""
    try {
        $request = [System.Net.WebRequest]::Create("$ScmHost/api/logstream")
        $request.Headers.Add("Authorization", $headers.Authorization)
        $request.Timeout = -1
        $reader = New-Object System.IO.StreamReader($request.GetResponse().GetResponseStream())
        while (-not $reader.EndOfStream) {
            $line = $reader.ReadLine()
            if ($line) { Write-Host $line }
        }
    } catch {
        Write-Warn "Stream ended: $($_.Exception.Message)"
        Write-Host "  Portal: func-eldk-webhook → Functions → [name] → Monitor" -ForegroundColor Yellow
    }
}

# ─── Main ─────────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║   func-eldk-webhook  —  Deploy & Test Tool      ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "  Action : $Action"
Write-Host "  Source : $SourceDir"
Write-Host "  Zip    : $ZipPath"
Write-Host "  Host   : $FunctionAppHost"

switch ($Action) {
    "Build"          { Invoke-Build | Out-Null }
    "BuildAndDeploy" { Invoke-Login; $ok = Invoke-Build; if ($ok) { Invoke-Deploy } }
    "Deploy"         { Invoke-Login; Invoke-Deploy }
    "Test"           { Invoke-Test }
    "Logs"           { Invoke-Login; Invoke-Logs }
    "Status"         { Invoke-Login; Invoke-Status }
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
