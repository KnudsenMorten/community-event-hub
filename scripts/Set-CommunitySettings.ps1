<#
.SYNOPSIS
    Bootstrap a freshly-deployed Community Event Hub: set the application settings the
    infrastructure template does NOT set, and report the ones that are missing or still point at
    somebody else's event.

.DESCRIPTION
    The infrastructure template (infra/) provisions the resources and sets the plumbing settings —
    database, Key Vault, storage, telemetry, the external-write guards. It deliberately does NOT set
    the two groups a community must supply for themselves:

      * E-MAIL      the hub cannot sign anybody in without it (sign-in is a PIN sent by e-mail)
      * INTEGRATIONS which external systems this community actually uses

    🔴 UNTIL THOSE ARE SET, A DEPLOYED HUB LOOKS HEALTHY AND IS NOT USABLE.
       There is no startup validation: the site boots, /health returns 200, and the first symptom is
       that a sign-in PIN never arrives — with the real cause several layers down inside an SMTP
       retry. `-Check` exists to turn that into a sentence.

    ⚠️ AND THE DEFAULTS ARE NOT NEUTRAL. Unset e-mail settings fall back to the UPSTREAM event's own
       identity — its from-address, its display name, its organizer inbox, its relay. A community
       that does not override them is trying to send mail AS somebody else's domain: a decent relay
       refuses it outright, and the organizer notices that do go out land in the upstream event's
       inbox, not yours. `-Check` reports these as errors, not as "not set yet".

    THREE MODES:

      -Check                Read a deployed app's settings and report what is missing, wrong or
                            inherited. Changes nothing.
      -OutFile <path>       Write the settings as JSON, for
                            `az webapp config appsettings set --settings @<path>`, or as a record
                            you can review before applying. Nothing is contacted.
      -Apply                Set them directly on the web app (and the jobs app, if given).

.NOTES
    ── WHAT IS REQUIRED, AND WHAT MERELY MATTERS ────────────────────────────────────────────────
    REQUIRED for the hub to function at all:
      Email__SmtpHost, Email__SmtpPort, Email__SmtpUsername, Email__SmtpKey, Email__FromAddress

    STRONGLY RECOMMENDED on a first environment:
      Email__RedirectAllTo   every outgoing message goes to this ONE address instead of its real
                             recipient. Nothing can reach your community by accident while you are
                             still setting up. Clear it when you are ready to go live — that is the
                             single switch that opens the floodgates, so set it deliberately.
      Email__FromDisplayName, Email__OrganizerInbox

    OPTIONAL — every integration is off unless switched on here, and an off integration makes NO
    external calls at all. A community with none of these still gets participants, PIN sign-in,
    tasks, reminders, forms, sessions, surveys, sponsors and e-mail.

    ── THE SECOND GUARD ─────────────────────────────────────────────────────────────────────────
    Enabling an integration does not by itself permit WRITING to it. `Integrations__AllowExternalWrites`
    and the per-system switches stay off until you decide each system is safe to write to. This
    script leaves them alone unless you pass -AllowExternalWrites, because "read from it" and
    "write to it" are different decisions and the second one is not reversible in other people's
    systems.

    ── SECRETS ──────────────────────────────────────────────────────────────────────────────────
    -SmtpKey is a secret. Prefer a Key Vault reference over the literal value:
        -SmtpKey '@Microsoft.KeyVault(VaultName=<vault>;SecretName=<secret>)'
    With -OutFile the value is written to the file as given, so do not commit a file containing a
    literal key. -Check never prints secret values, only whether they are set.

.EXAMPLE
    ./Set-CommunitySettings.ps1 -Check -ResourceGroup rg-myhub-prod -WebAppName myhub-web
    Ask a deployed environment what is wrong with it. Changes nothing.

.EXAMPLE
    ./Set-CommunitySettings.ps1 -OutFile settings.json `
        -SmtpHost smtp.example.net -SmtpUsername apikey -SmtpKey '<secret>' `
        -FromAddress 'info@mycommunity.org' -FromDisplayName 'My Community' `
        -OrganizerInbox 'info@mycommunity.org' -RedirectAllTo 'me@mycommunity.org'
    Produce the settings file for a first environment, with all mail safely redirected to one inbox.

.EXAMPLE
    ./Set-CommunitySettings.ps1 -Apply -ResourceGroup rg-myhub-prod `
        -WebAppName myhub-web -JobsAppName myhub-jobs `
        -SmtpHost smtp.example.net -SmtpUsername apikey -SmtpKey '<secret>' `
        -FromAddress 'info@mycommunity.org' -EnableSessionize
    Apply to BOTH hosts. The jobs host runs the scheduled work and needs the same settings.
#>

#Requires -Version 7.0

[CmdletBinding(DefaultParameterSetName = 'Check')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Check')]    [switch]$Check,
    [Parameter(Mandatory, ParameterSetName = 'OutFile')]  [string]$OutFile,
    [Parameter(Mandatory, ParameterSetName = 'Apply')]    [switch]$Apply,

    # --- where (Check / Apply) -------------------------------------------------------------
    [string]$ResourceGroup,
    [string]$WebAppName,
    # The Functions host. It runs the scheduled jobs and needs the SAME settings — a hub with
    # settings on the web app only has a working site and a silent back end.
    [string]$JobsAppName,

    # --- e-mail ---------------------------------------------------------------------------
    [string]$SmtpHost,
    [int]$SmtpPort = 587,
    [string]$SmtpUsername,
    [string]$SmtpKey,
    [string]$FromAddress,
    [string]$FromDisplayName,
    [string]$OrganizerInbox,
    # Send EVERYTHING to this one address instead of the real recipient. The safest way to run a
    # new environment. Clear it to go live.
    [string]$RedirectAllTo,
    # Stops every outgoing message. Useful while investigating; nothing queues up behind it.
    [switch]$EmailKillSwitch,

    # --- integrations (all off unless switched on) -----------------------------------------
    [switch]$EnableZoho,
    [switch]$EnableSessionize,
    [switch]$EnableWooCommerce,
    [switch]$EnableEconomicErp,
    [switch]$EnableEconomicRoles,
    [switch]$EnableLinkedIn,
    [switch]$EnableCompanyManager,
    [switch]$EnableSharePoint,
    [switch]$EnableOpenAI,
    [switch]$EnableCvrLookup,
    [switch]$EnableFxRates,

    # --- writing to those systems (a separate decision) ------------------------------------
    [switch]$AllowExternalWrites,
    [switch]$AllowWriteZoho,
    [switch]$AllowWriteLinkedIn,
    [switch]$AllowWriteErp,
    [switch]$AllowWriteSharePoint,
    [switch]$AllowWriteWebshop
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# 🔴 Captured HERE, at script scope, and never read inside a function.
#
# `$PSBoundParameters` inside a function is the FUNCTION's bound parameters, not the script's — so
# `if ($PSBoundParameters.ContainsKey('RedirectAllTo'))` inside a helper is always false, and the
# setting is SILENTLY DROPPED. That bug shipped for about ten minutes and was the worst kind: a
# community passes -RedirectAllTo expecting every mail to be captured, gets no warning, and the
# environment mails their real members instead.
$Bound = $PSBoundParameters

# Defaults that belong to the UPSTREAM event. A fork inheriting any of these is misconfigured, not
# merely unconfigured — see the warning in .DESCRIPTION.
$UpstreamDefaults = @{
    'Email__SmtpHost'        = 'smtp-relay.brevo.com'
    'Email__FromAddress'     = 'info@expertslive.dk'
    'Email__FromDisplayName' = 'Experts Live Denmark'
    'Email__OrganizerInbox'  = 'info@expertslive.dk'
}

function Test-AzCli {
    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw "The Azure CLI (az) is required for -Check and -Apply. Use -OutFile to produce a " +
              "settings file without it."
    }
}

function Get-AppSettings([string]$rg, [string]$name) {
    $json = az webapp config appsettings list -g $rg -n $name -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $json) {
        throw "Could not read app settings for '$name' in '$rg'. Check the names, and that " +
              "'az login' has been run against the right subscription."
    }
    $map = @{}
    foreach ($s in ($json | ConvertFrom-Json)) { $map[$s.name] = $s.value }
    return $map
}

# ---------------------------------------------------------------------------------------------
# Build the settings this invocation wants
# ---------------------------------------------------------------------------------------------
function Build-Settings {
    $s = [ordered]@{}

    if ($SmtpHost)        { $s['Email__SmtpHost'] = $SmtpHost }
    # Always emitted, default included: -Check treats a missing port as a blocking problem, so
    # omitting it here would make this script produce an environment its own check then fails.
    $s['Email__SmtpPort'] = "$SmtpPort"
    if ($SmtpUsername)    { $s['Email__SmtpUsername'] = $SmtpUsername }
    if ($SmtpKey)         { $s['Email__SmtpKey'] = $SmtpKey }
    if ($FromAddress)     { $s['Email__FromAddress'] = $FromAddress }
    if ($FromDisplayName) { $s['Email__FromDisplayName'] = $FromDisplayName }
    if ($OrganizerInbox)  { $s['Email__OrganizerInbox'] = $OrganizerInbox }
    # Passing an EMPTY -RedirectAllTo is meaningful: it is how you go live. So this tests whether
    # the parameter was supplied, not whether it has a value.
    if ($Bound.ContainsKey('RedirectAllTo')) { $s['Email__RedirectAllTo'] = $RedirectAllTo }
    if ($EmailKillSwitch) { $s['Email__KillSwitch'] = 'true' }

    # Integrations. A switch that is NOT passed is written as false rather than omitted, so running
    # this script produces a KNOWN posture instead of leaving whatever was there before.
    $s['Zoho__Enabled']           = "$([bool]$EnableZoho)".ToLower()
    $s['Sessionize__Enabled']     = "$([bool]$EnableSessionize)".ToLower()
    $s['WooCommerce__Enabled']    = "$([bool]$EnableWooCommerce)".ToLower()
    $s['EconomicErp__Enabled']    = "$([bool]$EnableEconomicErp)".ToLower()
    $s['EconomicRoles__Enabled']  = "$([bool]$EnableEconomicRoles)".ToLower()
    $s['LinkedIn__Enabled']       = "$([bool]$EnableLinkedIn)".ToLower()
    $s['CompanyManager__Enabled'] = "$([bool]$EnableCompanyManager)".ToLower()
    $s['SharePoint__Enabled']     = "$([bool]$EnableSharePoint)".ToLower()
    $s['OpenAI__Enabled']         = "$([bool]$EnableOpenAI)".ToLower()
    $s['CvrLookup__Enabled']      = "$([bool]$EnableCvrLookup)".ToLower()
    $s['FxRates__Enabled']        = "$([bool]$EnableFxRates)".ToLower()

    # 🔒 Write guards are only touched when explicitly asked. Silently setting them to false could
    # switch OFF a working integration on a re-run; silently setting them true is worse.
    if ($AllowExternalWrites)  { $s['Integrations__AllowExternalWrites'] = 'true' }
    if ($AllowWriteZoho)       { $s['Integrations__ExternalWrites__Zoho'] = 'true' }
    if ($AllowWriteLinkedIn)   { $s['Integrations__ExternalWrites__LinkedIn'] = 'true' }
    if ($AllowWriteErp)        { $s['Integrations__ExternalWrites__Erp'] = 'true' }
    if ($AllowWriteSharePoint) { $s['Integrations__ExternalWrites__SharePoint'] = 'true' }
    if ($AllowWriteWebshop)    { $s['Integrations__ExternalWrites__Webshop'] = 'true' }

    return $s
}

# ---------------------------------------------------------------------------------------------
# CHECK
# ---------------------------------------------------------------------------------------------
if ($Check) {
    if (-not $ResourceGroup -or -not $WebAppName) {
        throw "-Check needs -ResourceGroup and -WebAppName."
    }
    Test-AzCli

    $apps = @([pscustomobject]@{ Role = 'web'; Name = $WebAppName })
    if ($JobsAppName) { $apps += [pscustomobject]@{ Role = 'jobs'; Name = $JobsAppName } }

    $problems = 0
    foreach ($app in $apps) {
        Write-Host ""
        Write-Host "── $($app.Name)  [$($app.Role)] ─────────────────────────────────────────"
        $cur = Get-AppSettings $ResourceGroup $app.Name

        # 1. Required for the hub to work at all.
        foreach ($k in @('Email__SmtpHost', 'Email__SmtpPort', 'Email__SmtpUsername',
                         'Email__SmtpKey', 'Email__FromAddress')) {
            if (-not $cur.ContainsKey($k) -or [string]::IsNullOrWhiteSpace($cur[$k])) {
                Write-Host "  MISSING  $k" -ForegroundColor Red
                $problems++
            }
        }

        # 2. Present, but inherited from the upstream event — worse than missing.
        foreach ($k in $UpstreamDefaults.Keys) {
            if ($cur.ContainsKey($k) -and $cur[$k] -eq $UpstreamDefaults[$k]) {
                Write-Host "  INHERITED $k = $($cur[$k])" -ForegroundColor Red
                Write-Host "            That is the UPSTREAM event's value, not yours." -ForegroundColor Red
                $problems++
            }
        }

        # 3. Live-mail posture — not a fault, but the thing people forget they left on or off.
        $redirect = $(if ($cur.ContainsKey('Email__RedirectAllTo')) { $cur['Email__RedirectAllTo'] } else { '' })
        if ([string]::IsNullOrWhiteSpace($redirect)) {
            Write-Host "  LIVE MAIL Email__RedirectAllTo is empty - messages go to their REAL recipients." -ForegroundColor Yellow
        }
        else {
            Write-Host "  SAFE MODE Email__RedirectAllTo = $redirect (nothing reaches anyone else)" -ForegroundColor Green
        }
        if ($cur.ContainsKey('Email__KillSwitch') -and $cur['Email__KillSwitch'] -match 'true') {
            Write-Host "  KILLED    Email__KillSwitch is ON - NO mail is sent at all." -ForegroundColor Yellow
        }

        # 4. Integrations: report the posture, and catch "enabled but not configured".
        $enabled = @()
        foreach ($k in ($cur.Keys | Where-Object { $_ -like '*__Enabled' } | Sort-Object)) {
            if ($cur[$k] -match 'true') { $enabled += $k -replace '__Enabled', '' }
        }
        Write-Host "  Integrations ON: $(if ($enabled) { $enabled -join ', ' } else { '(none - the hub makes no external calls)' })"
        if ($enabled -contains 'Sessionize' -and
            (-not $cur.ContainsKey('Sessionize__EndpointId') -or
             [string]::IsNullOrWhiteSpace($cur['Sessionize__EndpointId']) -or
             $cur['Sessionize__EndpointId'] -like 'REPLACE_*')) {
            Write-Host "  MISSING  Sessionize__EndpointId (Sessionize is ON but has no endpoint)" -ForegroundColor Red
            $problems++
        }
    }

    Write-Host ""
    if ($problems -eq 0) {
        Write-Host ">> No blocking problems found." -ForegroundColor Green
        Write-Host "   (This checks SETTINGS only. It cannot tell you whether the relay accepts your"
        Write-Host "    credentials — send yourself a sign-in PIN to prove that end to end.)"
    }
    else {
        Write-Host ">> $problems blocking problem(s). Until these are fixed the site will look" -ForegroundColor Red
        Write-Host "   healthy and nobody will be able to sign in, because sign-in is a PIN by e-mail." -ForegroundColor Red
        exit 1
    }
    return
}

# ---------------------------------------------------------------------------------------------
# OUTFILE / APPLY
# ---------------------------------------------------------------------------------------------
$settings = Build-Settings

# Refuse a half-configured bootstrap rather than producing an environment that fails later.
$missing = @('Email__SmtpHost', 'Email__SmtpUsername', 'Email__SmtpKey', 'Email__FromAddress') |
    Where-Object { -not $settings.Contains($_) }
if ($missing) {
    throw "These are required for the hub to work and were not supplied: $($missing -join ', '). " +
          "Sign-in is a PIN sent by e-mail, so an environment without them boots and then cannot " +
          "sign anybody in. Pass -SmtpHost/-SmtpUsername/-SmtpKey/-FromAddress."
}
if ($settings['Email__FromAddress'] -eq $UpstreamDefaults['Email__FromAddress']) {
    throw "-FromAddress is the UPSTREAM event's address ($($UpstreamDefaults['Email__FromAddress'])). " +
          "Use your own community's address — your relay will refuse to send as a domain you do not own."
}

Write-Host ""
Write-Host "Settings to be written ($($settings.Count)):"
foreach ($k in $settings.Keys) {
    $v = $(if ($k -match 'Key|Secret|Password') { '(secret - not shown)' } else { $settings[$k] })
    Write-Host ("  {0,-42} {1}" -f $k, $v)
}
if (-not $settings.Contains('Email__RedirectAllTo') -or
    [string]::IsNullOrWhiteSpace($settings['Email__RedirectAllTo'])) {
    Write-Host ""
    Write-Host "  !! Email__RedirectAllTo is not set: this environment will mail REAL people." -ForegroundColor Yellow
    Write-Host "     On a new environment, pass -RedirectAllTo <your address> first." -ForegroundColor Yellow
}

if ($OutFile) {
    # The shape `az webapp config appsettings set --settings @file` expects.
    $arr = @($settings.Keys | ForEach-Object {
        [ordered]@{ name = $_; value = $settings[$_]; slotSetting = $false }
    })
    $arr | ConvertTo-Json -Depth 4 | Set-Content -Path $OutFile -Encoding utf8
    Write-Host ""
    Write-Host ">> Written to $OutFile"
    Write-Host "   Apply with:"
    Write-Host "     az webapp config appsettings set -g <rg> -n <app> --settings @$OutFile"
    if ($settings.Contains('Email__SmtpKey') -and $settings['Email__SmtpKey'] -notlike '@Microsoft.KeyVault*') {
        Write-Host ""
        Write-Host "  !! This file contains a literal SMTP key. Do not commit it." -ForegroundColor Yellow
        Write-Host "     Prefer -SmtpKey '@Microsoft.KeyVault(VaultName=...;SecretName=...)'." -ForegroundColor Yellow
    }
    return
}

# --- Apply -----------------------------------------------------------------------------------
if (-not $ResourceGroup -or -not $WebAppName) { throw "-Apply needs -ResourceGroup and -WebAppName." }
Test-AzCli

if (-not $JobsAppName) {
    Write-Host ""
    Write-Host "  !! No -JobsAppName given. The Functions host runs every scheduled job and needs" -ForegroundColor Yellow
    Write-Host "     the SAME settings — without them you get a working site and a silent back end." -ForegroundColor Yellow
}

$targets = @($WebAppName)
if ($JobsAppName) { $targets += $JobsAppName }

$pairs = @($settings.Keys | ForEach-Object { "$_=$($settings[$_])" })
foreach ($t in $targets) {
    Write-Host ""
    Write-Host ">> Applying $($settings.Count) setting(s) to $t ..."
    az webapp config appsettings set -g $ResourceGroup -n $t --settings @pairs -o none
    if ($LASTEXITCODE -ne 0) { throw "Failed to set app settings on '$t'." }
    Write-Host ">> $t updated."
}

Write-Host ""
Write-Host ">> Setting app settings RESTARTS the app; give it a minute before testing."
Write-Host ">> Then prove it end to end: request a sign-in PIN and confirm the mail arrives."
Write-Host "   -Check verifies the settings exist; only a real PIN proves the relay accepts them."
