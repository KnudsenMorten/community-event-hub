<#
.SYNOPSIS
    Deploy the CEH web app or Functions jobs app using **Az PowerShell** — no `az` CLI, no PEM.

.DESCRIPTION
    §347's "better fix, worth doing", built 2026-07-26. A straight port of `deploy-app.ps1`,
    which is `az`-CLI based and therefore needs a certificate PEM **on disk** — the single most
    repeated session-killer on this project (§345): `az` cannot read the Windows certificate store,
    it re-reads the PEM on every token renewal, and the PEM is (correctly) not kept, so every new
    session inherits a login that looks valid and dies on first use.

    Az PowerShell reads the cert straight out of `Cert:\LocalMachine\My` via
    `Connect-AzAccount -CertificateThumbprint`, so nothing is written to disk and a cold session
    works immediately.

    Behaviour is deliberately IDENTICAL to deploy-app.ps1 — same build flags, same zip construction,
    same slot→warm→swap→warm sequence, same abort-before-swap guard. Only the Azure calls changed.

.PARAMETER Env
    dev | prod.

.PARAMETER App
    web (default) | jobs. The Functions app has no slots (Flex Consumption) — it is a direct
    zip deploy, exactly as in the original.

.PARAMETER SkipConnect
    Reuse an existing Az context instead of connecting (useful when already connected).
#>
param(
    [Parameter(Mandatory)][ValidateSet('dev','prod')][string]$Env,
    [ValidateSet('web','jobs')][string]$App = 'web',
    [switch]$SkipConnect
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

# --- identity (§345: the SPN cert from the Windows store, never `az login`) ----
$Tenant       = '7825c48b-861b-41fd-b635-ffab1aff7d13'
$AppId        = '9e0974ed-4842-4258-942e-60fe0e2c740d'   # ELDK-EventHub-Code-Deployment
$Thumbprint   = '57E70458D352A6698AD602D84DF7AE13A8C4DFD2'
$Subscription = '772440e1-adf8-4fbe-82f9-bb977b55bc8b'

$apps = @{
    dev  = @{ rg = 'rg-eldk27hub-dev';  app = 'eldk27hub-web-devz237e'; fn = 'eldk27hub-fn-devz237e'; url = 'https://dev.eldk27.eventhub.expertslive.dk' }
    prod = @{ rg = 'rg-eldk27hub-prod'; app = 'eldk27hub-web-prodpdrq'; fn = 'eldk27hub-fn-prodpdrq'; url = 'https://eldk27.eventhub.expertslive.dk' }
}
$t = $apps[$Env]

if (-not $SkipConnect) {
    Write-Host ">> Connecting as the deployment SPN (cert from Cert:\LocalMachine\My) ..." -ForegroundColor Cyan
    Connect-AzAccount -ServicePrincipal -Tenant $Tenant -ApplicationId $AppId `
        -CertificateThumbprint $Thumbprint -Subscription $Subscription -WarningAction SilentlyContinue | Out-Null
}

# --- build ---------------------------------------------------------------------
$proj   = if ($App -eq 'jobs') { 'src/CommunityHub.Jobs/CommunityHub.Jobs.csproj' } else { 'src/CommunityHub/CommunityHub.csproj' }
$outDir = if ($App -eq 'jobs') { 'publish-jobs-out' } else { 'publish-out' }

Write-Host ">> Building $App (Release)..." -ForegroundColor Cyan
# WEB: ReadyToRun for linux-x64, framework-dependent — same as deploy-app.ps1. R2R pre-compiles
# IL so the first hit of each page is not JIT-bound (the old 10-30s cold starts).
# The output dir is cleaned first so a file DELETED from the repo cannot linger in the zip.
$outPath = Join-Path $repo $outDir
if (Test-Path $outPath) { Remove-Item $outPath -Recurse -Force }
$pubArgs = @('publish', (Join-Path $repo $proj), '-c', 'Release', '-o', $outPath)
if ($App -eq 'web') { $pubArgs += @('-r', 'linux-x64', '--self-contained', 'false', '-p:PublishReadyToRun=true') }
dotnet @pubArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# --- zip (forward-slash entries; Compress-Archive would break Linux Kudu) -------
$artDir = Join-Path $repo 'deploy-artifacts'
New-Item -ItemType Directory -Force $artDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$zip   = Join-Path $artDir ("{0}-{1}-{2}.zip" -f $App, $Env, $stamp)

# NB: do NOT use Compress-Archive — under Windows PowerShell 5.1 it writes BACKSLASH entry names
# and Linux Kudu rejects such zips with a blind HTTP 400 (cost an evening on 2026-06-11).
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
if (Test-Path $zip) { Remove-Item $zip -Force }
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem $outPath -Recurse -File)) {
        $rel = $file.FullName.Substring($outPath.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $file.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }
Write-Host ">> Artifact: $zip" -ForegroundColor Cyan

Get-ChildItem $artDir -Filter ("{0}-{1}-*.zip" -f $App, $Env) |
    Sort-Object Name -Descending | Select-Object -Skip 10 | Remove-Item -Force -ErrorAction SilentlyContinue

# --- jobs: direct zip deploy (Flex Consumption has no slots) --------------------
if ($App -eq 'jobs') {
    Write-Host ">> Deploying jobs to $($t.fn) ..." -ForegroundColor Cyan
    Publish-AzWebApp -ResourceGroupName $t.rg -Name $t.fn -ArchivePath $zip -Force | Out-Null
    Write-Host ">> $Env jobs deployed." -ForegroundColor Green
    return
}

# --- web: slot -> warm -> swap -> warm ------------------------------------------
$slots = @(Get-AzWebAppSlot -ResourceGroupName $t.rg -Name $t.app -ErrorAction SilentlyContinue |
           ForEach-Object { ($_.Name -split '/')[-1] })

if ($slots -notcontains 'staging') {
    Write-Host ">> No 'staging' slot -- deploying DIRECTLY to production (brief restart)." -ForegroundColor Yellow
    Publish-AzWebApp -ResourceGroupName $t.rg -Name $t.app -ArchivePath $zip -Force | Out-Null
    Write-Host ">> $Env web deployed." -ForegroundColor Green
    return
}

Write-Host ">> 'staging' slot found -- slot-swap deploy (near-zero downtime)" -ForegroundColor Green

# Keep the slot's runtime stack in lockstep with production BEFORE deploying code to it. A
# framework upgrade bumps prod's linuxFxVersion; if staging still runs the old stack the new build
# 503s there and the swap carries the mismatch into production (the .NET 10 cutover blip,
# 2026-06-26).
$prodFx = (Get-AzWebApp -ResourceGroupName $t.rg -Name $t.app).SiteConfig.LinuxFxVersion
$slotFx = (Get-AzWebAppSlot -ResourceGroupName $t.rg -Name $t.app -Slot 'staging').SiteConfig.LinuxFxVersion
if ($prodFx -and ("$prodFx".Trim() -ne "$slotFx".Trim())) {
    Write-Host ">> Aligning staging slot runtime '$slotFx' -> '$prodFx' ..." -ForegroundColor Cyan
    Set-AzWebAppSlot -ResourceGroupName $t.rg -Name $t.app -Slot 'staging' -LinuxFxVersion $prodFx | Out-Null
}

Publish-AzWebApp -ResourceGroupName $t.rg -Name $t.app -Slot 'staging' -ArchivePath $zip -Force | Out-Null

# Warm the slot until it answers. JIT + EF model build happen HERE, not in production.
$slotHost = (Get-AzWebAppSlot -ResourceGroupName $t.rg -Name $t.app -Slot 'staging').DefaultHostName
Write-Host ">> Warming https://$slotHost ..." -ForegroundColor Cyan
$ok = $false
for ($i = 0; $i -lt 30; $i++) {
    try {
        if ((Invoke-WebRequest "https://$slotHost/" -UseBasicParsing -TimeoutSec 20).StatusCode -eq 200) { $ok = $true; break }
    } catch { }
    Start-Sleep -Seconds 5
}
# The guard that matters: never swap a slot that did not come up.
if (-not $ok) { throw "staging slot never answered 200 -- NOT swapping. Investigate https://$slotHost first." }

Write-Host ">> Swapping staging -> production ..." -ForegroundColor Cyan
Switch-AzWebAppSlot -ResourceGroupName $t.rg -Name $t.app -SourceSlotName 'staging' -DestinationSlotName 'production' | Out-Null
Write-Host ">> Swap complete. Rollback = swap back (tools/rollback-app.ps1 -Env $Env)." -ForegroundColor Green

# Post-swap warm-up of the REAL production URL: the swap restarts the incoming worker while
# applying production config, so the pre-swap warm-up does not carry over. Without this the first
# real visitor pays the full first-touch cost (40s measured on /volunteer/signup, 2026-06-12).
Write-Host ">> Warming production hot paths ..." -ForegroundColor Cyan
foreach ($path in '/', '/survey/eldk27-topics', '/survey/eldk27-topics/results',
                  '/Login', '/volunteer/signup', '/Contributors') {
    try { [void](Invoke-WebRequest "$($t.url.TrimEnd('/'))$path" -UseBasicParsing -TimeoutSec 90) } catch { }
}
Write-Host ">> $Env web deployed and warmed: $($t.url)" -ForegroundColor Green
