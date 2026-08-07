#Requires -Version 5.1
<#
.SYNOPSIS
    Plant one login PIN per ROLE for the documentation-screenshot harness and emit them as JSON.

.DESCRIPTION
    §952 / §957. The screenshot harness signs in ONCE PER ROLE, because a single session visiting
    every role's pages photographs the ACCESS GATE rather than the feature -- which is how
    "This page is for sponsors only" reached the public mirror as the sponsor experience.

    Five sessions therefore need five PINs. This wraps plant-test-pins.ps1 over the accounts in
    config/docs-screenshot-accounts.json and writes { "<role>": "<pin>" } for the harness to read
    via $env:DOCS_PINS.

    NO PIN E-MAIL IS SENT. Rows are written straight to the database, which is the operator's
    standing constraint (2026-08-02: "i get tons of pin sign in right now") -- five roles times a
    weekly refresh would otherwise be five unwanted mails a week.

    ON PROD THIS IS AN AUTHORISED, NAMED EXCEPTION to the read-only-PROD rule (§957 decision 2),
    scoped to LoginPins rows for these five accounts and nothing else. Note that "leave them" costs
    nothing and cleans up after itself: LoginPins rows carry ExpiresAt and are single-use, so
    nothing accumulates -- and equally, every run must re-plant. There is no such thing as a
    standing PIN left in production.

    Requires: az CLI signed in with SQL access (see CLAUDE.md -- deploys/az need the PEM export).

.EXAMPLE
    pwsh -File tools/plant-docs-pins.ps1 -Env prod
    $env:DOCS_PINS = "$PWD/scratchpad/docs-pins.json"
    npx playwright test docs-screenshots --project=docs
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')]
    [string]$Env = 'prod',

    # Where the { role: pin } map is written. Keep it OUT of the repo -- it is a live credential
    # for its ~2-hour life, and tests/ + config/ are both tracked.
    [string]$OutFile = (Join-Path $env:TEMP 'ceh-docs-pins.json'),

    [string]$AccountsFile = (Join-Path $PSScriptRoot '..\config\docs-screenshot-accounts.json'),

    # PINs are single-use: one is consumed per login, and the harness logs in once per role PER
    # VIEWPORT (desktop + mobile). 6 gives every role its two logins plus retries.
    [int]$Count = 6,

    # ⚠️ RankMinutes must outlast the WHOLE capture, not just the login. A planted row only
    # outranks the row the login form itself inserts while it is still stamped in the future, so a
    # 40-minute capture with -RankMinutes 5 silently loses to its own freshly-requested PIN and the
    # last roles fail at login. The capture sweeps ~48 targets x 2 viewports; 90 minutes is
    # comfortable headroom, and ValidMinutes must be larger still or the rows expire mid-run.
    [int]$RankMinutes  = 90,
    [int]$ValidMinutes = 150
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $AccountsFile)) { throw "accounts file not found: $AccountsFile" }
$accounts = (Get-Content -Raw -Path $AccountsFile | ConvertFrom-Json).accounts

$plant = Join-Path $PSScriptRoot 'plant-test-pins.ps1'
if (-not (Test-Path $plant)) { throw "plant-test-pins.ps1 not found next to this script" }

$pins = [ordered]@{}
$failed = @()

foreach ($role in $accounts.PSObject.Properties.Name) {
    $acct = $accounts.$role
    Write-Host "Planting for $role <$($acct.email)> role $($acct.role) on $($Env.ToUpper())..." -ForegroundColor Cyan
    try {
        $pin = & $plant -OrganizerEmail $acct.email -Role $acct.role -Env $Env `
                        -Count $Count -RankMinutes $RankMinutes -ValidMinutes $ValidMinutes
        # The script writes progress with Write-Host and returns the PIN as its only pipeline
        # output, but take the last element rather than trusting that -- a stray emitted object
        # would otherwise be stored as the PIN and every login would fail with no clue why.
        $pin = @($pin)[-1]
        if ($pin -notmatch '^\d{6}$') { throw "unexpected PIN value '$pin'" }
        $pins[$role] = $pin
    }
    catch {
        # Keep going: one missing role costs that role's chapter its pictures, but stopping costs
        # every chapter. The harness reports a role with no PIN as a loud per-target skip.
        Write-Warning "  FAILED for ${role}: $($_.Exception.Message)"
        $failed += $role
    }
}

if (-not $pins.Count) { throw "no PINs planted for any role -- check the az login and the accounts file." }

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
# ASCII, no BOM: the harness strips one defensively, but not writing it is better than stripping it.
$pins | ConvertTo-Json | Set-Content -Path $OutFile -Encoding ascii

Write-Host ""
Write-Host "Planted $($pins.Count)/$($accounts.PSObject.Properties.Name.Count) role PIN(s) -> $OutFile" -ForegroundColor Green
if ($failed.Count) {
    Write-Warning "NO PIN for: $($failed -join ', ') -- those roles' pages will be SKIPPED, not captured."
}
Write-Host "  `$env:DOCS_PINS = '$OutFile'" -ForegroundColor Yellow
