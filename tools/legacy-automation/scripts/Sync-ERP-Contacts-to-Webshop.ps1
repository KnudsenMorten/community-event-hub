#------------------------------------------------------------------------------------------------
Write-Output "***********************************************************************************************"
Write-Output "Experts Live Denmark Integration Economic -> Webshop"
Write-Output ""
Write-Output "Purpose: This script sync contacts from ERP and sends contacts to Webshop"
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

function Get-CmAllUsers {
    $all  = @()
    $page = 1
    $per  = 200

    do {
        Write-Host "[GET] Company Manager GET users (page $page)"
        $result = Invoke-CmRequest -Method GET -Path "users" -Query @{ per_page = $per; page = $page }
        if ($result) { $all += $result }
        $page++
    } while ($result -and $result.Count -eq $per)

    Write-Host "   Total webshop users retrieved: $($all.Count)"
    return $all
}

function Get-EconomicCustomerContacts {
    param(
        [Parameter(Mandatory)]
        $Customers,

        [Parameter(Mandatory)]
        $Headers
    )

    $results = @()
    $customerCount = $Customers.Count
    $current = 0

    Write-Host "Starting contact retrieval for $customerCount customers..." -ForegroundColor Cyan

    foreach ($customer in $Customers) {

        $current++
        $contactsUrl = $customer.contacts

        Write-Host ""
        Write-Host "[$current/$customerCount] Processing customer $($customer.customerNumber) - $($customer.name)" -ForegroundColor Yellow
        Write-Verbose "Calling API: $contactsUrl"

        if (-not $contactsUrl) {
            Write-Host "No contacts endpoint found for this customer" -ForegroundColor DarkYellow
            continue
        }

        try {
            $response = Invoke-RestMethod `
                -Method Get `
                -Uri $contactsUrl `
                -Headers $Headers

            $contactCount = $response.collection.Count
            Write-Host "Retrieved $contactCount contacts" -ForegroundColor Green

            foreach ($contact in $response.collection) {

                # Safe property access for fields the API sometimes omits
                $contactName  = if ($contact.PSObject.Properties.Name -contains 'name')  { $contact.name }  else { $null }
                $contactEmail = if ($contact.PSObject.Properties.Name -contains 'email') { $contact.email } else { $null }
                $contactNotes = if ($contact.PSObject.Properties.Name -contains 'notes') { $contact.notes } else { $null }

                Write-Host "  -> Contact: $contactName | $contactEmail" -ForegroundColor Gray

                $results += [PSCustomObject]@{
                    ERPcustomerNumber = $customer.customerNumber
                    ERPcustomerName   = $customer.name
                    ERPcontactName    = $contactName
                    ERPcontactEmail   = $contactEmail
                    ERPcontactNotes   = $contactNotes
                }
            }
        }
        catch {
            Write-Host "Failed retrieving contacts for customer $($customer.customerNumber)" -ForegroundColor Red
            Write-Host $_.Exception.Message -ForegroundColor DarkRed
        }
    }

    Write-Host ""
    Write-Host "Contact retrieval finished" -ForegroundColor Cyan
    Write-Host "Total contacts collected: $($results.Count)" -ForegroundColor Green

    return $results
}

function Build-Username {
    param([string]$Email, [string]$Name, [hashtable]$ExistingUsernames = @{})

    # Derive base username from email local-part, fall back to name
    if (-not [string]::IsNullOrWhiteSpace($Email)) {
        $base = ($Email -split '@')[0]
        $base = $base.ToLower() -replace '[^a-z0-9.\-]', '.'
    } elseif (-not [string]::IsNullOrWhiteSpace($Name)) {
        $base = ($Name.Trim().ToLower() -replace '\s+', '.') -replace '[^a-z0-9.\-]', ''
    } else {
        $base = 'user.' + [System.Guid]::NewGuid().ToString('N').Substring(0, 8)
    }

    # Ensure uniqueness by appending a numeric suffix if the username is already taken
    $candidate = $base
    $counter   = 2
    while ($ExistingUsernames.ContainsKey($candidate)) {
        $candidate = "{0}.{1}" -f $base, $counter
        $counter++
    }

    return $candidate
}

function Split-ContactName {
    param([string]$FullName)

    if ([string]::IsNullOrWhiteSpace($FullName)) { return @('', '') }

    $parts = $FullName.Trim() -split '\s+'
    if ($parts.Count -eq 1) { return @($parts[0], '') }

    return @(($parts[0..($parts.Count - 2)] -join ' '), $parts[-1])
}

##############################################
# STEP 1 - Get Economic Customers
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 1 - Fetching customers from e-conomic ERP" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$economic_customers = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/customers?pagesize=1000" -Headers $Economic_headers_REST

# Filter to allowed customer groups - only sync contacts for customers in these groups
$allowedCustomerGroups = @(1)
$economic_customers = @($economic_customers | Where-Object {
    $allowedCustomerGroups -contains [int]$_.customerGroup.customerGroupNumber
})
Write-Host "Customers in allowed groups: $($economic_customers.Count)" -ForegroundColor Green

##############################################
# STEP 2 - Get ERP Contacts for all customers
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 2 - Fetching contacts from e-conomic ERP" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$customerContacts = Get-EconomicCustomerContacts `
    -Customers $economic_customers `
    -Headers $Economic_headers_REST

# Filter out contacts with no email - cannot create a WP user without one
# Wrap with @(...) to guarantee an array even when 0 or 1 results
$contactsWithEmail = @($customerContacts | Where-Object { -not [string]::IsNullOrWhiteSpace($_.ERPcontactEmail) })
$contactsNoEmail   = @($customerContacts | Where-Object { [string]::IsNullOrWhiteSpace($_.ERPcontactEmail) })

Write-Host ""
Write-Host "Contacts with email    : $($contactsWithEmail.Count)" -ForegroundColor Green
Write-Host "Contacts without email : $($contactsNoEmail.Count) (skipped - no WP user can be created)" -ForegroundColor Yellow

##############################################
# STEP 3 - Get Webshop Companies & Users
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 3 - Fetching companies and users from Webshop" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$webshop_companies = Get-CmAllCompanies
$webshop_users     = Get-CmAllUsers

# Build lookup: erp_customer_number -> webshop company
$webshopCompanyByErpNumber = @{}
foreach ($c in $webshop_companies) {
    if (-not [string]::IsNullOrWhiteSpace($c.erp_customer_number)) {
        $webshopCompanyByErpNumber[[string]$c.erp_customer_number] = $c
    }
}

# Build lookup: email (lowercase) -> webshop user
$webshopUserByEmail = @{}
foreach ($u in $webshop_users) {
    if (-not [string]::IsNullOrWhiteSpace($u.email)) {
        $webshopUserByEmail[$u.email.ToLower()] = $u
    }
}

Write-Host "Webshop companies indexed : $($webshopCompanyByErpNumber.Count)" -ForegroundColor Green
Write-Host "Webshop users indexed     : $($webshopUserByEmail.Count)"         -ForegroundColor Green

##############################################
# STEP 4 - Detect missing / existing contacts
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 4 - Detecting missing contacts in Webshop" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$contactsMissing  = @()
$contactsExisting = @()

foreach ($contact in $contactsWithEmail) {
    $emailKey = $contact.ERPcontactEmail.ToLower()

    if ($webshopUserByEmail.ContainsKey($emailKey)) {
        $contactsExisting += $contact
    }
    else {
        $contactsMissing += $contact
        Write-Host "  MISSING: $($contact.ERPcontactEmail) | $($contact.ERPcontactName) | Customer [$($contact.ERPcustomerNumber)]" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "Already in webshop : $($contactsExisting.Count)" -ForegroundColor Green
Write-Host "Missing in webshop : $($contactsMissing.Count)"  -ForegroundColor Yellow

##############################################
# STEP 5 - Create missing users and link to company
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 5 - Creating missing users in Webshop" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$created      = @()
$createFailed = @()

# Build a lookup of usernames already in use (webshop + ones we create this run)
$usedUsernames = @{}
foreach ($u in $webshop_users) {
    if (-not [string]::IsNullOrWhiteSpace($u.username)) {
        $usedUsernames[$u.username.ToLower()] = $true
    }
}

foreach ($contact in $contactsMissing) {
    $firstName, $lastName = Split-ContactName -FullName $contact.ERPcontactName
    $username = Build-Username -Email $contact.ERPcontactEmail -Name $contact.ERPcontactName -ExistingUsernames $usedUsernames
    $usedUsernames[$username.ToLower()] = $true   # Reserve it immediately for this run

    # Resolve the webshop company ID for this contact's ERP customer number
    $erpKey         = [string]$contact.ERPcustomerNumber
    $webshopCompany = $webshopCompanyByErpNumber[$erpKey]
    $companyId      = if ($webshopCompany) { [int]$webshopCompany.id } else { $null }

    Write-Host ""
    Write-Host "  CREATING: $($contact.ERPcontactEmail) | $($contact.ERPcontactName) -> Company ID $companyId" -ForegroundColor Yellow

    $body = [ordered]@{
        username   = $username
        email      = $contact.ERPcontactEmail
        first_name = $firstName
        last_name  = $lastName
    }

    $companyIdInBody = $false
    if ($companyId) {
        $body['company_id'] = $companyId
        $companyIdInBody    = $true
    }

    try {
        $result = Invoke-CmRequest -Method POST -Path "users" -Body $body

        Write-Host "  [OK]  Created WP user ID $($result.id) ($($contact.ERPcontactEmail))" -ForegroundColor Green

        if ($companyId -and -not $companyIdInBody) {
            # Fallback: link separately if company_id was not accepted inline
            Invoke-CmRequest -Method POST -Path "companies/$companyId/users" -Body @{ user_id = [int]$result.id } | Out-Null
            Write-Host "      Linked to company ID $companyId" -ForegroundColor Green
        }

        $created += [PSCustomObject]@{
            ERPcustomerNumber = $contact.ERPcustomerNumber
            ERPcustomerName   = $contact.ERPcustomerName
            ERPcontactEmail   = $contact.ERPcontactEmail
            ERPcontactName    = $contact.ERPcontactName
            WpUserId          = $result.id
            WebshopCompanyId  = $companyId
        }
    }
    catch {
        Write-Host "  [FAILED] FAILED creating user $($contact.ERPcontactEmail): $($_.Exception.Message)" -ForegroundColor Red
        $createFailed += [PSCustomObject]@{
            ERPcustomerNumber = $contact.ERPcustomerNumber
            ERPcontactEmail   = $contact.ERPcontactEmail
            ERPcontactName    = $contact.ERPcontactName
            Error             = $_.Exception.Message
        }
    }
}

##############################################
# STEP 6 - Update existing users
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 6 - Updating existing users in Webshop" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

$updated      = @()
$updateFailed = @()

foreach ($contact in $contactsExisting) {
    $emailKey    = $contact.ERPcontactEmail.ToLower()
    $webshopUser = $webshopUserByEmail[$emailKey]

    $firstName, $lastName = Split-ContactName -FullName $contact.ERPcontactName

    # Resolve company
    $erpKey         = [string]$contact.ERPcustomerNumber
    $webshopCompany = $webshopCompanyByErpNumber[$erpKey]
    $companyId      = if ($webshopCompany) { [int]$webshopCompany.id } else { $null }

    Write-Host ""
    Write-Host "  UPDATING: WP user ID $($webshopUser.id) ($($contact.ERPcontactEmail))" -ForegroundColor Cyan

    $body = [ordered]@{
        first_name = $firstName
        last_name  = $lastName
    }

    try {
        Invoke-CmRequest -Method PUT -Path "users/$($webshopUser.id)" -Body $body | Out-Null

        # Link user to company if not already linked to any company
        if ($companyId) {
            $currentCompany   = $webshopUser.company
            $currentCompanyId = if ($currentCompany -and $currentCompany.id) { [int]$currentCompany.id } else { $null }
            $currentCompanyLabel = if ($currentCompanyId) { [string]$currentCompanyId } else { 'none' }

            if ($currentCompanyId) {
                # Already linked to a company - skip relinking regardless of which company
                if ($currentCompanyId -ne $companyId) {
                    Write-Host "      SKIP relink: user already linked to company ID $currentCompanyId (target was $companyId)" -ForegroundColor DarkGray
                }
            } else {
                # Not linked to any company - link now
                Write-Host "      Linking to company ID $companyId (was none)" -ForegroundColor Yellow
                Invoke-CmRequest -Method POST -Path "companies/$companyId/users" -Body @{ user_id = [int]$webshopUser.id } | Out-Null
            }
        }

        Write-Host "  [OK]  Updated WP user ID $($webshopUser.id)" -ForegroundColor Green

        $updated += [PSCustomObject]@{
            ERPcustomerNumber = $contact.ERPcustomerNumber
            ERPcontactEmail   = $contact.ERPcontactEmail
            WpUserId          = $webshopUser.id
            WebshopCompanyId  = $companyId
        }
    }
    catch {
        Write-Host "  [FAILED] FAILED updating user $($contact.ERPcontactEmail): $($_.Exception.Message)" -ForegroundColor Red
        $updateFailed += [PSCustomObject]@{
            ERPcustomerNumber = $contact.ERPcustomerNumber
            ERPcontactEmail   = $contact.ERPcontactEmail
            WpUserId          = $webshopUser.id
            Error             = $_.Exception.Message
        }
    }
}


##############################################
# STEP 7 - Post-action: populate default signer and event coordinator
#   on companies where those fields are still empty (one-time population).
#
# Source: $contactsWithEmail (already in memory from STEP 2)
#   ERPcontactNotes format: Type:1;Role:1,2
#   Role 1 = Signer  -> default_signer_id
#   Role 2 = Event Coordinator -> event_coordination_default_contact_id
#
# No extra API calls for user lookup - uses $webshopUserByEmail (already indexed).
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "STEP 7 - Post-action: Populating default signer and event coordinator" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan

function Get-RolesFromNotes {
    param([string]$Notes)
    # Handles all separator styles by finding the 'Role:' segment with regex
    # and splitting its value on commas:
    #   'Type:1;Role:1,2'  -> 1,2
    #   'Type:1,Role:1'    -> 1
    #   'Type:1,Role:1,2'  -> 1,2
    #   'Role:2'           -> 2

    $roles = @()

    if ([string]::IsNullOrWhiteSpace($Notes)) {
        # Force array return so .Count works under strict mode
        return ,$roles
    }

    # Capture everything after 'Role:' up to the next semicolon (or end of string).
    # This works regardless of whether key-value pairs are separated by ; or ,
    # because the only thing that follows the role values (when more pairs exist)
    # would also be 'Role:' style segments - which we don't have.
    if ($Notes -imatch 'Role:([0-9,\s]+)') {
        foreach ($r in ($Matches[1] -split ',')) {
            $r = $r.Trim()
            if ($r -match '^\d+$') { $roles += [int]$r }
        }
    }

    # Force array return so .Count works under strict mode even with 0 or 1 items
    return ,$roles
}

# Build unique set of webshop company IDs touched this run
$touchedCompanyIds = @{}
foreach ($c in ($created + $updated)) {
    if ($c.WebshopCompanyId) {
        $touchedCompanyIds[[string]$c.WebshopCompanyId] = $true
    }
}

Write-Host "Companies touched this run: $($touchedCompanyIds.Count)" -ForegroundColor DarkGray

$signerSet      = 0
$coordinatorSet = 0

foreach ($companyId in $touchedCompanyIds.Keys) {

    Write-Host ""
    Write-Host "  Checking company ID $companyId" -ForegroundColor Yellow

    # Fetch company details to check if fields are already populated
    try {
        $companyDetails = Invoke-CmRequest -Method GET -Path "companies/$companyId"
    }
    catch {
        Write-Host "  [FAILED] Could not fetch company $companyId : $($_.Exception.Message)" -ForegroundColor Red
        continue
    }

    $companyName        = [string]$companyDetails.name
    $erpCustomerNumber  = [string]$companyDetails.erp_customer_number
    $currentSigner      = [int]$companyDetails.default_signer_id
    $currentCoordinator = [int]$companyDetails.event_coordination_default_contact_id

    Write-Host "  Company: $companyName | ERP: $erpCustomerNumber" -ForegroundColor Yellow

    $needSigner      = ($currentSigner -eq 0)
    $needCoordinator = ($currentCoordinator -eq 0)

    if (-not $needSigner -and -not $needCoordinator) {
        Write-Host "  [SKIP] Both fields already populated (signer=$currentSigner, coordinator=$currentCoordinator)" -ForegroundColor DarkGray
        continue
    }

    Write-Host "  Needs signer: $needSigner | Needs coordinator: $needCoordinator" -ForegroundColor DarkGray

    if ([string]::IsNullOrWhiteSpace($erpCustomerNumber)) {
        Write-Host "  [SKIP] No ERP customer number on webshop company $companyId" -ForegroundColor Yellow
        continue
    }

    # Get all ERP contacts for this company from $contactsWithEmail (already in memory)
    $companyContacts = @($contactsWithEmail | Where-Object {
        [string]$_.ERPcustomerNumber -eq $erpCustomerNumber
    })

    if ($companyContacts.Count -eq 0) {
        Write-Host "  [SKIP] No ERP contacts found for customer $erpCustomerNumber" -ForegroundColor DarkGray
        continue
    }

    Write-Host "  ERP contacts for this company: $($companyContacts.Count)" -ForegroundColor DarkGray

    $resolvedSigner      = $null
    $resolvedCoordinator = $null

    foreach ($contact in $companyContacts) {

        $roles = Get-RolesFromNotes -Notes $contact.ERPcontactNotes

        Write-Host "    Contact: $($contact.ERPcontactEmail) | Notes: '$($contact.ERPcontactNotes)' | Roles: $($roles -join ',')" -ForegroundColor DarkGray

        if ($roles.Count -eq 0) { continue }

        # Look up the webshop user ID via email (already indexed in $webshopUserByEmail)
        $emailKey   = $contact.ERPcontactEmail.ToLower()
        $webshopUser = $webshopUserByEmail[$emailKey]

        if (-not $webshopUser) {
            Write-Host "    -> No webshop user found for '$($contact.ERPcontactEmail)'" -ForegroundColor Yellow
            continue
        }

        $wpUserId = [int]$webshopUser.id

        if ($needSigner -and $null -eq $resolvedSigner -and $roles -contains 1) {
            $resolvedSigner = $wpUserId
            Write-Host "    -> Signer: WP user ID $wpUserId ($($contact.ERPcontactEmail))" -ForegroundColor Green
        }

        if ($needCoordinator -and $null -eq $resolvedCoordinator -and $roles -contains 2) {
            $resolvedCoordinator = $wpUserId
            Write-Host "    -> Coordinator: WP user ID $wpUserId ($($contact.ERPcontactEmail))" -ForegroundColor Green
        }

        if ((-not $needSigner -or $null -ne $resolvedSigner) -and
            (-not $needCoordinator -or $null -ne $resolvedCoordinator)) {
            break
        }
    }

    # Build PUT body with only fields that need setting
    $updateBody = [ordered]@{}

    if ($needSigner -and $null -ne $resolvedSigner) {
        $updateBody['default_signer_id'] = $resolvedSigner
        $signerSet++
    } elseif ($needSigner) {
        Write-Host "  [WARN] No contact with Role=1 (signer) found for company $companyId ($companyName)" -ForegroundColor Yellow
    }

    if ($needCoordinator -and $null -ne $resolvedCoordinator) {
        $updateBody['event_coordination_default_contact_id'] = $resolvedCoordinator
        $coordinatorSet++
    } elseif ($needCoordinator) {
        Write-Host "  [WARN] No contact with Role=2 (coordinator) found for company $companyId ($companyName)" -ForegroundColor Yellow
    }

    if ($updateBody.Count -gt 0) {
        try {
            Invoke-CmRequest -Method PUT -Path "companies/$companyId" -Body $updateBody | Out-Null
            Write-Host "  [OK]  Company $companyId ($companyName) updated" -ForegroundColor Green
        }
        catch {
            Write-Host "  [FAILED] Could not update company $companyId : $($_.Exception.Message)" -ForegroundColor Red
        }
    }
}

Write-Host ""
Write-Host "Default signers set      : $signerSet" -ForegroundColor Green
Write-Host "Default coordinators set : $coordinatorSet" -ForegroundColor Green

##############################################
# Summary
##############################################

Write-Host ""
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "SYNC SUMMARY" -ForegroundColor Cyan
Write-Host "---------------------------------------------------" -ForegroundColor Cyan
Write-Host "ERP contacts fetched      : $($customerContacts.Count)"     -ForegroundColor White
Write-Host "Contacts skipped (no email): $($contactsNoEmail.Count)"     -ForegroundColor Yellow
Write-Host "Users created             : $($created.Count)"              -ForegroundColor Green
Write-Host "Users updated             : $($updated.Count)"              -ForegroundColor Green
Write-Host "Create failures           : $($createFailed.Count)"         -ForegroundColor $(if ($createFailed.Count -gt 0) { 'Red' } else { 'Green' })
Write-Host "Update failures           : $($updateFailed.Count)"         -ForegroundColor $(if ($updateFailed.Count -gt 0) { 'Red' } else { 'Green' })

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
Write-Host "[OK] Sync-ERP-Contacts-to-Webshop completed." -ForegroundColor Green
