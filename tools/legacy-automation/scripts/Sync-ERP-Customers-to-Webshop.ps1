#------------------------------------------------------------------------------------------------
Write-Output "***********************************************************************************************"
Write-Output "Experts Live Denmark Integration Economic -> Webshop"
Write-Output ""
Write-Output "Purpose: This script extracts data from Economic and sends data to Webshop"
Write-Output ""
Write-Output "Support: Morten Knudsen - mok@expertslive.dk"
Write-Output "***********************************************************************************************"
#------------------------------------------------------------------------------------------------

$ScriptDirectory = $PSScriptRoot
$global:PathScripts = Split-Path -parent $ScriptDirectory

Import-Module "$ScriptDirectory\Secrets.psm1" -Global -force -WarningAction SilentlyContinue
Import_Secrets

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

function Invoke-EconomicPagedGet {
    param(
        [Parameter(Mandatory=$true)][string]$Uri,
        [Parameter(Mandatory=$true)][hashtable]$Headers
    )

    Write-Host "[GET] Economic GET (page 1): $Uri"

    $collection = @()
    $page = 1

    $resp = Invoke-RestMethod -Method Get -Headers $Headers -Uri $Uri

    if ($resp.collection) {
        $collection += $resp.collection
        Write-Host "   -> Rows received: $($resp.collection.Count)"
    }

    while ($resp.pagination -and ($resp.pagination.PSObject.Properties.Name -contains 'nextPage') -and $resp.pagination.nextPage) {
        $page++
        Write-Host "[GET] Economic GET (page $page): $($resp.pagination.nextPage)"

        $resp = Invoke-RestMethod -Method Get -Headers $Headers -Uri $resp.pagination.nextPage

        if ($resp.collection) {
            $collection += $resp.collection
            Write-Host "   -> Rows received: $($resp.collection.Count)  |  Total so far: $($collection.Count)"
        }
    }

    Write-Host "[OK] Economic API paging finished"
    Write-Host "   Total rows collected: $($collection.Count)"

    return $collection
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
        $invokeParams['Body']        = ($Body | ConvertTo-Json -Depth 10)
        $invokeParams['ContentType'] = 'application/json; charset=utf-8'
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
    # Company Manager returns all companies - no server-side paging needed for typical volumes,
    # but we loop per_page just in case.
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

##############################################
# Map ERP VAT zone -> Company Manager vat_zone_number
# e-conomic: 1=Domestic, 2=EU, 3=Outside EU, 4=Domestic no VAT
# Company Manager: 0=not set, 1=Danish Tax, 2=No Danish VAT
##############################################
function Convert-VatZoneNumber {
    param([int]$ErpVatZoneNumber)
    switch ($ErpVatZoneNumber) {
        1       { return 1 }   # Domestic with VAT
        2       { return 2 }   # EU - no Danish VAT
        3       { return 2 }   # Outside EU - no Danish VAT
        4       { return 2 }   # Domestic but no VAT
        default { return 0 }   # Unknown
    }
}

function Convert-CountryToIso2 {
    param([string]$CountryName)

    # e-conomic returns full country names (often in Danish). Map to ISO 3166-1 alpha-2.
    $map = @{
        # Danish names (most common in e-conomic DK)
        'Danmark'              = 'DK'
        'Sverige'              = 'SE'
        'Norge'                = 'NO'
        'Finland'              = 'FI'
        'Island'               = 'IS'
        'Tyskland'             = 'DE'
        'Holland'              = 'NL'
        'Nederlandene'         = 'NL'
        'Belgien'              = 'BE'
        'Frankrig'             = 'FR'
        'Spanien'              = 'ES'
        'Portugal'             = 'PT'
        'Italien'              = 'IT'
        'Schweiz'              = 'CH'
        'Ostrig'               = 'AT'
        'Polen'                = 'PL'
        'Tjekkiet'             = 'CZ'
        'Slovakiet'            = 'SK'
        'Ungarn'               = 'HU'
        'Rumænien'             = 'RO'
        'Bulgarien'            = 'BG'
        'Kroatien'             = 'HR'
        'Slovenien'            = 'SI'
        'Estland'              = 'EE'
        'Letland'              = 'LV'
        'Litauen'              = 'LT'
        'Irland'               = 'IE'
        'Storbritannien'       = 'GB'
        'England'              = 'GB'
        'Skotland'             = 'GB'
        'Grønland'             = 'GL'
        'Færøerne'             = 'FO'
        'USA'                  = 'US'
        'Canada'               = 'CA'
        'Australien'           = 'AU'
        'Kina'                 = 'CN'
        'Japan'                = 'JP'
        'Indien'               = 'IN'
        'Brasilien'            = 'BR'
        'Rusland'              = 'RU'
        'Ukraine'              = 'UA'
        'Tyrkiet'              = 'TR'
        'Israel'               = 'IL'
        # English names (also common)
        'Denmark'              = 'DK'
        'Sweden'               = 'SE'
        'Norway'               = 'NO'
        'Germany'              = 'DE'
        'Netherlands'          = 'NL'
        'Belgium'              = 'BE'
        'France'               = 'FR'
        'Spain'                = 'ES'
        'Italy'                = 'IT'
        'Switzerland'          = 'CH'
        'Austria'              = 'AT'
        'Poland'               = 'PL'
        'Czech Republic'       = 'CZ'
        'Czechia'              = 'CZ'
        'Hungary'              = 'HU'
        'Romania'              = 'RO'
        'Bulgaria'             = 'BG'
        'Croatia'              = 'HR'
        'Slovenia'             = 'SI'
        'Estonia'              = 'EE'
        'Latvia'               = 'LV'
        'Lithuania'            = 'LT'
        'Ireland'              = 'IE'
        'United Kingdom'       = 'GB'
        'Great Britain'        = 'GB'
        'Greenland'            = 'GL'
        'Faroe Islands'        = 'FO'
        'United States'                = 'US'
        'United States Of America'     = 'US'
        'America'                      = 'US'
        'Amerika'                      = 'US'
        'Australia'            = 'AU'
        'China'                = 'CN'
        'India'                = 'IN'
        'Brazil'               = 'BR'
        'Russia'               = 'RU'
        'Turkey'               = 'TR'
    }

    if ([string]::IsNullOrWhiteSpace($CountryName)) {
        return 'DK'   # Default to Denmark for e-conomic DK tenants
    }

    $trimmed = $CountryName.Trim()

    # Already a 2-letter ISO code - return as-is (uppercased)
    if ($trimmed.Length -eq 2 -and $trimmed -match '^[A-Za-z]{2}$') {
        return $trimmed.ToUpper()
    }

    # e-conomic sometimes sends 'Country Name (XX)' with ISO code in parentheses - extract it
    if ($trimmed -match '\(([A-Za-z]{2})\)$') {
        return $Matches[1].ToUpper()
    }

    # Lookup in map (case-insensitive)
    foreach ($key in $map.Keys) {
        if ($key -ieq $trimmed) {
            return $map[$key]
        }
    }

    # Not found - log a warning and default to DK to ensure a valid 2-char code is always returned
    Write-Host "  WARNING: Unknown country name '$trimmed' not in map - defaulting to DK. Add it to Convert-CountryToIso2." -ForegroundColor Red
    return 'DK'
}

function Get-PublicCompanyNameFromLegal {
    # Derive the "Public Company Name" (the short brand name used in
    # announcements + sponsor listings + Event Hub UIs) from the ERP legal
    # name by stripping the trailing legal-form suffix (A/S, ApS, Ltd, Inc,
    # GmbH, ...). Examples:
    #   "SOFTWARECENTRAL A/S"      -> "SOFTWARECENTRAL"
    #   "2linkIT ApS"              -> "2linkIT"
    #   "Acme Holdings, Inc."      -> "Acme Holdings"
    #   "Globex GmbH & Co. KG"     -> "Globex"
    # Pattern matches at end-of-string only, so middle-of-name uses of the
    # token (e.g. "A/S of America") are NOT stripped. Applied iteratively up
    # to 3 passes so dual suffixes like "Acme, Inc., Ltd." reduce cleanly.
    param([string]$LegalName)

    if ([string]::IsNullOrWhiteSpace($LegalName)) { return '' }
    $name = $LegalName.Trim()

    # Trailing legal-form suffix. Brand-name words like "Holdings", "Group",
    # "Solutions" are NOT in the list -- those are part of the identity.
    $suffixPattern = '(?i)[,\s]*\b(A/?S|Ap[Ss]|IVS|I/?S|K/?S|P/?S|Ltd\.?|Limited|Inc\.?|Incorporated|LLC|LLP|PLC|GmbH(\s*&\s*Co\.?\s*KG)?|AG|UG|KG|OHG|SARL|SAS|S\.?A\.?S?|Pty(\s+Ltd\.?)?|B\.?V\.?|N\.?V\.?|Oy[j]?|AB|s\.?r\.?o\.?|Sp\.?\s*z\s*o\.?\s*o\.?|sp\.?\s*k\.?|Sp\.?\s*k\.?|Co\.?|Corp\.?|Corporation)\.?\s*$'

    for ($i = 0; $i -lt 3; $i++) {
        $next = [regex]::Replace($name, $suffixPattern, '').Trim().TrimEnd(',', '.', '-')
        $next = $next.Trim()
        if ($next -eq $name -or [string]::IsNullOrWhiteSpace($next)) { break }
        $name = $next
    }

    return $name
}

function Build-CompanyBody {
    # $ExistingWebshopCompany is OPTIONAL. When null (creating a new webshop
    # company) the body always carries the derived name_public. When the
    # existing webshop company has a NON-EMPTY company_name_public the field
    # is OMITTED from the PUT body, so a human-curated short name is never
    # silently overwritten by the auto-derived one. Behavior owner: organizer.
    param($ErpCustomer, $ExistingWebshopCompany = $null)

    # Support both flat (Select-Object flattened) and nested e-conomic objects
    $vatZoneNumber = if ($ErpCustomer.PSObject.Properties['vatZoneNumber'] -and $ErpCustomer.vatZoneNumber) {
                         [int]$ErpCustomer.vatZoneNumber
                     } elseif ($ErpCustomer.vatZone -and $ErpCustomer.vatZone.vatZoneNumber) {
                         [int]$ErpCustomer.vatZone.vatZoneNumber
                     } else { 0 }

    $vatZone    = Convert-VatZoneNumber  -ErpVatZoneNumber $vatZoneNumber
    $isoCountry = Convert-CountryToIso2 -CountryName ([string]$ErpCustomer.country)

    # Use ERP email field -> sent to webshop as billing_email (empty string if not set)
    $resolvedEmail = [string]$ErpCustomer.email

    $body = [ordered]@{
        name                            = [string]$ErpCustomer.name
        erp_customer_number             = [string]$ErpCustomer.customerNumber
        corporate_identification_number = [string]$ErpCustomer.corporateIdentificationNumber
        currency                        = [string]$ErpCustomer.currency
        vat_zone_number                 = $vatZone
        phone                           = [string]$ErpCustomer.telephoneAndFaxNumber
        web_address                     = [string]$ErpCustomer.website
        billing_address_1               = [string]$ErpCustomer.address
        billing_city                    = [string]$ErpCustomer.city
        billing_postcode                = [string]$ErpCustomer.zip
        billing_country                 = $isoCountry
        billing_email                   = $resolvedEmail
    }

    # company_name_public: only set when the webshop side does NOT already
    # have one. For NEW companies ($ExistingWebshopCompany is $null) this
    # always fires; for existing companies we honour an organizer-curated
    # value over the auto-derived one.
    $existingPublic = $null
    if ($ExistingWebshopCompany) {
        if ($ExistingWebshopCompany.PSObject.Properties['company_name_public']) {
            $existingPublic = [string]$ExistingWebshopCompany.company_name_public
        } elseif ($ExistingWebshopCompany.PSObject.Properties['name_public']) {
            $existingPublic = [string]$ExistingWebshopCompany.name_public
        }
    }

    if ([string]::IsNullOrWhiteSpace($existingPublic)) {
        $derived = Get-PublicCompanyNameFromLegal -LegalName ([string]$ErpCustomer.name)
        if (-not [string]::IsNullOrWhiteSpace($derived)) {
            $body['company_name_public'] = $derived
        }
    }

    return $body
}

##############################################
# Get Economic Customers
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 1 - Fetching customers from e-conomic ERP" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$economic_customers = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/customers?pagesize=1000" -Headers $Economic_headers_REST

# Filter to allowed customer groups BEFORE flattening - use nested property directly
$allowedCustomerGroups = @(1)
$economic_customers = $economic_customers | Where-Object {
    $allowedCustomerGroups -contains [int]$_.customerGroup.customerGroupNumber
}
Write-Host "Customers in allowed groups: $($economic_customers.Count)" -ForegroundColor Green

# Build flat array with all fields needed for sync
$economic_customers_flat = $economic_customers | Select-Object `
    customerNumber,
    currency,
    @{Name="paymentTermsNumber"; Expression = { $_.paymentTerms.paymentTermsNumber }},
    @{Name="customerGroupNumber"; Expression = { $_.customerGroup.customerGroupNumber }},
    @{Name="vatZoneNumber"; Expression = { $_.vatZone.vatZoneNumber }},
    address,
    balance,
    dueAmount,
    corporateIdentificationNumber,
    city,
    country,
    name,
    zip,
    telephoneAndFaxNumber,
    website,
    email,
    publicEntryNumber

Write-Host "ERP customers loaded: $($economic_customers_flat.Count)" -ForegroundColor Green

##############################################
# Get Webshop Companies
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 2 - Fetching companies from Webshop (Company Manager)" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$webshop_companies = Get-CmAllCompanies

# Build lookup 1: erp_customer_number -> webshop company object
$webshopByErpNumber = @{}
foreach ($c in $webshop_companies) {
    if (-not [string]::IsNullOrWhiteSpace($c.erp_customer_number)) {
        $webshopByErpNumber[[string]$c.erp_customer_number] = $c
    }
}

# Build lookup 2: webshop company ID -> webshop company object
# Used as fallback: if ERP customer has publicEntryNumber set (written back by
# Sync-Webshop-PublicEntryNumber-to-ERP), we can match on that even if
# erp_customer_number is not yet set on the webshop side.
$webshopById = @{}
foreach ($c in $webshop_companies) {
    if ($c.id) {
        $webshopById[[string]$c.id] = $c
    }
}

Write-Host "Webshop companies indexed by ERP number : $($webshopByErpNumber.Count)" -ForegroundColor Green
Write-Host "Webshop companies indexed by ID         : $($webshopById.Count)" -ForegroundColor Green

##############################################
# (1) Detect missing customers in Webshop
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 3 - Detecting missing companies in Webshop" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$missing  = @()
$existing = @()

foreach ($erp in $economic_customers_flat) {
    $key = [string]$erp.customerNumber

    if ($webshopByErpNumber.ContainsKey($key)) {
        # Match via erp_customer_number on webshop company (primary method)
        $existing += $erp
    }
    elseif (-not [string]::IsNullOrWhiteSpace($erp.publicEntryNumber) -and
             $webshopById.ContainsKey([string]$erp.publicEntryNumber)) {
        # Match via publicEntryNumber in ERP -> webshop company ID (fallback method)
        Write-Host "  MATCHED via publicEntryNumber [$($erp.publicEntryNumber)]: [$key] $($erp.name)" -ForegroundColor DarkGray
        $existing += $erp
    }
    else {
        $missing += $erp
        Write-Host "  MISSING: [$key] $($erp.name)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Already in webshop : $($existing.Count)" -ForegroundColor Green
Write-Host "Missing in webshop : $($missing.Count)"  -ForegroundColor Yellow

##############################################
# Create missing companies
##############################################

$created = @()
$createFailed = @()

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "---------------------------------------------------" -ForegroundColor Cyan
    Write-Host "STEP 3a - Creating missing companies in Webshop" -ForegroundColor Cyan
    Write-Host "---------------------------------------------------" -ForegroundColor Cyan

    foreach ($erp in $missing) {
        Write-Host ""
        Write-Host "  CREATING: [$($erp.customerNumber)] $($erp.name)" -ForegroundColor Yellow

        $body = Build-CompanyBody -ErpCustomer $erp
        $bodyJson = $body | ConvertTo-Json -Depth 10
        Write-Host "  JSON body (POST):" -ForegroundColor DarkGray
        Write-Host $bodyJson -ForegroundColor DarkGray
        try {
            $result = Invoke-CmRequest -Method POST -Path "companies" -Body $body

            Write-Host "  [OK]  Created webshop company ID $($result.id) for ERP [$($erp.customerNumber)] $($erp.name)" -ForegroundColor Green

            $created += [PSCustomObject]@{
                ErpCustomerNumber = $erp.customerNumber
                ErpName           = $erp.name
                WebshopCompanyId  = $result.id
            }
        }
        catch {
            Write-Host "  [FAILED] FAILED creating [$($erp.customerNumber)] $($erp.name): $($_.Exception.Message)" -ForegroundColor Red
            $createFailed += [PSCustomObject]@{
                ErpCustomerNumber = $erp.customerNumber
                ErpName           = $erp.name
                Error             = $_.Exception.Message
            }
        }
    }
}

##############################################
# (2) Update metadata of existing companies in Webshop
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 4 - Updating existing companies in Webshop" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$updated      = @()
$updateFailed = @()

foreach ($erp in $existing) {
    $key = [string]$erp.customerNumber

    # Resolve webshop company - primary: erp_customer_number, fallback: publicEntryNumber
    $webshopCompany = if ($webshopByErpNumber.ContainsKey($key)) {
                          $webshopByErpNumber[$key]
                      } elseif (-not [string]::IsNullOrWhiteSpace($erp.publicEntryNumber) -and
                                $webshopById.ContainsKey([string]$erp.publicEntryNumber)) {
                          $webshopById[[string]$erp.publicEntryNumber]
                      } else {
                          $null
                      }

    if (-not $webshopCompany) {
        Write-Host "  [FAILED] Cannot resolve webshop company for ERP [$key] $($erp.name)" -ForegroundColor Red
        continue
    }

    Write-Host ""
    Write-Host "  UPDATING: [$($erp.customerNumber)] $($erp.name) -> Webshop ID $($webshopCompany.id)" -ForegroundColor Cyan

    # Pass the existing webshop company so Build-CompanyBody can SKIP setting
    # company_name_public when the organizer has already curated one.
    $body = Build-CompanyBody -ErpCustomer $erp -ExistingWebshopCompany $webshopCompany
    $bodyJson = $body | ConvertTo-Json -Depth 10
    Write-Host "  JSON body (PUT):" -ForegroundColor DarkGray
    Write-Host $bodyJson -ForegroundColor DarkGray
    try {
        $result = Invoke-CmRequest -Method PUT -Path "companies/$($webshopCompany.id)" -Body $body

        Write-Host "  [OK]  Updated webshop company ID $($webshopCompany.id)" -ForegroundColor Green

        $updated += [PSCustomObject]@{
            ErpCustomerNumber = $erp.customerNumber
            ErpName           = $erp.name
            WebshopCompanyId  = $webshopCompany.id
        }
    }
    catch {
        Write-Host "  [FAILED] FAILED updating [$($erp.customerNumber)] $($erp.name): $($_.Exception.Message)" -ForegroundColor Red
        $updateFailed += [PSCustomObject]@{
            ErpCustomerNumber = $erp.customerNumber
            ErpName           = $erp.name
            WebshopCompanyId  = $webshopCompany.id
            Error             = $_.Exception.Message
        }
    }
}

##############################################
# Summary
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "SYNC SUMMARY" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "ERP customers fetched    : $($economic_customers_flat.Count)" -ForegroundColor White
Write-Host "Created in webshop       : $($created.Count)"       -ForegroundColor Green
Write-Host "Updated in webshop       : $($updated.Count)"       -ForegroundColor Green
Write-Host "Create failures          : $($createFailed.Count)"  -ForegroundColor $(if ($createFailed.Count -gt 0) { 'Red' } else { 'Green' })
Write-Host "Update failures          : $($updateFailed.Count)"  -ForegroundColor $(if ($updateFailed.Count -gt 0) { 'Red' } else { 'Green' })

if ($createFailed.Count -gt 0) {
    Write-Host ""
    Write-Host "Failed creates:" -ForegroundColor Red
    $createFailed | Format-Table -AutoSize | Out-String | Write-Host
}

if ($updateFailed.Count -gt 0) {
    Write-Host ""
    Write-Host "Failed updates:" -ForegroundColor Red
    $updateFailed | Format-Table -AutoSize | Out-String | Write-Host
}

Write-Host ""
Write-Host "[OK] Sync-ERP-Customers-to-Webshop completed." -ForegroundColor Green
