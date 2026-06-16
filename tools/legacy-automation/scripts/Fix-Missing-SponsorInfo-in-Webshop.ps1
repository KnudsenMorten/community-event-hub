#------------------------------------------------------------------------------------------------
Write-Output "***********************************************************************************************"
Write-Output "Experts Live Denmark Integration - Sponsors Excel -> Webshop"
Write-Output ""
Write-Output "Purpose: Reads sponsor data from Excel and updates webshop company public fields"
Write-Output "         (company_name_public, web_address, linkedin_url, twitter_url)"
Write-Output "         Matches Excel rows to webshop companies using normalised name comparison."
Write-Output ""
Write-Output "Support: Morten Knudsen - mok@expertslive.dk"
Write-Output "***********************************************************************************************"
#------------------------------------------------------------------------------------------------

$ScriptDirectory = $PSScriptRoot
$global:PathScripts = Split-Path -parent $ScriptDirectory

Import-Module "$ScriptDirectory\Secrets.psm1" -Global -Force -WarningAction SilentlyContinue
Import_Secrets

# Path to the sponsors Excel file
$ExcelPath = 'C:\Scripts-ELDK-Automation\OUTPUT\Webshop-SponsorsInfo.xlsx'

# Base URL for Company Manager REST API
$BaseUrl = "https://expertslive.dk"
$CmBase  = "$BaseUrl/wp-json/company-manager/v1"

# TLS 1.2 for PS 5.1
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

#------------------------------------------------------------------------------------------------
# Helpers
#------------------------------------------------------------------------------------------------

function Get-CmAuthHeader {
    $pair  = "{0}:{1}" -f $WpUserApi, $WpAppPassword
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($pair)
    return @{ Authorization = 'Basic ' + [Convert]::ToBase64String($bytes) }
}

function Invoke-CmRequest {
    param(
        [ValidateSet('GET','POST','PUT','DELETE')][string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Path,
        [hashtable]$Query = @{},
        [object]$Body = $null
    )

    $headers = Get-CmAuthHeader

    $queryString = ""
    if ($Query.Count -gt 0) {
        $pairs = foreach ($k in $Query.Keys) {
            "{0}={1}" -f [Uri]::EscapeDataString($k), [Uri]::EscapeDataString([string]$Query[$k])
        }
        $queryString = $pairs -join "&"
    }

    $uri = if ($queryString) { "$CmBase/$Path`?$queryString" } else { "$CmBase/$Path" }

    $invokeParams = @{
        Method      = $Method
        Uri         = $uri
        Headers     = $headers
        ErrorAction = 'Stop'
    }

    if ($Body) {
        $jsonBody = $Body | ConvertTo-Json -Depth 10
        $invokeParams['Body']        = [System.Text.Encoding]::UTF8.GetBytes($jsonBody)
        $invokeParams['ContentType'] = 'application/json; charset=utf-8'
        $invokeParams['Headers']['Content-Type'] = 'application/json; charset=utf-8'
    }

    try {
        return Invoke-RestMethod @invokeParams
    }
    catch {
        $status   = $null
        $respText = $null

        if ($_.Exception.Response -and ($_.Exception.Response -is [System.Net.HttpWebResponse])) {
            $status = [int]$_.Exception.Response.StatusCode
            try {
                $sr       = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
                $respText = $sr.ReadToEnd()
                $sr.Close()
            } catch {}
        }

        $msg = $_.Exception.Message
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $msg = "$msg | $($_.ErrorDetails.Message)" }
        $statusMsg = if ($status) { " (HTTP $status)" } else { "" }

        throw "Company Manager API call failed$($statusMsg): $msg`nURI: $uri`nResponse: $respText"
    }
}

function Get-CmAllCompanies {
    $all  = @()
    $page = 1
    $per  = 200

    do {
        Write-Host "[GET] Company Manager GET companies (page $page)"
        $result = Invoke-CmRequest -Method GET -Path "companies" -Query @{ per_page = $per; page = $page }
        if ($result) { $all += $result }
        $page++
    } while ($result -and $result.Count -eq $per)

    Write-Host "   Total webshop companies retrieved: $($all.Count)"
    return $all
}

function Normalize-Name {
    param([string]$Name)

    if ([string]::IsNullOrWhiteSpace($Name)) { return '' }

    $n = $Name.Trim().ToLower()

    # Replace punctuation with spaces
    $n = $n -replace '[\\.,;:/\\\\\-\(\)&]', ' '

    # Remove common legal suffixes
    $suffixes = @(
        'llc','inc','ltd','gmbh','aps','a/s','ab','bv','oy','oyj',
        'as','ag','sarl','sas','spa','pte','limited','corp',
        'corporation','company','co','dk','group','technologies',
        'technology','software','systems','solutions'
    )
    foreach ($suffix in $suffixes) {
        $pattern = '(^|\s)' + [regex]::Escape($suffix) + '($|\s)'
        $n = [regex]::Replace($n, $pattern, ' ')
    }

    # Collapse whitespace
    $n = ($n -replace '\s+', ' ').Trim()

    return $n
}

function Find-BestCompanyMatch {
    param(
        [string]$SourceName,
        [array]$WebshopCompanies
    )

    $sourceNorm = Normalize-Name -Name $SourceName
    if ([string]::IsNullOrWhiteSpace($sourceNorm)) { return $null }

    $bestMatch = $null
    $bestScore = 0

    foreach ($company in $WebshopCompanies) {
        # Try matching against both legal name and public name
        $candidates = @(
            [string]$company.name,
            [string]$company.company_name_public
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

        foreach ($candidate in $candidates) {
            $candidateNorm = Normalize-Name -Name $candidate

            if ([string]::IsNullOrWhiteSpace($candidateNorm)) { continue }

            # Score: exact=4, startsWith=3, contains(source in candidate)=2, contains(candidate in source)=1
            $score = 0
            if ($candidateNorm -eq $sourceNorm)                             { $score = 4 }
            elseif ($candidateNorm.StartsWith($sourceNorm) -or
                    $sourceNorm.StartsWith($candidateNorm))                  { $score = 3 }
            elseif ($candidateNorm.Contains($sourceNorm))                   { $score = 2 }
            elseif ($sourceNorm.Contains($candidateNorm))                   { $score = 1 }

            if ($score -gt $bestScore) {
                $bestScore = $score
                $bestMatch = $company
            }
        }
    }

    # Only return if we have at least a startsWith match to avoid false positives
    if ($bestScore -ge 2) { return $bestMatch }

    return $null
}

function Clean-Url {
    param([string]$Url)

    if ([string]::IsNullOrWhiteSpace($Url)) { return '' }

    $trimmed = $Url.Trim()

    # If it looks like a bare name rather than a URL (no dot, no slash, no http), skip it
    if ($trimmed -notmatch '[\./]' -and $trimmed -notmatch '^https?://') {
        return ''
    }

    # Ensure https:// prefix
    if ($trimmed -notmatch '^https?://') {
        $trimmed = "https://" + $trimmed
    }

    # Remove trailing slashes for consistency
    $trimmed = $trimmed.TrimEnd('/')

    return $trimmed
}

function Clean-TwitterUrl {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }

    $trimmed = $Value.Trim()

    # Looks like a bare name rather than a URL
    if ($trimmed -notmatch '[\./]' -and $trimmed -notmatch '^https?://' -and $trimmed -notmatch '^@') {
        return ''
    }

    # Strip leading @ and build URL
    if ($trimmed.StartsWith('@')) {
        return "https://x.com/" + $trimmed.TrimStart('@')
    }

    if ($trimmed -notmatch '^https?://') {
        return "https://" + $trimmed
    }

    return $trimmed.TrimEnd('/')
}

#------------------------------------------------------------------------------------------------
# STEP 1 - Load sponsors from Excel
#------------------------------------------------------------------------------------------------

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 1 - Loading sponsors from Excel" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

if (-not (Test-Path $ExcelPath)) {
    throw "Excel file not found: $ExcelPath"
}

$rawRows = Import-Excel -Path $ExcelPath

# Skip entirely empty rows
$sponsorRows = $rawRows | Where-Object {
    -not [string]::IsNullOrWhiteSpace($_.CompanyNameAnnouncement) -or
    -not [string]::IsNullOrWhiteSpace($_.BillingCompany)
}

Write-Host "Sponsor rows loaded: $($sponsorRows.Count)" -ForegroundColor Green

#------------------------------------------------------------------------------------------------
# STEP 2 - Deduplicate Excel rows by CompanyNameAnnouncement
# Keep the row with the most data (LinkedIn, Twitter, Website populated)
#------------------------------------------------------------------------------------------------

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 2 - Deduplicating sponsor rows" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$sponsorByName = @{}
foreach ($row in $sponsorRows) {
    $key = $row.CompanyNameAnnouncement.Trim().ToLower()
    if ([string]::IsNullOrWhiteSpace($key)) { $key = $row.BillingCompany.Trim().ToLower() }

    $score = (
        ([int](-not [string]::IsNullOrWhiteSpace($row.LinkedInURL))) +
        ([int](-not [string]::IsNullOrWhiteSpace($row.TwitterHandle))) +
        ([int](-not [string]::IsNullOrWhiteSpace($row.Website)))
    )

    if (-not $sponsorByName.ContainsKey($key) -or $score -gt $sponsorByName[$key].Score) {
        $sponsorByName[$key] = [PSCustomObject]@{
            Row   = $row
            Score = $score
        }
    }
}

$uniqueSponsors = $sponsorByName.Values | ForEach-Object { $_.Row }
Write-Host "Unique sponsor entries after dedup: $($uniqueSponsors.Count)" -ForegroundColor Green

#------------------------------------------------------------------------------------------------
# STEP 3 - Fetch webshop companies
#------------------------------------------------------------------------------------------------

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 3 - Fetching companies from Webshop" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$webshopCompanies = Get-CmAllCompanies

#------------------------------------------------------------------------------------------------
# STEP 4 - Match and update
#------------------------------------------------------------------------------------------------

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 4 - Matching and updating webshop companies" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$updated      = @()
$skipped      = @()
$noMatch      = @()

foreach ($sponsor in $uniqueSponsors) {

    $sourceName = if (-not [string]::IsNullOrWhiteSpace($sponsor.CompanyNameAnnouncement)) {
                      $sponsor.CompanyNameAnnouncement
                  } else {
                      $sponsor.BillingCompany
                  }

    Write-Host ""
    Write-Host "  Sponsor: $sourceName" -ForegroundColor Yellow

    $match = Find-BestCompanyMatch -SourceName $sourceName -WebshopCompanies $webshopCompanies

    if (-not $match) {
        Write-Host "  [NO MATCH] No webshop company found for '$sourceName'" -ForegroundColor Red
        $noMatch += $sourceName
        continue
    }

    Write-Host "  -> Matched to webshop company ID $($match.id): $($match.name)" -ForegroundColor DarkGray

    # Build update body with only webshop-owned fields
    $linkedIn  = Clean-Url         -Url   $sponsor.LinkedInURL
    $twitter   = Clean-TwitterUrl  -Value $sponsor.TwitterHandle
    $website   = Clean-Url         -Url   $sponsor.Website
    $publicName = $sponsor.CompanyNameAnnouncement.Trim()

    $body = [ordered]@{}

    if (-not [string]::IsNullOrWhiteSpace($publicName)) {
        $body['company_name_public'] = $publicName
    }
    if (-not [string]::IsNullOrWhiteSpace($website)) {
        $body['web_address'] = $website
    }
    if (-not [string]::IsNullOrWhiteSpace($linkedIn)) {
        $body['linkedin_url'] = $linkedIn
    }
    if (-not [string]::IsNullOrWhiteSpace($twitter)) {
        $body['twitter_url'] = $twitter
    }

    if ($body.Count -eq 0) {
        Write-Host "  [SKIPPED] No webshop fields to update for '$sourceName'" -ForegroundColor DarkGray
        $skipped += $sourceName
        continue
    }

    $bodyJson = $body | ConvertTo-Json -Depth 5
    Write-Host "  JSON body (PUT):" -ForegroundColor DarkGray
    Write-Host $bodyJson -ForegroundColor DarkGray

    try {
        Invoke-CmRequest -Method PUT -Path "companies/$($match.id)" -Body $body | Out-Null
        Write-Host "  [OK]  Updated company ID $($match.id) ($($match.name))" -ForegroundColor Green

        $updated += [PSCustomObject]@{
            SourceName      = $sourceName
            WebshopId       = $match.id
            WebshopName     = $match.name
            PublicName      = $publicName
            Website         = $website
            LinkedIn        = $linkedIn
            Twitter         = $twitter
        }
    }
    catch {
        Write-Host "  [FAILED] Failed updating company ID $($match.id): $($_.Exception.Message)" -ForegroundColor Red
    }
}

#------------------------------------------------------------------------------------------------
# Summary
#------------------------------------------------------------------------------------------------

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "SYNC SUMMARY" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "Sponsor rows in Excel      : $($sponsorRows.Count)"    -ForegroundColor White
Write-Host "Unique after dedup         : $($uniqueSponsors.Count)" -ForegroundColor White
Write-Host "Updated in webshop         : $($updated.Count)"        -ForegroundColor Green
Write-Host "Skipped (no fields)        : $($skipped.Count)"        -ForegroundColor DarkGray
Write-Host "No match found             : $($noMatch.Count)"        -ForegroundColor $(if ($noMatch.Count -gt 0) { 'Red' } else { 'Green' })

if ($noMatch.Count -gt 0) {
    Write-Host ""
    Write-Host "Companies with no webshop match:" -ForegroundColor Red
    $noMatch | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
}

Write-Host ""
Write-Host "[OK] Sync-SponsorsInfo-to-Webshop completed." -ForegroundColor Green
