#Requires -Version 5.1
<#
.SYNOPSIS
    Role-by-role end-to-end scenario SIMULATION runner (all five roles).

.DESCRIPTION
    Drives the "Backend+GUI scenario simulation with seeded data" requirement
    (docs/REQUIREMENTS.md §12, docs/TESTS.md §10) as ONE command, role by role,
    proving the backend AND the GUI halves work together off the shared rich seed
    (tests/CommunityHub.Core.Tests/Scenario/ScenarioSeed.cs).

    Coverage is now the full set of five roles:
      * organizer  — backend OrganizerActionQueueScenarioTests   + GUI scenario-organizer.spec.ts
      * speaker    — backend SpeakerMilestoneScenarioTests        + GUI scenario-speaker.spec.ts
      * sponsor    — backend SponsorBoothLeadsScenarioTests       + GUI scenario-sponsor.spec.ts
      * volunteer  — backend VolunteerAndAttendeeScenarioTests    + GUI scenario-volunteer.spec.ts
      * attendee   — backend VolunteerAndAttendeeScenarioTests    + GUI scenario-attendee.spec.ts
    (volunteer + attendee share ONE backend scenario class — the filter is
    de-duplicated so the class runs once even when both roles are selected.)

    Each role runs in two phases:
      1. BACKEND half  — xUnit + EF in-memory (always-on, no app / DB / secrets).
         This is the source of truth that the role's DB state changes correctly.
      2. GUI half      — Playwright scenario-<role>.spec.ts on the narrow
         (iPhone SE ~375px) viewport. WITHOUT planted PINs / a reachable hub it
         SELF-SKIPS cleanly (no browser window is ever opened). With -Deep it
         plants single-use DEV PINs and drives the real PIN login + write-path
         postbacks against DEV (where all outbound mail is governed by the CEH
         allowlist / redirect — no real recipient is mailed).

    Additive + read-only/test-only: this runner only invokes existing tests and
    the existing PIN planter. It changes no production code or default behaviour.

.PARAMETER Role
    Which role(s) to simulate: organizer, speaker, sponsor, volunteer, attendee,
    or all (default — every role).

.PARAMETER Deep
    Also drive the GUI half live against DEV (plants single-use PINs + real PIN
    login). DEV-only. Without it the GUI half runs but self-skips with no PINs,
    which still proves the specs are well-formed and never pop a browser window.

.PARAMETER BackendOnly
    Run only the always-on backend half (no Playwright at all). Handy for CI /
    machines without a Node toolchain.

.PARAMETER OrganizerEmail
    DEV organizer login (only used with -Deep). Must be an @expertslive.dk
    address (the one allowed real domain).

.PARAMETER SpeakerEmail
    DEV speaker login (only used with -Deep).

.PARAMETER SponsorEmail
    DEV sponsor-contact login (only used with -Deep).

.PARAMETER VolunteerEmail
    DEV volunteer login (only used with -Deep).

.PARAMETER AttendeeEmail
    DEV attendee login (only used with -Deep).

.EXAMPLE
    ./tools/run-scenario-sim.ps1
    # all five roles: backend half (real run) + GUI half (self-skips without PINs)

.EXAMPLE
    ./tools/run-scenario-sim.ps1 -Role speaker -BackendOnly
    # just the speaker backend scenario, no Node needed

.EXAMPLE
    ./tools/run-scenario-sim.ps1 -Deep
    # full live simulation vs DEV (plants PINs, real PIN login both roles)
#>
[CmdletBinding()]
param(
    [ValidateSet('organizer', 'speaker', 'sponsor', 'volunteer', 'attendee', 'all')]
    [string]$Role = 'all',
    [switch]$Deep,
    [switch]$BackendOnly,
    [string]$OrganizerEmail = 'mok@expertslive.dk',
    [string]$SpeakerEmail   = 'mok@mortenknudsen.net',
    [string]$SponsorEmail   = 'mok@expertslive.dk',
    [string]$VolunteerEmail = 'mok@expertslive.dk',
    [string]$AttendeeEmail  = 'mok@expertslive.dk'
)
$ErrorActionPreference = 'Stop'

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here
$testProj = Join-Path $repoRoot 'tests\CommunityHub.Core.Tests\CommunityHub.Core.Tests.csproj'
$pwDir    = Join-Path $repoRoot 'tests\playwright'
$planter  = Join-Path $here 'plant-test-pins.ps1'

if ($Deep -and $BackendOnly) {
    throw "-Deep and -BackendOnly are mutually exclusive (-Deep drives the GUI half live)."
}

# The full five-role scope. Each entry maps a role to its backend xUnit scenario
# class, its GUI Playwright spec, and (for -Deep) the ParticipantRole id used to
# plant a single-use DEV PIN. NOTE: volunteer + attendee share ONE backend class
# (VolunteerAndAttendeeScenarioTests) — the backend filter de-dupes below.
$roles = @(
    [pscustomobject]@{
        Name = 'organizer'; BackendTest = 'OrganizerActionQueueScenarioTests'
        GuiSpec = 'scenario-organizer'; ParticipantRole = 0; Email = $OrganizerEmail
    }
    [pscustomobject]@{
        Name = 'speaker'; BackendTest = 'SpeakerMilestoneScenarioTests'
        GuiSpec = 'scenario-speaker'; ParticipantRole = 1; Email = $SpeakerEmail
    }
    [pscustomobject]@{
        Name = 'sponsor'; BackendTest = 'SponsorBoothLeadsScenarioTests'
        GuiSpec = 'scenario-sponsor'; ParticipantRole = 4; Email = $SponsorEmail
    }
    [pscustomobject]@{
        Name = 'volunteer'; BackendTest = 'VolunteerAndAttendeeScenarioTests'
        GuiSpec = 'scenario-volunteer'; ParticipantRole = 3; Email = $VolunteerEmail
    }
    [pscustomobject]@{
        Name = 'attendee'; BackendTest = 'VolunteerAndAttendeeScenarioTests'
        GuiSpec = 'scenario-attendee'; ParticipantRole = 5; Email = $AttendeeEmail
    }
)
if ($Role -ne 'all') { $roles = @($roles | Where-Object { $_.Name -eq $Role }) }

Write-Host "CEH scenario simulation — roles: $($roles.Name -join ', ')" -ForegroundColor Cyan
Write-Host ''

# ---------------------------------------------------------------------------
# Phase 1 — BACKEND half (always-on, EF in-memory). One filtered dotnet test
# run covering every selected role's scenario class. This is the gate: a red
# backend half fails the whole run.
# ---------------------------------------------------------------------------
$filter = (($roles.BackendTest | Sort-Object -Unique) | ForEach-Object { "FullyQualifiedName~Scenario.$_" }) -join '|'
Write-Host "[backend] dotnet test (filter: $filter)" -ForegroundColor Cyan
& dotnet test $testProj -c Release --nologo --filter $filter
if ($LASTEXITCODE -ne 0) {
    throw "Backend scenario half FAILED (exit $LASTEXITCODE). The simulation is not green; fix the backend before the GUI half."
}
Write-Host "[backend] PASS" -ForegroundColor Green
Write-Host ''

if ($BackendOnly) {
    Write-Host 'BackendOnly: skipping the GUI half by request.' -ForegroundColor Yellow
    return
}

# ---------------------------------------------------------------------------
# Phase 2 — GUI half (Playwright, narrow viewport). Headless only — Playwright
# never opens a window. Without -Deep the specs self-skip (no PINs); with -Deep
# we plant single-use DEV PINs and drive the real login + write postbacks.
# ---------------------------------------------------------------------------
if (-not (Test-Path (Join-Path $pwDir 'node_modules'))) {
    Write-Host '[gui] installing Playwright deps (first run)...' -ForegroundColor Cyan
    Push-Location $pwDir
    try { npm install | Out-Null } finally { Pop-Location }
}

Push-Location $pwDir
try {
    $env:TARGET = 'DEV'
    Remove-Item Env:CEH_BASE_URL -ErrorAction SilentlyContinue

    if ($Deep) {
        function Plant([string]$email, [int]$roleId, [int]$count) {
            try { return (& $planter -OrganizerEmail $email -Role $roleId -Count $count) }
            catch { Write-Warning "Plant failed for $email (role $roleId): $($_.Exception.Message)"; return '' }
        }
        # Each role's GUI spec reads a specific {ROLE}_EMAIL + {ROLE}_PIN pair from
        # the environment (tests/playwright/support/scenario.ts). The organizer PIN
        # env var is ADMIN_PIN; the rest follow the {ROLE}_PIN convention.
        $pinEnv = @{
            organizer = 'ADMIN_PIN'; speaker = 'SPEAKER_PIN'; sponsor = 'SPONSOR_PIN'
            volunteer = 'VOLUNTEER_PIN'; attendee = 'ATTENDEE_PIN'
        }
        Write-Host '[gui] planting single-use DEV PINs (valid ~14 min)...' -ForegroundColor Cyan
        foreach ($r in $roles) {
            # 2 PINs covers a retry / a second navigation within one spec.
            Set-Item -Path "Env:$($r.Name.ToUpper())_EMAIL" -Value $r.Email
            Set-Item -Path "Env:$($pinEnv[$r.Name])"        -Value (Plant $r.Email $r.ParticipantRole 2)
        }
    }
    else {
        Write-Host '[gui] no -Deep: the GUI half self-skips without planted PINs (no browser is opened).' -ForegroundColor Yellow
    }

    $specs = @($roles.GuiSpec)
    Write-Host "[gui] npx playwright test $($specs -join ' ') (iPhone SE narrow viewport)" -ForegroundColor Cyan
    & npx playwright test @specs --project="iPhone SE (narrow viewport)" --reporter=list
    $guiExit = $LASTEXITCODE
}
finally { Pop-Location }

if ($guiExit -ne 0) {
    throw "GUI scenario half FAILED (exit $guiExit)."
}
$guiMode = if ($Deep) { 'ran live' } else { 'self-skipped without PINs' }
Write-Host ''
Write-Host "Scenario simulation complete (backend PASS; GUI half $guiMode)." -ForegroundColor Green
