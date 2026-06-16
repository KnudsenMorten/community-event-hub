#------------------------------------------------------------------------------------------------
Write-Output "Experts Live Denmark Integration - Webshop -> Economic"
Write-Output "Purpose: Reads all companies from webshop and writes the webshop company ID"
Write-Output "         back into the publicEntryNumber field on the matching ERP customer."
Write-Output "Support: Morten Knudsen - mok@expertslive.dk"
#------------------------------------------------------------------------------------------------

$ScriptDirectory = $PSScriptRoot
$global:PathScripts = Split-Path -parent $ScriptDirectory

Import-Module "$ScriptDirectory\Secrets.psm1" -Global -Force -WarningAction SilentlyContinue
Import_Secrets

$BaseUrl = "https://expertslive.dk"
$CmBase  = "$BaseUrl/wp-json/company-manager/v1"

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

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

    $headers     = Get-CmAuthHeader
    $queryString = ""
    if ($Query.Count -gt 0) {
        $pairs = foreach ($k in $Query.Keys) {
            "{0}={1}" -f [Uri]::EscapeDataString($k), [Uri]::EscapeDataString([string]$Query[$k])
        }
        $queryString = $pairs -join "&"
    }
    $uri = if ($queryString) { "$CmBase/$Path`?$queryString" } else { "$CmBase/$Path" }
    $invokeParams = @{ Method = $Method; Uri = $uri; Headers = $headers; ErrorAction = 'Stop' }
    if ($Body) {
        $jsonBody = $Body | ConvertTo-Json -Depth 10
        $invokeParams['Body']        = [System.Text.Encoding]::UTF8.GetBytes($jsonBody)
        $invokeParams['ContentType'] = 'application/json; charset=utf-8'
        $invokeParams['Headers']['Content-Type'] = 'application/json; charset=utf-8'
    }
    try { return Invoke-RestMethod @invokeParams }
    catch {
        $status = $null; $respText = $null
        if ($_.Exception.Response -and ($_.Exception.Response -is [System.Net.HttpWebResponse])) {
            $status = [int]$_.Exception.Response.StatusCode
            try { $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $respText = $sr.ReadToEnd(); $sr.Close() } catch {}
        }
        $msg = $_.Exception.Message
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $msg = "$msg | $($_.ErrorDetails.Message)" }
        throw "Company Manager API failed$(if ($status) { " (HTTP $status)" }): $msg`nResponse: $respText"
    }
}

function Get-CmAllCompanies {
    $all = @(); $page = 1; $per = 200
    do {
        Write-Host "[GET] Company Manager companies (page $page)"
        $result = Invoke-CmRequest -Method GET -Path "companies" -Query @{ per_page = $per; page = $page }
        if ($result) { $all += $result }
        $page++
    } while ($result -and $result.Count -eq $per)
    Write-Host "   Total retrieved: $($all.Count)"
    return $all
}

function Invoke-EconomicPut {
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][hashtable]$Headers,
        [Parameter(Mandatory)][object]$Body
    )
    $jsonBody  = $Body | ConvertTo-Json -Depth 10
    $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($jsonBody)
    $hdrs      = $Headers.Clone()
    $hdrs['Content-Type'] = 'application/json; charset=utf-8'
    try {
        return Invoke-RestMethod -Method PUT -Uri $Uri -Headers $hdrs -Body $bodyBytes -ContentType 'application/json; charset=utf-8' -ErrorAction Stop
    }
    catch {
        $status = $null; $respText = $null
        if ($_.Exception.Response -and ($_.Exception.Response -is [System.Net.HttpWebResponse])) {
            $status = [int]$_.Exception.Response.StatusCode
            try { $sr = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream()); $respText = $sr.ReadToEnd(); $sr.Close() } catch {}
        }
        $msg = $_.Exception.Message
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $msg = "$msg | $($_.ErrorDetails.Message)" }
        throw "Economic PUT failed$(if ($status) { " (HTTP $status)" }): $msg`nURI: $Uri`nResponse: $respText"
    }
}

##############################################
# STEP 1 - Get all webshop companies
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 1 - Fetching companies from Webshop" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$webshopCompanies = Get-CmAllCompanies

$linked = $webshopCompanies | Where-Object { -not [string]::IsNullOrWhiteSpace($_.erp_customer_number) }

Write-Host "Companies with ERP customer number    : $($linked.Count)" -ForegroundColor Green
Write-Host "Companies without ERP customer number : $($webshopCompanies.Count - $linked.Count)" -ForegroundColor Yellow

##############################################
# STEP 2 - Write webshop company ID back
#           into publicEntryNumber in Economic
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 2 - Updating publicEntryNumber in Economic" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$updated = @()
$skipped = @()
$failed  = @()
$current = 0
$total   = $linked.Count

foreach ($company in $linked) {

    $current++
    $erpCustomerNumber = [string]$company.erp_customer_number
    $webshopId         = [string]$company.id
    $companyName       = [string]$company.name

    Write-Host ""
    Write-Host "  [$current/$total] Webshop ID $webshopId | ERP [$erpCustomerNumber] $companyName" -ForegroundColor Yellow

    # Fetch full ERP customer first - we check publicEntryNumber directly in ERP
    # before deciding whether to update
    $erpUri = "https://restapi.e-conomic.com/customers/$erpCustomerNumber"

    try {
        $erpCustomer = Invoke-RestMethod -Method Get -Headers $Economic_headers_REST -Uri $erpUri -ErrorAction Stop
    }
    catch {
        Write-Host "  [FAILED] Could not fetch ERP customer [$erpCustomerNumber]: $($_.Exception.Message)" -ForegroundColor Red
        $failed += [PSCustomObject]@{ WebshopId = $webshopId; ErpCustomerNumber = $erpCustomerNumber; Name = $companyName; Error = $_.Exception.Message }
        continue
    }

    # Check the value currently in ERP - skip if already correct
    $currentValue = [string]$erpCustomer.publicEntryNumber
    if ($currentValue -eq $webshopId) {
        Write-Host "  [SKIPPED] ERP publicEntryNumber already = $webshopId" -ForegroundColor DarkGray
        $skipped += $companyName
        continue
    }

    Write-Host "  ERP publicEntryNumber: '$currentValue' -> will set to '$webshopId'" -ForegroundColor DarkGray

    # Convert the full ERP customer object to a hashtable so we preserve ALL existing
    # fields (CVR, address, phone, email, etc.) and only change publicEntryNumber.
    # e-conomic strips read-only/computed properties on PUT automatically.
    $putBody = @{}
    foreach ($prop in $erpCustomer.PSObject.Properties) {
        # Skip read-only navigation/hypermedia properties that Economic rejects on PUT
        $skip = @('self','contacts','templates','totals','deliveryLocations','invoices',
                  'balance','dueAmount','lastUpdated')
        if ($skip -contains $prop.Name) { continue }
        $putBody[$prop.Name] = $prop.Value
    }
    # Set the field we actually want to update
    $putBody['publicEntryNumber'] = $webshopId

    Write-Host "  Setting publicEntryNumber = $webshopId" -ForegroundColor DarkGray

    try {
        Invoke-EconomicPut -Uri $erpUri -Headers $Economic_headers_REST -Body $putBody | Out-Null
        Write-Host "  [OK]  ERP [$erpCustomerNumber] $companyName -> publicEntryNumber = $webshopId" -ForegroundColor Green
        $updated += [PSCustomObject]@{ WebshopId = $webshopId; ErpCustomerNumber = $erpCustomerNumber; Name = $companyName }
    }
    catch {
        Write-Host "  [FAILED] $($_.Exception.Message)" -ForegroundColor Red
        $failed += [PSCustomObject]@{ WebshopId = $webshopId; ErpCustomerNumber = $erpCustomerNumber; Name = $companyName; Error = $_.Exception.Message }
    }
}

##############################################
# SUMMARY
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "SYNC SUMMARY" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "Webshop companies fetched      : $($webshopCompanies.Count)" -ForegroundColor White
Write-Host "Linked to ERP                  : $($linked.Count)"           -ForegroundColor White
Write-Host "Updated in Economic            : $($updated.Count)"          -ForegroundColor Green
Write-Host "Skipped (already correct)      : $($skipped.Count)"          -ForegroundColor DarkGray
Write-Host "Failed                         : $($failed.Count)"           -ForegroundColor $(if ($failed.Count -gt 0) { 'Red' } else { 'Green' })

if ($failed.Count -gt 0) {
    Write-Host ""
    Write-Host "Failed updates:" -ForegroundColor Red
    $failed | Format-Table -AutoSize | Out-String | Write-Host
}

Write-Host ""
Write-Host "[OK] Sync-Webshop-PublicEntryNumber-to-ERP completed." -ForegroundColor Green
