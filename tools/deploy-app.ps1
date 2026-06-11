#Requires -Version 5.1
<#
.SYNOPSIS
    Build + deploy the Community Hub web app with minimal downtime and a
    rollback trail.

.DESCRIPTION
    Flow:
      1. dotnet publish (Release) -> publish-out/
      2. Zip to deploy-artifacts/web-<env>-<timestamp>.zip (kept for rollback;
         the last 10 are retained).
      3. If the target app has a 'staging' deployment slot (requires S1+ --
         see tools/enable-slot-deploys.ps1): deploy the zip to the SLOT,
         warm it up, then SWAP. Production traffic moves in seconds and the
         old build stays in the slot for instant rollback (swap back).
      4. Otherwise (B1 today): direct zip deploy with a health-check loop --
         expect ~1-2 minutes of restart/cold-start downtime.

.PARAMETER Env
    dev | prod

.EXAMPLE
    .\tools\deploy-app.ps1 -Env dev
    .\tools\deploy-app.ps1 -Env prod
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('dev','prod')][string]$Env
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# az CLI writes harmless warnings to stderr (e.g. the 32-bit-Python notice),
# which PS 5.1 under EAP=Stop turns into terminating NativeCommandErrors.
# Run az with EAP=Continue and judge by exit code.
function Invoke-Az {
    param([Parameter(Mandatory)][string[]]$AzArgs)
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $errFile = Join-Path $env:TEMP ("az-err-{0}.txt" -f [guid]::NewGuid().ToString('N'))
    try { $out = az @AzArgs 2>$errFile } finally { $ErrorActionPreference = $eap }
    $script:AzExit = $LASTEXITCODE
    if ($script:AzExit -ne 0 -and (Test-Path $errFile)) {
        # Surface the REAL az error: drop the Python warning AND the PS 5.1
        # NativeCommandError wrapper lines around it.
        Get-Content $errFile | Where-Object {
            $_ -and $_ -notmatch '32-bit Python|UserWarning|cryptography|CategoryInfo|FullyQualifiedErrorId|NativeCommandError|^\s*\+ ' -and $_.Trim() -ne ''
        } | Select-Object -First 10 | ForEach-Object { Write-Host "   az: $_" -ForegroundColor Red }
    }
    Remove-Item $errFile -Force -ErrorAction SilentlyContinue
    return $out
}

$apps = @{
    dev  = @{ rg = 'rg-eldk27hub-dev';  app = 'eldk27hub-web-devz237e';  url = 'https://dev.eldk27.eventhub.expertslive.dk' }
    prod = @{ rg = 'rg-eldk27hub-prod'; app = 'eldk27hub-web-prodpdrq';  url = 'https://eldk27.eventhub.expertslive.dk' }
}
$t = $apps[$Env]

Write-Host ">> Building (Release)..." -ForegroundColor Cyan
dotnet publish (Join-Path $repo 'src/CommunityHub/CommunityHub.csproj') -c Release -o (Join-Path $repo 'publish-out') | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$artDir = Join-Path $repo 'deploy-artifacts'
New-Item -ItemType Directory -Force $artDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$zip = Join-Path $artDir ("web-{0}-{1}.zip" -f $Env, $stamp)

# NB: do NOT use Compress-Archive here. Under Windows PowerShell 5.1 it
# writes BACKSLASH entry names (App_Data\Surveys\...) and the App Service
# runs LINUX Kudu, which rejects such zips with a blind HTTP 400 (cost us
# an evening on 2026-06-11). Build the zip with explicit forward-slash
# entry names so it deploys from any PowerShell host.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$src = Join-Path $repo 'publish-out'
if (Test-Path $zip) { Remove-Item $zip -Force }
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem $src -Recurse -File)) {
        $rel = $file.FullName.Substring($src.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }
Write-Host ">> Artifact: $zip" -ForegroundColor Cyan

# retention: keep last 10 per env
Get-ChildItem $artDir -Filter ("web-{0}-*.zip" -f $Env) | Sort-Object Name -Descending | Select-Object -Skip 10 | Remove-Item -Force -ErrorAction SilentlyContinue

# Slot present? -> zero-downtime swap path.
$slots = Invoke-Az @('webapp','deployment','slot','list','-g',$t.rg,'-n',$t.app,'--query','[].name','-o','tsv')
if ($slots -contains 'staging') {
    Write-Host ">> 'staging' slot found -- slot-swap deploy (near-zero downtime)" -ForegroundColor Green
    [void](Invoke-Az @('webapp','deploy','-g',$t.rg,'-n',$t.app,'--slot','staging','--src-path',$zip,'--type','zip','--output','none'))
    if ($script:AzExit -ne 0) { throw "slot deploy failed." }

    # Warm the slot until it answers (JIT + EF model build happen here, NOT in prod).
    $slotHost = Invoke-Az @('webapp','show','-g',$t.rg,'-n',$t.app,'--slot','staging','--query','defaultHostName','-o','tsv')
    Write-Host ">> Warming https://$slotHost ..." -ForegroundColor Cyan
    $ok = $false
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $r = Invoke-WebRequest "https://$slotHost/" -UseBasicParsing -TimeoutSec 20
            if ($r.StatusCode -eq 200) { $ok = $true; break }
        } catch { }
        Start-Sleep -Seconds 5
    }
    if (-not $ok) { throw "staging slot never answered 200 -- NOT swapping. Investigate https://$slotHost first." }

    Write-Host ">> Swapping staging -> production ..." -ForegroundColor Cyan
    [void](Invoke-Az @('webapp','deployment','slot','swap','-g',$t.rg,'-n',$t.app,'--slot','staging','--target-slot','production','--output','none'))
    if ($script:AzExit -ne 0) { throw "swap failed." }
    Write-Host ">> Swap complete. Rollback = .\tools\rollback-app.ps1 -Env $Env (swaps back instantly)." -ForegroundColor Green

    # Post-swap warm-up of the REAL production URL. The swap restarts the
    # incoming worker while applying production config, so the pre-swap slot
    # warm-up does not carry over; without this the first real visitor pays
    # ~15s of JIT + EF-model + SQL-pool warm-up (seen 2026-06-11 as a
    # Playwright goto timeout). WEBSITE_SWAP_WARMUP_PING_PATH=/health gates
    # the pointer switch; these hits warm the hot paths behind it.
    Write-Host ">> Warming production hot paths ..." -ForegroundColor Cyan
    foreach ($path in '/', '/survey/eldk27-topics', '/Login') {
        try { [void](Invoke-WebRequest "$($t.url.TrimEnd('/'))$path" -UseBasicParsing -TimeoutSec 60) } catch { }
    }
} else {
    Write-Host ">> No staging slot (B1 doesn't support slots; see tools/enable-slot-deploys.ps1). Direct deploy -- expect a short outage." -ForegroundColor Yellow
    [void](Invoke-Az @('webapp','deploy','-g',$t.rg,'-n',$t.app,'--src-path',$zip,'--type','zip','--output','none'))
    if ($script:AzExit -ne 0) { throw "deploy failed." }
}

# Post-deploy health check on the real URL.
Write-Host ">> Health check $($t.url) ..." -ForegroundColor Cyan
$healthy = $false
for ($i = 0; $i -lt 24; $i++) {
    try {
        $r = Invoke-WebRequest $t.url -UseBasicParsing -TimeoutSec 20
        if ($r.StatusCode -eq 200) { $healthy = $true; break }
    } catch { }
    Start-Sleep -Seconds 5
}
if ($healthy) { Write-Host ">> $Env is healthy." -ForegroundColor Green }
else { Write-Host ">> WARNING: $($t.url) not answering 200 after 2 minutes -- check logs: az webapp log tail -g $($t.rg) -n $($t.app)" -ForegroundColor Red }
