#Requires -Version 5.1
<#
.SYNOPSIS
    Post-deploy GUI validation gate for the Community Event Hub.

.DESCRIPTION
    Runs the comprehensive Playwright route validation (tests/playwright/
    comprehensive-validation.spec.ts, tag @validate) against a deployed
    environment. Intended to be run immediately AFTER a deploy — DEV or the
    PROD slot — as a release gate (see CLAUDE.md "Release flow" and
    docs/TESTS.md). Every key route is checked for: renders (not 404 / not
    5xx), no console error, mobile (~360px) layout, a11y landmarks, real
    content / honest empty state, plus role-gating.

    Two layers:
      * ANONYMOUS  — public routes + the "login required" / role-gating
        contract. No DB, no PIN; runs against any TARGET (DEV or PROD). This is
        the always-runnable gate and the ONLY layer that is safe to point at
        PROD once TestMode is removed (read-only, no impersonation).
      * AUTHENTICATED (DEV only, -Deep) — plants single-use PINs per role and
        deep-sweeps every route each role can reach + negative role-gating.
        DEV redirects all mail, so it is side-effect-free.

.PARAMETER Target
    DEV (default) or PROD. Selects the base URL the spec hits.

.PARAMETER BaseUrl
    Explicit base URL (e.g. a staging-slot URL during a slot deploy). Overrides
    -Target. Maps to CEH_BASE_URL.

.PARAMETER Deep
    Also run the authenticated per-role route sweep + negative role-gating
    (DEV only; plants PINs). Without it, only the anonymous layer runs.

.PARAMETER SurveySlug
    Survey slug for the public survey/results routes (default eldk27-topics).

.EXAMPLE
    ./tools/run-post-deploy-validation.ps1                       # anonymous gate vs DEV
.EXAMPLE
    ./tools/run-post-deploy-validation.ps1 -Deep                 # full DEV gate (plants PINs)
.EXAMPLE
    ./tools/run-post-deploy-validation.ps1 -Target PROD          # anonymous gate vs PROD (post-swap)
.EXAMPLE
    ./tools/run-post-deploy-validation.ps1 -BaseUrl https://app-...-staging.azurewebsites.net
#>
[CmdletBinding()]
param(
    [ValidateSet('DEV', 'PROD')] [string]$Target = 'DEV',
    [string]$BaseUrl,
    [switch]$Deep,
    [string]$SurveySlug = 'eldk27-topics',
    [string]$OrganizerEmail = 'mok@expertslive.dk',
    [string]$SpeakerEmail   = 'mok@mortenknudsen.net',
    [string]$VolunteerEmail = 'knudsen_morten@hotmail.com',
    [string]$AttendeeEmail  = 'mortenknudsen1974@gmail.com',
    [string]$SponsorEmail   = 'mok@2linkit.net'
)
$ErrorActionPreference = 'Stop'
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$pwDir   = Join-Path $here '..\tests\playwright'
$planter = Join-Path $here 'plant-test-pins.ps1'

if ($Deep -and $Target -ne 'DEV') {
    throw "-Deep is DEV-only (it plants PINs + signs in as each role). Use the anonymous gate against PROD."
}

Push-Location $pwDir
try {
    $env:TARGET      = $Target
    $env:SURVEY_SLUG = $SurveySlug
    if ($BaseUrl) { $env:CEH_BASE_URL = $BaseUrl; Write-Host "Target: $BaseUrl (explicit)" -ForegroundColor Cyan }
    else          { Remove-Item Env:CEH_BASE_URL -ErrorAction SilentlyContinue; Write-Host "Target: $Target" -ForegroundColor Cyan }

    if (-not $Deep) {
        Write-Host 'Running ANONYMOUS post-deploy validation (public routes + auth/role-gating contract)...' -ForegroundColor Cyan
        npx playwright test comprehensive-validation `
            --project="iPhone SE (narrow viewport)" --grep '@validate.*anonymous' --reporter=list
        return
    }

    # --- Deep (DEV) -----------------------------------------------------------
    function Plant([string]$email, [int]$role, [int]$count) {
        try { return (& $planter -OrganizerEmail $email -Role $role -Count $count) }
        catch { Write-Warning "Plant failed for $email (role $role): $($_.Exception.Message)"; return '' }
    }

    Write-Host 'Planting single-use DEV PINs (valid ~14 min)...' -ForegroundColor Cyan
    # One PIN per login: the per-role authed sweep logs in once, and each
    # non-organizer also logs in once for the negative role-gating test.
    $env:ORGANIZER_EMAIL = $OrganizerEmail; $env:ADMIN_PIN     = Plant $OrganizerEmail 0 1
    $env:SPEAKER_EMAIL   = $SpeakerEmail;   $env:SPEAKER_PIN   = Plant $SpeakerEmail   1 2
    $env:VOLUNTEER_EMAIL = $VolunteerEmail; $env:VOLUNTEER_PIN = Plant $VolunteerEmail 3 2
    $env:ATTENDEE_EMAIL  = $AttendeeEmail;  $env:ATTENDEE_PIN  = Plant $AttendeeEmail  5 2
    $env:SPONSOR_EMAIL   = $SponsorEmail;   $env:SPONSOR_PIN   = Plant $SponsorEmail   4 2

    Write-Host 'Running FULL post-deploy validation (anonymous + per-role deep sweep + role-gating)...' -ForegroundColor Cyan
    npx playwright test comprehensive-validation `
        --project="iPhone SE (narrow viewport)" --grep '@validate' --reporter=list

    # Switch-user (real impersonation) round-trip. Needs a fresh organizer PIN
    # (the comprehensive sweep above consumed the first one); plant one more and
    # drive the feature end-to-end: switch INTO a user (lands on their hub, not
    # EditOnBehalf), no nested impersonation, then Return to organizer.
    Write-Host 'Running switch-user impersonation round-trip (real act-as)...' -ForegroundColor Cyan
    $env:ADMIN_PIN = Plant $OrganizerEmail 0 1
    npx playwright test feature-impersonation `
        --project="iPhone SE (narrow viewport)" --grep '@feature' --reporter=list
}
finally { Pop-Location }
