#Requires -Version 5.1
<#
.SYNOPSIS
    One-command runner for the CEH GUI feature suite (Playwright, DEV-only).

.DESCRIPTION
    Plants fresh single-use PINs for every canonical DEV test user, exports the
    email+PIN env vars the specs read, and runs the @gui-tagged feature specs on
    the narrow viewport. Without -All it runs only what it can authenticate; any
    block whose credentials are missing self-skips, so the run never hard-fails
    for lack of a role account.

    All outbound email a spec triggers is caught by the DEV `Email:RedirectAllTo`
    inbox — no real recipient is ever mailed. Read CLAUDE.md for the canonical
    test users and tests/TESTS.md §5 for the wider test story.

    PREREQUISITE: az CLI signed in with Key Vault + DEV SQL access (the planter
    reads the SQL admin password from the DEV vault and inserts PIN rows). If the
    DEV SQL is AAD-only, the planter cannot run from SQL auth — in that case the
    authenticated blocks self-skip and only the anonymous specs (sign-in
    contract, public survey/results, public volunteer signup) run.

.PARAMETER Anonymous
    Run only the no-auth specs (sign-in contract, surveys, public signup). Needs
    no PIN planting and no DB access — safe anywhere with network to the hub.

.EXAMPLE
    ./tests/playwright/run-gui-suite.ps1                 # auth what we can, skip the rest
.EXAMPLE
    ./tests/playwright/run-gui-suite.ps1 -Anonymous      # no DB needed
#>
[CmdletBinding()]
param(
    [switch]$Anonymous,
    [string]$OrganizerEmail = 'mok@expertslive.dk',
    [string]$SpeakerEmail   = 'mok@mortenknudsen.net',
    [string]$VolunteerEmail = 'knudsen_morten@hotmail.com',
    [string]$AttendeeEmail  = 'mortenknudsen1974@gmail.com',
    [string]$SponsorEmail   = 'mok@2linkit.net'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$planter = Join-Path $here '..\..\tools\plant-test-pins.ps1'

Push-Location $here
try {
    $env:TARGET = 'DEV'
    if ($Anonymous) {
        Write-Host 'Running anonymous @gui specs only (no PIN planting).' -ForegroundColor Cyan
        npx playwright test feature-signin feature-surveys feature-forms `
            --project="iPhone SE (narrow viewport)" --grep '@gui' --reporter=list
        return
    }

    # Plant PINs per role. Each block self-skips if its plant fails / role missing.
    function Plant([string]$email, [int]$role, [int]$count) {
        try { return (& $planter -OrganizerEmail $email -Role $role -Count $count) }
        catch { Write-Warning "Plant failed for $email (role $role): $($_.Exception.Message)"; return '' }
    }

    Write-Host 'Planting single-use DEV PINs (valid ~14 min)...' -ForegroundColor Cyan
    $env:ORGANIZER_EMAIL = $OrganizerEmail; $env:ADMIN_PIN     = Plant $OrganizerEmail 0 14
    $env:SPEAKER_EMAIL   = $SpeakerEmail;   $env:SPEAKER_PIN   = Plant $SpeakerEmail   1 4
    $env:VOLUNTEER_EMAIL = $VolunteerEmail; $env:VOLUNTEER_PIN = Plant $VolunteerEmail 3 3
    $env:ATTENDEE_EMAIL  = $AttendeeEmail;  $env:ATTENDEE_PIN  = Plant $AttendeeEmail  5 2
    $env:SPONSOR_EMAIL   = $SponsorEmail;   $env:SPONSOR_PIN   = Plant $SponsorEmail   4 4

    Write-Host 'Running the full @gui feature suite on the narrow viewport...' -ForegroundColor Cyan
    npx playwright test --project="iPhone SE (narrow viewport)" --grep '@gui' --reporter=list
}
finally { Pop-Location }
