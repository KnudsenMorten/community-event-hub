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

.PARAMETER App
    web (default) | jobs. 'jobs' publishes CommunityHub.Jobs and pushes it to
    the Functions app via config-zip (no slots on Flex Consumption; the
    Functions host restarts in seconds and timers self-heal, so direct deploy
    is fine there).

.EXAMPLE
    .\tools\deploy-app.ps1 -Env dev
    .\tools\deploy-app.ps1 -Env prod
    .\tools\deploy-app.ps1 -Env prod -App jobs
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('dev','prod')][string]$Env,
    [ValidateSet('web','jobs')][string]$App = 'web'
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
    dev  = @{ rg = 'rg-eldk27hub-dev';  app = 'eldk27hub-web-devz237e';  fn = 'eldk27hub-fn-devz237e';  url = 'https://dev.eldk27.eventhub.expertslive.dk' }
    prod = @{ rg = 'rg-eldk27hub-prod'; app = 'eldk27hub-web-prodpdrq'; fn = 'eldk27hub-fn-prodpdrq'; url = 'https://eldk27.eventhub.expertslive.dk' }
}
$t = $apps[$Env]

$proj   = if ($App -eq 'jobs') { 'src/CommunityHub.Jobs/CommunityHub.Jobs.csproj' } else { 'src/CommunityHub/CommunityHub.csproj' }
$outDir = if ($App -eq 'jobs') { 'publish-jobs-out' } else { 'publish-out' }

Write-Host ">> Building $App (Release)..." -ForegroundColor Cyan
# WEB: publish ReadyToRun (crossgen for the App Service linux-x64 runtime). R2R
# pre-compiles IL -> native so the FIRST hit of each page/query is not JIT-bound,
# which (with the in-process startup warm-up) is what killed the ~10-30s
# cold-start hangs. Framework-dependent (--self-contained false): still runs on
# the App Service's installed .NET 8, the zip stays small, and an incompatible
# R2R image silently falls back to JIT (never a crash).
# Clean the publish output first so a file DELETED from the repo (e.g. a removed
# email template) does not linger in the zip from a previous build.
$outPath = Join-Path $repo $outDir
if (Test-Path $outPath) { Remove-Item $outPath -Recurse -Force }
$pubArgs = @('publish', (Join-Path $repo $proj), '-c', 'Release', '-o', (Join-Path $repo $outDir))
if ($App -eq 'web') {
    $pubArgs += @('-r', 'linux-x64', '--self-contained', 'false', '-p:PublishReadyToRun=true')
}
dotnet @pubArgs | Out-Null
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$artDir = Join-Path $repo 'deploy-artifacts'
New-Item -ItemType Directory -Force $artDir | Out-Null
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$zip = Join-Path $artDir ("{0}-{1}-{2}.zip" -f $App, $Env, $stamp)

# NB: do NOT use Compress-Archive here. Under Windows PowerShell 5.1 it
# writes BACKSLASH entry names (App_Data\Surveys\...) and the App Service
# runs LINUX Kudu, which rejects such zips with a blind HTTP 400 (cost us
# an evening on 2026-06-11). Build the zip with explicit forward-slash
# entry names so it deploys from any PowerShell host.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$src = Join-Path $repo $outDir
if (Test-Path $zip) { Remove-Item $zip -Force }
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem $src -Recurse -File)) {
        $rel = $file.FullName.Substring($src.Length + 1).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $rel, [System.IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $archive.Dispose() }
Write-Host ">> Artifact: $zip" -ForegroundColor Cyan

# retention: keep last 10 per env per app
Get-ChildItem $artDir -Filter ("{0}-{1}-*.zip" -f $App, $Env) | Sort-Object Name -Descending | Select-Object -Skip 10 | Remove-Item -Force -ErrorAction SilentlyContinue

# --- jobs (Functions app): direct config-zip deploy, then done -------------
if ($App -eq 'jobs') {
    Write-Host ">> Deploying jobs to $($t.fn) ..." -ForegroundColor Cyan
    [void](Invoke-Az @('functionapp','deployment','source','config-zip','-g',$t.rg,'-n',$t.fn,'--src',$zip,'--output','none'))
    if ($script:AzExit -ne 0) { throw "functions deploy failed." }
    Write-Host ">> $Env jobs deployed." -ForegroundColor Green
    return
}

# Slot present? -> zero-downtime swap path.
$slots = Invoke-Az @('webapp','deployment','slot','list','-g',$t.rg,'-n',$t.app,'--query','[].name','-o','tsv')
if ($slots -contains 'staging') {
    Write-Host ">> 'staging' slot found -- slot-swap deploy (near-zero downtime)" -ForegroundColor Green
    [void](Invoke-Az @('webapp','deploy','-g',$t.rg,'-n',$t.app,'--slot','staging','--src-path',$zip,'--type','zip','--clean','true','--output','none'))
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
    # Every anonymous page: a path missing here costs its first visitor the
    # full first-touch warm-up (measured 40s on /volunteer/signup 2026-06-12).
    foreach ($path in '/', '/survey/eldk27-topics', '/survey/eldk27-topics/results',
                      '/Login', '/volunteer/signup', '/Contributors') {
        try { [void](Invoke-WebRequest "$($t.url.TrimEnd('/'))$path" -UseBasicParsing -TimeoutSec 90) } catch { }
    }
} else {
    Write-Host ">> No staging slot (B1 doesn't support slots; see tools/enable-slot-deploys.ps1). Direct deploy -- expect a short outage." -ForegroundColor Yellow
    [void](Invoke-Az @('webapp','deploy','-g',$t.rg,'-n',$t.app,'--src-path',$zip,'--type','zip','--clean','true','--output','none'))
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

# --- Go-live LOG-VALIDATION gate (REQUIREMENTS: validate logs after initial
#     login) ---------------------------------------------------------------
# A green health probe is not enough: the container can still be restart-looping
# on the start-timeout, or the app can be logging unhandled exceptions / SQL
# login failures that users feel as multi-second hangs. After the app is up and
# warmed, pull the live container log and assert the LATEST boot started cleanly
# and is not erroring. Fails the deploy (non-zero exit) so a bad go-live is
# caught here, not by the first user.
Write-Host ">> Validating deployment logs ..." -ForegroundColor Cyan
$logZip = Join-Path $env:TEMP ("eldk-{0}-deploylog-{1}.zip" -f $Env, [guid]::NewGuid().ToString('N'))
$logDir = Join-Path $env:TEMP ("eldk-{0}-deploylog" -f $Env)
try {
    if (Test-Path $logDir) { Remove-Item $logDir -Recurse -Force -ErrorAction SilentlyContinue }
    [void](Invoke-Az @('webapp','log','download','-g',$t.rg,'-n',$t.app,'--log-file',$logZip))
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($logZip, $logDir)

    $docker = Get-ChildItem $logDir -Recurse -File -Filter *docker*.log -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $docker) {
        Write-Host ">> WARN: no container log found to validate (skipping gate)." -ForegroundColor Yellow
    } else {
        $lines = @(Get-Content $docker.FullName)
        if ($lines.Count -eq 0) {
            Write-Host ">> WARN: container log empty (skipping gate)." -ForegroundColor Yellow
            return
        }
        # Scope to the CURRENT boot: lines after the last container (re)start so an
        # OLD timeout from a prior boot never fails a clean go-live.
        $startIdx = 0
        for ($k = $lines.Count - 1; $k -ge 0; $k--) {
            if ($lines[$k] -match 'Container start method called|Running the command: dotnet') { $startIdx = $k; break }
        }
        $boot = @($lines[$startIdx..($lines.Count - 1)])
        $bootText = $boot -join "`n"

        $started   = $bootText -match 'Application started|Site started|startup probe succeeded'
        # FATAL app patterns ALWAYS fail the gate (real faults the health check can miss).
        $fatalPattern = 'Unhandled exception|Login failed for|crash loop|FATAL'
        $errs = @($boot | Where-Object { $_ -match $fatalPattern } | Select-Object -First 8)
        # STARTUP TIMEOUTS need care: a cold start can exceed the 230s limit, get
        # retried by Azure, and recover — after which the platform keeps a STALE
        # "LastError: ContainerTimeout" field on EVERY subsequent state line (even INFO
        # ones like "Container is running"). Matching that blindly false-fails a healthy
        # go-live. So count a timeout ONLY when it is a genuine ERR-level stop event
        # (DetailsLevel: ERR) that is NOT followed by a successful start/probe later in
        # the window (i.e. it never recovered). A recovered timeout is benign.
        for ($ti = 0; $ti -lt $boot.Count; $ti++) {
            if ($boot[$ti] -notmatch 'ContainerTimeout|did not start within expected time') { continue }
            if ($boot[$ti] -notmatch 'DetailsLevel:\s*ERR') { continue }   # stale field on an INFO line — ignore
            $recovered = $false
            for ($j = $ti + 1; $j -lt $boot.Count; $j++) {
                if ($boot[$j] -match 'Site started|startup probe succeeded|Container is running') { $recovered = $true; break }
            }
            if (-not $recovered) { $errs += $boot[$ti] }
        }
        $errs = @($errs | Select-Object -First 8)
        $probeMatch = $boot | Select-String -Pattern 'startup probe succeeded after ([\d\.]+) seconds' | Select-Object -Last 1
        $probe = if ($probeMatch) { $probeMatch.Matches[0].Groups[1].Value } else { $null }

        if ($probe) { Write-Host "   startup probe: ${probe}s" -ForegroundColor Gray }
        # The REAL gate is error detection. (We do NOT require an "Application started"
        # line: on the slot-swap path the worker starts in the STAGING slot, so the
        # post-swap production log legitimately has no fresh start line — and the
        # health-check above already proved the app is serving 200.)
        if ($errs) {
            Write-Host ">> LOG-VALIDATION FAILED -- the latest boot logged errors:" -ForegroundColor Red
            $errs | ForEach-Object { Write-Host "   $_" -ForegroundColor Red }
            throw "Deployment log validation failed for $Env. NOT clean -- investigate before calling it live."
        }
        if (-not $started) {
            Write-Host ">> Note: no fresh 'Application started' in the prod log window (expected on a slot-swap); health check already passed." -ForegroundColor Gray
        }
        Write-Host ">> Logs validated: no errors in the latest boot window." -ForegroundColor Green
    }
} finally {
    Remove-Item $logZip -Force -ErrorAction SilentlyContinue
}
