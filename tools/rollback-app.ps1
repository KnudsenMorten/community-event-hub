#Requires -Version 5.1
<#
.SYNOPSIS
    Roll back the Community Hub web app.

.DESCRIPTION
    Two modes, picked automatically:

    SLOT MODE (staging slot exists -- S1+): the previous production build is
    still sitting in the staging slot after the last swap, so rollback is a
    swap back. Seconds, no rebuild.

    ARTIFACT MODE (no slot -- B1 today): lists the timestamped zips kept by
    deploy-app.ps1 under deploy-artifacts/ and redeploys the one you pick
    (default: the previous one). Same short outage as a normal deploy.

.EXAMPLE
    .\tools\rollback-app.ps1 -Env prod              # previous build
    .\tools\rollback-app.ps1 -Env prod -List        # show available artifacts
    .\tools\rollback-app.ps1 -Env prod -Artifact web-prod-20260611-2030.zip
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('dev','prod')][string]$Env,
    [string]$Artifact,
    [switch]$List
)

$ErrorActionPreference = 'Stop'
function Invoke-Az {
    param([Parameter(Mandatory)][string[]]$AzArgs)
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = az @AzArgs 2>$null } finally { $ErrorActionPreference = $eap }
    $script:AzExit = $LASTEXITCODE
    return $out
}
$repo = Split-Path -Parent $PSScriptRoot
$apps = @{
    dev  = @{ rg = 'rg-eldk27hub-dev';  app = 'eldk27hub-web-devz237e' }
    prod = @{ rg = 'rg-eldk27hub-prod'; app = 'eldk27hub-web-prodpdrq' }
}
$t = $apps[$Env]
$artDir = Join-Path $repo 'deploy-artifacts'

$slots = Invoke-Az @('webapp','deployment','slot','list','-g',$t.rg,'-n',$t.app,'--query','[].name','-o','tsv')
if ($slots -contains 'staging' -and -not $Artifact -and -not $List) {
    Write-Host ">> Slot mode: swapping staging back into production (the slot holds the previous build)..." -ForegroundColor Cyan
    [void](Invoke-Az @('webapp','deployment','slot','swap','-g',$t.rg,'-n',$t.app,'--slot','staging','--target-slot','production','--output','none'))
    if ($script:AzExit -ne 0) { throw "swap-back failed." }
    Write-Host ">> Rolled back via swap." -ForegroundColor Green
    return
}

$zips = @(Get-ChildItem $artDir -Filter ("web-{0}-*.zip" -f $Env) -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
if ($List -or -not $zips) {
    if (-not $zips) { Write-Host "No artifacts for '$Env' under $artDir. Artifacts are created by deploy-app.ps1." -ForegroundColor Yellow; return }
    $zips | ForEach-Object { $_.Name }
    return
}

$target = if ($Artifact) { $zips | Where-Object { $_.Name -eq $Artifact } | Select-Object -First 1 }
          else { $zips | Select-Object -Skip 1 -First 1 }   # previous build
if (-not $target) { throw "Artifact not found. Use -List to see what's available." }

Write-Host ">> Redeploying $($target.Name) to $Env ..." -ForegroundColor Cyan
[void](Invoke-Az @('webapp','deploy','-g',$t.rg,'-n',$t.app,'--src-path',$target.FullName,'--type','zip','--output','none'))
if ($script:AzExit -ne 0) { throw "rollback deploy failed." }
Write-Host ">> Rollback deployed." -ForegroundColor Green
