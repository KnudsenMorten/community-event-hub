#------------------------------------------------------------------------------------------------
# Experts Live Denmark
# Zoho Backstage + Zoho Bookings Reconciliation
#------------------------------------------------------------------------------------------------

$ScriptDirectory = $PSScriptRoot
$global:PathScripts = Split-Path -Parent $ScriptDirectory
Import-Module "$ScriptDirectory\Secrets.psm1" -Global -Force -WarningAction SilentlyContinue
Import_Secrets

#------------------------------------------------------------------------------------------------
# Configuration
#------------------------------------------------------------------------------------------------
$EventName = "ELDK27"
$Mode      = "Both"
$WhatIf    = $true

$ZohoApiDomain = "https://www.zohoapis.eu"

$SmtpServer  = "smtp-relay.brevo.com"
$SmtpPort    = 587
$FromDisplay = "Experts Live Denmark"
$FromAddress = "info@expertslive.dk"

$BookingsServiceNameRegex = '(?i)master\s*class'
$TwoDayTicketNameRegex    = '(?i)\b2-day\b' # 2-day

$DisableThrottling = $true
$ReminderEveryDays = 1

#------------------------------------------------------------------------------------------------
# Output Paths
#------------------------------------------------------------------------------------------------
$OutDir = "C:\Scripts-ELDK-Automation\OUTPUT"
$MissingAppointmentsCsv = Join-Path $OutDir "backstage_2day_missing_bookings.csv"
$BookingsWithout2DayCsv = Join-Path $OutDir "bookings_without_2dayticket.csv"
$EmailPreviewDir = Join-Path $OutDir "email_previews"
$EmailPreviewIndex = Join-Path $OutDir "email_previews_index.csv"

if (-not (Test-Path -LiteralPath $OutDir)) {
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
}

if (-not (Test-Path -LiteralPath $EmailPreviewDir)) {
    New-Item -ItemType Directory -Path $EmailPreviewDir -Force | Out-Null
}

#------------------------------------------------------------------------------------------------
# Utility Functions
#------------------------------------------------------------------------------------------------
function Write-Info($msg) { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err ($msg) { Write-Host "[ERR ] $msg" -ForegroundColor Red }

function Ensure-OutDir {
    if (-not (Test-Path $OutDir)) {
        New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    }
    if (-not (Test-Path $EmailPreviewDir)) {
        New-Item -ItemType Directory -Path $EmailPreviewDir -Force | Out-Null
    }
}

function NormText([object]$v) {
    if ($null -eq $v) { return "" }
    return ([string]$v).Trim()
}

function NormEmail([object]$v) {
    $s = NormText $v
    if ($s -eq "") { return "" }
    return $s.ToLowerInvariant()
}

# ---- Booking + agenda links
$BookingUrl         = 'https://book.expertslive.dk'
$EventSiteUrl       = 'https://eldk27.expertslive.dk'
$AgendaUrl          = 'https://eldk27.expertslive.dk/#/agenda?day=1&lang=en'
$WaitlistUrl        = 'https://waitlist.masterclass.expertslive.dk'
$AskExpertsOpenDate = Get-Date '2027-01-05'

# ---- Email templates

$ReminderSubjectTpl_Purpose1 = "Ticket issue: Missing Master Class Registration for $($EventName)"

$ReminderBodyTpl_Purpose1 = @"
Hi {{FIRST}},<br><br>

Thank you for purchasing a <strong>2-day ticket</strong> for <strong>$EventName – Experts Live Denmark</strong>.<br>
However, our records indicate that you have <strong>not yet completed the required seat reservation</strong> for the <strong>Master Class</strong> of your choice.<br><br>

Master Classes are reserved on a <strong>first-come, first-served basis</strong> and typically fill up very quickly, so we recommend booking your seat as soon as possible.<br><br>

You can explore all available Master Classes in the <a href="$AgendaUrl"><strong>event agenda</strong></a>.<br><br>

<strong><a href="$BookingUrl">Reserve your Master Class seat here</a></strong><br><br>

<strong>WAITLIST</strong><br>
If your preferred Master Class is already fully booked, we recommend selecting an alternative class while also joining the waiting list for your preferred class.<br><br>

If a seat becomes available, you will automatically be notified.<br><br>

Best regards,<br>
<strong>Experts Live Denmark</strong>
"@



$ReminderSubjectTpl_Purpose2 = "Ticket issue: Complete your 2-day ticket purchase to attend master class at $EventName"

$ReminderBodyTpl_Purpose2 = @"
Hi {{FIRST}},<br><br>

Thank you for reserving a seat for a <strong>Master Class</strong> at <strong>$EventName – Experts Live Denmark</strong>.<br><br>

However, our records indicate that you have not yet purchased the required <strong>2-day event ticket</strong>, which is needed in order to attend the Master Classes.<br><br>

You can purchase your ticket here:<br>
<a href="$EventSiteUrl"><strong>Purchase your 2-day ticket</strong></a><br><br>

In many cases this happens when the Master Class reservation is made using a different email address than the one used when purchasing the event ticket.<br><br>

If you believe this message was sent in error, or if you need assistance, please contact us at  
<a href="mailto:info@expertslive.dk">info@expertslive.dk</a> and we will be happy to help.<br><br>

Best regards,<br>
<strong>Experts Live Denmark</strong>
"@


#------------------------------------------------------------------------------------------------
# Recursive Property Lookup
#------------------------------------------------------------------------------------------------
function Find-FirstValueRecursive {
    param($Object,[string[]]$Candidates,[int]$Depth = 0,[int]$MaxDepth = 4)
    if ($Depth -gt $MaxDepth) { return $null }
    try {
        foreach ($prop in $Object.PSObject.Properties) {
            if ($Candidates -contains $prop.Name) {
                if ($prop.Value) { return $prop.Value }
            }
        }
        foreach ($prop in $Object.PSObject.Properties) {
            if ($prop.Value -is [System.Collections.IEnumerable] -and !($prop.Value -is [string])) {
                foreach ($child in $prop.Value) {
                    $v = Find-FirstValueRecursive $child $Candidates ($Depth+1)
                    if ($v) { return $v }
                }
            } else {
                $v = Find-FirstValueRecursive $prop.Value $Candidates ($Depth+1)
                if ($v) { return $v }
            }
        }
    } catch {}
    return $null
}

#------------------------------------------------------------------------------------------------
# Zoho OAuth
#------------------------------------------------------------------------------------------------
function Get-ZohoAccessToken {
    param($ClientId,$ClientSecret,$RefreshToken)
    try {
        $resp = Invoke-RestMethod -Method POST `
        -Uri "https://accounts.zoho.eu/oauth/v2/token" `
        -Body @{
            refresh_token = $RefreshToken
            client_id     = $ClientId
            client_secret = $ClientSecret
            grant_type    = "refresh_token"
        }
        return $resp.access_token
    } catch {
        Write-Err "Failed to obtain Zoho token: $($_.Exception.Message)"
        return $null
    }
}

#------------------------------------------------------------------------------------------------
# Zoho Bookings
#------------------------------------------------------------------------------------------------
function Get-ZohoBookingsAppointments {
    param($Token,$FromTime,$ToTime)
    $uri = "$ZohoApiDomain/bookings/v1/json/fetchappointment"
    $page=1
    $results=@()
    $hasMore=$true
    while ($hasMore) {
        $payload = @{
            from_time=$FromTime.ToString("dd-MMM-yyyy HH:mm:ss")
            to_time=$ToTime.ToString("dd-MMM-yyyy HH:mm:ss")
            page=$page
            per_page=100
        }
        $json=$payload | ConvertTo-Json -Compress
        $boundary=[guid]::NewGuid().ToString()
$body=@"
--$boundary
Content-Disposition: form-data; name="data"

$json
--$boundary--
"@
        $headers=@{
            Authorization="Zoho-oauthtoken $Token"
            Accept="application/json"
            "Content-Type"="multipart/form-data; boundary=$boundary"
        }
        $resp=Invoke-RestMethod -Uri $uri -Method POST -Headers $headers -Body $body
        $items=$resp.response.returnvalue.data
        $results+=$items
        $hasMore=$resp.response.returnvalue.next_page_available
        $page++
    }
    return $results
}

#------------------------------------------------------------------------------------------------
# Zoho Orders
#------------------------------------------------------------------------------------------------
function Get-ZohoOrders {
    param($Token)
    $uri="$ZohoApiBase/portals/$ZohoPortalId/events/$ZohoEventId/orders"
    $headers=@{ Authorization="Zoho-oauthtoken $Token" }
    $resp=Invoke-RestMethod -Uri $uri -Headers $headers
    return $resp.orders
}

#------------------------------------------------------------------------------------------------
# Email Sender
#------------------------------------------------------------------------------------------------
function Send-ReminderEmail {
    param($To,$FirstName,$Subject,$BodyHtml)

    if ($WhatIf) {
        $timestamp = Get-Date
        $safeEmail = ($To -replace '[^a-zA-Z0-9@\._-]', '_')
        $file = Join-Path $EmailPreviewDir "$($timestamp.ToString('yyyyMMdd_HHmmss'))_$safeEmail.html"

$html=@"
<html>
<body>
<h3>$Subject</h3>
$BodyHtml
</body>
</html>
"@

        [System.IO.File]::WriteAllText($file,$html)

        $entry = [pscustomobject]@{
            Timestamp = $timestamp.ToString('s')
            To        = $To
            FirstName = $FirstName
            Subject   = $Subject
            File      = $file
        }

        if (Test-Path -LiteralPath $EmailPreviewIndex) {
            $entry | Export-Csv -Path $EmailPreviewIndex -NoTypeInformation -Append -Encoding UTF8
        }
        else {
            $entry | Export-Csv -Path $EmailPreviewIndex -NoTypeInformation -Encoding UTF8
        }

        Write-Warn "WHATIF EMAIL -> $To"
        Write-Host "Preview saved -> $file" -ForegroundColor DarkYellow
        return $true
    }

    try {
        $securePass = ConvertTo-SecureString $SmtpPass -AsPlainText -Force
        $cred = New-Object System.Management.Automation.PSCredential($SmtpUser,$securePass)
        $from = "$FromDisplay <$FromAddress>"

        Send-MailMessage `
            -To $To `
            -From $from `
            -Subject $Subject `
            -Body $BodyHtml `
            -BodyAsHtml `
            -SmtpServer $SmtpServer `
            -Port $SmtpPort `
            -Credential $cred `
            -UseSsl

        return $true
    }
    catch {
        Write-Err "Email failed: $($_.Exception.Message)"
        return $false
    }
}

function Invoke-ZohoBookingsPagedFetchAppointment {
    param(
        [Parameter(Mandatory=$true)][string]$Token,
        [Parameter(Mandatory=$true)][datetime]$FromTime,
        [Parameter(Mandatory=$true)][datetime]$ToTime,
        [Parameter(Mandatory=$true)][string]$ApiDomain,
        [int]$PerPage = 100
    )

    $uri = "$ApiDomain/bookings/v1/json/fetchappointment"
    $collection = @()
    $page = 1
    $nextPageAvailable = $true
    $culture = [System.Globalization.CultureInfo]::InvariantCulture

    while ($nextPageAvailable) {

        $dataObject = @{
            from_time = $FromTime.ToString("dd-MMM-yyyy HH:mm:ss", $culture)
            to_time   = $ToTime.ToString("dd-MMM-yyyy HH:mm:ss", $culture)
            page      = $page
            per_page  = $PerPage
        }

        $json = $dataObject | ConvertTo-Json -Compress

        $boundary = [guid]::NewGuid().ToString()

$multipartBody = @"
--$boundary
Content-Disposition: form-data; name="data"

$json
--$boundary--
"@

        $headers = @{
            Authorization = "Zoho-oauthtoken $Token"
            Accept        = "application/json"
            "Content-Type" = "multipart/form-data; boundary=$boundary"
        }

        Write-Host "📅 Fetching page $page"
        Write-Host "From: $($dataObject.from_time)"
        Write-Host "To  : $($dataObject.to_time)"

        try {
            $resp = Invoke-RestMethod `
                -Method Post `
                -Uri $uri `
                -Headers $headers `
                -Body $multipartBody
        }
        catch {
            Write-Host "❌ HTTP error on page $page" -ForegroundColor Red
            Write-Host $_.Exception.Message

            if ($_.Exception.Response) {
                $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                $raw = $reader.ReadToEnd()
                Write-Host "Raw response:"
                Write-Host $raw
            }

            break
        }

        $pageItems = @()

        if ($resp.response.returnvalue.data) {
            $pageItems = @($resp.response.returnvalue.data)
        }
        elseif ($resp.response.returnvalue.response) {
            $pageItems = @($resp.response.returnvalue.response)
        }

        $collection += $pageItems

        Write-Host "Rows received: $($pageItems.Count) | Total so far: $($collection.Count)"

        $nextPageAvailable = $false
        if ($resp.response.returnvalue.PSObject.Properties.Name -contains "next_page_available") {
            $nextPageAvailable = [bool]$resp.response.returnvalue.next_page_available
        }

        $page++
    }

    Write-Host "✔️ Fetch finished. Total rows: $($collection.Count)"

    return ,$collection
}


#------------------------------------------------------------------------------------------------
# MAIN
#------------------------------------------------------------------------------------------------
Ensure-OutDir
Write-Host ""
Write-Host "=== Zoho Reconciliation ($EventName) ===" -ForegroundColor Green

Write-Info "Getting Zoho OAuth token..."
$ZohoAccessToken=Get-ZohoAccessToken $ZohoClientId $ZohoClientSecret $ZohoRefreshToken
if (-not $ZohoAccessToken) { throw "Cannot continue without Zoho token." }

function Invoke-ZohoApi {
  param([Parameter(Mandatory)][string]$Method,[Parameter(Mandatory)][string]$Uri,[Parameter(Mandatory)][string]$Token,[hashtable]$Body = $null)
  $headers = @{ Authorization = "Zoho-oauthtoken $Token"; Accept = "application/json" }
  try {
    if ($Body) {
      $json = ($Body | ConvertTo-Json -Depth 12)
      return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -ContentType "application/json" -Body $json
    } else {
      return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers
    }
  } catch {
    $errMsg = $_.Exception.Message
    try {
      if ($_.Exception.Response -and $_.Exception.Response.GetResponseStream) {
        $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
        $body   = $reader.ReadToEnd()
        if ($body) { $errMsg = "$errMsg`nResponse body: $body" }
      }
    } catch {}
    throw "Zoho API $Method $Uri failed: $errMsg"
  }
}

function Get-ZohoPaged {
  param(
    [Parameter(Mandatory)][string]$Path,
    [Parameter(Mandatory)][string]$Token,
    [int]$PerPage = 200,
    [hashtable]$Query = $null
  )
  if ([string]::IsNullOrWhiteSpace($Path)) { throw "Get-ZohoPaged: Path is empty." }

  $base = $ZohoApiBase.TrimEnd('/')
  if (-not $Path.StartsWith('/')) { $Path = "/$Path" }

  $items = @()
  $page  = 1
  do {
    $uri = New-Object System.UriBuilder("$base$Path")
    $pairs = New-Object System.Collections.Generic.List[string]
    $pairs.Add("per_page=$PerPage"); $pairs.Add("page=$page")
    if ($Query) { foreach ($kv in $Query.GetEnumerator()) { if ($kv.Key) { $pairs.Add(("{0}={1}" -f $kv.Key, $kv.Value)) } } }
    $uri.Query = ($pairs -join "&")
    $url = $uri.Uri.AbsoluteUri

    Write-Host "🌐 Zoho GET $url"
    $resp = Invoke-ZohoApi -Method GET -Uri $url -Token $Token

    $list = @()
    if     ($resp.PSObject.Properties.Name -contains 'sponsors')   { $list = $resp.sponsors }
    elseif ($resp.PSObject.Properties.Name -contains 'exhibitors') { $list = $resp.exhibitors }
    elseif ($resp.PSObject.Properties.Name -contains 'orders')     { $list = $resp.orders }
    elseif ($resp.PSObject.Properties.Name -contains 'data')       { $list = $resp.data }

    if ($list) { $items += $list }

    $hasMore = $false
    if ($resp.PSObject.Properties.Name -contains 'pagination') {
      $hasMore = [bool]$resp.pagination.has_more_items; $page++
    }
  } while ($hasMore)

  return ,$items
}

function Get-ZohoAllOrders {
  param(
    [Parameter(Mandatory)][string]$Token,
    [Parameter(Mandatory)][string]$PortalId,
    [Parameter(Mandatory)][string]$EventId,
    [int]$PerPage = 200,
    [hashtable]$Query = $null
  )
  $path = "/portals/$PortalId/events/$EventId/orders"
  return Get-ZohoPaged -Path $path -Token $Token -PerPage $PerPage -Query $Query
}

# ---- Get token FIRST
Write-Host "🔑 Getting Zoho access token..."
$ZohoAccessToken = Get-ZohoAccessToken -ClientId $ZohoClientId -ClientSecret $ZohoClientSecret -RefreshToken $ZohoRefreshToken
if (-not $ZohoAccessToken) { Write-Host "⚠️  Zoho token unavailable; Zoho operations will be skipped." -ForegroundColor Yellow }

# ---- Orders
$ZohoOrders = @()

if ($ZohoAccessToken) {
  try {
    Write-Host "📦 Fetching Zoho orders..."
    $ZohoOrders = Get-ZohoAllOrders -Token $ZohoAccessToken -PortalId $ZohoPortalId -EventId $ZohoEventId
    Write-Host "✔️ Orders loaded: $($ZohoOrders.Count)"
  }
  catch {
    Write-Host "❌ Failed to fetch Zoho orders: $($_.Exception.Message)" -ForegroundColor Red
  }
}


$MasterClassDate = "09-02-2027"

$BookingsFrom = [datetime]::ParseExact("$($MasterClassDate) 00:00:00", "dd-MM-yyyy HH:mm:ss", $culture)
$BookingsTo   = [datetime]::ParseExact("$($MasterClassDate) 23:59:59", "dd-MM-yyyy HH:mm:ss", $culture)

$ZohoBookingsAppointments = Invoke-ZohoBookingsPagedFetchAppointment `
    -Token $ZohoAccessToken `
    -ApiDomain $ZohoApiDomain `
    -FromTime $BookingsFrom `
    -ToTime $BookingsTo `
    -PerPage 100

# Active Appointments
    $ActiveAppointments = $ZohoBookingsAppointments | Where-Object { $_.status -ne "cancel" }

# ---- Build flat order/ticket export array
function Join-NonEmpty {
    param([string[]]$Parts)
    (($Parts | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ', ')
}

$ZohoOrdersExport = foreach ($order in $ZohoOrders) {

    $tickets = @($order.tickets)
    if (-not $tickets -or $tickets.Count -eq 0) {
        continue
    }

    foreach ($ticket in $tickets) {

        $purchasedOn = $null
        $orderTime   = $null
        if ($order.created_time) {
            try {
                $dt = [datetime]::Parse($order.created_time).ToLocalTime()
                $purchasedOn = $dt.ToString('yyyy-MM-dd')
                $orderTime   = $dt.ToString('HH:mm:ss')
            } catch {
                $purchasedOn = $order.created_time
                $orderTime   = $order.created_time
            }
        }

        $billingAddressFull = Join-NonEmpty @(
            $order.billing_address.streetAddress1
            $order.billing_address.streetAddress2
            $order.billing_address.zipcode
            $order.billing_address.city
            $order.billing_address.state
            $order.billing_address.country
        )

        $gatewayTransactionId = $null
        $paymentGateway       = $null
        if ($order.payments -and @($order.payments).Count -gt 0) {
            $gatewayTransactionId = $order.payments[0].reference_id
            $paymentGateway       = $order.payments[0].type
        }

        $promoCode = $null
        if ($ticket.promo_code) {
            $promoCode = $ticket.promo_code
        }
        elseif ($order.cost.promo_code) {
            $promoCode = $order.cost.promo_code
        }

        [PSCustomObject]@{
            ORDER_ID                         = $order.id
            TICKET_ID                        = $ticket.id
            TICKET_CLASS                     = $ticket.ticket_name
            FIRST_NAME                       = $ticket.contact.first_name
            LAST_NAME                        = $ticket.contact.last_name
            EMAIL                            = $ticket.contact.email
            COMPANY_NAME                     = $ticket.contact.company_name
            MOBILE_NO                        = $ticket.contact.mobile_no
            SINGLE_CHOICE                    = $ticket.contact.single_choice
            SINGLE_CHOICE_3                  = $ticket.contact.single_choice_3
            SINGLE_CHOICE_2                  = $ticket.contact.single_choice_2
            SINGLE_CHOICE_1                  = $ticket.contact.single_choice_1
            MULTIPLE_CHOICE                  = $ticket.contact.multiple_choice
            DESIGNATION                      = $ticket.contact.designation
            PURCHASER_FIRST_NAME             = $order.contact.purchaser_first_name
            PURCHASER_LAST_NAME              = $order.contact.purchaser_last_name
            PURCHASER_MOBILE_NO              = $order.contact.purchaser_mobile_no
            PURCHASER_EMAIL                  = $order.contact.purchaser_email
            PURCHASER_BILLING_ADDRESS        = $billingAddressFull
            PURCHASER_TAX_REGISTRATION_NO    = $order.contact.tax_registration_no
            AMOUNT_COLLECTED                 = $ticket.total
            ATTENDEE_STATUS                  = $ticket.status_string
            PURCHASED_ON                     = $purchasedOn
            ORDER_TIME                       = $orderTime
            PROMO_CODE                       = $promoCode
            AFFILIATE_CODE                   = $null
            PAYMENT_MODE                     = $order.payment_option_name
            PAYMENT_STATUS                   = $order.payment_status_string
            PAYMENT_GATEWAY                  = $paymentGateway
            GATEWAY_TRANSACTION_ID           = $gatewayTransactionId
            PURCHASER_BILLING_CITY           = $order.billing_address.city
            PURCHASER_BILLING_ADDRESS_NAME   = $order.billing_address.name
            PURCHASER_BILLING_ADDRESS_LINE_1 = $order.billing_address.streetAddress1
            PURCHASER_BILLING_ADDRESS_LINE_2 = $order.billing_address.streetAddress2
            PURCHASER_BILLING_STATE          = $order.billing_address.state
            PURCHASER_BILLING_ZIP_CODE       = $order.billing_address.zipcode
            PURCHASER_BILLING_COUNTRY        = $order.billing_address.country
        }
    }
}

Write-Host "✔️ Flattened rows created: $($ZohoOrdersExport.Count)"


$ordersNormalized = $ZohoOrdersExport |
    Where-Object { -not [string]::IsNullOrWhiteSpace($_.EMAIL) } |
    ForEach-Object {
        [pscustomobject]@{
            Email           = NormEmail $_.EMAIL
            TicketClassName = NormText $_.TICKET_CLASS
            FirstName       = NormText $_.FIRST_NAME
            LastName        = NormText $_.LAST_NAME
        }
    }

$appointmentsNormalized=@()
foreach ($a in $ActiveAppointments) {
    $email=NormEmail (Find-FirstValueRecursive $a @("customer_email","email"))
    $svc=NormText (Find-FirstValueRecursive $a @("service_name","service"))
    if (-not $email) { continue }
    if ($svc -notmatch $BookingsServiceNameRegex) { continue }
    $appointmentsNormalized += [pscustomobject]@{
        Email=$email
        ServiceName=$svc
        Status=$a.status
    }
}

$twoDayOrders = $ordersNormalized | Where-Object {
    $_.TicketClassName -and $_.TicketClassName -match $TwoDayTicketNameRegex
}

$appointmentsByEmail = @{}

foreach ($a in $appointmentsNormalized) {

    if (-not $appointmentsByEmail.ContainsKey($a.Email)) {
        $appointmentsByEmail[$a.Email] = @()
    }

    $appointmentsByEmail[$a.Email] += $a
}


$missing=@()
foreach ($o in $twoDayOrders) {
    if (-not $appointmentsByEmail.ContainsKey($o.Email)) { $missing+=$o }
}

$missing | Export-Csv $MissingAppointmentsCsv -NoTypeInformation
Write-Info "2-day attendees missing master class booking: $($missing.Count)"

$extra=@()
foreach ($kv in $appointmentsByEmail.GetEnumerator()) {
    $email=$kv.Key
    $match=$twoDayOrders | Where-Object { $_.Email -eq $email }
    if (-not $match) { $extra+=$kv.Value[0] }
}

$extra | Export-Csv $BookingsWithout2DayCsv -NoTypeInformation
Write-Info "Bookings without 2-day ticket: $($extra.Count)"

foreach ($row in $missing) {
    $first = if ($row.FirstName) { $row.FirstName } else { "there" }

    $body = $ReminderBodyTpl_Purpose1.Replace('{{FIRST}}', $first)

    Send-ReminderEmail `
        $row.Email `
        $first `
        $ReminderSubjectTpl_Purpose1 `
        $body
}

foreach ($row in $extra) {
    $first = if ($row.FirstName) { $row.FirstName } else { "there" }

    $body = $ReminderBodyTpl_Purpose2.Replace('{{FIRST}}', $first)

    Send-ReminderEmail `
        $row.Email `
        $first `
        $ReminderSubjectTpl_Purpose2 `
        $body
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
