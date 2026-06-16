#------------------------------------------------------------------------------------------------
Write-Output "***********************************************************************************************"
Write-Output "Experts Live Denmark Integration Zoho -> e-conomic"
Write-Output ""
Write-Output "Purpose: Detect claimed Zoho coupon orders and create missing draft invoices in e-conomic"
Write-Output ""
Write-Output "Support: Morten Knudsen - mok@expertslive.dk"
Write-Output "***********************************************************************************************"
#------------------------------------------------------------------------------------------------

$ScriptDirectory = $PSScriptRoot
$global:PathScripts = Split-Path -Parent $ScriptDirectory
Import-Module "$ScriptDirectory\Secrets.psm1" -Global -Force -WarningAction SilentlyContinue
Import_Secrets

$EventName = "ELDK27"
$WhatIf = $true

#------------------------------------------------------------------------------------------------
# Configuration
#------------------------------------------------------------------------------------------------
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$SmtpServer  = "smtp-relay.brevo.com"
$SmtpPort    = 587
$FromDisplay = "Experts Live Denmark"
$FromAddress = "info@expertslive.dk"
$AlertTo     = @("mok@expertslive.dk")

$CouponSettingsPath = "C:\Scripts-ELDK-Automation\Settings\ZohoBackstageCoupons_Invoicing.csv"
$OutputDir          = "C:\Scripts-ELDK-Automation\OUTPUT"
$PendingCouponsCsv  = Join-Path $OutputDir "Zoho-Coupon-InvoicesPending.csv"
$SkippedCouponsCsv  = Join-Path $OutputDir "Zoho-Coupon-InvoicesSkipped.csv"
$FailedCouponsCsv   = Join-Path $OutputDir "Zoho-Coupon-InvoicesFailed.csv"

$EconomicLayoutNameLike = "English*"
$VendorEmployeeNumber   = 1

$InvoiceableCouponTypes = @(
    'AllocatedPrepaymentByCustomer',
    'ClaimableAdHocPaymentByCustomer'
)

#------------------------------------------------------------------------------------------------
# Helpers
#------------------------------------------------------------------------------------------------
function Write-Stage {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [ValidateSet('INFO','WARN','ERR','OK','DBG')][string]$Level = 'INFO'
    )

    $color = switch ($Level) {
        'INFO' { 'Cyan' }
        'WARN' { 'Yellow' }
        'ERR'  { 'Red' }
        'OK'   { 'Green' }
        'DBG'  { 'Gray' }
    }

    Write-Host "[$Level] $Message" -ForegroundColor $color
}

function Ensure-Directory {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
}

function NormText {
    param([object]$Value)

    if ($null -eq $Value) { return "" }
    return ([string]$Value).Trim()
}

function NormEmail {
    param([object]$Value)

    $s = NormText $Value
    if ([string]::IsNullOrWhiteSpace($s)) { return "" }
    return $s.ToLowerInvariant()
}

function Join-NonEmpty {
    param([string[]]$Parts)

    (($Parts | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) -join ', ')
}

#------------------------------------------------------------------------------------------------
# Email
#------------------------------------------------------------------------------------------------
function Send-AlertMail {
    param(
        [Parameter(Mandatory = $true)][string]$Subject,
        [Parameter(Mandatory = $true)][string]$Body
    )

    try {
        $securePass = ConvertTo-SecureString $SmtpPass -AsPlainText -Force
        $cred = New-Object System.Management.Automation.PSCredential($SmtpUser, $securePass)
        $from = "$FromDisplay <$FromAddress>"

        Write-Stage "Sending alert mail to $($AlertTo -join ', ')" 'WARN'

        Send-MailMessage `
            -To $AlertTo `
            -From $from `
            -Subject $Subject `
            -Body $Body `
            -SmtpServer $SmtpServer `
            -Port $SmtpPort `
            -Credential $cred `
            -UseSsl
    }
    catch {
        Write-Stage "FAILED to send alert mail: $($_.Exception.Message)" 'ERR'
    }
}

function Send-ProcessAlert {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$Message,
        [string]$Details = ""
    )

    $subject = "ALERT: Experts Live Denmark Zoho -> e-conomic | $Stage"
    $body = @"
Stage:
$Stage

Message:
$Message

Details:
$Details

Host:
$env:COMPUTERNAME

User:
$env:USERNAME

Time:
$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@

    Send-AlertMail -Subject $subject -Body $body
}

function Send-InvoiceReadyMail {
    param(
        [Parameter(Mandatory = $true)]$CreatedDrafts,
        [Parameter(Mandatory = $true)][string]$EventName
    )

    if (-not $CreatedDrafts -or $CreatedDrafts.Count -eq 0) {
        Write-Stage "No created drafts -> no success mail sent." 'DBG'
        return
    }

    try {
        $securePass = ConvertTo-SecureString $SmtpPass -AsPlainText -Force
        $cred = New-Object System.Management.Automation.PSCredential($SmtpUser, $securePass)
        $from = "$FromDisplay <$FromAddress>"

        $subject = "Coupon draft invoices ready in e-conomic - $EventName"

        $draftLines = ($CreatedDrafts | ForEach-Object {
            "Reference: {0} | Coupon: {1} | Customer: {2} | CustomerNumber: {3} | Currency: {4} | DraftInvoiceNumber: {5}" -f `
                $_.Reference,
                $_.Coupon,
                $_.Customer,
                $_.CustomerNumber,
                $_.Currency,
                $_.DraftInvoiceNumber
        }) -join "`r`n"

        $body = @"
The Zoho coupon draft invoice creation has completed successfully.

Event:
$EventName

Created draft invoices:
$draftLines

Total created:
$($CreatedDrafts.Count)

Time:
$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@

        Write-Stage "Sending invoice-ready mail to $($AlertTo -join ', ')" 'OK'

        Send-MailMessage `
            -To $AlertTo `
            -From $from `
            -Subject $subject `
            -Body $body `
            -SmtpServer $SmtpServer `
            -Port $SmtpPort `
            -Credential $cred `
            -UseSsl
    }
    catch {
        Write-Stage "FAILED to send invoice-ready mail: $($_.Exception.Message)" 'ERR'
    }
}

function Send-CouponSetupMissingMail {
    param(
        [Parameter(Mandatory = $true)][string]$CouponName,
        [Parameter(Mandatory = $true)][string]$BillingType,
        [string]$OrderId = "",
        [string]$Reference = "",
        [string]$PurchaserName = "",
        [string]$PurchaserEmail = "",
        [string]$CompanyName = ""
    )

    $subject = "ACTION REQUIRED: Coupon missing ERP mapping - $CouponName"

    $body = @"
A Zoho coupon claim was detected, but the coupon is not fully configured for invoicing.

Coupon:
$CouponName

Billing type:
$BillingType

Reference:
$Reference

OrderId:
$OrderId

Purchaser:
$PurchaserName

Purchaser email:
$PurchaserEmail

Company:
$CompanyName

Settings file:
$CouponSettingsPath

Required CSV structure:
Couponname;CouponBillingDetails;ErpCustomerNumberInvoicing

Allowed billing types:
AllocatedPrepaymentByCustomer
ClaimableAdHocPaymentByCustomer
NoInvoicing

Time:
$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@

    Send-AlertMail -Subject $subject -Body $body
}

#------------------------------------------------------------------------------------------------
# Coupon settings
#------------------------------------------------------------------------------------------------
function Get-CouponInvoicingSettings {
    param(
        [Parameter(Mandatory = $true)][string]$Path
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Coupon settings file not found: $Path"
    }

    $rows = Import-Csv -Path $Path -Delimiter ';'
    $validTypes = @(
        'AllocatedPrepaymentByCustomer',
        'ClaimableAdHocPaymentByCustomer',
        'NoInvoicing'
    )

    foreach ($row in $rows) {
        $couponName = NormText $row.Couponname
        $billingType = NormText $row.CouponBillingDetails

        if ([string]::IsNullOrWhiteSpace($couponName)) {
            throw "Coupon settings file contains empty Couponname."
        }

        if ($validTypes -notcontains $billingType) {
            throw "Invalid CouponBillingDetails '$billingType' for coupon '$couponName'."
        }
    }

    return $rows
}

function Get-CouponSetting {
    param(
        [Parameter(Mandatory = $true)][string]$CouponName,
        [Parameter(Mandatory = $true)]$CouponSettings
    )

    return $CouponSettings |
        Where-Object { (NormText $_.Couponname) -eq $CouponName } |
        Select-Object -First 1
}

#------------------------------------------------------------------------------------------------
# Zoho
#------------------------------------------------------------------------------------------------
function Get-ZohoAccessToken {
    param(
        [Parameter(Mandatory = $true)][string]$ClientId,
        [Parameter(Mandatory = $true)][string]$ClientSecret,
        [Parameter(Mandatory = $true)][string]$RefreshToken
    )

    try {
        $resp = Invoke-RestMethod `
            -Method POST `
            -Uri "https://accounts.zoho.eu/oauth/v2/token" `
            -Body @{
                refresh_token = $RefreshToken
                client_id     = $ClientId
                client_secret = $ClientSecret
                grant_type    = "refresh_token"
            }

        return $resp.access_token
    }
    catch {
        throw "Failed to obtain Zoho token: $($_.Exception.Message)"
    }
}

function Invoke-ZohoApi {
    param(
        [Parameter(Mandatory = $true)][string]$Method,
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][string]$Token,
        [hashtable]$Body = $null
    )

    $headers = @{
        Authorization = "Zoho-oauthtoken $Token"
        Accept        = "application/json"
    }

    try {
        if ($Body) {
            $json = $Body | ConvertTo-Json -Depth 12
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers -ContentType "application/json" -Body $json
        }
        else {
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $headers
        }
    }
    catch {
        $errMsg = $_.Exception.Message

        try {
            if ($_.Exception.Response -and $_.Exception.Response.GetResponseStream) {
                $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                $raw = $reader.ReadToEnd()
                if ($raw) { $errMsg = "$errMsg`nResponse body: $raw" }
            }
        }
        catch {}

        throw "Zoho API $Method $Uri failed: $errMsg"
    }
}

function Get-ZohoPaged {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Token,
        [int]$PerPage = 200,
        [hashtable]$Query = $null
    )

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw "Get-ZohoPaged: Path is empty."
    }

    $base = $ZohoApiBase.TrimEnd('/')
    if (-not $Path.StartsWith('/')) { $Path = "/$Path" }

    $items = @()
    $page = 1

    do {
        $uri = New-Object System.UriBuilder("$base$Path")
        $pairs = New-Object System.Collections.Generic.List[string]
        $pairs.Add("per_page=$PerPage")
        $pairs.Add("page=$page")

        if ($Query) {
            foreach ($kv in $Query.GetEnumerator()) {
                if ($kv.Key) {
                    $pairs.Add(("{0}={1}" -f $kv.Key, $kv.Value))
                }
            }
        }

        $uri.Query = ($pairs -join "&")
        $url = $uri.Uri.AbsoluteUri

        Write-Stage "Zoho GET $url" 'DBG'
        $resp = Invoke-ZohoApi -Method GET -Uri $url -Token $Token

        $list = @()
        if     ($resp.PSObject.Properties.Name -contains 'orders') { $list = $resp.orders }
        elseif ($resp.PSObject.Properties.Name -contains 'data')   { $list = $resp.data }

        if ($list) { $items += $list }

        $hasMore = $false
        if ($resp.PSObject.Properties.Name -contains 'pagination') {
            $hasMore = [bool]$resp.pagination.has_more_items
            $page++
        }
    } while ($hasMore)

    return ,$items
}

function Get-ZohoAllOrders {
    param(
        [Parameter(Mandatory = $true)][string]$Token,
        [Parameter(Mandatory = $true)][string]$PortalId,
        [Parameter(Mandatory = $true)][string]$EventId
    )

    $path = "/portals/$PortalId/events/$EventId/orders"
    return Get-ZohoPaged -Path $path -Token $Token
}

function Convert-ZohoOrdersToCouponRows {
    param(
        [Parameter(Mandatory = $true)]$ZohoOrders
    )

    $rows = foreach ($order in $ZohoOrders) {
        $tickets = @($order.tickets)

        if (-not $tickets -or $tickets.Count -eq 0) { continue }

        foreach ($ticket in $tickets) {
            $promoCode = $null

            if ($ticket.promo_code) {
                $promoCode = NormText $ticket.promo_code
            }
            elseif ($order.cost -and $order.cost.promo_code) {
                $promoCode = NormText $order.cost.promo_code
            }

            if ([string]::IsNullOrWhiteSpace($promoCode)) { continue }

            $ticketBasePriceDkk = 0
            if ($ticket.base_price -ne $null) {
                try { $ticketBasePriceDkk = [double]$ticket.base_price } catch { $ticketBasePriceDkk = 0 }
            }

            $ticketTotalDkk = 0
            if ($ticket.total -ne $null) {
                try { $ticketTotalDkk = [double]$ticket.total } catch { $ticketTotalDkk = 0 }
            }

            $purchasedOn = $null
            $orderTime = $null
            if ($order.created_time) {
                try {
                    $dt = [datetime]::Parse($order.created_time).ToLocalTime()
                    $purchasedOn = $dt.ToString('yyyy-MM-dd')
                    $orderTime   = $dt.ToString('HH:mm:ss')
                }
                catch {
                    $purchasedOn = [string]$order.created_time
                    $orderTime   = [string]$order.created_time
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

            [pscustomobject]@{
                OrderId                  = [string]$order.id
                TicketId                 = [string]$ticket.id
                CouponName               = $promoCode
                Reference                = "$promoCode-$($order.id)"
                Email                    = NormEmail $ticket.contact.email
                FirstName                = NormText $ticket.contact.first_name
                LastName                 = NormText $ticket.contact.last_name
                CompanyName              = NormText $ticket.contact.company_name

                TicketClassName          = NormText $ticket.ticket_name
                TicketName               = NormText $ticket.ticket_name

                UnitPriceDkk             = $ticketBasePriceDkk
                Amount                   = $ticketTotalDkk
                Currency                 = "DKK"

                Status                   = NormText $ticket.status_string
                PurchaserFirstName       = NormText $order.contact.purchaser_first_name
                PurchaserLastName        = NormText $order.contact.purchaser_last_name
                PurchaserEmail           = NormEmail $order.contact.purchaser_email
                PurchaserMobileNo        = NormText $order.contact.purchaser_mobile_no
                PurchaserCompanyName     = NormText $order.billing_address.name
                PurchaserBillingAddress  = $billingAddressFull
                PurchasedOn              = $purchasedOn
                OrderTime                = $orderTime
                PaymentStatus            = NormText $order.payment_status_string
            }
        }
    }

    return @($rows)
}

#------------------------------------------------------------------------------------------------
# e-conomic
#------------------------------------------------------------------------------------------------
function Invoke-EconomicPagedGet {
    param(
        [Parameter(Mandatory = $true)][string]$Uri,
        [Parameter(Mandatory = $true)][hashtable]$Headers
    )

    $collection = @()
    $resp = Invoke-RestMethod -Method Get -Headers $Headers -Uri $Uri

    if ($resp.collection) {
        $collection += $resp.collection
    }

    while ($resp.pagination -and ($resp.pagination.PSObject.Properties.Name -contains 'nextPage') -and $resp.pagination.nextPage) {
        $resp = Invoke-RestMethod -Method Get -Headers $Headers -Uri $resp.pagination.nextPage
        if ($resp.collection) {
            $collection += $resp.collection
        }
    }

    return $collection
}

function Get-EconomicLayout {
    param(
        [Parameter(Mandatory = $true)]$Layouts,
        [string]$NameLike = "English*"
    )

    $layout = $Layouts | Where-Object { $_.name -like $NameLike } | Select-Object -First 1

    if (-not $layout) {
        throw "No e-conomic layout found matching '$NameLike'"
    }

    return $layout
}

function Get-EconomicPaymentTermsScope {
    param(
        [Parameter(Mandatory = $true)]$Customer,
        [Parameter(Mandatory = $true)]$PaymentTerms
    )

    $paymentTermsScope = $PaymentTerms |
        Where-Object { $_.paymentTermsNumber -eq $Customer.paymentTerms.paymentTermsNumber } |
        Select-Object -First 1

    if (-not $paymentTermsScope) {
        throw "Payment terms not found for customerNumber '$($Customer.customerNumber)'"
    }

    return $paymentTermsScope
}

function Get-EconomicVatZoneScope {
    param(
        [Parameter(Mandatory = $true)]$Customer,
        [Parameter(Mandatory = $true)]$VatZones
    )

    $vatZoneScope = $VatZones |
        Where-Object { $_.vatZoneNumber -eq $Customer.vatZone.vatZoneNumber } |
        Select-Object -First 1

    if (-not $vatZoneScope) {
        throw "VAT zone not found for customerNumber '$($Customer.customerNumber)'"
    }

    return $vatZoneScope
}

function Get-EconomicCustomerContactNumber {
    param(
        [Parameter(Mandatory = $true)]$Customer
    )

    $contactNumber = $null

    if ($Customer.PSObject.Properties.Name -contains 'attention' -and $Customer.attention) {
        $contactNumber = $Customer.attention.customerContactNumber
    }

    if (-not $contactNumber -and ($Customer.PSObject.Properties.Name -contains 'customerContact') -and $Customer.customerContact) {
        $contactNumber = $Customer.customerContact.customerContactNumber
    }

    return $contactNumber
}

function New-EconomicRecipientObject {
    param(
        [Parameter(Mandatory = $true)]$Customer,
        [Parameter(Mandatory = $true)]$VatZoneScope
    )

    $recipientCountry = $Customer.country
    if (-not $recipientCountry) { $recipientCountry = "DK" }

    $recipient = [ordered]@{
        name    = $Customer.name
        address = $Customer.address
        zip     = $Customer.zip
        city    = $Customer.city
        country = $recipientCountry
        vatZone = @{
            vatZoneNumber = $Customer.vatZone.vatZoneNumber
            self          = $VatZoneScope.self
        }
    }

    $contactNumber = Get-EconomicCustomerContactNumber -Customer $Customer
    if ($contactNumber) {
        $recipient.attention = @{
            customerContactNumber = $contactNumber
        }
    }

    if ($Customer.ean) {
        $recipient.ean = $Customer.ean
    }

    return $recipient
}

function Get-EconomicCouponProductNumber {
    param(
        [Parameter(Mandatory = $true)][int]$VatZoneNumber
    )

    switch ($VatZoneNumber) {
        1 { return "1000" }
        2 { return "2000" }
        3 { return "2000" }
        4 { return "2000" }
        default { throw "Unsupported VAT zone '$VatZoneNumber'. Expected 1-4." }
    }
}

function Convert-AmountFromDkkOrFail {
    param(
        [Parameter(Mandatory = $true)][double]$AmountFrom,
        [Parameter(Mandatory = $true)][string]$To_Currency
    )

    if ([string]::IsNullOrWhiteSpace($To_Currency)) {
        throw "To_Currency is empty"
    }

    $targetCurrency = $To_Currency.ToUpper()

    if ($targetCurrency -eq "DKK") {
        return [double]([math]::Round($AmountFrom, 2))
    }

    $uri = "https://onesimpleapi.com/api/exchange_rate?token=$($global:Currency_APIKey)&output=text&from_currency=DKK&to_currency=$targetCurrency&from_value=$AmountFrom"

    try {
        $AmountToRaw = [double](Invoke-RestMethod -Uri $uri -Method Get -ErrorAction Stop)

        if ($AmountToRaw -le 0) {
            throw "Invalid exchange result returned from API: '$AmountToRaw'"
        }

        return [double]([math]::Round($AmountToRaw, 2))
    }
    catch {
        $ErrorMessage = $_.Exception.Message
        $RawResponse = $null

        if ($_.Exception.Response) {
            try {
                $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                $RawResponse = $reader.ReadToEnd()
            }
            catch {}
        }

        $details = @"
From currency : DKK
To currency   : $targetCurrency
Amount        : $AmountFrom
Request URI   : $uri

Error:
$ErrorMessage

Raw response:
$RawResponse
"@

        Send-ProcessAlert -Stage "Currency Conversion" -Message "Currency conversion API call failed" -Details $details
        throw "Currency conversion API call failed: $ErrorMessage"
    }
}

#------------------------------------------------------------------------------------------------
# Coupon invoice line logic
#
# Product number:
# - VAT zone 1  -> product 1000
# - VAT zone 2  -> product 2000
# - VAT zone 3  -> product 2000
# - VAT zone 4  -> product 2000
#
# Pricing:
# - Source ticket prices from Zoho are treated as DKK
# - Invoice line is based on ticket class details from the Zoho order
# - Unit price is read from the order/ticket details
#
# Currency handling:
# - If ERP customer currency is DKK, no conversion is applied
# - If ERP customer currency is EUR or USD, price is converted from DKK
#   using the exchange-rate API

# We have 3 types of CouponBillingDetails: 
#* AllocatedPrepaymentByCustomer. Customer claims an amount and uses plus-addressing and secures the ticket (allocated). New coupon (mail): Read Purchaser info from the order and send info about linking to ErpCustomerNumber
#* ClaimableAdHocPaymentByCustomer. Ticket can be claimed by any user with coupon code. Partner will pay if claimed. Ticket is NOT allocated "first come, first served". Ad hoc biling. New coupon (send mail and ask to link customer in settings file) where Invoice must be sent to
#* NoInvoicing. Coupon will not be invoiced (free, included)

#------------------------------------------------------------------------------------------------
function New-CouponInvoiceLines {
    param(
        [Parameter(Mandatory = $true)]$CouponRows,
        [Parameter(Mandatory = $true)][int]$VatZoneNumber,
        [Parameter(Mandatory = $true)][string]$CustomerCurrency
    )

    $targetCurrency = $CustomerCurrency.ToUpper()
    if ([string]::IsNullOrWhiteSpace($targetCurrency)) {
        $targetCurrency = "DKK"
    }

    $productNumber = Get-EconomicCouponProductNumber -VatZoneNumber $VatZoneNumber

    $lineNo = 0
    $invoiceLines = @()

    foreach ($row in $CouponRows) {

        $lineNo++

        $originalUnitPriceDkk = [double]$row.UnitPriceDkk
        $convertedUnitPrice   = $originalUnitPriceDkk
        $conversionText       = $null

        if ($targetCurrency -ne "DKK") {

            $convertedUnitPrice = Convert-AmountFromDkkOrFail `
                -AmountFrom $originalUnitPriceDkk `
                -To_Currency $targetCurrency

            $conversionText = @(
                "Currency conversion applied: DKK -> $targetCurrency"
                "Original DKK $originalUnitPriceDkk -> $targetCurrency $convertedUnitPrice"
            ) -join "`r`n"
        }

        $descriptionParts = @(
            "Ticket Class: $($row.TicketClassName)"
            "Attendee: $($row.FirstName) $($row.LastName)".Trim()
            "Email: $($row.Email)"
            "Coupon: $($row.CouponName)"
            "Zoho OrderId: $($row.OrderId)"
            "Zoho TicketId: $($row.TicketId)"
        )

        if ($conversionText) {
            $descriptionParts += $conversionText
        }

        $description = ($descriptionParts -join "`r`n")

        $invoiceLines += [pscustomobject]@{
            lineNumber   = [int]$lineNo
            sortKey      = [int]$lineNo
            description  = [string]$description
            quantity     = 1
            unitNetPrice = [double]([math]::Round($convertedUnitPrice, 2))
            product      = @{
                productNumber = [string]$productNumber
            }
            unit         = @{
                unitNumber = 1
                self       = "https://restapi.e-conomic.com/units/1"
            }
        }
    }

    return ,$invoiceLines
}

function New-EconomicDraftInvoiceCoupon {
    param(
        [Parameter(Mandatory = $true)]$EconomicCustomer,
        [Parameter(Mandatory = $true)]$VatZones,
        [Parameter(Mandatory = $true)]$Layouts,
        [Parameter(Mandatory = $true)]$PaymentTerms,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [Parameter(Mandatory = $true)]$CouponRow,
        [Parameter(Mandatory = $true)]$InvoiceLines,
        [string]$LayoutNameLike = "English*",
        [int]$VendorEmployeeNumber = 1,
        [switch]$WhatIf
    )

    $vatZoneScope      = Get-EconomicVatZoneScope -Customer $EconomicCustomer -VatZones $VatZones
    $layoutScope       = Get-EconomicLayout -Layouts $Layouts -NameLike $LayoutNameLike
    $paymentTermsScope = Get-EconomicPaymentTermsScope -Customer $EconomicCustomer -PaymentTerms $PaymentTerms
    $recipientObject   = New-EconomicRecipientObject -Customer $EconomicCustomer -VatZoneScope $vatZoneScope
    $contactNumber     = Get-EconomicCustomerContactNumber -Customer $EconomicCustomer

    $invoiceCurrency = [string]$EconomicCustomer.currency
    if ([string]::IsNullOrWhiteSpace($invoiceCurrency)) { $invoiceCurrency = "DKK" }

    $references = [ordered]@{
        other = [string]$CouponRow.Reference
        vendorReference = @{
            employeeNumber = $VendorEmployeeNumber
        }
    }

    if ($contactNumber) {
        $references.customerContact = @{
            customerContactNumber = $contactNumber
        }
    }

    $body = [ordered]@{
        currency     = $invoiceCurrency
        customer     = @{
            customerNumber = [int]$EconomicCustomer.customerNumber
            self           = [string]$EconomicCustomer.self
        }
        date         = (Get-Date).ToString("yyyy-MM-dd")
        layout       = @{
            layoutNumber = [int]$layoutScope.layoutNumber
            self         = [string]$layoutScope.self
        }
        paymentTerms = @{
            paymentTermsNumber = [int]$paymentTermsScope.paymentTermsNumber
            self               = [string]$paymentTermsScope.self
        }
        recipient    = $recipientObject
        notes        = @{
            heading   = "$EventName"
            textLine1 = "Ticket Coupon: $($CouponRow.CouponName)"
        }
        references   = $references
        lines        = $InvoiceLines
    }

    $bodyJson = $body | ConvertTo-Json -Depth 30
    $uri = "https://restapi.e-conomic.com/invoices/drafts"

    if ($WhatIf) {
        Write-Stage "WHATIF: would create e-conomic draft for '$($EconomicCustomer.name)' with reference '$($CouponRow.Reference)'" 'WARN'
        Write-Host $bodyJson
        return [pscustomobject]@{
            draftInvoiceNumber = "WHATIF"
        }
    }

    Write-Stage "Creating e-conomic draft for '$($EconomicCustomer.name)' with reference '$($CouponRow.Reference)'" 'INFO'

    $created = Invoke-RestMethod `
        -Method POST `
        -Headers $Headers `
        -Uri $uri `
        -Body $bodyJson `
        -ContentType "application/json;charset=UTF-8"

    return $created
}

#------------------------------------------------------------------------------------------------
# MAIN
#------------------------------------------------------------------------------------------------
try {
    Ensure-Directory -Path $OutputDir

    Write-Host ""
    Write-Stage "Loading coupon settings..." 'INFO'
    $CouponSettings = Get-CouponInvoicingSettings -Path $CouponSettingsPath
    Write-Stage "Coupon settings loaded: $($CouponSettings.Count)" 'OK'

    Write-Host ""
    Write-Stage "Getting Zoho access token..." 'INFO'
    $ZohoAccessToken = Get-ZohoAccessToken -ClientId $ZohoClientId -ClientSecret $ZohoClientSecret -RefreshToken $ZohoRefreshToken
    Write-Stage "Zoho access token acquired" 'OK'

    Write-Host ""
    Write-Stage "Fetching Zoho orders..." 'INFO'
    $ZohoOrders = Get-ZohoAllOrders -Token $ZohoAccessToken -PortalId $ZohoPortalId -EventId $ZohoEventId
    Write-Stage "Zoho orders loaded: $($ZohoOrders.Count)" 'OK'

    Write-Host ""
    Write-Stage "Flattening coupon claims from Zoho orders..." 'INFO'
    $CouponRows = Convert-ZohoOrdersToCouponRows -ZohoOrders $ZohoOrders
    Write-Stage "Coupon claim ticket rows found: $($CouponRows.Count)" 'OK'

    if (-not $CouponRows -or $CouponRows.Count -eq 0) {
        Write-Stage "No coupon claims found. Nothing to process." 'OK'
        return
    }

    $CouponRows | Export-Csv -Path $PendingCouponsCsv -NoTypeInformation -Encoding UTF8
    Write-Stage "Coupon claim rows exported to $PendingCouponsCsv" 'OK'

    Write-Host ""
    Write-Stage "Grouping coupon claims by reference..." 'INFO'
    $CouponGroups = @($CouponRows | Group-Object Reference)
    Write-Stage "Coupon references to evaluate: $($CouponGroups.Count)" 'OK'

    Write-Host ""
    Write-Stage "Fetching e-conomic master data..." 'INFO'
    $economic_customers    = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/customers?pagesize=1000"     -Headers $Economic_headers_REST
    $economic_Layouts      = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/layouts?pagesize=1000"       -Headers $Economic_headers_REST
    $economic_VatZones     = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/vat-zones?pagesize=1000"     -Headers $Economic_headers_REST
    $economic_paymentterms = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/payment-terms?pagesize=1000" -Headers $Economic_headers_REST
    Write-Stage "e-conomic master data loaded" 'OK'

    Write-Host ""
    Write-Stage "Fetching existing e-conomic invoice references..." 'INFO'
    $economic_invoices_booked = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/invoices/booked?pagesize=1000" -Headers $Economic_headers_REST
    $economic_invoices_sent   = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/invoices/sent?pagesize=1000"   -Headers $Economic_headers_REST
    $economic_invoices_drafts = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/invoices/drafts?pagesize=1000" -Headers $Economic_headers_REST

    $ExistingReferences = New-Object 'System.Collections.Generic.HashSet[string]'

    foreach ($invoiceSet in @($economic_invoices_booked, $economic_invoices_sent, $economic_invoices_drafts)) {
        foreach ($invoice in $invoiceSet) {
            if ($invoice.references -and $invoice.references.other) {
                $refOther = [string]$invoice.references.other
                if (-not [string]::IsNullOrWhiteSpace($refOther)) {
                    [void]$ExistingReferences.Add($refOther)
                }
            }
        }
    }

    Write-Stage "Existing invoice references found: $($ExistingReferences.Count)" 'OK'

    $CreatedDrafts = @()
    $FailedDrafts  = @()
    $SkippedRows   = @()

    Write-Host ""
    Write-Stage "Processing grouped coupon claims..." 'INFO'

    foreach ($group in $CouponGroups) {
        $groupRows = @($group.Group)
        if (-not $groupRows -or $groupRows.Count -eq 0) {
            continue
        }

        $row = $groupRows[0]
        $couponSetting = $null

        try {
            Write-Stage "Evaluating reference '$($row.Reference)' with $($groupRows.Count) ticket line(s)" 'DBG'

            $couponSetting = Get-CouponSetting -CouponName $row.CouponName -CouponSettings $CouponSettings

            if (-not $couponSetting) {
                Send-CouponSetupMissingMail `
                    -CouponName $row.CouponName `
                    -BillingType "UNKNOWN" `
                    -OrderId $row.OrderId `
                    -Reference $row.Reference `
                    -PurchaserName ("{0} {1}" -f $row.PurchaserFirstName, $row.PurchaserLastName).Trim() `
                    -PurchaserEmail $row.PurchaserEmail `
                    -CompanyName $row.PurchaserCompanyName

                $SkippedRows += [pscustomobject]@{
                    Reference = $row.Reference
                    Coupon    = $row.CouponName
                    Reason    = "Coupon not found in settings file"
                    TicketCount = $groupRows.Count
                }

                Write-Stage "Skipping '$($row.Reference)' because coupon is not in settings file" 'WARN'
                continue
            }

            $billingType = NormText $couponSetting.CouponBillingDetails

            if ($billingType -eq 'NoInvoicing') {
                $SkippedRows += [pscustomobject]@{
                    Reference = $row.Reference
                    Coupon    = $row.CouponName
                    Reason    = "NoInvoicing"
                    TicketCount = $groupRows.Count
                }

                Write-Stage "Skipping '$($row.Reference)' because coupon is marked NoInvoicing" 'DBG'
                continue
            }

            if ($InvoiceableCouponTypes -notcontains $billingType) {
                $SkippedRows += [pscustomobject]@{
                    Reference = $row.Reference
                    Coupon    = $row.CouponName
                    Reason    = "Unsupported billing type"
                    TicketCount = $groupRows.Count
                }

                Write-Stage "Skipping '$($row.Reference)' because billing type '$billingType' is unsupported" 'WARN'
                continue
            }

            if ($ExistingReferences.Contains([string]$row.Reference)) {
                $SkippedRows += [pscustomobject]@{
                    Reference = $row.Reference
                    Coupon    = $row.CouponName
                    Reason    = "Already invoiced"
                    TicketCount = $groupRows.Count
                }

                Write-Stage "Skipping '$($row.Reference)' because it already exists in e-conomic" 'DBG'
                continue
            }

            $erpCustomerNumberText = NormText $couponSetting.ErpCustomerNumberInvoicing

            if ([string]::IsNullOrWhiteSpace($erpCustomerNumberText)) {
                Send-CouponSetupMissingMail `
                    -CouponName $row.CouponName `
                    -BillingType $billingType `
                    -OrderId $row.OrderId `
                    -Reference $row.Reference `
                    -PurchaserName ("{0} {1}" -f $row.PurchaserFirstName, $row.PurchaserLastName).Trim() `
                    -PurchaserEmail $row.PurchaserEmail `
                    -CompanyName $row.PurchaserCompanyName

                $SkippedRows += [pscustomobject]@{
                    Reference = $row.Reference
                    Coupon    = $row.CouponName
                    Reason    = "ERP customer number missing in settings"
                    TicketCount = $groupRows.Count
                }

                Write-Stage "Skipping '$($row.Reference)' because ERP customer mapping is missing" 'WARN'
                continue
            }

            $erpCustomerNumber = [int]$erpCustomerNumberText

            $economicCustomer = $economic_customers |
                Where-Object { $_.customerNumber -eq $erpCustomerNumber } |
                Select-Object -First 1

            if (-not $economicCustomer) {
                throw "No e-conomic customer found for customerNumber '$erpCustomerNumber'"
            }

            $vatZoneNumber = [int]$economicCustomer.vatZone.vatZoneNumber

            $customerCurrency = [string]$economicCustomer.currency
            if ([string]::IsNullOrWhiteSpace($customerCurrency)) {
                $customerCurrency = "DKK"
            }

            $invoiceLines = New-CouponInvoiceLines `
                -CouponRows $groupRows `
                -VatZoneNumber $vatZoneNumber `
                -CustomerCurrency $customerCurrency

            if (-not $invoiceLines -or $invoiceLines.Count -eq 0) {
                throw "No invoice lines generated for reference '$($row.Reference)'"
            }

            $created = New-EconomicDraftInvoiceCoupon `
                -EconomicCustomer $economicCustomer `
                -VatZones $economic_VatZones `
                -Layouts $economic_Layouts `
                -PaymentTerms $economic_paymentterms `
                -Headers $Economic_headers_REST `
                -CouponRow $row `
                -InvoiceLines $invoiceLines `
                -LayoutNameLike $EconomicLayoutNameLike `
                -VendorEmployeeNumber $VendorEmployeeNumber `
                -WhatIf:$WhatIf

            if ($created) {
                $CreatedDrafts += [pscustomobject]@{
                    Reference          = $row.Reference
                    Coupon             = $row.CouponName
                    Customer           = $economicCustomer.name
                    CustomerNumber     = [int]$economicCustomer.customerNumber
                    Currency           = $customerCurrency
                    DraftInvoiceNumber = $created.draftInvoiceNumber
                    TicketCount        = $groupRows.Count
                }

                Write-Stage "Prepared invoice for '$($row.Reference)' with $($groupRows.Count) invoice line(s)" 'OK'
            }
        }
        catch {
            $err = $_.Exception.Message

            $FailedDrafts += [pscustomobject]@{
                Reference   = $row.Reference
                Coupon      = $row.CouponName
                CompanyName = $row.CompanyName
                Email       = $row.Email
                TicketCount = $groupRows.Count
                Error       = $err
            }

            Write-Stage "FAILED for '$($row.Reference)': $err" 'ERR'

            $configuredCustomer = $null
            if ($couponSetting) {
                $configuredCustomer = NormText $couponSetting.ErpCustomerNumberInvoicing
            }

            $details = @"
Reference:
$($row.Reference)

Coupon:
$($row.CouponName)

Billing company:
$($row.CompanyName)

Purchaser:
$($row.PurchaserFirstName) $($row.PurchaserLastName)

Purchaser email:
$($row.PurchaserEmail)

Configured ERP customer:
$configuredCustomer

Tickets in order:
$($groupRows.Count)

Error:
$err
"@

            Send-ProcessAlert `
                -Stage "Coupon Draft Invoice Creation" `
                -Message "Failed creating coupon draft invoice for $($row.Reference)" `
                -Details $details
        }
    }

    if ($SkippedRows.Count -gt 0) {
        $SkippedRows | Export-Csv -Path $SkippedCouponsCsv -NoTypeInformation -Encoding UTF8
        Write-Stage "Skipped rows exported to $SkippedCouponsCsv" 'OK'
    }

    if ($FailedDrafts.Count -gt 0) {
        $FailedDrafts | Export-Csv -Path $FailedCouponsCsv -NoTypeInformation -Encoding UTF8
        Write-Stage "Failed rows exported to $FailedCouponsCsv" 'OK'
    }

    Write-Host ""
    Write-Stage "Coupon draft invoice creation finished. Created=$($CreatedDrafts.Count) Skipped=$($SkippedRows.Count) Failed=$($FailedDrafts.Count)" 'INFO'

    if ($CreatedDrafts.Count -gt 0) {
        Send-InvoiceReadyMail -CreatedDrafts $CreatedDrafts -EventName $EventName
    }

    if ($FailedDrafts.Count -gt 0) {
        $FailedDrafts | Format-Table -AutoSize | Out-String | Write-Host
    }
}
catch {
    $err = $_.Exception.Message

    Write-Stage "TOP-LEVEL FAILURE: $err" 'ERR'
    Send-ProcessAlert -Stage "Top-Level Script Failure" -Message "Unhandled exception in Zoho -> e-conomic coupon script" -Details $err
    throw
}
