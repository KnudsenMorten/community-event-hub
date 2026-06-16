using namespace System.Net

param($Request, $TriggerMetadata)

Write-Host "webhook-syncorders triggered"

# =====================================================================================
# webhook-syncorders — order -> e-conomic draft invoice
# -------------------------------------------------------------------------------------
# Ported from the active VM batch script Sync-Webshop-Orders-Create-ERP-Invoice.ps1,
# scoped to a SINGLE completed WooCommerce order delivered by the webhook payload.
#
# Payload: either a full WooCommerce order object, or { "order_id": <int> } / { "id": <int> }
# in which case the order is fetched from the WooCommerce REST API.
#
# Secrets come from App Settings via profile.ps1 (env vars, KV-backed in Azure) — no
# plaintext credentials in source. Idempotent: an order already represented by an
# e-conomic draft/booked invoice (references.other = "WebshopOrderId-<num>") is skipped.
# =====================================================================================

[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$BaseUrl            = $Global:WebshopBaseUrl
$CompanyManagerBase = "$BaseUrl/wp-json/company-manager/v1"
$EventName          = $Global:EventName

# ─── Helpers: email ────────────────────────────────────────────────────────────
function Send-Mail {
    param([string]$Subject, [string]$Body)
    try {
        $securePass = ConvertTo-SecureString $Global:SmtpPass -AsPlainText -Force
        $cred       = New-Object System.Management.Automation.PSCredential($Global:SmtpUser, $securePass)
        Send-MailMessage `
            -To         $Global:AlertTo `
            -From       "$($Global:FromDisplay) <$($Global:FromAddress)>" `
            -Subject    $Subject `
            -Body       $Body `
            -SmtpServer $Global:SmtpServer `
            -Port       $Global:SmtpPort `
            -Credential $cred `
            -UseSsl
    } catch {
        Write-Host "Failed to send mail: $($_.Exception.Message)"
    }
}

# ─── Helpers: e-conomic ────────────────────────────────────────────────────────
function Invoke-EconomicPagedGet {
    param([string]$Uri, [hashtable]$Headers)
    $collection = @()
    $resp = Invoke-RestMethod -Method Get -Headers $Headers -Uri $Uri
    if ($resp.collection) { $collection += $resp.collection }
    while ($resp.pagination -and ($resp.pagination.PSObject.Properties.Name -contains 'nextPage') -and $resp.pagination.nextPage) {
        $resp = Invoke-RestMethod -Method Get -Headers $Headers -Uri $resp.pagination.nextPage
        if ($resp.collection) { $collection += $resp.collection }
    }
    return $collection
}

# ─── Helpers: WooCommerce / WordPress ──────────────────────────────────────────
function Get-WpAuthHeader {
    $pair = "$($Global:WpUserApi):$($Global:WpAppPassword)"
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
    $allQuery["consumer_key"]    = $Global:ConsumerKey
    $allQuery["consumer_secret"] = $Global:ConsumerSecret
    $pairs = foreach ($k in $allQuery.Keys) {
        "{0}={1}" -f [System.Uri]::EscapeDataString([string]$k), [System.Uri]::EscapeDataString([string]$allQuery[$k])
    }
    $uri = "$root/$Path`?$($pairs -join '&')"
    Invoke-RestMethod -Method $Method -Uri $uri -ErrorAction Stop
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

# ─── Helpers: invoice building ─────────────────────────────────────────────────
function Get-EconomicLayout {
    param([Parameter(Mandatory)]$Layouts, [string]$NameLike = "Dansk*")
    $layout = $Layouts | Where-Object { $_.name -like $NameLike } | Select-Object -First 1
    if (-not $layout) { throw "No e-conomic layout found matching '$NameLike'" }
    return $layout
}

function Get-EconomicPaymentTermsScope {
    param([Parameter(Mandatory)]$Customer, [Parameter(Mandatory)]$PaymentTerms)
    $scope = $PaymentTerms | Where-Object { $_.paymentTermsNumber -eq $Customer.paymentTerms.paymentTermsNumber } | Select-Object -First 1
    if (-not $scope) { throw "Payment terms not found for customerNumber '$($Customer.customerNumber)'" }
    return $scope
}

function Get-EconomicVatZoneScope {
    param([Parameter(Mandatory)]$Customer, [Parameter(Mandatory)]$VatZones)
    $scope = $VatZones | Where-Object { $_.vatZoneNumber -eq $Customer.vatZone.vatZoneNumber } | Select-Object -First 1
    if (-not $scope) { throw "VAT zone not found for customerNumber '$($Customer.customerNumber)'" }
    return $scope
}

function Get-EconomicCustomerContactNumber {
    param([Parameter(Mandatory)]$Customer)
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
    param([Parameter(Mandatory)]$Customer, [Parameter(Mandatory)]$VatZoneScope)
    $recipientCountry = $Customer.country
    if (-not $recipientCountry) { $recipientCountry = "DK" }
    $recipient = [ordered]@{
        name    = $Customer.name
        address = $Customer.address
        zip     = $Customer.zip
        city    = $Customer.city
        country = $recipientCountry
        vatZone = @{ vatZoneNumber = $Customer.vatZone.vatZoneNumber; self = $VatZoneScope.self }
    }
    $contactNumber = Get-EconomicCustomerContactNumber -Customer $Customer
    if ($contactNumber) { $recipient.attention = @{ customerContactNumber = $contactNumber } }
    if ($Customer.ean)  { $recipient.ean = $Customer.ean }
    return $recipient
}

function Convert-AmountFromEuroOrFail {
    param([Parameter(Mandatory)][double]$AmountFrom, [Parameter(Mandatory)][string]$To_Currency)
    if ([string]::IsNullOrWhiteSpace($To_Currency)) { throw "To_Currency is empty" }
    if ($To_Currency.ToUpper() -eq "EUR") { return [double]([math]::Ceiling($AmountFrom)) }
    $uri = "https://onesimpleapi.com/api/exchange_rate?token=$($Global:Currency_APIKey)&output=text&from_currency=EUR&to_currency=$To_Currency&from_value=$AmountFrom"
    $raw = [double](Invoke-RestMethod -Uri $uri -Method Get -ErrorAction Stop)
    if ($raw -le 0) { throw "Invalid exchange result returned from API: '$raw'" }
    return [double]([math]::Ceiling($raw))
}

function New-WebshopInvoiceLines {
    param(
        [Parameter(Mandatory)]$OrderLines,
        [Parameter(Mandatory)][int]$VatZoneNumber,
        [Parameter(Mandatory)][string]$CustomerCurrency,
        [string]$HeaderDescription = $null
    )
    $invoiceLines = @()
    $lineNo = 0
    if ($HeaderDescription) {
        $lineNo++
        $invoiceLines += [pscustomobject]@{ lineNumber = [int]$lineNo; sortKey = [int]$lineNo; description = [string]$HeaderDescription }
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
            $convertedUnitPrice = Convert-AmountFromEuroOrFail -AmountFrom $originalUnitPriceEur -To_Currency $targetCurrency
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
            product      = @{ productNumber = [string]$economicProductNumber }
            unit         = @{ unitNumber = 1; self = "https://restapi.e-conomic.com/units/1" }
        }
    }
    return ,$invoiceLines
}

function New-EconomicDraftInvoiceWebshop {
    param(
        [Parameter(Mandatory)]$EconomicCustomer,
        [Parameter(Mandatory)]$VatZones,
        [Parameter(Mandatory)]$Layouts,
        [Parameter(Mandatory)]$PaymentTerms,
        [Parameter(Mandatory)][hashtable]$Headers,
        [Parameter(Mandatory)][string]$OrderNumber,
        [Parameter(Mandatory)]$InvoiceDate,
        [Parameter(Mandatory)]$InvoiceLines,
        [string]$Heading = "Webshop Order",
        [string]$TextLine1 = "",
        [string]$LayoutNameLike = "Dansk*",
        [int]$VendorEmployeeNumber = 1
    )
    $vatZoneScope      = Get-EconomicVatZoneScope -Customer $EconomicCustomer -VatZones $VatZones
    $layoutScope       = Get-EconomicLayout -Layouts $Layouts -NameLike $LayoutNameLike
    $paymentTermsScope = Get-EconomicPaymentTermsScope -Customer $EconomicCustomer -PaymentTerms $PaymentTerms
    $contactNumber     = Get-EconomicCustomerContactNumber -Customer $EconomicCustomer
    $recipientObject   = New-EconomicRecipientObject -Customer $EconomicCustomer -VatZoneScope $vatZoneScope

    $invoiceCurrency = [string]$EconomicCustomer.currency
    if ([string]::IsNullOrWhiteSpace($invoiceCurrency)) { $invoiceCurrency = "EUR" }

    $references = [ordered]@{
        other = "WebshopOrderId-$OrderNumber"
        vendorReference = @{ employeeNumber = $VendorEmployeeNumber }
    }
    if ($contactNumber) { $references.customerContact = @{ customerContactNumber = $contactNumber } }

    $body = [ordered]@{
        currency     = $invoiceCurrency
        customer     = @{ customerNumber = [int]$EconomicCustomer.customerNumber; self = [string]$EconomicCustomer.self }
        date         = [string]$InvoiceDate
        layout       = @{ layoutNumber = [int]$layoutScope.layoutNumber; self = [string]$layoutScope.self }
        paymentTerms = @{ paymentTermsNumber = [int]$paymentTermsScope.paymentTermsNumber; self = [string]$paymentTermsScope.self }
        recipient    = $recipientObject
        notes        = @{ heading = [string]$Heading; textLine1 = [string]$TextLine1 }
        references   = $references
        lines        = $InvoiceLines
    }

    $bodyJson = $body | ConvertTo-Json -Depth 30
    return Invoke-RestMethod -Method POST -Headers $Headers -Uri "https://restapi.e-conomic.com/invoices/drafts" -Body $bodyJson -ContentType "application/json;charset=UTF-8"
}

# ─── Main ───────────────────────────────────────────────────────────────────────
try {
    $headers = $Global:Economic_headers_REST

    # Parse payload
    $data = $Request.Body
    if ($data -is [hashtable] -or $data -is [System.Management.Automation.PSCustomObject]) {
        $payload = $data
    } else {
        $payload = $data | ConvertFrom-Json -ErrorAction Stop
    }
    Write-Host "Payload received: $($Request.Body | ConvertTo-Json -Depth 10)"

    # Resolve the order object: either the full order in the payload, or fetch by id.
    $order = $null
    if ($payload.line_items -or $payload.meta_data) {
        $order = $payload
    } else {
        $orderId = $payload.order_id
        if (-not $orderId) { $orderId = $payload.id }
        if (-not $orderId) {
            $msg = "Payload contains neither a full order (line_items/meta_data) nor an order_id/id."
            Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{ StatusCode = [HttpStatusCode]::BadRequest; Body = $msg })
            return
        }
        Write-Host "Fetching order $orderId from WooCommerce..."
        $order = Invoke-WooRequest -Path "orders/$orderId"
    }

    $orderNumber = [string]$order.number
    if ([string]::IsNullOrWhiteSpace($orderNumber)) { $orderNumber = [string]$order.id }

    # Only completed orders are invoiced.
    if ($order.status -and $order.status -ne 'completed') {
        $msg = "Order $orderNumber status '$($order.status)' is not 'completed' — skipped."
        Write-Host $msg
        Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{ StatusCode = [HttpStatusCode]::OK; Body = $msg })
        return
    }

    # New-design orders carry _cm_company_id; legacy user-meta orders are not invoiced here.
    $companyId = Get-OrderCompanyId -Order $order
    if (-not $companyId) {
        $msg = "Order $orderNumber has no _cm_company_id (legacy user-meta order) — not invoiced by this endpoint."
        Write-Host $msg
        Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{ StatusCode = [HttpStatusCode]::OK; Body = $msg })
        return
    }

    # Resolve the Company Manager company -> e-conomic customer number.
    $company = Invoke-WpApiGet -Uri "$CompanyManagerBase/companies/$companyId"
    $erpNum  = [string]$company.erp_customer_number
    if ([string]::IsNullOrWhiteSpace($erpNum)) {
        throw "Company #$companyId ('$($company.name)') has no erp_customer_number set in Company Manager."
    }

    # e-conomic master data + existing-invoice references (idempotency).
    $economicCustomers   = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/customers?pagesize=1000"      -Headers $headers
    $economicLayouts     = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/layouts?pagesize=1000"        -Headers $headers
    $economicVatZones    = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/vat-zones?pagesize=1000"      -Headers $headers
    $economicPayTerms    = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/payment-terms?pagesize=1000"  -Headers $headers
    $invoicesBooked      = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/invoices/booked?pagesize=1000" -Headers $headers
    $invoicesDrafts      = Invoke-EconomicPagedGet -Uri "https://restapi.e-conomic.com/invoices/drafts?pagesize=1000" -Headers $headers

    $orderRef = "WebshopOrderId-$orderNumber"
    $alreadyInvoiced = @($invoicesBooked + $invoicesDrafts) | Where-Object {
        $_.references -and $_.references.other -and ([string]$_.references.other -eq $orderRef)
    } | Select-Object -First 1
    if ($alreadyInvoiced) {
        $msg = "Order $orderNumber already has an e-conomic invoice (ref $orderRef) — skipped (idempotent)."
        Write-Host $msg
        Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{ StatusCode = [HttpStatusCode]::OK; Body = $msg })
        return
    }

    $economicCustomer = $economicCustomers | Where-Object { [string]$_.customerNumber -eq $erpNum } | Select-Object -First 1
    if (-not $economicCustomer) {
        throw "erp_customer_number '$erpNum' (company '$($company.name)') not found in e-conomic master data."
    }

    # Build line items from the order.
    $orderDateLocal = $null
    if ($order.date_created_gmt) { try { $orderDateLocal = (Get-Date $order.date_created_gmt).ToLocalTime() } catch {} }
    $lines = @()
    foreach ($li in $order.line_items) {
        $lines += [pscustomobject]@{
            OrderNumber    = $orderNumber
            OrderDateLocal = $orderDateLocal
            ProductName    = $li.name
            Quantity       = $li.quantity
            UnitPrice      = [decimal]$li.price
        }
    }
    if ($lines.Count -eq 0) { throw "Order $orderNumber has no line items." }

    $vatZoneNumber    = [int]$economicCustomer.vatZone.vatZoneNumber
    $customerCurrency = [string]$economicCustomer.currency
    if ([string]::IsNullOrWhiteSpace($customerCurrency)) { $customerCurrency = "EUR" }

    $invoiceLines = New-WebshopInvoiceLines `
        -OrderLines $lines `
        -VatZoneNumber $vatZoneNumber `
        -CustomerCurrency $customerCurrency `
        -HeaderDescription "Sponsor Webshop Order: $orderNumber`r`n`r`n"

    $created = New-EconomicDraftInvoiceWebshop `
        -EconomicCustomer $economicCustomer `
        -VatZones $economicVatZones `
        -Layouts $economicLayouts `
        -PaymentTerms $economicPayTerms `
        -Headers $headers `
        -OrderNumber $orderNumber `
        -InvoiceDate (Get-Date).ToString("yyyy-MM-dd") `
        -InvoiceLines $invoiceLines `
        -Heading "$EventName - Experts Live Denmark" `
        -TextLine1 "Sponsorship" `
        -LayoutNameLike "English*"

    Write-Host "Draft created for order $orderNumber -> draftInvoiceNumber=$($created.draftInvoiceNumber)"

    Send-Mail `
        -Subject "Invoice draft ready in e-conomic - $EventName (order $orderNumber)" `
        -Body @"
A webshop order has been turned into an e-conomic draft invoice.

OrderNumber        : $orderNumber
Customer           : $($economicCustomer.name)
CustomerNumber     : $($economicCustomer.customerNumber)
Currency           : $customerCurrency
DraftInvoiceNumber : $($created.draftInvoiceNumber)
Time               : $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@

    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::OK
        Body       = ($created | ConvertTo-Json -Depth 10)
    })

} catch {
    $errMsg = $_.Exception.Message
    Write-Host "ERROR: $errMsg"

    Send-Mail `
        -Subject "FAILED: Order was NOT processed in e-conomic" `
        -Body @"
An order could NOT be processed in e-conomic via the webhook integration.

Time    : $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Error   : $errMsg
Payload : $($Request.Body | ConvertTo-Json -Depth 10)
"@

    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::InternalServerError
        Body       = "Error: $errMsg"
    })
}
