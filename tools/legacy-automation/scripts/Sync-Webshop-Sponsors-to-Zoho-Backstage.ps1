# ------------------------------------------------------------------------------------------------
# Experts Live Denmark - Sync Webshop Company Manager -> Zoho Backstage
#
# Purpose:
#   Pull companies + primary contact from Webshop Company Manager and create/update
#   them as SPONSORS in Zoho Backstage. A company is created as one sponsor row per
#   matched Zoho sponsor category (a company can be in multiple categories - e.g. a
#   Platinum exhibitor that also bought a Coffee sponsorship gets Platinum sponsors +
#   Hospitality & comfort sponsor).
#
#   Resolution uses:
#     1. A LIVE GET /wp-json/wc/v3/products (status=any) from WooCommerce for the
#        AUTHORITATIVE product_id -> product Categories lookup (so order line_items
#        only need product_id). No CSV export to keep in sync.
#     2. The mapping JSON (-MappingFile) for name-overrides + category-tag -> Zoho
#        sponsor category names + booth-tier keywords.
#     3. A live GET /sponsor_categories from Zoho for name -> id resolution.
#
#   For each company that bought a booth product, an exhibitor request is also
#   filed at its highest-priority booth tier (platinum > diamond > gold > feature)
#   using the booth_category_id values in the mapping JSON.
#
#   No CLI params. Tunables (WhatIfMode dry-run flag, cutoff date, role
#   priority, mapping file path) are constants at the top of the script -- edit
#   there. Default WhatIfMode = $true (dry-run).
#
# Support: Morten Knudsen - mok@expertslive.dk
# ------------------------------------------------------------------------------------------------

# Tunables (edit here, no CLI parm needed)
$WhatIfMode              = $false  # <-- flip to $false for a real run (POST/PATCH to Zoho)
$PrimaryContactRoleOrder = @('coordinator','signer','member','owner')
$OrdersAfter             = [datetime]'2026-05-01'
# Order statuses to treat as "bought". WooCommerce default fetch was completed-only,
# which silently drops a sponsorship paid-but-not-yet-completed (e.g. 'processing').
# Comma-joined into the ?status= query (WC accepts a list). Refunded/cancelled/failed
# are deliberately excluded so a reversed order never creates a sponsor row.
$OrderStatuses           = @('completed','processing','on-hold')
$DumpFetchedOrders       = $true   # <-- prints every fetched order (id/status/company/cmId/customer/products) for diagnosis
$MappingFile             = Join-Path $PSScriptRoot 'zoho-mapping.eldk27.json'
$NotifyEmailTo           = 'mok@expertslive.dk'   # one summary mail per company on CREATE
$NotifyEmailFrom         = 'mok@expertslive.dk'
$LogoFolder              = Join-Path $PSScriptRoot 'sponsor_logos'
# Event Hub Azure SQL databases - script upserts a Participant row (Role=Sponsor)
# in BOTH dev and prod for each company synced. Admin pwd fetched from KV at startup.
$EventHubTargets = @(
    @{ Env='dev';  KvName='kveldk27hubdevz237e';  SqlServer='eldk27hub-sql-devz237e.database.windows.net';  SqlDb='eldk27hub-db'; AdminUser='eldk27hubadmin' },
    @{ Env='prod'; KvName='kveldk27hubprodpdrq'; SqlServer='eldk27hub-sql-prodpdrq.database.windows.net'; SqlDb='eldk27hub-db'; AdminUser='eldk27hubadmin' }
)
$EventHubEventCode = 'ELDK27'   # Events.Code lookup; resolves to EventId per DB

Write-Output "***********************************************************************************************"
Write-Output "Experts Live Denmark - Sync Webshop Company Manager -> Zoho Backstage (sponsors per category)"
Write-Output "Support: Morten Knudsen - mok@expertslive.dk"
Write-Output "***********************************************************************************************"

$ScriptDirectory = $PSScriptRoot
Import-Module "$ScriptDirectory\Secrets.psm1" -Global -Force -WarningAction SilentlyContinue
Import_Secrets

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$BaseUrl = "https://expertslive.dk"
$CmBase  = "$BaseUrl/wp-json/company-manager/v1"

# ------------------------------------------------------------------------------------------------
# Helpers (Company Manager)
# ------------------------------------------------------------------------------------------------

function Get-CmAuthHeader {
    $pair  = "{0}:{1}" -f $WpUserApi, $WpAppPassword
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($pair)
    return @{ Authorization = 'Basic ' + [Convert]::ToBase64String($bytes); 'Content-Type' = 'application/json' }
}

function Invoke-CmPaged {
    param([Parameter(Mandatory)][string]$RelativePath, [int]$PerPage = 100)
    $headers = Get-CmAuthHeader
    $page = 1
    $all  = @()
    do {
        $sep = if ($RelativePath -match '\?') { '&' } else { '?' }
        $uri = "$CmBase/$RelativePath$sep" + "per_page=$PerPage&page=$page"
        try {
            $resp = Invoke-RestMethod -Method GET -Uri $uri -Headers $headers
        } catch {
            Write-Host "[CM ] WARN page $page failed: $($_.Exception.Message)" -ForegroundColor Yellow
            break
        }
        if (-not $resp) { break }
        $list = @($resp)
        $all += $list
        if ($list.Count -lt $PerPage) { break }
        $page++
    } while ($true)
    return $all
}

function Get-CmCompanies      { Invoke-CmPaged -RelativePath 'companies' }
function Get-CmCompanyContacts {
    param([Parameter(Mandatory)][int]$CompanyId)
    Invoke-CmPaged -RelativePath "companies/$CompanyId/users"
}
function Get-CmUser {
    param([Parameter(Mandatory)][int]$UserId)
    try {
        return Invoke-RestMethod -Method GET -Headers (Get-CmAuthHeader) `
            -Uri "$CmBase/users/$UserId"
    } catch {
        Write-Host "[CM ] WARN /users/$UserId failed: $($_.Exception.Message)" -ForegroundColor Yellow
        return $null
    }
}

# ------------------------------------------------------------------------------------------------
# Helpers (WooCommerce orders since cutoff date)
# ------------------------------------------------------------------------------------------------

function Get-WooBasicAuthHeader {
    $pair  = "{0}:{1}" -f $ConsumerKey, $ConsumerSecret
    $bytes = [System.Text.Encoding]::ASCII.GetBytes($pair)
    return @{ Authorization = 'Basic ' + [Convert]::ToBase64String($bytes); 'Content-Type' = 'application/json' }
}

function Get-WooOrdersAfter {
    param(
        [Parameter(Mandatory)][datetime]$After,
        [int]$PerPage = 100,
        [string[]]$Statuses = @('completed')
    )
    $headers = Get-WooBasicAuthHeader
    $afterIso = $After.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    $statusParam = ($Statuses | Where-Object { $_ }) -join ','
    if (-not $statusParam) { $statusParam = 'any' }
    $page = 1
    $all  = @()
    do {
        $uri = "$BaseUrl/wp-json/wc/v3/orders?status=$statusParam&per_page=$PerPage&page=$page&after=$afterIso"
        try {
            $resp = Invoke-RestMethod -Method GET -Uri $uri -Headers $headers
        } catch {
            Write-Host "[Woo] WARN page $page failed: $($_.Exception.Message)" -ForegroundColor Yellow
            break
        }
        if (-not $resp) { break }
        $list = @($resp)
        $all += $list
        if ($list.Count -lt $PerPage) { break }
        $page++
    } while ($true)
    return $all
}

# Diagnostic: dump every fetched order so attribution gaps are visible at a glance.
# Shows the three keys the sync attributes on (billing.company, _cm_company_id meta,
# customer_id) plus the order's product_ids/names. Use to confirm a "missing" order
# is actually being returned by the API and to see how it is keyed.
function Write-OrderDump {
    param([Parameter(Mandatory)]$Orders)
    Write-Host "[Woo] ---- fetched order dump ($($Orders.Count)) -------------------------------------------" -ForegroundColor DarkGray
    foreach ($o in ($Orders | Where-Object { $_ })) {
        $id     = if ($o.PSObject.Properties.Name -contains 'id') { [string]$o.id } else { '?' }
        $status = if ($o.PSObject.Properties.Name -contains 'status') { [string]$o.status } else { '?' }
        $date   = if ($o.PSObject.Properties.Name -contains 'date_created') { [string]$o.date_created } else { '' }
        $bill   = ''
        if ($o.PSObject.Properties.Name -contains 'billing' -and $o.billing -and
            $o.billing.PSObject.Properties.Name -contains 'company') { $bill = [string]$o.billing.company }
        $cmId = ''
        if ($o.PSObject.Properties.Name -contains 'meta_data' -and $o.meta_data) {
            $cm = $o.meta_data | Where-Object { $_.key -eq '_cm_company_id' } | Select-Object -First 1
            if ($cm -and $cm.value) { $cmId = [string]$cm.value }
        }
        $cust = if ($o.PSObject.Properties.Name -contains 'customer_id') { [string]$o.customer_id } else { '' }
        $items = @()
        if ($o.PSObject.Properties.Name -contains 'line_items' -and $o.line_items) {
            foreach ($li in $o.line_items) {
                # NB: $PID is a PowerShell reserved auto-variable (the current
                # process id). Using $prodIdLi here so the assignment doesn't
                # error with VariableNotWritable.
                $prodIdLi = ''
                foreach ($pn in @('product_id','productId','id')) {
                    if ($li.PSObject.Properties.Name -contains $pn -and $li.$pn) { $prodIdLi = [string]$li.$pn; break }
                }
                $pname = if ($li.PSObject.Properties.Name -contains 'name' -and $li.name) { [string]$li.name } else { '' }
                $items += ("{0}:{1}" -f $prodIdLi, $pname)
            }
        }
        Write-Host ("[Woo]   #{0,-7} {1,-12} {2}  bill='{3}' cmId='{4}' cust='{5}' nameKey='{6}'" -f `
            $id, $status, $date, $bill, $cmId, $cust, (Get-NameKey $bill)) -ForegroundColor DarkGray
        foreach ($it in $items) { Write-Host ("[Woo]       - {0}" -f $it) -ForegroundColor DarkGray }
    }
    Write-Host "[Woo] ---- end order dump ------------------------------------------------------" -ForegroundColor DarkGray
}

# Normalize a company name into a comparison key so that billing-company strings
# from WooCommerce orders ("2linkIT ApS") match Company Manager display names
# ("2LINKIT"). Lowercases, drops punctuation, and strips a trailing legal-form
# suffix (ApS, A/S, GmbH, Ltd, etc.). "2linkIT ApS" and "2LINKIT" -> "2linkit".
function Get-NameKey {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name)) { return '' }
    $s = $Name.Trim().ToLowerInvariant()
    $s = $s -replace '/', ''                      # "a/s" -> "as" (keep as one token)
    $s = $s -replace '[^\p{L}\p{Nd}\s]', ' '      # punctuation/symbols -> space
    $s = ($s -replace '\s+', ' ').Trim()
    if (-not $s) { return '' }
    # Trailing legal-form tokens to peel off (one or more, e.g. "... aps").
    $legal = @('aps','as','ab','asa','oyj','oy','gmbh','ag','ug','ltd','limited',
               'llc','inc','incorporated','corp','corporation','bv','nv','sarl',
               'sas','sa','kg','kft','plc','pty')
    $parts = @($s -split ' ')
    while ($parts.Count -gt 1 -and ($legal -contains $parts[-1])) {
        $parts = @($parts[0..($parts.Count - 2)])
    }
    return ($parts -join ' ')
}

# Build (nameKey -> @{Name; CmCompanyId}) + (cmCompanyId -> @{...}) from orders.
function Get-ActiveCompanySet {
    param([Parameter(Mandatory)]$Orders)
    $byName = @{}
    $byCmId = @{}
    foreach ($o in ($Orders | Where-Object { $_ })) {
        $bill = $null
        if ($o.PSObject.Properties.Name -contains 'billing' -and $o.billing -and
            $o.billing.PSObject.Properties.Name -contains 'company') { $bill = [string]$o.billing.company }
        $cmId = $null
        if ($o.PSObject.Properties.Name -contains 'meta_data' -and $o.meta_data) {
            $cm = $o.meta_data | Where-Object { $_.key -eq '_cm_company_id' } | Select-Object -First 1
            if ($cm -and $cm.value) { $cmId = [string]$cm.value }
        }
        if (-not [string]::IsNullOrWhiteSpace($bill)) {
            $key = Get-NameKey $bill
            if ($key) {
                if (-not $byName.ContainsKey($key)) { $byName[$key] = @{ Name = $bill.Trim(); CmCompanyId = $cmId } }
                elseif (-not $byName[$key].CmCompanyId -and $cmId) { $byName[$key].CmCompanyId = $cmId }
            }
        }
        if ($cmId -and -not $byCmId.ContainsKey($cmId)) {
            $billName = if ($bill) { $bill } else { "" }
            $byCmId[$cmId] = @{ Name = $billName; CmCompanyId = $cmId }
        }
    }
    return @{ ByName = $byName; ByCmId = $byCmId }
}

# ------------------------------------------------------------------------------------------------
# Helpers (Zoho Backstage)
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

function Get-ZohoPaged {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Token, [int]$PerPage = 200)
    $base  = $ZohoApiBase.TrimEnd('/')
    $items = @()
    $page  = 1
    do {
        $sep  = if ($Path -match '\?') { '&' } else { '?' }
        $url  = "$base$Path$sep" + "per_page=$PerPage&page=$page"
        $resp = Invoke-ZohoApi -Method GET -Uri $url -Token $Token
        $list = @()
        foreach ($p in @('sponsors','exhibitors','sponsorship_types','sponsor_categories','booth_categories','data')) {
            if ($resp.PSObject.Properties.Name -contains $p -and $resp.$p) { $list = @($resp.$p); break }
        }
        if ($list) { $items += $list }
        $hasMore = $false
        if ($resp.PSObject.Properties.Name -contains 'pagination') {
            $hasMore = [bool]$resp.pagination.has_more_items
        }
        $page++
    } while ($hasMore)
    return ,$items
}

# Return @{ nameLower -> id } from Zoho's sponsorship_types
# (https://www.zoho.com/backstage/api/v3/get-sponsor-categories.html).
function Get-ZohoSponsorCategoryIdByName {
    param([Parameter(Mandatory)][string]$Token)
    # sponsorship_types is a small finite set per event - no pagination, and the
    # endpoint rejects per_page/page as 400 "Extra param found". Plain GET.
    # When the event has no categories defined yet Zoho responds 400
    # "No Sponsorship Categories Available" - treat as empty set with a hint.
    $url = "$($ZohoApiBase.TrimEnd('/'))/portals/$ZohoPortalId/events/$ZohoEventId/sponsorship_types"
    try {
        $resp = Invoke-ZohoApi -Method GET -Uri $url -Token $Token
    } catch {
        $body = ''
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $body = $_.ErrorDetails.Message }
        elseif ($_.Exception.Response) {
            try { $body = (New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())).ReadToEnd() } catch {}
        }
        if ($body -match 'No Sponsorship Categories Available') {
            Write-Host "[Zoho] WARN  No sponsor categories defined on event $ZohoEventId yet." -ForegroundColor Yellow
            Write-Host "[Zoho]       Add them in Backstage UI: Event > Sponsors > Categories, then re-run." -ForegroundColor Yellow
        } else {
            Write-Host "[Zoho] WARN  sponsorship_types GET failed: $body  ($($_.Exception.Message))" -ForegroundColor Yellow
        }
        return @{}
    }
    $list = @()
    foreach ($p in @('sponsorship_types','sponsor_categories','data')) {
        if ($resp.PSObject.Properties.Name -contains $p -and $resp.$p) { $list = @($resp.$p); break }
    }
    $idx = @{}
    foreach ($c in $list) {
        $name = $null
        foreach ($n in @('name','category_name','title')) {
            if ($c.PSObject.Properties.Name -contains $n -and $c.$n) { $name = [string]$c.$n; break }
        }
        $id = $null
        foreach ($p in @('id','sponsorship_type_id','sponsor_category_id','category_id')) {
            if ($c.PSObject.Properties.Name -contains $p -and $c.$p) { $id = [string]$c.$p; break }
        }
        if ($name -and $id) { $idx[$name.Trim().ToLowerInvariant()] = $id }
    }
    return $idx
}

# Auto-discover booth categories from Zoho. Zoho calls them "exhibitor_categories"
# at the API level (UI shows them as "Booth categories"). Returns @{ nameLower -> id }.
function Get-ZohoBoothCategoryIdByName {
    param([Parameter(Mandatory)][string]$Token)
    $url  = "$($ZohoApiBase.TrimEnd('/'))/portals/$ZohoPortalId/events/$ZohoEventId/exhibitor_categories"
    try {
        $resp = Invoke-ZohoApi -Method GET -Uri $url -Token $Token
    } catch {
        Write-Host "[Zoho] WARN exhibitor_categories GET failed: $($_.Exception.Message)" -ForegroundColor Yellow
        return @{}
    }
    $list = @()
    foreach ($p in @('exhibitor_categories','booth_categories','data')) {
        if ($resp.PSObject.Properties.Name -contains $p -and $resp.$p) { $list = @($resp.$p); break }
    }
    $idx = @{}
    foreach ($c in $list) {
        $name = $null
        foreach ($n in @('name','category_name','title')) {
            if ($c.PSObject.Properties.Name -contains $n -and $c.$n) { $name = [string]$c.$n; break }
        }
        $id = $null
        foreach ($p in @('id','exhibitor_category_id','booth_category_id','category_id')) {
            if ($c.PSObject.Properties.Name -contains $p -and $c.$p) { $id = [string]$c.$p; break }
        }
        if ($name -and $id) { $idx[$name.Trim().ToLowerInvariant()] = $id }
    }
    return $idx
}

# Read all existing sponsors and return BOTH:
#   ByCompany : { nameLower -> { sponsorshipTypeId -> sponsor } }  (idempotent upsert key)
#   ByTypeName: { sponsorshipTypeNameLower -> sponsorshipTypeId } (auto-discovers category IDs
#               from any sponsor already created in that category - bypasses the broken
#               /sponsorship_types endpoint).
function Get-ZohoSponsorIndex {
    param([Parameter(Mandatory)][string]$Token)
    $list = Get-ZohoPaged -Path "/portals/$ZohoPortalId/events/$ZohoEventId/sponsors" -Token $Token
    $byCompany  = @{}
    $byTypeName = @{}
    foreach ($s in $list) {
        $name = $null
        foreach ($n in @('company_name','companyName','name','sponsorName')) {
            if ($s.PSObject.Properties.Name -contains $n -and $s.$n) { $name = [string]$s.$n; break }
        }
        $typeId   = $null
        $typeName = $null
        foreach ($p in @('sponsorship_type','sponsorship_type_id','sponsor_category_id')) {
            if ($s.PSObject.Properties.Name -contains $p -and $s.$p) { $typeId = [string]$s.$p; break }
        }
        foreach ($p in @('sponsorship_type_name','sponsor_category_name','category_name')) {
            if ($s.PSObject.Properties.Name -contains $p -and $s.$p) { $typeName = [string]$s.$p; break }
        }
        if ($name) {
            $key = $name.Trim().ToLowerInvariant()
            if (-not $byCompany.ContainsKey($key)) { $byCompany[$key] = @{} }
            if ($typeId) { $byCompany[$key][$typeId] = $s } else { $byCompany[$key]['_none_'] = $s }
        }
        if ($typeId -and $typeName) { $byTypeName[$typeName.Trim().ToLowerInvariant()] = $typeId }
    }
    return @{ ByCompany = $byCompany; ByTypeName = $byTypeName }
}

function Get-ZohoExhibitorIndex {
    param([Parameter(Mandatory)][string]$Token)
    $list = Get-ZohoPaged -Path "/portals/$ZohoPortalId/events/$ZohoEventId/exhibitors" -Token $Token
    $idx = @{}
    foreach ($e in $list) {
        $name = $null
        foreach ($n in @('company_name','companyName','name','exhibitorName')) {
            if ($e.PSObject.Properties.Name -contains $n -and $e.$n) { $name = [string]$e.$n; break }
        }
        if ($name) { $idx[$name.Trim().ToLowerInvariant()] = $e }
    }
    return $idx
}

function Get-PrimaryContact {
    param([Parameter(Mandatory)]$Contacts, [Parameter(Mandatory)][string[]]$RoleOrder)
    if (-not $Contacts -or @($Contacts).Count -eq 0) { return $null }
    foreach ($role in $RoleOrder) {
        foreach ($c in $Contacts) {
            $roles = @()
            if ($c.PSObject.Properties.Name -contains 'roles' -and $c.roles) { $roles = @($c.roles) }
            elseif ($c.PSObject.Properties.Name -contains 'role' -and $c.role) { $roles = @($c.role) }
            $rolesLower = @($roles | ForEach-Object { ("$_").ToLowerInvariant() })
            if ($rolesLower -contains $role.ToLowerInvariant()) { return $c }
        }
    }
    return $Contacts[0]
}

function Get-ContactPart {
    param($Contact, [string[]]$FieldCandidates)
    foreach ($f in $FieldCandidates) {
        if ($Contact.PSObject.Properties.Name -contains $f -and $Contact.$f) {
            return [string]$Contact.$f
        }
    }
    return ""
}

function Remove-EmptyFields {
    param([Parameter(Mandatory)][hashtable]$Map)
    function _IsBlank($v) {
        if ($null -eq $v) { return $true }
        if ($v -is [string]) { return [string]::IsNullOrWhiteSpace($v) }
        return $false   # numerics (incl. 0), bools (incl. false) are NOT blank
    }
    $clean = @{}
    foreach ($k in $Map.Keys) {
        $v = $Map[$k]
        if ($v -is [hashtable]) {
            $inner = @{}
            foreach ($ik in $v.Keys) { if (-not (_IsBlank $v[$ik])) { $inner[$ik] = $v[$ik] } }
            if ($inner.Keys.Count -gt 0) { $clean[$k] = $inner }
        } elseif (-not (_IsBlank $v)) {
            $clean[$k] = $v
        }
    }
    return $clean
}

function Build-ZohoSponsorPayload {
    param(
        [string]$CompanyName, [string]$Website,
        [string]$Description, [string]$SponsorshipTypeId,
        [string]$First, [string]$Last, [string]$Email, [string]$Phone
    )
    # Field shape confirmed against a manually-edited sponsor row:
    #   sponsorship_type   = category id (NOT sponsorship_type_id)
    #   website_url        (not "website")
    #   description        (not "short_description")
    #   contact { first_name, last_name, email[, phone_no] }
    # currency_code + language are server-set; sending them -> "Extra key found".
    $payload = @{
        company_name     = $CompanyName
        website_url      = $Website
        description      = $Description
        sponsorship_type = $SponsorshipTypeId
    }
    # phone_no rejected by Zoho ("Extra key found") - sponsor.contact is
    # name + email only. Phone lives on the exhibitor record instead.
    $contact = @{
        first_name = $First
        last_name  = $Last
        email      = $Email
    }
    $payload.contact = $contact
    return (Remove-EmptyFields $payload)
}

function Build-ZohoExhibitorPayload {
    param(
        [string]$CompanyName, [string]$Website,
        [string]$First, [string]$Last, [string]$Email, [string]$Phone,
        [string]$Designation, [string]$ShortDescription, [string]$Overview,
        [string]$LinkedIn, [string]$Twitter, [string]$Facebook,
        [string]$ExhibitorCategoryId, [string]$BoothLabel,
        [int]$Amount = 0, [string]$CurrencyCode = "EUR"
    )
    if ([string]::IsNullOrWhiteSpace($ExhibitorCategoryId)) { return $null }
    if ([string]::IsNullOrWhiteSpace($BoothLabel))          { return $null }
    if ([string]::IsNullOrWhiteSpace($Website))             { $Website = "https://expertslive.dk" }   # required field
    if ([string]::IsNullOrWhiteSpace($Last))                { $Last = "-" }                            # required field
    # Documented POST /exhibitors schema (Zoho v3 create-an-exhibitor.html):
    #   REQUIRED: exhibitor_category_id, company_name, website_url, booth_label,
    #             amount, currency_code, contact{first_name, last_name, email}
    #   OPTIONAL: contact{designation, mobile_no}, company_overview,
    #             company_short_description, company_social_pages{facebook, linkedin}
    # currency_code is documented as required but Zoho rejects it ("Extra key
    # found") - server-derived from booth category. Drop it.
    $payload = @{
        exhibitor_category_id     = $ExhibitorCategoryId
        company_name              = $CompanyName
        website_url               = $Website
        booth_label               = $BoothLabel
        amount                    = $Amount
        company_short_description = $ShortDescription
        company_overview          = $Overview
        contact = @{
            first_name  = $First
            last_name   = $Last
            email       = $Email
            designation = $Designation
            mobile_no   = $Phone
        }
    }
    $social = @{}
    if ($LinkedIn) { $social.linkedin = $LinkedIn }
    if ($Facebook) { $social.facebook = $Facebook }
    if ($Twitter)  { $social.twitter  = $Twitter  }
    if ($social.Keys.Count -gt 0) { $payload.company_social_pages = $social }
    return (Remove-EmptyFields $payload)
}

# ------------------------------------------------------------------------------------------------
# Mapping + Webshop product catalog
# ------------------------------------------------------------------------------------------------

function Load-MappingFile {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Mapping file not found: $Path" }
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
}

# Per product: list of Zoho sponsor category names.
# Resolution order: nameOverrides win (single category); otherwise every product
# category tag listed in webshopCategoryToSponsorCategory contributes.
function Resolve-ProductSponsorCategories {
    param([string]$ProductName, [string[]]$ProductCategories, $Mapping)
    $hit = @{}
    $nl = ([string]$ProductName).ToLowerInvariant()
    foreach ($ovr in @($Mapping.nameOverridesForSponsorCategory)) {
        if ([string]::IsNullOrWhiteSpace($ovr.match)) { continue }
        if ($nl.Contains(([string]$ovr.match).ToLowerInvariant())) {
            $hit[[string]$ovr.sponsorCategory] = $true
            return @($hit.Keys)
        }
    }
    $tagMap = $Mapping.webshopCategoryToSponsorCategory
    foreach ($cat in $ProductCategories) {
        $catTrim = ([string]$cat).Trim()
        if (-not $catTrim) { continue }
        if ($tagMap.PSObject.Properties.Name -contains $catTrim) {
            $hit[[string]$tagMap.$catTrim] = $true
        }
    }
    return @($hit.Keys)
}

# Per product: highest-priority booth tier (platinum > diamond > gold > feature).
# CATEGORY-ONLY resolution -- the product NAME is intentionally NOT scanned,
# because too many sponsor names contain words like "feature" / "gold" in
# their announcement copy and would mis-classify ("Info Feature Exhibitor",
# "Gold Star Coffee Sponsor", etc.). The WooCommerce category tag is the
# single source of truth: it's chosen deliberately by the shop operator and
# cannot drift via marketing edits to product names.
function Resolve-ProductBoothTier {
    param([string[]]$ProductCategories, $Mapping)
    $cats = @($ProductCategories | Where-Object { $_ } | ForEach-Object { ([string]$_).ToLowerInvariant() })
    if ($cats.Count -eq 0) { return $null }
    foreach ($tier in @('platinum','diamond','gold','feature')) {
        $kws = $Mapping.tierKeywordsForBoothCategory.$tier
        foreach ($k in @($kws)) {
            if ([string]::IsNullOrWhiteSpace($k)) { continue }
            $kl = ([string]$k).ToLowerInvariant()
            foreach ($c in $cats) {
                if ($c.Contains($kl)) { return $tier }
            }
        }
    }
    return $null
}

# Extract the booth label from the product name.
#   "Gold Exhibitor ELDK27 - Booth E-26"                          -> "E-26"
#   "Lounge sponsor ELDK27 with Exhibitor Booth E-27 ..."         -> "E-27"
#   "Appreciation Sponsor ELDK27 with Exhibitor Booth E-24 ..."   -> "E-24"
# Returns $null if the product name doesn't carry a booth slot.
function Resolve-ProductBoothLabel {
    param([string]$ProductName)
    if ([string]::IsNullOrWhiteSpace($ProductName)) { return $null }
    if ($ProductName -match 'Booth\s+(E-\d+)') { return $Matches[1] }
    return $null
}

# productId(string) -> @{ Name; Categories[]; SponsorCategories[]; BoothTier; BoothLabel }
#
# Source of truth is the LIVE WooCommerce REST API (no CSV export to keep current).
# status=any is deliberate: it includes draft / private / pending / [DEV] products
# that the default publish-only query would silently drop - the exact failure mode
# that left booth tier/label empty when this ran off a stale CSV.
#
# NOTE: order line_items carry the product_id of the *parent* product for simple
# products, but a *variation* id for variable products. Variations are NOT returned
# by /products (they live under /products/{id}/variations). All ELDK27 booth/tier
# products are simple, so this is a non-issue today; if a future product_id comes
# back unresolved despite existing in the shop, that's the first thing to check.
function Get-WooProductCatalogFromApi {
    param([Parameter(Mandatory)]$Mapping, [int]$PerPage = 100)
    $headers = Get-WooBasicAuthHeader
    $map  = @{}
    $page = 1
    do {
        $uri = "$BaseUrl/wp-json/wc/v3/products?status=any&per_page=$PerPage&page=$page"
        try {
            $resp = Invoke-RestMethod -Method GET -Uri $uri -Headers $headers
        } catch {
            Write-Host "[Cat ] WARN products page $page failed: $($_.Exception.Message)" -ForegroundColor Yellow
            break
        }
        if (-not $resp) { break }
        $list = @($resp)
        if ($list.Count -eq 0) { break }
        foreach ($p in $list) {
            $id = [string]$p.id
            if ([string]::IsNullOrWhiteSpace($id)) { continue }
            $name = [string]$p.name
            # /products returns categories as objects {id, name, slug}; project to names
            # so they line up with what the old CSV's "Categories" column carried.
            $cats = @()
            if ($p.PSObject.Properties.Name -contains 'categories' -and $p.categories) {
                $cats = @($p.categories | ForEach-Object { [string]$_.name } | Where-Object { $_ })
            }
            $sponsorCats = Resolve-ProductSponsorCategories -ProductName $name -ProductCategories $cats -Mapping $Mapping
            $boothTier   = Resolve-ProductBoothTier -ProductCategories $cats -Mapping $Mapping
            $boothLabel  = Resolve-ProductBoothLabel -ProductName $name
            $map[$id] = @{
                Name              = $name
                Categories        = $cats
                SponsorCategories = $sponsorCats
                BoothTier         = $boothTier
                BoothLabel        = $boothLabel
            }
        }
        if ($list.Count -lt $PerPage) { break }
        $page++
    } while ($true)
    return $map
}

# Substring-match the tier keyword (gold/diamond/platinum/feature) inside the
# names Zoho returns - "Gold 4m" matches tier "gold", "Diamond 5m" matches
# "diamond", etc. Returns null if the live Zoho list has no name containing
# the tier keyword.
# Find a local logo file for a company. Convention:
#   <LogoFolder>\<CompanyName>.<png|jpg|jpeg|gif|webp>  (case-insensitive)
function Find-CompanyLogo {
    param([Parameter(Mandatory)][string]$CompanyName, [Parameter(Mandatory)][string]$Folder)
    if (-not (Test-Path -LiteralPath $Folder)) { return $null }
    $stemLower = $CompanyName.Trim().ToLowerInvariant()
    foreach ($f in Get-ChildItem -LiteralPath $Folder -File -ErrorAction SilentlyContinue) {
        if ([IO.Path]::GetFileNameWithoutExtension($f.Name).ToLowerInvariant() -eq $stemLower) {
            if ($f.Extension -match '^\.(png|jpg|jpeg|gif|webp)$') { return $f.FullName }
        }
    }
    return $null
}

function Send-CreateNotification {
    param(
        [Parameter(Mandatory)][string]$To, [Parameter(Mandatory)][string]$From,
        [Parameter(Mandatory)][string]$CompanyName,
        [string[]]$CreatedSponsorCategories = @(),
        [string]$ExhibitorBoothLabel,
        [string]$ContactDisplay,
        [string]$LocalLogoPath,
        [string]$ExpectedLogoFolder
    )
    if (-not $To -or -not $From) { return }
    if ($CreatedSponsorCategories.Count -eq 0 -and -not $ExhibitorBoothLabel) { return }
    $subject = "[ELDK27] New Zoho Backstage entry created: $CompanyName"
    $lines = @()
    $lines += "A new sponsor/exhibitor was synced to Zoho Backstage."
    $lines += ""
    $lines += "Company : $CompanyName"
    if ($ContactDisplay) { $lines += "Contact : $ContactDisplay" }
    if ($CreatedSponsorCategories.Count -gt 0) {
        $lines += ("Sponsor rows created in {0} categor(ies):" -f $CreatedSponsorCategories.Count)
        foreach ($cat in $CreatedSponsorCategories) { $lines += "  - $cat" }
    }
    if ($ExhibitorBoothLabel) {
        $lines += "Exhibitor created on booth: $ExhibitorBoothLabel"
    }
    $lines += ""
    $lines += "ACTION REQUIRED -- upload logo manually in Backstage UI:"
    $lines += "  1. Open the sponsor (and exhibitor) for '$CompanyName' in Backstage"
    $lines += "  2. Click 'Choose Logo' / 'Upload Logo' on each"
    if ($LocalLogoPath) {
        $lines += "  3. Pick the file:  $LocalLogoPath"
    } else {
        $lines += "  3. LOGO MISSING -- drop a file named '$CompanyName.png' (or .jpg/.jpeg/.gif/.webp)"
        $lines += "     into:  $ExpectedLogoFolder"
        $lines += "     then upload it in Backstage."
    }
    $lines += ""
    $lines += "(Zoho's v3 API does not expose a logo upload endpoint; the Backstage UI logo"
    $lines += " upload requires browser session cookies which a service script cannot use.)"
    $lines += ""
    $lines += "-- Sync-Webshop-Sponsors-to-Zoho-Backstage.ps1"
    $body = $lines -join "`r`n"
    $pwd = ConvertTo-SecureString $SmtpPass -AsPlainText -Force
    $cred = New-Object System.Management.Automation.PSCredential ($SmtpUser, $pwd)
    try {
        Send-MailMessage -SmtpServer 'smtp-relay.brevo.com' -Port 587 -UseSsl `
            -Credential $cred -From $From -To $To -Subject $subject -Body $body -Encoding UTF8
        Write-Host "[Mail] notification sent to $To for '$CompanyName'" -ForegroundColor DarkGray
    } catch {
        Write-Host "[Mail] WARN failed to notify '$To' for '$CompanyName': $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

# ------------------------------------------------------------------------------------------------
# Event Hub Azure SQL helpers
# ------------------------------------------------------------------------------------------------

function Get-KvSecretValue {
    param([Parameter(Mandatory)][string]$VaultName, [Parameter(Mandatory)][string]$SecretName)
    try {
        return (az keyvault secret show --vault-name $VaultName --name $SecretName --query value -o tsv 2>$null)
    } catch {
        Write-Host "[KV  ] WARN failed to fetch $SecretName from $VaultName : $($_.Exception.Message)" -ForegroundColor Yellow
        return $null
    }
}

function Get-EventHubEventId {
    param([Parameter(Mandatory)][string]$ConnectionString, [Parameter(Mandatory)][string]$EventCode)
    $conn = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        $cmd.CommandText = "SELECT TOP 1 Id FROM dbo.Events WHERE Code = @code"
        [void]$cmd.Parameters.AddWithValue('@code', $EventCode)
        $v = $cmd.ExecuteScalar()
        if ($null -eq $v -or $v -is [DBNull]) { return $null }
        return [int]$v
    } finally { $conn.Close() }
}

function Invoke-EventHubSeedParticipant {
    param(
        [Parameter(Mandatory)][string]$ConnectionString,
        [Parameter(Mandatory)][int]$EventId,
        [Parameter(Mandatory)][string]$Email,
        [string]$FullName, [string]$Phone, [string]$CompanyId,
        [int]$Role = 4   # 4 = Sponsor (see CommunityHub.Core.Domain.ParticipantRole)
    )
    if ([string]::IsNullOrWhiteSpace($Email)) { return 'skip-no-email' }
    $emailKey = $Email.Trim().ToLowerInvariant()

    $conn = New-Object System.Data.SqlClient.SqlConnection($ConnectionString)
    try {
        $conn.Open()
        $cmd = $conn.CreateCommand()
        # Idempotent UPSERT by (EventId, Email).
        $cmd.CommandText = @'
MERGE dbo.Participants AS target
USING (SELECT @EventId AS EventId, @Email AS Email) AS src
ON target.EventId = src.EventId AND target.Email = src.Email
WHEN MATCHED THEN UPDATE SET
    FullName         = @FullName,
    Phone            = @Phone,
    Role             = @Role,
    SponsorCompanyId = @CompanyId,
    IsActive         = 1
WHEN NOT MATCHED THEN INSERT
    (EventId, Email, FullName, Phone, Role, SponsorCompanyId, IsActive, CreatedAt)
    VALUES (@EventId, @Email, @FullName, @Phone, @Role, @CompanyId, 1, SYSDATETIMEOFFSET())
OUTPUT $action AS Action;
'@
        [void]$cmd.Parameters.AddWithValue('@EventId',  $EventId)
        [void]$cmd.Parameters.AddWithValue('@Email',    $emailKey)
        [void]$cmd.Parameters.AddWithValue('@FullName', [string]$FullName)
        $pPhone   = if ([string]::IsNullOrWhiteSpace($Phone))     { [DBNull]::Value } else { $Phone }
        $pCompany = if ([string]::IsNullOrWhiteSpace($CompanyId)) { [DBNull]::Value } else { $CompanyId }
        [void]$cmd.Parameters.AddWithValue('@Phone',     $pPhone)
        [void]$cmd.Parameters.AddWithValue('@Role',      $Role)
        [void]$cmd.Parameters.AddWithValue('@CompanyId', $pCompany)
        $r = $cmd.ExecuteScalar()
        return [string]$r   # "INSERT" or "UPDATE"
    } finally { $conn.Close() }
}

function Get-BoothCategoryIdForTier {
    param([string]$Tier, [hashtable]$BoothCatIdByName)
    if (-not $Tier -or -not $BoothCatIdByName -or $BoothCatIdByName.Count -eq 0) { return $null }
    $t = $Tier.ToLowerInvariant()
    foreach ($k in $BoothCatIdByName.Keys) {
        if ($k.Contains($t)) { return $BoothCatIdByName[$k] }
    }
    return $null
}

# ------------------------------------------------------------------------------------------------
# Upsert / submit
# ------------------------------------------------------------------------------------------------

function Upsert-ZohoSponsor {
    param(
        [Parameter(Mandatory)][string]$Token,
        [Parameter(Mandatory)][hashtable]$Payload,
        [Parameter(Mandatory)][hashtable]$SponsorByCompany,
        [Parameter(Mandatory)][string]$CompanyName,
        [Parameter(Mandatory)][string]$SponsorshipTypeId,
        [Parameter(Mandatory)][string]$SponsorshipTypeName
    )
    $key = $CompanyName.Trim().ToLowerInvariant()
    $existing = $null
    if ($SponsorByCompany.ContainsKey($key) -and $SponsorByCompany[$key].ContainsKey($SponsorshipTypeId)) {
        $existing = $SponsorByCompany[$key][$SponsorshipTypeId]
    }

    if ($existing) {
        $id = $null
        foreach ($p in @('id','sponsor_id')) { if ($existing.PSObject.Properties.Name -contains $p -and $existing.$p) { $id = [string]$existing.$p; break } }
        if (-not $id) { Write-Host "[Zoho] [SKIP] Sponsor '$CompanyName' / '$SponsorshipTypeName' exists but no id returned" -ForegroundColor Yellow; return 'skip' }
        $uri = "$($ZohoApiBase.TrimEnd('/'))/portals/$ZohoPortalId/events/$ZohoEventId/sponsors/$id"
        if ($WhatIfMode) { Write-Host "[Zoho] WHATIF PATCH sponsor '$CompanyName' / '$SponsorshipTypeName' (id=$id)" -ForegroundColor Yellow; return 'update' }
        try {
            Invoke-ZohoApi -Method PUT -Uri $uri -Token $Token -Body $Payload | Out-Null
            Write-Host "[Zoho] UPDATED sponsor '$CompanyName' / '$SponsorshipTypeName' (id=$id)" -ForegroundColor Cyan
            return 'update'
        } catch {
            $body = if ($_.ErrorDetails) { $_.ErrorDetails.Message } else { $_.Exception.Message }
            Write-Host "[Zoho] FAIL update sponsor '$CompanyName' / '$SponsorshipTypeName': $body" -ForegroundColor Red
            Write-Host "       payload: $($Payload | ConvertTo-Json -Depth 6 -Compress)" -ForegroundColor DarkRed
            return 'fail'
        }
    } else {
        $uri = "$($ZohoApiBase.TrimEnd('/'))/portals/$ZohoPortalId/events/$ZohoEventId/sponsors"
        if ($WhatIfMode) { Write-Host "[Zoho] WHATIF POST sponsor '$CompanyName' / '$SponsorshipTypeName'" -ForegroundColor Yellow; return 'create' }
        try {
            Invoke-ZohoApi -Method POST -Uri $uri -Token $Token -Body $Payload | Out-Null
            Write-Host "[Zoho] CREATED sponsor '$CompanyName' / '$SponsorshipTypeName'" -ForegroundColor Green
            return 'create'
        } catch {
            $body = if ($_.ErrorDetails) { $_.ErrorDetails.Message } else { $_.Exception.Message }
            Write-Host "[Zoho] FAIL create sponsor '$CompanyName' / '$SponsorshipTypeName': $body" -ForegroundColor Red
            Write-Host "       payload: $($Payload | ConvertTo-Json -Depth 6 -Compress)" -ForegroundColor DarkRed
            return 'fail'
        }
    }
}

function Submit-ZohoExhibitor {
    param([Parameter(Mandatory)][string]$Token, [Parameter(Mandatory)][hashtable]$Payload,
          [Parameter(Mandatory)]$ZohoExhibitorIndex, [Parameter(Mandatory)][string]$CompanyName)
    $key = $CompanyName.Trim().ToLowerInvariant()
    if ($ZohoExhibitorIndex.ContainsKey($key)) {
        Write-Host "[Zoho] Exhibitor already exists for '$CompanyName' -- skipping" -ForegroundColor Cyan
        return 'skip'
    }
    $uri = "$($ZohoApiBase.TrimEnd('/'))/portals/$ZohoPortalId/events/$ZohoEventId/exhibitors"
    if ($WhatIfMode) { Write-Host "[Zoho] WHATIF POST exhibitor '$CompanyName' (boothLabel=$($Payload.booth_label))" -ForegroundColor Yellow; return 'create' }
    try {
        $created = Invoke-ZohoApi -Method POST -Uri $uri -Token $Token -Body $Payload
        Write-Host "[Zoho] CREATED exhibitor '$CompanyName' (boothLabel=$($Payload.booth_label), id=$($created.id))" -ForegroundColor Green
        return 'create'
    } catch {
        # KNOWN Zoho v3 limitation: POST /exhibitors returns 400 "booth label is a
        # required parameter" regardless of field name (boothLabel, booth_label,
        # boothId, booth_id, nested booth.label, etc.) - all probed. The booth
        # records DO exist (GET /booths returns them with booth_label populated).
        # Suspected cause: this endpoint requires state from the
        # Exhibitor Request Form approval workflow that direct API POST can't
        # replicate. Soft-fail so the sponsor flow ships clean. Organizer
        # creates the exhibitor manually in Backstage UI from the sponsor row.
        $body = if ($_.ErrorDetails) { $_.ErrorDetails.Message } else { $_.Exception.Message }
        Write-Host "[Zoho] WARN exhibitor '$CompanyName' (booth_label=$($Payload.booth_label)) NOT created: $body" -ForegroundColor Yellow
        Write-Host "       payload: $($Payload | ConvertTo-Json -Depth 6 -Compress)" -ForegroundColor DarkGray
        return 'fail'
    }
}

# ------------------------------------------------------------------------------------------------
# Main
# ------------------------------------------------------------------------------------------------

Write-Host ""
Write-Host "[Map ] Loading mapping: $MappingFile"
$mapping = Load-MappingFile -Path $MappingFile

Write-Host "[Cat ] Loading product catalog from WooCommerce API (status=any)..."
$productCatalog = Get-WooProductCatalogFromApi -Mapping $mapping
Write-Host "[Cat ]   $($productCatalog.Count) product(s) in catalog."

Write-Host ""
Write-Host "[Hub ] Resolving Event Hub SQL targets (dev + prod)..."
foreach ($t in $EventHubTargets) {
    $pwd = Get-KvSecretValue -VaultName $t.KvName -SecretName 'sql-admin-password'
    if (-not $pwd) {
        Write-Host "[Hub ] WARN  '$($t.Env)' sql-admin-password missing in KV $($t.KvName) -- will skip seeding this env." -ForegroundColor Yellow
        $t.ConnStr = $null; $t.EventId = $null; continue
    }
    $t.ConnStr = "Server=$($t.SqlServer);Database=$($t.SqlDb);User Id=$($t.AdminUser);Password=$pwd;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30"
    try {
        $t.EventId = Get-EventHubEventId -ConnectionString $t.ConnStr -EventCode $EventHubEventCode
        Write-Host "[Hub ]   $($t.Env): EventId for '$EventHubEventCode' = $($t.EventId)" -ForegroundColor DarkGray
    } catch {
        Write-Host "[Hub ] WARN  '$($t.Env)' EventId lookup failed: $($_.Exception.Message) -- will skip seeding this env." -ForegroundColor Yellow
        $t.EventId = $null
    }
}

Write-Host ""
Write-Host "[Zoho] Refreshing access token..."
$token = Get-ZohoAccessToken
if (-not $token) { throw "Could not obtain a Zoho access token. Aborting." }

Write-Host "[Zoho] Pulling booth categories from Zoho..."
$boothCatIdByName = Get-ZohoBoothCategoryIdByName -Token $token
Write-Host "[Zoho]   $($boothCatIdByName.Count) booth categor(ies) in Backstage:"
foreach ($k in ($boothCatIdByName.Keys | Sort-Object)) {
    Write-Host ("[Zoho]     - {0,-40} {1}" -f $k, $boothCatIdByName[$k]) -ForegroundColor DarkGray
}

Write-Host "[Zoho] Pulling existing sponsors index..."
$sponsorIdx         = Get-ZohoSponsorIndex -Token $token
$zohoSponsorByCo    = $sponsorIdx.ByCompany
$sponsorCatIdByName = @{}
foreach ($k in $sponsorIdx.ByTypeName.Keys) { $sponsorCatIdByName[$k] = $sponsorIdx.ByTypeName[$k] }
Write-Host "[Zoho]   $($zohoSponsorByCo.Count) unique sponsor company name(s) in Backstage."
Write-Host "[Zoho]   $($sponsorCatIdByName.Count) sponsorship_type(s) auto-discovered from existing sponsors."

# Merge JSON-pinned sponsor_category IDs (workaround: Zoho /sponsorship_types
# endpoint returns empty for this account). JSON IDs WIN over auto-discovered.
if ($mapping.sponsorCategoryIds) {
    $pinned = 0
    foreach ($prop in $mapping.sponsorCategoryIds.PSObject.Properties) {
        $v = [string]$prop.Value
        if ([string]::IsNullOrWhiteSpace($v) -or $v.StartsWith('PUT_')) { continue }
        $sponsorCatIdByName[$prop.Name.Trim().ToLowerInvariant()] = $v
        $pinned++
    }
    Write-Host "[Zoho]   $pinned sponsorship_type id(s) pinned from zoho-mapping.eldk27.json."
}
Write-Host "[Zoho]   sponsor category name -> id table (total $($sponsorCatIdByName.Count)):"
foreach ($k in ($sponsorCatIdByName.Keys | Sort-Object)) {
    Write-Host ("[Zoho]     - {0,-40} {1}" -f $k, $sponsorCatIdByName[$k]) -ForegroundColor DarkGray
}

Write-Host "[Zoho] Pulling existing exhibitors index..."
$zohoExhibitorIdx = Get-ZohoExhibitorIndex -Token $token
Write-Host "[Zoho]   $($zohoExhibitorIdx.Count) existing exhibitor(s) in Backstage."

Write-Host ""
Write-Host "[Woo] Pulling orders [$($OrderStatuses -join ',')] since $($OrdersAfter.ToString('yyyy-MM-dd'))..."
$orders = Get-WooOrdersAfter -After $OrdersAfter -Statuses $OrderStatuses
Write-Host "[Woo]   $($orders.Count) order(s) since cutoff."
if ($DumpFetchedOrders) { Write-OrderDump -Orders $orders }

$active = Get-ActiveCompanySet -Orders $orders
$activeNames = @($active.ByName.Keys)
$activeCmIds = @($active.ByCmId.Keys)
Write-Host "[Woo]   Active companies: $($activeNames.Count) by name; $($activeCmIds.Count) by Company Manager id."

Write-Host ""
Write-Host "[CM ] Pulling companies from Company Manager..."
$allCompanies = Get-CmCompanies
Write-Host "[CM ]   $($allCompanies.Count) company(ies) found (pre-filter)."

$companies = @()
foreach ($co in ($allCompanies | Where-Object { $_ })) {
    # Check every name field the company exposes - legal name, public name, title -
    # so a company whose orders bill under any of its name variants is still kept.
    $cNameKeys = @{}
    foreach ($n in @('name','company_name','company_name_public','title','displayName','legal_name')) {
        if ($co.PSObject.Properties.Name -contains $n -and $co.$n) {
            $nk = Get-NameKey ([string]$co.$n)
            if ($nk) { $cNameKeys[$nk] = $true }
        }
    }
    $cId = if ($co.PSObject.Properties.Name -contains 'id') { [string]$co.id } else { $null }
    $matchById   = $cId -and ($activeCmIds -contains $cId)
    $matchByName = $false
    foreach ($nk in $cNameKeys.Keys) { if ($activeNames -contains $nk) { $matchByName = $true; break } }
    if ($matchById -or $matchByName) { $companies += $co }
}
Write-Host "[CM ]   $($companies.Count) company(ies) kept after order-date filter."

# Per-company product_id set. Orders are attributed three ways so that ALL of a
# sponsor's orders are collected, not just the ones whose billing string happens
# to equal the CM display name:
#   ByCompanyName : normalized billing.company  (Get-NameKey, suffix-insensitive)
#   ByCmId        : the _cm_company_id order meta
#   ByCustomerId  : the WooCommerce customer_id (WP user who placed the order)
$productIdsByCompanyName = @{}
$productIdsByCmId        = @{}
$productIdsByCustomerId  = @{}
foreach ($o in ($orders | Where-Object { $_ })) {
    $bill = $null
    if ($o.PSObject.Properties.Name -contains 'billing' -and $o.billing -and
        $o.billing.PSObject.Properties.Name -contains 'company') { $bill = [string]$o.billing.company }
    $cmId = $null
    if ($o.PSObject.Properties.Name -contains 'meta_data' -and $o.meta_data) {
        $cm = $o.meta_data | Where-Object { $_.key -eq '_cm_company_id' } | Select-Object -First 1
        if ($cm -and $cm.value) { $cmId = [string]$cm.value }
    }
    $custId = $null
    foreach ($cf in @('customer_id','customerId')) {
        if ($o.PSObject.Properties.Name -contains $cf -and $o.$cf) {
            $v = [string]$o.$cf
            if ($v -and $v -ne '0') { $custId = $v }
            break
        }
    }
    $ids = @()
    if ($o.PSObject.Properties.Name -contains 'line_items' -and $o.line_items) {
        foreach ($li in $o.line_items) {
            $prodId = $null
            foreach ($pn in @('product_id','productId','id')) {
                if ($li.PSObject.Properties.Name -contains $pn -and $li.$pn) { $prodId = [string]$li.$pn; break }
            }
            if ($prodId) { $ids += $prodId }
        }
    }
    if ($bill) {
        $k = Get-NameKey $bill
        if ($k) {
            if (-not $productIdsByCompanyName.ContainsKey($k)) { $productIdsByCompanyName[$k] = @{} }
            foreach ($prodId in $ids) { $productIdsByCompanyName[$k][$prodId] = $true }
        }
    }
    if ($cmId) {
        if (-not $productIdsByCmId.ContainsKey($cmId)) { $productIdsByCmId[$cmId] = @{} }
        foreach ($prodId in $ids) { $productIdsByCmId[$cmId][$prodId] = $true }
    }
    if ($custId) {
        if (-not $productIdsByCustomerId.ContainsKey($custId)) { $productIdsByCustomerId[$custId] = @{} }
        foreach ($prodId in $ids) { $productIdsByCustomerId[$custId][$prodId] = $true }
    }
}

$counters = @{
    Considered             = 0
    SponsorRowsCreated     = 0
    SponsorRowsUpdated     = 0
    SponsorRowsFailed      = 0
    Skipped                = ($allCompanies.Count - $companies.Count)
    SkippedNoPublicName    = 0
    ExhibitorRequests      = 0
    ExhibitorSkippedNoTier = 0
}

# Collect every company whose Public Company Name is unset so the script can
# email mok@expertslive.dk ONE summary at the end. We deliberately do NOT
# auto-derive the public name here (an upstream sync, Sync-ERP-Customers-to-
# Webshop.ps1, already strips suffixes on customer creation). If a company
# still has no public name at this stage it is a curation gap the organizer
# must close manually -- creating the Zoho sponsor/exhibitor row with the
# legal "ApS / A/S / Ltd" suffix is exactly what this guard is here to
# prevent.
$missingPublicName = New-Object System.Collections.Generic.List[object]

foreach ($co in ($companies | Where-Object { $_ })) {
    $counters.Considered++

    $companyName = $null
    foreach ($n in @('name','company_name','title','displayName')) {
        if ($co.PSObject.Properties.Name -contains $n -and $co.$n) { $companyName = [string]$co.$n; break }
    }
    if ([string]::IsNullOrWhiteSpace($companyName)) {
        Write-Host "[CM ] WARN  Skipping company row with no name" -ForegroundColor Yellow
        continue
    }
    $companyId = if ($co.PSObject.Properties.Name -contains 'id') { [int]$co.id } else { $null }

    # Re-GET the company detail to grab the role pointer fields (list endpoint
    # may omit them). Same pattern as Sync-ERP-Contacts-to-Webshop.ps1.
    $coFull = $null
    if ($companyId) {
        try { $coFull = Invoke-RestMethod -Method GET -Headers (Get-CmAuthHeader) -Uri "$CmBase/companies/$companyId" }
        catch { Write-Host "[CM ] WARN company detail fetch failed for id=$companyId : $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    if (-not $coFull) { $coFull = $co }

    # Prefer company_name_public ("CodeTwo") over legal name ("CodeTwo sp. z o.o. sp. k.").
    # When the public name is MISSING, skip the company outright and queue an alert
    # for mok -- the organizer must set it manually so the Zoho sponsor/exhibitor
    # row never ships with "ApS / A/S / Ltd" in its title.
    $publicName = ''
    if ($coFull.PSObject.Properties.Name -contains 'company_name_public') {
        $publicName = [string]$coFull.company_name_public
    }
    if ([string]::IsNullOrWhiteSpace($publicName)) {
        Write-Host "[CM ] SKIP  '$companyName' (id=$companyId) -- company_name_public is empty; will alert mok@expertslive.dk." -ForegroundColor Yellow
        $counters.SkippedNoPublicName++
        $cmEditUrl = "$BaseUrl/wp-admin/admin.php?page=cm-companies&action=edit&id=$companyId"
        $missingPublicName.Add([pscustomobject]@{
            CompanyId   = $companyId
            LegalName   = $companyName
            EditUrl     = $cmEditUrl
        }) | Out-Null
        continue
    }
    $companyName = $publicName
    $website = Get-ContactPart $coFull @('web_address','website','url','company_url')
    $coDesc  = Get-ContactPart $coFull @('description','about','company_description')

    # Resolve the event coordinator (default), fall back to signer, then approver.
    $coordinatorId = 0
    foreach ($fld in @('event_coordination_default_contact_id','default_signer_id','approver_default_contact_id')) {
        if ($coFull.PSObject.Properties.Name -contains $fld) {
            $v = [int]$coFull.$fld
            if ($v -gt 0) { $coordinatorId = $v; break }
        }
    }
    $first = ''; $last = ''; $email = ''; $phone = ''
    if ($coordinatorId -gt 0) {
        $user = Get-CmUser -UserId $coordinatorId
        if ($user) {
            $first = Get-ContactPart $user @('first_name','firstname','given_name')
            $last  = Get-ContactPart $user @('last_name','lastname','surname','family_name')
            $email = Get-ContactPart $user @('email','user_email','email_address','billing_email')
            $phone = Get-ContactPart $user @('billing_phone','phone','phone_number','telephone','mobile')
        }
    }
    # Final fallback: company-level billing fields (email/phone) so the exhibitor request still goes through.
    if (-not $email) { $email = Get-ContactPart $coFull @('billing_email','email') }
    if (-not $phone) { $phone = Get-ContactPart $coFull @('phone','billing_phone') }

    # Collect product_ids this company bought. Union across THREE attribution
    # signals so a sponsor's full order set is captured even when the billing
    # company string, the cm-id meta, and the purchasing user disagree:
    #   1. every normalized name variant the company exposes
    #   2. the cm company id
    #   3. the WP user ids of the company's contacts (who placed the orders)
    $prodIdSet  = @{}
    $matchedVia = @()

    $coNameKeys = @{}
    foreach ($n in @('name','company_name','company_name_public','title','displayName','legal_name')) {
        if ($coFull.PSObject.Properties.Name -contains $n -and $coFull.$n) {
            $nk = Get-NameKey ([string]$coFull.$n)
            if ($nk) { $coNameKeys[$nk] = $true }
        }
    }
    # $companyName may already be the public name; make sure its key is in the set.
    $nk = Get-NameKey $companyName
    if ($nk) { $coNameKeys[$nk] = $true }
    foreach ($k in $coNameKeys.Keys) {
        if ($productIdsByCompanyName.ContainsKey($k)) {
            foreach ($p in $productIdsByCompanyName[$k].Keys) { $prodIdSet[$p] = $true }
            $matchedVia += "name:$k"
        }
    }

    if ($companyId -and $productIdsByCmId.ContainsKey([string]$companyId)) {
        foreach ($k in $productIdsByCmId[[string]$companyId].Keys) { $prodIdSet[$k] = $true }
        $matchedVia += "cmId:$companyId"
    }

    # Contact WP user ids -> match WooCommerce customer_id. Best-effort; the
    # coordinator id (already resolved above) is always included.
    $contactUserIds = @{}
    if ($coordinatorId -gt 0) { $contactUserIds[[string]$coordinatorId] = $true }
    if ($companyId) {
        foreach ($c in @(Get-CmCompanyContacts -CompanyId $companyId)) {
            foreach ($idf in @('id','user_id','ID','wp_user_id','userId')) {
                if ($c.PSObject.Properties.Name -contains $idf -and $c.$idf) {
                    $cidv = [string]$c.$idf
                    if ($cidv -and $cidv -ne '0') { $contactUserIds[$cidv] = $true }
                    break
                }
            }
        }
    }
    foreach ($uid in $contactUserIds.Keys) {
        if ($productIdsByCustomerId.ContainsKey($uid)) {
            foreach ($k in $productIdsByCustomerId[$uid].Keys) { $prodIdSet[$k] = $true }
            $matchedVia += "customer:$uid"
        }
    }

    # Translate product_ids -> sponsor categories + booth tier + booth label via the catalog.
    # The booth label is taken from the SAME product that wins the tier race (so a
    # company with a Platinum + a Coffee sponsorship gets the Platinum booth label).
    $sponsorCatSet = @{}
    $boothTier     = $null
    $boothLabel    = $null
    $tierRank = @{ platinum = 4; diamond = 3; gold = 2; feature = 1 }
    foreach ($prodId in $prodIdSet.Keys) {
        if (-not $productCatalog.ContainsKey($prodId)) {
            Write-Host "[Cat ] WARN  product_id $prodId bought but not in Woo catalog (variation id? deleted product?) -- skipping" -ForegroundColor Yellow
            continue
        }
        $entry = $productCatalog[$prodId]
        foreach ($c in @($entry.SponsorCategories)) { if ($c) { $sponsorCatSet[$c] = $true } }
        $bt = $entry.BoothTier
        if ($bt -and (-not $boothTier -or $tierRank[$bt] -gt $tierRank[$boothTier])) {
            $boothTier  = $bt
            $boothLabel = $entry.BoothLabel
        }
    }
    $sponsorCategories = @($sponsorCatSet.Keys | Sort-Object)

    Write-Host ""
    Write-Host "=> '$companyName' (cm-id=$companyId) | contact='$first $last' <$email>"
    Write-Host ("   products={0}  matchedVia=[{1}]  sponsorCategories=[{2}]  boothTier={3}  boothLabel={4}" -f `
        $prodIdSet.Count, ($matchedVia -join ', '), ($sponsorCategories -join ' | '), $boothTier, $boothLabel)

    # One sponsor row per matched category. The (name -> id) table is built from
    # JSON-pinned IDs + IDs auto-discovered from existing sponsors. Unknown
    # categories are skipped with a clear warning.
    if ($sponsorCategories.Count -eq 0) {
        Write-Host "[Zoho] no sponsor categories resolved for '$companyName' -- skipping sponsor upsert" -ForegroundColor DarkYellow
    }
    $createdCatsForNotify = @()
    foreach ($catName in $sponsorCategories) {
        $catKey = $catName.ToLowerInvariant()
        $catId  = $null
        if ($sponsorCatIdByName.ContainsKey($catKey)) { $catId = $sponsorCatIdByName[$catKey] }
        if (-not $catId) {
            Write-Host "[Zoho] [SKIP] sponsor category '$catName' has no id (pin it in zoho-mapping.eldk27.json or create one sponsor in that category in Backstage)" -ForegroundColor Yellow
            continue
        }
        $payload = Build-ZohoSponsorPayload -CompanyName $companyName -Website $website `
            -Description $coDesc -SponsorshipTypeId $catId `
            -First $first -Last $last -Email $email -Phone $phone
        $r = Upsert-ZohoSponsor -Token $token -Payload $payload -SponsorByCompany $zohoSponsorByCo `
            -CompanyName $companyName -SponsorshipTypeId $catId -SponsorshipTypeName $catName
        switch ($r) {
            'create' { $counters.SponsorRowsCreated++; $createdCatsForNotify += $catName }
            'update' { $counters.SponsorRowsUpdated++ }
            'fail'   { $counters.SponsorRowsFailed++ }
        }
    }

    $boothCategoryId = Get-BoothCategoryIdForTier -Tier $boothTier -BoothCatIdByName $boothCatIdByName
    if (-not $boothCategoryId) {
        $counters.ExhibitorSkippedNoTier++
        Write-Host "[Zoho] Skipping exhibitor for '$companyName' (booth tier='$boothTier', no booth product)" -ForegroundColor DarkYellow
    } elseif (-not $boothLabel) {
        $counters.ExhibitorSkippedNoTier++
        Write-Host "[Zoho] Skipping exhibitor for '$companyName' (no boothLabel parsed from product name)" -ForegroundColor DarkYellow
    } else {
        $exhibFirst = if ([string]::IsNullOrWhiteSpace($first)) { "Contact" } else { $first }
        $linkedIn   = Get-ContactPart $coFull @('linkedin_url','linkedin')
        $twitter    = Get-ContactPart $coFull @('twitter_url','twitter')
        $facebook   = Get-ContactPart $coFull @('facebook_url','facebook')
        $exhibPayload = Build-ZohoExhibitorPayload -CompanyName $companyName -Website $website `
            -First $exhibFirst -Last $last -Email $email -Phone $phone `
            -ShortDescription $coDesc -Overview $coDesc `
            -LinkedIn $linkedIn -Twitter $twitter -Facebook $facebook `
            -ExhibitorCategoryId $boothCategoryId -BoothLabel $boothLabel
        if ($exhibPayload) {
            $er = Submit-ZohoExhibitor -Token $token -Payload $exhibPayload `
                -ZohoExhibitorIndex $zohoExhibitorIdx -CompanyName $companyName
            if ($er -eq 'create') { $counters.ExhibitorRequests++ }
        }
    }

    # Logo check (local-folder only - Zoho's API doesn't expose logo presence).
    $localLogo = Find-CompanyLogo -CompanyName $companyName -Folder $LogoFolder
    if ($localLogo) {
        Write-Host "[Logo] '$companyName' -- logo file present: $localLogo (upload manually in Backstage UI)" -ForegroundColor DarkGray
    } else {
        Write-Host "[Logo] '$companyName' -- WARN no logo file in $LogoFolder (expected: $companyName.png/.jpg/...)" -ForegroundColor Yellow
    }

    # Event Hub seed: upsert the event coordinator as a Sponsor-role Participant
    # in BOTH dev and prod databases. Skips silently if no email or no DB target.
    if ($email) {
        foreach ($t in $EventHubTargets) {
            if (-not $t.ConnStr -or -not $t.EventId) { continue }
            try {
                $action = Invoke-EventHubSeedParticipant -ConnectionString $t.ConnStr -EventId $t.EventId `
                    -Email $email -FullName "$first $last".Trim() -Phone $phone -CompanyId ([string]$companyId)
                Write-Host "[Hub ] '$companyName' / '$email' -> $($t.Env) DB: $action" -ForegroundColor DarkGray
            } catch {
                Write-Host "[Hub ] FAIL seed '$email' -> $($t.Env) DB: $($_.Exception.Message)" -ForegroundColor Red
            }
        }
    }

    # One summary email per company on actual CREATE (not on update or skip).
    $createdExhibitorLabel = if ($er -eq 'create') { $boothLabel } else { $null }
    if ($createdCatsForNotify.Count -gt 0 -or $createdExhibitorLabel) {
        Send-CreateNotification -To $NotifyEmailTo -From $NotifyEmailFrom `
            -CompanyName $companyName `
            -CreatedSponsorCategories $createdCatsForNotify `
            -ExhibitorBoothLabel $createdExhibitorLabel `
            -ContactDisplay "$first $last <$email>" `
            -LocalLogoPath $localLogo `
            -ExpectedLogoFolder $LogoFolder
    }
    $er = $null   # reset for next company
}

# One summary email per run listing every company that needs a public name.
# Sent ONLY when at least one company was skipped; otherwise the inbox stays
# clean. Best-effort: an SMTP hiccup never fails the run.
if ($missingPublicName.Count -gt 0 -and -not $WhatIfMode -and $NotifyEmailTo -and $SmtpUser -and $SmtpPass) {
    $subj  = "[ELDK27] $($missingPublicName.Count) companies missing Public Company Name -- Zoho sync skipped them"
    $lines = @()
    $lines += "The following companies were SKIPPED by Sync-Webshop-Sponsors-to-Zoho-Backstage.ps1"
    $lines += "because their 'Public Company Name' (company_name_public) is empty in Company Manager."
    $lines += ""
    $lines += "Reason for skipping: we do not want the Zoho sponsor/exhibitor rows to be created with"
    $lines += "the legal-form suffix (ApS / A/S / Ltd / GmbH ...) in the displayed name."
    $lines += ""
    $lines += "Please open each company in Company Manager and set the short Public Company Name,"
    $lines += "then the next run of this script will push them to Zoho."
    $lines += ""
    foreach ($m in $missingPublicName) {
        $lines += ("  - {0,-40}  id={1,-4}  {2}" -f $m.LegalName, $m.CompanyId, $m.EditUrl)
    }
    $lines += ""
    $lines += "-- Sync-Webshop-Sponsors-to-Zoho-Backstage.ps1"
    $body = $lines -join "`r`n"
    try {
        $pwd  = ConvertTo-SecureString $SmtpPass -AsPlainText -Force
        $cred = New-Object System.Management.Automation.PSCredential ($SmtpUser, $pwd)
        Send-MailMessage -SmtpServer 'smtp-relay.brevo.com' -Port 587 -UseSsl `
            -Credential $cred -From $NotifyEmailFrom -To $NotifyEmailTo `
            -Subject $subj -Body $body -Encoding UTF8
        Write-Host "[Mail] missing-public-name summary sent to $NotifyEmailTo ($($missingPublicName.Count) entries)" -ForegroundColor DarkGray
    } catch {
        Write-Host "[Mail] WARN failed to send missing-public-name summary to '$NotifyEmailTo': $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-Host ""
Write-Host "==============================================="
Write-Host "Summary"
Write-Host "==============================================="
Write-Host "  Cut-off                          : $($OrdersAfter.ToString('yyyy-MM-dd'))"
Write-Host "  Zoho event                       : $ZohoEventId"
Write-Host "  Mapping file                     : $MappingFile"
Write-Host "  Product source                   : WooCommerce API (live, status=any)"
Write-Host "  CM companies skipped (no order)  : $($counters.Skipped)"
Write-Host "  CM companies considered          : $($counters.Considered)"
Write-Host "  CM companies skipped (no public) : $($counters.SkippedNoPublicName)" -ForegroundColor $(if ($counters.SkippedNoPublicName -gt 0) {'Yellow'} else {'Green'})
Write-Host "  Sponsor rows created             : $($counters.SponsorRowsCreated)"
Write-Host "  Sponsor rows updated             : $($counters.SponsorRowsUpdated)"
Write-Host "  Sponsor rows failed              : $($counters.SponsorRowsFailed)"
Write-Host "  Exhibitor requests filed         : $($counters.ExhibitorRequests)"
Write-Host "  Exhibitor skipped (no tier/id)   : $($counters.ExhibitorSkippedNoTier)"
if ($WhatIfMode) { Write-Host "  (WhatIf mode - no writes performed)" -ForegroundColor Yellow }
