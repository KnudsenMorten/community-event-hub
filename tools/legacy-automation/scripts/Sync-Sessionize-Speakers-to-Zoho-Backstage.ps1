# ------------------------------------------------------------------------------------------------
# Experts Live Denmark - Sync Sessionize speakers -> Zoho Backstage
#
# Purpose:
#   Read the Sessionize 'flattened accepted sessions' Excel export and create / update
#   speakers in Zoho Backstage via the v3 API. Idempotent on email (case-insensitive).
#
# Support: Morten Knudsen - mok@expertslive.dk
# ------------------------------------------------------------------------------------------------

param(
    [Parameter(Mandatory=$true)][string]$ExcelPath,
    [switch]$WhatIf,
    # 2-letter country code applied when the Sessionize row has no country
    [string]$DefaultCountry = "DK"
)

Write-Output "***********************************************************************************************"
Write-Output "Experts Live Denmark - Sync Sessionize speakers -> Zoho Backstage"
Write-Output ""
Write-Output "Purpose:"
Write-Output "  Read the Sessionize 'flattened accepted sessions' Excel export and create / update"
Write-Output "  speakers in Zoho Backstage via the v3 API. Idempotent on email (case-insensitive)."
Write-Output ""
Write-Output "Support: Morten Knudsen - mok@expertslive.dk"
Write-Output "***********************************************************************************************"

$ScriptDirectory = $PSScriptRoot
Import-Module "$ScriptDirectory\Secrets.psm1" -Global -Force -WarningAction SilentlyContinue
Import_Secrets

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

if (-not (Get-Module -ListAvailable -Name ImportExcel)) {
    throw "ImportExcel module is required. Install-Module ImportExcel -Scope CurrentUser"
}

if (-not (Test-Path -LiteralPath $ExcelPath)) {
    throw "File not found: $ExcelPath"
}

# ------------------------------------------------------------------------------------------------
# Zoho helpers
# ------------------------------------------------------------------------------------------------

function Get-ZohoAccessToken {
    try {
        return (Invoke-RestMethod -Method POST -Uri "https://accounts.zoho.eu/oauth/v2/token" -Body @{
            refresh_token = $ZohoRefreshToken
            client_id     = $ZohoClientId
            client_secret = $ZohoClientSecret
            grant_type    = "refresh_token"
        }).access_token
    } catch {
        Write-Host "[Zoho] Failed to refresh access token: $($_.Exception.Message)" -ForegroundColor Red
        return $null
    }
}

function Invoke-ZohoApi {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Token,
        $Body = $null
    )
    $headers = @{ Authorization = "Zoho-oauthtoken $Token"; Accept = "application/json" }
    if ($Body) {
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers `
            -ContentType "application/json" -Body ($Body | ConvertTo-Json -Depth 12)
    }
    return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers
}

function Get-ZohoSpeakersIndex {
    param([Parameter(Mandatory)][string]$Token)
    $base = $ZohoApiBase.TrimEnd('/')
    $page = 1
    $idx  = @{}
    do {
        $url  = "$base/portals/$ZohoPortalId/events/$ZohoEventId/speakers?per_page=200&page=$page"
        Write-Host "[Zoho] GET $url"
        try {
            $resp = Invoke-ZohoApi -Method GET -Uri $url -Token $Token
        } catch {
            Write-Host "[Zoho] WARN failed to read speakers page $page : $($_.Exception.Message)" -ForegroundColor Yellow
            break
        }
        $list = @()
        if     ($resp.PSObject.Properties.Name -contains 'speakers') { $list = $resp.speakers }
        elseif ($resp.PSObject.Properties.Name -contains 'data')     { $list = $resp.data }
        foreach ($s in ($list | Where-Object { $_ })) {
            $email = $null
            foreach ($f in @('email','email_address')) {
                if ($s.PSObject.Properties.Name -contains $f -and $s.$f) { $email = [string]$s.$f; break }
            }
            if ($email) { $idx[$email.Trim().ToLowerInvariant()] = $s }
        }
        $hasMore = $false
        if ($resp.PSObject.Properties.Name -contains 'pagination') {
            $hasMore = [bool]$resp.pagination.has_more_items
        }
        $page++
    } while ($hasMore)
    return $idx
}

# ------------------------------------------------------------------------------------------------
# Sessionize parsing (dedupe across one-row-per-session export)
# ------------------------------------------------------------------------------------------------

function Get-FirstNonEmpty {
    param($Row, [string[]]$Candidates)
    foreach ($name in $Candidates) {
        $prop = $Row.PSObject.Properties | Where-Object { $_.Name -ieq $name } | Select-Object -First 1
        if ($prop -and -not [string]::IsNullOrWhiteSpace([string]$prop.Value)) {
            return [string]$prop.Value
        }
    }
    return ""
}

Write-Host ""
Write-Host "[Sess] Reading $ExcelPath ..."
$rows = Import-Excel -Path $ExcelPath
Write-Host "[Sess]   $($rows.Count) row(s) (sessions x speakers)."

$bySpeaker = @{}
foreach ($r in $rows) {
    $email = (Get-FirstNonEmpty $r @('Email','E-mail','Email Address','Speaker 1 Email')).Trim().ToLowerInvariant()
    if (-not $email) { continue }
    if ($bySpeaker.ContainsKey($email)) { continue }   # first occurrence wins
    $bySpeaker[$email] = [pscustomobject]@{
        Email         = $email
        FirstName     = Get-FirstNonEmpty $r @('FirstName','First Name','Speaker 1 FirstName')
        LastName      = Get-FirstNonEmpty $r @('LastName','Last Name','Surname','Speaker 1 LastName')
        TagLine       = Get-FirstNonEmpty $r @('TagLine','Tag Line','Tagline','Headline','Speaker 1 Tagline')
        Bio           = Get-FirstNonEmpty $r @('Bio','Biography','Speaker 1 Biography')
        Blog          = Get-FirstNonEmpty $r @('Blog','Website','Speaker 1 Blog')
        LinkedIn      = Get-FirstNonEmpty $r @('LinkedIn','Speaker 1 LinkedIn')
        Twitter       = Get-FirstNonEmpty $r @('X (Twitter)','Twitter','Twitter/X','X','Speaker 1 Twitter','Speaker 1 Twitter/X')
        ProfilePicture= Get-FirstNonEmpty $r @('Profile Picture','Photo','Speaker 1 Profile Picture')
        Country       = Get-FirstNonEmpty $r @('Country','Speaker 1 Country')
    }
}
Write-Host "[Sess]   $($bySpeaker.Count) unique speaker(s) after dedupe."

# ------------------------------------------------------------------------------------------------
# Main
# ------------------------------------------------------------------------------------------------

Write-Host ""
Write-Host "[Zoho] Refreshing access token..."
$token = Get-ZohoAccessToken
if (-not $token) { throw "Could not obtain Zoho access token. Aborting." }

Write-Host "[Zoho] Pulling existing speakers index..."
$zohoIdx = Get-ZohoSpeakersIndex -Token $token
Write-Host "[Zoho]   $($zohoIdx.Count) existing speaker(s) in Backstage."

$counters = @{ Created = 0; Updated = 0; SkippedExisting = 0; Failed = 0; NoPhotoEndpoint = 0 }

$endpoint = "$($ZohoApiBase.TrimEnd('/'))/portals/$ZohoPortalId/events/$ZohoEventId/speakers"

foreach ($s in $bySpeaker.Values) {

    $country = if ($s.Country -and $s.Country.Length -ge 2) { $s.Country.Substring(0,2).ToUpperInvariant() } else { $DefaultCountry }

    $payload = @{
        email               = $s.Email
        name                = $s.FirstName
        last_name           = $s.LastName
        country             = $country
        company             = ""                   # Sessionize doesn't expose company by default
        designation         = $s.TagLine           # Sessionize Tagline ~ designation
        description         = $s.Bio
        linkedin            = $s.LinkedIn
        twitter             = $s.Twitter
    }
    # Strip empties so Zoho doesn't reject blanks.
    $clean = @{}
    foreach ($k in $payload.Keys) {
        $v = $payload[$k]
        if ($null -ne $v -and -not [string]::IsNullOrWhiteSpace([string]$v)) { $clean[$k] = $v }
    }

    Write-Host ""
    Write-Host "=> $($s.Email)  $($s.FirstName) $($s.LastName)"

    if ($zohoIdx.ContainsKey($s.Email)) {
        $counters.SkippedExisting++
        Write-Host "[Zoho] EXISTS in Backstage -- skipping create (update endpoint TBD)"
        continue
    }

    if ($WhatIf) {
        Write-Host "[Zoho] WHATIF POST speaker '$($s.Email)' payload keys: $($clean.Keys -join ', ')" -ForegroundColor Yellow
        continue
    }

    try {
        $created = Invoke-ZohoApi -Method POST -Uri $endpoint -Token $token -Body $clean
        $counters.Created++
        Write-Host "[Zoho] CREATED speaker '$($s.Email)' (id=$($created.id))" -ForegroundColor Green

        # ---- Photo upload (NOT supported by documented v3 endpoint) ----------
        # Zoho's public v3 speakers API doesn't document a photo upload path
        # at the time of writing. The Sessionize 'Profile Picture' field is a
        # URL we COULD pass when Zoho exposes it; until then, the speaker
        # uploads their photo through the Backstage UI after first sign-in.
        if ($s.ProfilePicture) {
            $counters.NoPhotoEndpoint++
            Write-Host "[Zoho] (photo URL provided: $($s.ProfilePicture) -- no documented v3 photo-upload endpoint yet; speaker uploads via Backstage UI)" -ForegroundColor DarkYellow
        }
    } catch {
        $counters.Failed++
        Write-Host "[Zoho] FAIL create speaker '$($s.Email)': $($_.Exception.Message)" -ForegroundColor Red
    }
}

Write-Host ""
Write-Host "==============================================="
Write-Host "Summary"
Write-Host "==============================================="
Write-Host "  Unique speakers     : $($bySpeaker.Count)"
Write-Host "  Created             : $($counters.Created)"
Write-Host "  Skipped (existing)  : $($counters.SkippedExisting)"
Write-Host "  Failed              : $($counters.Failed)"
Write-Host "  Photos not uploaded : $($counters.NoPhotoEndpoint)  (no documented endpoint)"
if ($WhatIf) { Write-Host "  (WhatIf -- no writes performed)" -ForegroundColor Yellow }
