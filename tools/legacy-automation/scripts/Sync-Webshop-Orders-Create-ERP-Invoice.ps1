#------------------------------------------------------------------------------------------------
Write-Output "***********************************************************************************************"
Write-Output "Experts Live Denmark Integration Webshop -> Economic"
Write-Output ""
Write-Output "Purpose: Pull completed WooCommerce orders (new Company Manager design) and create e-conomic draft invoices"
Write-Output ""
Write-Output "Support: Morten Knudsen - mok@expertslive.dk"
Write-Output "***********************************************************************************************"
#------------------------------------------------------------------------------------------------

$ScriptDirectory = $PSScriptRoot
$global:PathScripts = Split-Path -parent $ScriptDirectory

Import-Module "$ScriptDirectory\Secrets.psm1" -Global -Force -WarningAction SilentlyContinue
Import_Secrets

$EventName = "ELDK27"

# Email (SMTP)
$SmtpServer  = "smtp-relay.brevo.com"
$SmtpPort    = 587
$FromDisplay = "Experts Live Denmark"
$FromAddress = "info@expertslive.dk"
$AlertTo     = @("mok@expertslive.dk","mb@expertslive.dk")

# Webshop
$BaseUrl             = "https://expertslive.dk"
$CompanyManagerBase  = "$BaseUrl/wp-json/company-manager/v1"

# Only invoice orders created after this date. Orders before this are from the old user-meta design.
$GetOrdersAfter = [datetime]::ParseExact("16-04-2026", "dd-MM-yyyy", [System.Globalization.CultureInfo]::InvariantCulture)
$GetOrdersBefore = $null

# Dry-run switch. $true = simulate only (no e-conomic draft is created). $false = create drafts for real.
$WhatIf = $false

# --- TLS 1.2 for PS 5.1 ---
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ExcelOrdersPath    = 'C:\Scripts-ELDK-Automation\OUTPUT\Webshop-CompletedOrders.xlsx'
$ExcelItemsPath     = 'C:\Scripts-ELDK-Automation\OUTPUT\BILLING_OUTPUT\Webshop-OrderLineItems.xlsx'
$CompaniesExcelPath = 'C:\Scripts-ELDK-Automation\OUTPUT\Webshop-Companies.xlsx'

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

function Send-AlertMail {
    param(
        [Parameter(Mandatory = $true)][string]$Subject,
        [Parameter(Mandatory = $true)][string]$Body
    )

    try {
        $securePass = ConvertTo-SecureString $SmtpPass -AsPlainText -Force
        $cred = New-Object System.Management.Automation.PSCredential($SmtpUser, $securePass)
        $from = "$FromDisplay <$FromAddress>"

        Write-Host "Sending alert mail to $AlertTo" -ForegroundColor Yellow

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
        Write-Host "FAILED to send alert mail: $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Send-ProcessAlert {
    param(
        [Parameter(Mandatory = $true)][string]$Stage,
        [Parameter(Mandatory = $true)][string]$Message,
        [string]$Details = ""
    )

    $subject = "ALERT: Experts Live Denmark Webshop -> Economic | $Stage"
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
        Write-Host "No created drafts -> no success mail sent." -ForegroundColor DarkGray
        return
    }

    try {
        $securePass = ConvertTo-SecureString $SmtpPass -AsPlainText -Force
        $cred = New-Object System.Management.Automation.PSCredential($SmtpUser, $securePass)
        $from = "$FromDisplay <$FromAddress>"

        $subject = "Invoice drafts ready in e-conomic - $EventName"

        $draftLines = ($CreatedDrafts | ForEach-Object {
            "OrderNumber: {0} | Customer: {1} | CustomerNumber: {2} | Currency: {3} | DraftInvoiceNumber: {4}" -f `
                $_.OrderNumber,
                $_.Customer,
                $_.CustomerNumber,
                $_.Currency,
                $_.DraftInvoiceNumber
        }) -join "`r`n"

        $body = @"
The webshop invoice draft creation has completed successfully.

Event:
$EventName

Created draft invoices:
$draftLines

Total created:
$($CreatedDrafts.Count)

Time:
$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@

        Write-Host "Sending invoice-ready mail to $AlertTo" -ForegroundColor Green

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
        Write-Host "FAILED to send invoice-ready mail: $($_.Exception.Message)" -ForegroundColor Red
    }
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
    if ($resp.collection) { $collection += $resp.collection }

    while ($resp.pagination -and ($resp.pagination.PSObject.Properties.Name -contains 'nextPage') -and $resp.pagination.nextPage) {
        $resp = Invoke-RestMethod -Method Get -Headers $Headers -Uri $resp.pagination.nextPage
        if ($resp.collection) { $collection += $resp.collection }
    }

    return $collection
}

#------------------------------------------------------------------------------------------------
# WooCommerce / WordPress helpers
#------------------------------------------------------------------------------------------------
function Get-WpAuthHeader {
    $pair = "$($WpUserApi):$($WpAppPassword)"
    @{ Authorization = 'Basic ' + [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes($pair)) }
}

function Invoke-WpApiGet {
    param([Parameter(Mandatory)][string]$Uri)
    Invoke-RestMethod -Method GET -Uri $Uri -Headers (Get-WpAuthHeader) -ErrorAction Stop
}

function Invoke-WooRequest {
    param(
        [ValidateSet('GET','POST','PUT','DELETE')][string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Path,
        [hashtable]$Query = @{}
    )

    $root = "$BaseUrl/wp-json/wc/v3"

    $allQuery = @{}
    foreach ($k in $Query.Keys) { $allQuery[$k] = [string]$Query[$k] }
    $allQuery["consumer_key"]    = $ConsumerKey
    $allQuery["consumer_secret"] = $ConsumerSecret

    $pairs = foreach ($k in $allQuery.Keys) {
        $encodedKey = [System.Uri]::EscapeDataString([string]$k)
        $encodedVal = [System.Uri]::EscapeDataString([string]$allQuery[$k])
        "{0}={1}" -f $encodedKey, $encodedVal
    }
    $queryString = $pairs -join "&"

    $uri = "$root/$Path`?$queryString"

    try {
        $body = Invoke-RestMethod -Method $Method -Uri $uri -ErrorAction Stop
        [pscustomobject]@{ Body = $body; Uri = $uri }
    }
    catch {
        $status = $null
        $respText = $null

        if ($_.Exception.Response -and ($_.Exception.Response -is [System.Net.HttpWebResponse])) {
            $status = [int]$_.Exception.Response.StatusCode
            try {
                $sr = New-Object IO.StreamReader ($_.Exception.Response.GetResponseStream())
                $respText = $sr.ReadToEnd()
                $sr.Close()
            } catch {}
        }

        $msg = $_.Exception.Message
        if ($_.ErrorDetails -and $_.ErrorDetails.Message) { $msg = "$msg | $($_.ErrorDetails.Message)" }
        $statusMsg = if ($status) { " (HTTP $status)" } else { "" }

        throw "REST call failed$($statusMsg): $msg`nURI: $uri`nResponse: $respText"
    }
}

function Get-AllPages {
    param(
        [Parameter(Mandatory)][string]$Path,
        [hashtable]$Query = @{}
    )

    $page = 1
    $per = 100
    $all = @()

    do {
        $q = @{'page' = $page; 'per_page' = $per} + $Query
        Write-Host "Fetching page $page ($per per page) for '$Path' ..."
        $res = Invoke-WooRequest -Path $Path -Query $q
        if ($res.Body) { $all += $res.Body }

        $totalPages = 1
        $page++
    } while ($page -le $totalPages)

    return $all
}

function Get-OrdersCompleted {
    param(
        [Nullable[datetime]]$After,
        [Nullable[datetime]]$Before
    )

    $q = @{ status = 'completed' }
    if ($After)  { $q.after  = ($After.ToUniversalTime().ToString("s") + "Z") }
    if ($Before) { $q.before = ($Before.ToUniversalTime().ToString("s") + "Z") }

    Write-Host "Retrieving completed orders..."
    if ($After)  { Write-Host "  After : $($q.after)" }
    if ($Before) { Write-Host "  Before: $($q.before)" }

    Get-AllPages -Path 'orders' -Query $q
}

function Get-OrderMetaValue {
    param($Order, [string]$Key)
    $m = $Order.meta_data | Where-Object { $_.key -eq $Key } | Select-Object -First 1
    if ($m) { return $m.value } else { return $null }
}

function Get-OrderCompanyId {
    param($Order)
    $v = Get-OrderMetaValue -Order $Order -Key '_cm_company_id'
    if ([string]::IsNullOrWhiteSpace($v)) { return $null }
    [int]$v
}

function Get-CompanyById {
    param(
        [Parameter(Mandatory)][int]$Id,
        [hashtable]$Cache
    )

    if ($Cache -and $Cache.ContainsKey($Id)) { return $Cache[$Id] }

    $uri = "$CompanyManagerBase/companies/$Id"
    Write-Host "  Fetching company $Id ..." -ForegroundColor DarkGray
    $c = Invoke-WpApiGet -Uri $uri

    if ($Cache) { $Cache[$Id] = $c }
    return $c
}

#------------------------------------------------------------------------------------------------
# Invoice creation helpers
#------------------------------------------------------------------------------------------------
function Get-EconomicLayout {
    param(
        [Parameter(Mandatory = $true)]$Layouts,
        [string]$NameLike = "Dansk*"
    )

    $layout = $Layouts | Where-Object { $_.name -like $NameLike } | Select-Object -First 1
    if (-not $layout) { throw "No e-conomic layout found matching '$NameLike'" }

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

function Convert-AmountFromEuroOrFail {
    param(
        [Parameter(Mandatory = $true)][double]$AmountFrom,
        [Parameter(Mandatory = $true)][string]$To_Currency
    )

    if ([string]::IsNullOrWhiteSpace($To_Currency)) {
        throw "To_Currency is empty"
    }

    if ($To_Currency.ToUpper() -eq "EUR") {
        Write-Host "No conversion needed. Currency is already EUR." -ForegroundColor DarkGray
        return [double]([math]::Ceiling($AmountFrom))
    }

    Write-Host "Retrieving exchange rate EUR -> $To_Currency" -ForegroundColor Cyan
    $uri = "https://onesimpleapi.com/api/exchange_rate?token=$($global:Currency_APIKey)&output=text&from_currency=EUR&to_currency=$To_Currency&from_value=$AmountFrom"

    try {
        $AmountToRaw = [double](Invoke-RestMethod -Uri $uri -Method Get -ErrorAction Stop)

        if ($AmountToRaw -le 0) {
            throw "Invalid exchange result returned from API: '$AmountToRaw'"
        }

        $AmountTo = [double]([math]::Ceiling($AmountToRaw))
        Write-Host "Converted $AmountFrom EUR -> $AmountTo $To_Currency" -ForegroundColor Green
        return $AmountTo
    }
    catch {
        $ErrorMessage = $_.Exception.Message
        $RawResponse = $null

        if ($_.Exception.Response) {
            try {
                $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                $RawResponse = $reader.ReadToEnd()
            } catch {}
        }

        Write-Host "ERROR retrieving exchange rate / converted amount" -ForegroundColor Red
        Write-Host "URI: $uri" -ForegroundColor Red
        Write-Host "Error: $ErrorMessage" -ForegroundColor Red
        if ($RawResponse) { Write-Host "Raw response: $RawResponse" -ForegroundColor Yellow }

        $details = @"
From currency : EUR
To currency   : $To_Currency
Amount        : $AmountFrom
Request URI   : $uri

Error:
$ErrorMessage

Raw response:
$RawResponse
"@

        Send-ProcessAlert `
            -Stage "Currency Conversion" `
            -Message "Currency conversion API call failed" `
            -Details $details

        throw "Currency conversion API call failed: $ErrorMessage"
    }
}

function New-WebshopInvoiceLines {
    param(
        [Parameter(Mandatory = $true)]$OrderLines,
        [Parameter(Mandatory = $true)][int]$VatZoneNumber,
        [Parameter(Mandatory = $true)][string]$CustomerCurrency,
        [string]$HeaderDescription = $null
    )

    $invoiceLines = @()
    $lineNo = 0

    if ($HeaderDescription) {
        $lineNo++
        $invoiceLines += [pscustomobject]@{
            lineNumber  = [int]$lineNo
            sortKey     = [int]$lineNo
            description = [string]$HeaderDescription
        }
    }

    $economicProductNumber = switch ($VatZoneNumber) {
        1 { "1000" }  # Indland - Tax
        2 { "2000" }  # EU - No Tax
        3 { "2000" }  # Outside EU - No Tax
        4 { "2000" }  # Domestic no VAT - No Tax
        default { throw "Unsupported VAT zone '$VatZoneNumber'. Expected 1-4" }
    }

    $targetCurrency = [string]$CustomerCurrency
    if ([string]::IsNullOrWhiteSpace($targetCurrency)) { $targetCurrency = "EUR" }
    $targetCurrency = $targetCurrency.ToUpper()

    foreach ($line in $OrderLines) {
        $lineNo++

        $originalUnitPriceEur = [math]::Round([double]$line.UnitPrice, 2)
        $convertedUnitPrice = $originalUnitPriceEur
        $conversionText = $null

        if ($targetCurrency -ne "EUR") {
            $convertedUnitPrice = Convert-AmountFromEuroOrFail `
                -AmountFrom $originalUnitPriceEur `
                -To_Currency $targetCurrency

            $conversionText = @(
                "Currency conversion applied: EUR -> $targetCurrency"
                "Original EUR $originalUnitPriceEur -> $targetCurrency $convertedUnitPrice"
            ) -join "`r`n"
        }

        $descriptionParts = @()
        if ($line.ProductName)    { $descriptionParts += [string]$line.ProductName }
        if ($line.OrderNumber)    { $descriptionParts += "Webshop Order: $($line.OrderNumber)" }
        if ($line.OrderDateLocal) { $descriptionParts += "Order Date: $($line.OrderDateLocal)" }
        if ($conversionText)      { $descriptionParts += [string]$conversionText }

        $description = $descriptionParts -join "`r`n`r`n"

        $invoiceLines += [pscustomobject]@{
            lineNumber   = [int]$lineNo
            sortKey      = [int]$lineNo
            description  = [string]$description
            quantity     = [double]$line.Quantity
            unitNetPrice = [double]$convertedUnitPrice
            product      = @{
                productNumber = [string]$economicProductNumber
            }
            unit         = @{
                unitNumber = 1
                self       = "https://restapi.e-conomic.com/units/1"
            }
        }
    }

    return ,$invoiceLines
}

function New-EconomicDraftInvoiceWebshop {
    param(
        [Parameter(Mandatory = $true)]$EconomicCustomer,
        [Parameter(Mandatory = $true)]$VatZones,
        [Parameter(Mandatory = $true)]$Layouts,
        [Parameter(Mandatory = $true)]$PaymentTerms,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [Parameter(Mandatory = $true)][string]$OrderNumber,
        [Parameter(Mandatory = $true)]$InvoiceDate,
        [Parameter(Mandatory = $true)]$InvoiceLines,
        [string]$Heading = "Webshop Order",
        [string]$TextLine1 = "",
        [string]$HeaderDescription = "",
        [string]$LayoutNameLike = "Dansk*",
        [int]$VendorEmployeeNumber = 1,
        [string]$OtherReference = $null,
        [switch]$WhatIf
    )

    $vatZoneScope      = Get-EconomicVatZoneScope -Customer $EconomicCustomer -VatZones $VatZones
    $layoutScope       = Get-EconomicLayout -Layouts $Layouts -NameLike $LayoutNameLike
    $paymentTermsScope = Get-EconomicPaymentTermsScope -Customer $EconomicCustomer -PaymentTerms $PaymentTerms
    $contactNumber     = Get-EconomicCustomerContactNumber -Customer $EconomicCustomer
    $recipientObject   = New-EconomicRecipientObject -Customer $EconomicCustomer -VatZoneScope $vatZoneScope

    $invoiceCurrency = [string]$EconomicCustomer.currency
    if ([string]::IsNullOrWhiteSpace($invoiceCurrency)) { $invoiceCurrency = "EUR" }

    $refOther = if ($OtherReference) { $OtherReference } else { "WebshopOrderId-$OrderNumber" }

    $references = [ordered]@{
        other = $refOther
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
        date         = [string]$InvoiceDate
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
            heading   = [string]$Heading
            textLine1 = [string]$TextLine1
        }
        references   = $references
        lines        = $InvoiceLines
    }

    $bodyJson = $body | ConvertTo-Json -Depth 30
    $uri = "https://restapi.e-conomic.com/invoices/drafts"

    if ($WhatIf) {
        Write-Stage "WHATIF: would create e-conomic draft for customer '$($EconomicCustomer.name)' / order '$refOther' / currency '$invoiceCurrency'" 'WARN'
        Write-Host $bodyJson
        return $null
    }

    Write-Stage "Creating e-conomic draft for customer '$($EconomicCustomer.name)' / order '$refOther' / currency '$invoiceCurrency'" 'INFO'

    $created = Invoke-RestMethod `
        -Method POST `
        -Headers $Headers `
        -Uri $uri `
        -Body $bodyJson `
        -ContentType "application/json;charset=UTF-8"

    return $created
}

function Invoke-WebshopPendingInvoiceCreation {
    param(
        [Parameter(Mandatory = $true)]$PendingOrdersWithEconomicCustomer,
        [Parameter(Mandatory = $true)]$EconomicCustomers,
        [Parameter(Mandatory = $true)]$EconomicVatZones,
        [Parameter(Mandatory = $true)]$EconomicLayouts,
        [Parameter(Mandatory = $true)]$EconomicPaymentTerms,
        [Parameter(Mandatory = $true)][hashtable]$Headers,
        [datetime]$InvoiceDate = (Get-Date),
        [string]$LayoutNameLike = "Dansk*",
        [string]$Heading = "Webshop Order",
        [string]$TextLine1 = "",
        [switch]$WhatIf
    )

    $createdDrafts = @()
    $failedDrafts  = @()

    Write-Stage "Starting webshop invoice creation. Orders to process: $($PendingOrdersWithEconomicCustomer.Count)" 'INFO'

    foreach ($pendingOrder in $PendingOrdersWithEconomicCustomer) {
        $orderNumber    = [string]$pendingOrder.OrderNumber
        $companyName    = [string]$pendingOrder.CompanyName
        $erpCustomerNo  = [int]$pendingOrder.EconomicCustomerNumber
        $lines          = $pendingOrder.Lines

        Write-Stage "Processing pending webshop order '$orderNumber' for '$companyName' (ERP $erpCustomerNo)" 'INFO'

        $economicCustomer = $EconomicCustomers |
            Where-Object { [int]$_.customerNumber -eq $erpCustomerNo } |
            Select-Object -First 1

        if (-not $economicCustomer) {
            $err = "e-conomic customer with customerNumber $erpCustomerNo not found in master data"
            Write-Stage "Skipping order '$orderNumber' - $err" 'ERR'

            $details = @"
OrderNumber:
$orderNumber

Company:
$companyName

EconomicCustomerNumber:
$erpCustomerNo

Error:
$err
"@

            Send-ProcessAlert `
                -Stage "Customer Lookup" `
                -Message "Missing e-conomic customer for webshop order $orderNumber" `
                -Details $details

            $failedDrafts += [pscustomobject]@{
                OrderNumber = $orderNumber
                Customer    = $companyName
                Error       = $err
            }
            continue
        }

        $headerDescription = "Sponsor Webshop Order: $orderNumber`r`n`r`n"

        try {
            $vatZoneNumber    = [int]$economicCustomer.vatZone.vatZoneNumber
            $customerCurrency = [string]$economicCustomer.currency
            if ([string]::IsNullOrWhiteSpace($customerCurrency)) { $customerCurrency = "EUR" }

            Write-Stage "Order '$orderNumber' -> e-conomic '$($economicCustomer.name)' (customerNumber $($economicCustomer.customerNumber)) VAT zone $vatZoneNumber currency $customerCurrency" 'DBG'

            $invoiceLines = New-WebshopInvoiceLines `
                -OrderLines $lines `
                -VatZoneNumber $vatZoneNumber `
                -CustomerCurrency $customerCurrency `
                -HeaderDescription $headerDescription

            if (-not $invoiceLines -or $invoiceLines.Count -eq 0) {
                throw "No invoice lines generated"
            }

            $created = New-EconomicDraftInvoiceWebshop `
                -EconomicCustomer $economicCustomer `
                -VatZones $EconomicVatZones `
                -Layouts $EconomicLayouts `
                -PaymentTerms $EconomicPaymentTerms `
                -Headers $Headers `
                -OrderNumber $orderNumber `
                -InvoiceDate $InvoiceDate.ToString("yyyy-MM-dd") `
                -InvoiceLines $invoiceLines `
                -Heading $Heading `
                -TextLine1 $TextLine1 `
                -HeaderDescription $headerDescription `
                -LayoutNameLike $LayoutNameLike `
                -WhatIf:$WhatIf

            if ($created) {
                Write-Stage "Draft created for webshop order '$orderNumber' -> draftInvoiceNumber=$($created.draftInvoiceNumber)" 'OK'
                $createdDrafts += [pscustomobject]@{
                    OrderNumber        = $orderNumber
                    Customer           = [string]$economicCustomer.name
                    CustomerNumber     = [int]$economicCustomer.customerNumber
                    Currency           = $customerCurrency
                    DraftInvoiceNumber = $created.draftInvoiceNumber
                    Response           = $created
                }
            }
        }
        catch {
            $err = $_.Exception.Message
            Write-Stage "FAILED creating draft for webshop order '$orderNumber': $err" 'ERR'

            $details = @"
OrderNumber:
$orderNumber

Company:
$companyName

EconomicCustomerNumber:
$erpCustomerNo

EconomicCustomerName:
$($economicCustomer.name)

Error:
$err
"@

            Send-ProcessAlert `
                -Stage "Invoice Draft Creation" `
                -Message "Failed creating webshop invoice draft for order $orderNumber" `
                -Details $details

            $failedDrafts += [pscustomobject]@{
                OrderNumber = $orderNumber
                Customer    = $companyName
                Error       = $err
            }
        }
    }

    Write-Stage "Webshop invoice creation finished. Created=$($createdDrafts.Count) Failed=$($failedDrafts.Count)" 'INFO'

    if ($failedDrafts.Count -gt 0) {
        $failedText = ($failedDrafts | Format-Table -AutoSize | Out-String)
        Send-ProcessAlert `
            -Stage "Invoice Creation Summary" `
            -Message "One or more webshop invoices failed during processing" `
            -Details $failedText
    }

    return [pscustomobject]@{
        Created = @($createdDrafts)
        Failed  = @($failedDrafts)
    }
}

#------------------------------------------------------------------------------------------------
# MAIN
#------------------------------------------------------------------------------------------------
try {
    ############################################
    # 1. Fetch completed WooCommerce orders
    ############################################
    Write-Host ""
    Write-Host "Fetching completed orders from WooCommerce"
    $orders = Get-OrdersCompleted -After $GetOrdersAfter -Before $GetOrdersBefore
    Write-Host ("Fetched {0} orders" -f $orders.Count)

    ############################################
    # 2. Guard: defensively skip anything not 'completed' (cancelled/refunded/failed/pending).
    # The API filter already restricts to completed, but this makes intent explicit.
    ############################################
    $nonCompleted = @($orders | Where-Object { $_.status -ne 'completed' })
    if ($nonCompleted.Count -gt 0) {
        Write-Host ("Skipping {0} non-completed orders (statuses: {1})" -f `
            $nonCompleted.Count, `
            (($nonCompleted | Select-Object -ExpandProperty status -Unique) -join ',')) -ForegroundColor Yellow
    }
    $orders = @($orders | Where-Object { $_.status -eq 'completed' })

    ############################################
    # 3. Split new-design vs legacy
    # Legacy orders (no _cm_company_id) use the old user-meta model and are not invoiced by this script.
    ############################################
    $newDesignOrders = @()
    $legacyOrders    = @()

    foreach ($o in $orders) {
        if (Get-OrderCompanyId -Order $o) {
            $newDesignOrders += $o
        } else {
            $legacyOrders += $o
        }
    }

    Write-Host ""
    Write-Host ("New-design orders (with _cm_company_id) : {0}" -f $newDesignOrders.Count) -ForegroundColor Green
    Write-Host ("Legacy orders (skipped)                 : {0}" -f $legacyOrders.Count) -ForegroundColor DarkYellow

    if ($newDesignOrders.Count -eq 0) {
        Write-Stage "No new-design orders to process. Exiting." 'WARN'
        return
    }

    ############################################
    # 3. Resolve company records (cached)
    ############################################
    Write-Host ""
    Write-Host "Resolving Company Manager records for orders" -ForegroundColor Cyan

    $companyCache = @{}
    $orderCompany = @{}   # orderId -> company object

    foreach ($o in $newDesignOrders) {
        $cid = Get-OrderCompanyId -Order $o
        try {
            $company = Get-CompanyById -Id $cid -Cache $companyCache
            $orderCompany[[int]$o.id] = $company
            Write-Host ("Order {0} -> company #{1} {2} (ERP {3})" -f $o.number, $company.id, $company.name, $company.erp_customer_number) -ForegroundColor DarkGray
        }
        catch {
            Write-Host ("Order {0} -> FAILED fetching company {1}: {2}" -f $o.number, $cid, $_.Exception.Message) -ForegroundColor Red
        }
    }

    Write-Host ("Unique companies resolved: {0}" -f $companyCache.Count) -ForegroundColor Green

    ############################################
    # 4. Flatten orders (for Excel) and build line items
    ############################################
    Write-Host ""
    Write-Host "Flattening orders and line items"

    $rows     = @()
    $itemRows = @()

    foreach ($o in $newDesignOrders) {
        $company = $orderCompany[[int]$o.id]
        if (-not $company) { continue }

        $b = $o.billing
        $s = $o.shipping

        $rows += [pscustomobject][ordered]@{
            OrderId             = $o.id
            OrderNumber         = $o.number
            OrderDateLocal      = (Get-Date $o.date_created_gmt).ToLocalTime()
            Status              = $o.status
            Currency            = $o.currency
            Total               = [decimal]$o.total

            CompanyId           = $company.id
            CompanyName         = $company.name
            CompanyNamePublic   = $company.company_name_public
            CompanySlug         = $company.slug
            ErpCustomerNumber   = $company.erp_customer_number
            CompanyCurrency     = $company.currency
            CompanyVatZone      = $company.vat_zone_number
            CompanyCVR          = $company.corporate_identification_number
            CompanyEmail        = $company.email
            CompanyPhone        = $company.phone
            CompanyWebsite      = $company.web_address
            CompanyBillingEmail = $company.billing_email
            CompanyBillingRef   = $company.billing_reference

            CompanyBillingAddr1    = $company.billing_address_1
            CompanyBillingAddr2    = $company.billing_address_2
            CompanyBillingCity     = $company.billing_city
            CompanyBillingState    = $company.billing_state
            CompanyBillingPostcode = $company.billing_postcode
            CompanyBillingCountry  = $company.billing_country

            OrderBillingFirstName = $b.first_name
            OrderBillingLastName  = $b.last_name
            OrderBillingEmail     = $b.email
            OrderBillingPhone     = $b.phone
            OrderBillingAddress1  = $b.address_1
            OrderBillingCity      = $b.city
            OrderBillingCountry   = $b.country

            CustomerUserId     = $o.customer_id
        }

        if ($o.line_items) {
            foreach ($li in $o.line_items) {
                $itemRows += [pscustomobject][ordered]@{
                    OrderNumber         = $o.number
                    OrderDateLocal      = (Get-Date $o.date_created_gmt).ToLocalTime()
                    CompanyId           = $company.id
                    CompanyName         = $company.name
                    CompanyNamePublic   = $company.company_name_public
                    ErpCustomerNumber   = $company.erp_customer_number
                    CompanyCurrency     = $company.currency
                    CompanyVatZone      = $company.vat_zone_number
                    ProductId           = $li.product_id
                    ProductName         = $li.name
                    Quantity            = $li.quantity
                    UnitPrice           = [decimal]$li.price
                    Total               = [decimal]$li.total
                }
            }
        }
    }

    Write-Host ("Flattened {0} orders / {1} line items" -f $rows.Count, $itemRows.Count)

    ############################################
    # 5. Excel exports
    ############################################
    Write-Host ""
    Write-Host "Exporting Excel files"

    if (Test-Path $ExcelOrdersPath) { Remove-Item $ExcelOrdersPath -Force }
    $rows | Export-Excel -Path $ExcelOrdersPath -WorksheetName 'Completed' -AutoSize -AutoFilter -FreezeTopRow
    Write-Host "Orders -> $ExcelOrdersPath"

    $itemsDir = Split-Path -Parent $ExcelItemsPath
    if (-not (Test-Path $itemsDir)) { New-Item -ItemType Directory -Path $itemsDir -Force | Out-Null }
    if (Test-Path $ExcelItemsPath) { Remove-Item $ExcelItemsPath -Force }
    $itemRows | Export-Excel -Path $ExcelItemsPath -WorksheetName 'LineItems' -AutoSize -AutoFilter -FreezeTopRow
    Write-Host "Line items -> $ExcelItemsPath"

    if ($companyCache.Count -gt 0) {
        if (Test-Path $CompaniesExcelPath) { Remove-Item $CompaniesExcelPath -Force }
        $companyCache.Values |
            Sort-Object name |
            Select-Object id, name, slug, company_name_public, erp_customer_number, corporate_identification_number, currency, vat_zone_number, email, phone, billing_email, billing_reference, billing_address_1, billing_city, billing_postcode, billing_country, status, default_signer_id, event_coordination_default_contact_id |
            Export-Excel -Path $CompaniesExcelPath -WorksheetName 'Companies' -AutoSize -AutoFilter -FreezeTopRow
        Write-Host "Companies -> $CompaniesExcelPath"
    }

    ############################################
    # 6. Get ERP master data + invoice state
    ############################################
    $economic_customers      = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/customers?pagesize=1000"      -Headers $Economic_headers_REST
    $economic_Layouts        = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/layouts?pagesize=1000"        -Headers $Economic_headers_REST
    $economic_VatZones       = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/vat-zones?pagesize=1000"      -Headers $Economic_headers_REST
    $economic_paymentterms   = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/payment-terms?pagesize=1000"  -Headers $Economic_headers_REST

    $economic_invoices_booked = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/invoices/booked?pagesize=1000" -Headers $Economic_headers_REST
    $economic_invoices_drafts = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/invoices/drafts?pagesize=1000" -Headers $Economic_headers_REST

    ############################################
    # 7. Detect already-invoiced orders via references.other = "WebshopOrderId-<num>"
    ############################################

    Write-Host ""
    Write-Host "Building lookup of invoiced webshop order references from e-conomic (booked + drafts)" -ForegroundColor Cyan

    $InvoicedOrderRefs = New-Object 'System.Collections.Generic.HashSet[string]'

    $bookedRefCount = 0
    foreach ($inv in $economic_invoices_booked) {
        if ($inv.references -and $inv.references.other) {
            $r = [string]$inv.references.other
            if (-not [string]::IsNullOrWhiteSpace($r)) {
                if ($InvoicedOrderRefs.Add($r)) { $bookedRefCount++ }
            }
        }
    }

    $draftRefCount = 0
    foreach ($inv in $economic_invoices_drafts) {
        if ($inv.references -and $inv.references.other) {
            $r = [string]$inv.references.other
            if (-not [string]::IsNullOrWhiteSpace($r)) {
                if ($InvoicedOrderRefs.Add($r)) { $draftRefCount++ }
            }
        }
    }

    Write-Host ("Booked invoice refs : {0}" -f $bookedRefCount) -ForegroundColor Green
    Write-Host ("Draft invoice refs  : {0}" -f $draftRefCount)  -ForegroundColor Green
    Write-Host ("Total unique refs   : {0}" -f $InvoicedOrderRefs.Count) -ForegroundColor Green


    ############################################
    # 8. Build pending list (grouped by OrderNumber)
    ############################################
    $GroupedOrders = $itemRows | Group-Object OrderNumber

    $PendingOrdersWithEconomicCustomer    = New-Object 'System.Collections.Generic.List[object]'
    $PendingOrdersWithoutEconomicCustomer = New-Object 'System.Collections.Generic.List[object]'

    foreach ($group in $GroupedOrders) {
        $orderNumber = [string]$group.Name
        $lines       = $group.Group
        if (-not $lines -or $lines.Count -eq 0) { continue }

        $orderRef = "WebshopOrderId-$orderNumber"
        if ($InvoicedOrderRefs.Contains($orderRef)) {
            Write-Host ("ALREADY IN ECONOMIC (booked or draft): {0}" -f $orderNumber) -ForegroundColor Green
            continue
        }

        $first = $lines[0]
        $erpNum = [string]$first.ErpCustomerNumber

        if ([string]::IsNullOrWhiteSpace($erpNum)) {
            Write-Host ("Order {0}: company '{1}' (id {2}) has no erp_customer_number set in Company Manager" -f $orderNumber, $first.CompanyName, $first.CompanyId) -ForegroundColor Yellow

            [void]$PendingOrdersWithoutEconomicCustomer.Add([pscustomobject]@{
                OrderNumber            = $orderNumber
                CompanyId              = $first.CompanyId
                CompanyName            = $first.CompanyName
                EconomicCustomerNumber = $null
                Lines                  = $lines
                Reason                 = "Company has no erp_customer_number"
            })
            continue
        }

        $matched = $economic_customers | Where-Object { [string]$_.customerNumber -eq $erpNum } | Select-Object -First 1

        if ($matched) {
            Write-Host ("PENDING {0} -> ERP {1} / {2} ({3} / zone {4})" -f `
                $orderNumber, $matched.customerNumber, $matched.name, $matched.currency, $matched.vatZone.vatZoneNumber) -ForegroundColor Yellow

            [void]$PendingOrdersWithEconomicCustomer.Add([pscustomobject]@{
                OrderNumber            = $orderNumber
                CompanyId              = $first.CompanyId
                CompanyName            = $first.CompanyName
                EconomicCustomerNumber = [int]$matched.customerNumber
                Lines                  = $lines
            })
        }
        else {
            Write-Host ("Order {0}: erp_customer_number '{1}' from company '{2}' not found in e-conomic master data" -f $orderNumber, $erpNum, $first.CompanyName) -ForegroundColor Red

            [void]$PendingOrdersWithoutEconomicCustomer.Add([pscustomobject]@{
                OrderNumber            = $orderNumber
                CompanyId              = $first.CompanyId
                CompanyName            = $first.CompanyName
                EconomicCustomerNumber = $erpNum
                Lines                  = $lines
                Reason                 = "erp_customer_number not found in e-conomic"
            })
        }
    }

    Write-Host ""
    Write-Host ("Pending w/ ERP match    : {0}" -f $PendingOrdersWithEconomicCustomer.Count) -ForegroundColor Green
    Write-Host ("Pending w/o ERP match   : {0}" -f $PendingOrdersWithoutEconomicCustomer.Count) -ForegroundColor Yellow

    if ($PendingOrdersWithoutEconomicCustomer.Count -gt 0) {
        $missingText = ($PendingOrdersWithoutEconomicCustomer |
            Select-Object OrderNumber, CompanyId, CompanyName, EconomicCustomerNumber, Reason |
            Format-Table -AutoSize | Out-String)

        Send-ProcessAlert `
            -Stage "Pending Orders Without ERP Match" `
            -Message "One or more webshop orders could not be matched to an e-conomic customer" `
            -Details $missingText
    }

    ############################################
    # 9. Create ERP invoices
    ############################################
    $InvoiceDate = Get-Date

    $WebshopInvoiceResult = Invoke-WebshopPendingInvoiceCreation `
        -PendingOrdersWithEconomicCustomer $PendingOrdersWithEconomicCustomer `
        -EconomicCustomers $economic_customers `
        -EconomicVatZones $economic_VatZones `
        -EconomicLayouts $economic_Layouts `
        -EconomicPaymentTerms $economic_paymentterms `
        -Headers $Economic_headers_REST `
        -InvoiceDate $InvoiceDate `
        -Heading "$EventName - Experts Live Denmark" `
        -TextLine1 "Sponsorship" `
        -LayoutNameLike "English*" `
        -WhatIf:$WhatIf

    Write-Host ""
    Write-Host "Invoice creation result summary" -ForegroundColor Cyan
    Write-Host ("Created: {0}" -f $WebshopInvoiceResult.Created.Count) -ForegroundColor Green
    Write-Host ("Failed : {0}" -f $WebshopInvoiceResult.Failed.Count) -ForegroundColor Yellow

    if ($WebshopInvoiceResult.Created.Count -gt 0) {
        Send-InvoiceReadyMail -CreatedDrafts $WebshopInvoiceResult.Created -EventName $EventName
    }

    if ($WebshopInvoiceResult.Failed.Count -gt 0) {
        $WebshopInvoiceResult.Failed | Format-Table -AutoSize | Out-String | Write-Host
    }
}
catch {
    $err = $_.Exception.Message
    Write-Host "TOP-LEVEL FAILURE: $err" -ForegroundColor Red

    Send-ProcessAlert `
        -Stage "Top-Level Script Failure" `
        -Message "Unhandled exception in Webshop -> Economic script" `
        -Details $err

    throw
}
