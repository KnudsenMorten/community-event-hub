param(
    [string]$data,
    [string]$dataFile
)

#------------------------------------------------------------------------------------------------
Write-Output "***********************************************************************************************"
Write-Output "Create Customer in Economic via Webhook"
Write-Output ""
Write-Output "Support: Morten Knudsen - mok@expertslive.dk"
Write-Output "***********************************************************************************************"
#------------------------------------------------------------------------------------------------

$datafile = "C:\Scripts-ELDK-Automation\Integration\Webshop-Customer\In\customer.json"
$OutFolder = "C:\Scripts-ELDK-Automation\Integration\Webshop-Customer\Out"

$moveDataFileAfterRead          = $true
$dataFileUsed                   = $false
$script:ProcessedCustomerNumber = "unknown"   # populated after API call; used in finally block

function Show-InputStatus {
    Write-Host "Input status:" -ForegroundColor Cyan
    Write-Host "  -data provided    : $(-not [string]::IsNullOrWhiteSpace($data))"
    Write-Host "  -dataFile provided: $(-not [string]::IsNullOrWhiteSpace($dataFile))"
    Write-Host "  stdin redirected  : $([Console]::IsInputRedirected)"
}

Show-InputStatus

try {
    ##############################################
    # Input source 1: direct -data parameter
    ##############################################
    if (-not [string]::IsNullOrWhiteSpace($data)) {
        Write-Host "Using payload from -data parameter." -ForegroundColor Green
    }

    ##############################################
    # Input source 2: file passed in -dataFile
    ##############################################
    if ([string]::IsNullOrWhiteSpace($data) -and -not [string]::IsNullOrWhiteSpace($dataFile)) {
        if (-not (Test-Path -LiteralPath $dataFile)) {
            throw "dataFile not found: $dataFile"
        }

        Write-Host "Reading payload from data file: $dataFile" -ForegroundColor Yellow
        $data = Get-Content -LiteralPath $dataFile -Raw -ErrorAction Stop
        $dataFileUsed = $true

        if (-not [string]::IsNullOrWhiteSpace($data)) {
            Write-Host "Payload read from data file." -ForegroundColor Green
        }
        else {
            Write-Host "dataFile was empty or whitespace." -ForegroundColor Yellow
        }
    }

    ##############################################
    # Input source 3: stdin
    ##############################################
    if ([string]::IsNullOrWhiteSpace($data)) {
        try {
            if (-not [Console]::IsInputRedirected) {
                Write-Host "stdin is not redirected." -ForegroundColor Yellow
            }
            else {
                Write-Host "Reading stdin..." -ForegroundColor Yellow
                $stdinData = [Console]::In.ReadToEnd()
                Write-Host "stdin read completed." -ForegroundColor Green

                if (-not [string]::IsNullOrWhiteSpace($stdinData)) {
                    $data = $stdinData
                    Write-Host "Assigned stdin payload to `$data." -ForegroundColor Green
                }
                else {
                    Write-Host "stdin was empty or whitespace." -ForegroundColor Yellow
                }
            }
        }
        catch {
            Write-Host "Could not read stdin: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }

    Write-Host ""
    Write-Host "Payload length: $(if ($null -eq $data) { 0 } else { $data.Length })" -ForegroundColor Cyan
    Write-Host "Raw input received:" -ForegroundColor Cyan
    Write-Host $data

    $ScriptDirectory = $PSScriptRoot
    $global:PathScripts = Split-Path -parent $ScriptDirectory

    Import-Module "$ScriptDirectory\Secrets.psm1" -Global -Force -WarningAction SilentlyContinue
    Import_Secrets

    # Email (SMTP)
    $SmtpServer  = "smtp-relay.brevo.com"
    $SmtpPort    = 587
    $FromDisplay = "Experts Live Denmark"
    $FromAddress = "info@expertslive.dk"
    $AlertTo     = @("mok@expertslive.dk")

    function Send-NewCustomerCreatedMail {
        param(
            [Parameter(Mandatory = $true)]
            [psobject]$Customer
        )

        try {
            $securePass = ConvertTo-SecureString $Global:SmtpPass -AsPlainText -Force
            $cred       = New-Object System.Management.Automation.PSCredential($Global:SmtpUser, $securePass)
            $from       = "$FromDisplay <$FromAddress>"
            $subject    = "New customer created in e-conomic: $($Customer.name)"

            $body = @"
A new customer has been created in e-conomic.

CustomerNumber:
$($Customer.customerNumber)

Name:
$($Customer.name)

Email:
$($Customer.email)

Address:
$($Customer.address)

Zip:
$($Customer.zip)

City:
$($Customer.city)

Country:
$($Customer.country)

Phone:
$($Customer.telephoneAndFaxNumber)

CVR:
$($Customer.corporateIdentificationNumber)

Website:
$($Customer.website)

Time:
$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@

            Write-Host "Sending new-customer mail to $AlertTo" -ForegroundColor Green

            Send-MailMessage `
                -To         $AlertTo `
                -From       $from `
                -Subject    $subject `
                -Body       $body `
                -SmtpServer $SmtpServer `
                -Port       $SmtpPort `
                -Credential $cred `
                -UseSsl
        }
        catch {
            Write-Host "FAILED to send new-customer mail: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    function Send-CustomerFailureMail {
        param(
            [Parameter(Mandatory = $true)]
            [string]$ErrorMessage,

            [string]$SourceFile = "",
            [string]$Payload    = ""
        )

        try {
            $securePass = ConvertTo-SecureString $Global:SmtpPass -AsPlainText -Force
            $cred       = New-Object System.Management.Automation.PSCredential($Global:SmtpUser, $securePass)
            $from       = "$FromDisplay <$FromAddress>"
            $subject    = "❌ FAILED: Customer was NOT created in e-conomic"

            $body = @"
A customer could NOT be created in e-conomic via the webhook integration.

Time:
$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")

Error:
$ErrorMessage

Source File:
$SourceFile

Payload:
$Payload

Please investigate and re-process the file manually if needed.
"@

            Write-Host "Sending failure alert mail to $AlertTo" -ForegroundColor Red

            Send-MailMessage `
                -To         $AlertTo `
                -From       $from `
                -Subject    $subject `
                -Body       $body `
                -SmtpServer $SmtpServer `
                -Port       $SmtpPort `
                -Credential $cred `
                -UseSsl
        }
        catch {
            Write-Host "FAILED to send failure alert mail: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    function Invoke-EconomicPagedGet {
        param(
            [Parameter(Mandatory = $true)][string]$Uri,
            [Parameter(Mandatory = $true)][hashtable]$Headers
        )

        Write-Host "🌐 Economic GET (page 1): $Uri"

        $collection = @()
        $page = 1

        $resp = Invoke-RestMethod -Method Get -Headers $Headers -Uri $Uri

        if ($resp.collection) {
            $collection += $resp.collection
            Write-Host "   ↳ Rows received: $($resp.collection.Count)"
        }

        while ($resp.pagination -and ($resp.pagination.PSObject.Properties.Name -contains 'nextPage') -and $resp.pagination.nextPage) {
            $page++
            Write-Host "🌐 Economic GET (page $page): $($resp.pagination.nextPage)"

            $resp = Invoke-RestMethod -Method Get -Headers $Headers -Uri $resp.pagination.nextPage

            if ($resp.collection) {
                $collection += $resp.collection
                Write-Host "   ↳ Rows received: $($resp.collection.Count)  |  Total so far: $($collection.Count)"
            }
        }

        Write-Host "✔️ Economic API paging finished"
        Write-Host "   Total rows collected: $($collection.Count)"

        return $collection
    }

    function New-EconomicCustomer {
        param(
            [Parameter(Mandatory = $true)]
            [hashtable]$Headers,

            [Parameter(Mandatory = $true)]
            [string]$JsonData
        )

        Write-Host "📥 Raw input JSON received"

        try {
            $customerObject = $JsonData.Trim() | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            throw "Invalid JSON passed to input. Error: $($_.Exception.Message)"
        }

        $requiredChecks = @(
            @{ Name = "name";                              Valid = [bool]$customerObject.name },
            @{ Name = "currency";                          Valid = [bool]$customerObject.currency },
            @{ Name = "customerGroup.customerGroupNumber"; Valid = [bool]($customerObject.customerGroup -and $customerObject.customerGroup.customerGroupNumber) },
            @{ Name = "paymentTerms.paymentTermsNumber";   Valid = [bool]($customerObject.paymentTerms -and $customerObject.paymentTerms.paymentTermsNumber) },
            @{ Name = "vatZone.vatZoneNumber";             Valid = [bool]($customerObject.vatZone -and $customerObject.vatZone.vatZoneNumber) }
        )

        $missing = $requiredChecks | Where-Object { -not $_.Valid } | Select-Object -ExpandProperty Name
        if ($missing.Count -gt 0) {
            throw "Missing required customer field(s): $($missing -join ', ')"
        }

        $customerName = $customerObject.name.Trim()
        Write-Host "🔎 Checking if customer exists: $customerName"

        $existingCustomers = Invoke-EconomicPagedGet `
            -Uri     "https://restapi.e-conomic.com/customers?pagesize=1000" `
            -Headers $Headers

        $existing = $existingCustomers | Where-Object {
            $_.name -and $_.name.Trim().ToLower() -eq $customerName.ToLower()
        } | Select-Object -First 1

        if ($existing) {
            Write-Host "⚠️ Customer already exists. Skipping creation."
            Write-Host "   Existing CustomerNumber: $($existing.customerNumber)"

            return [pscustomobject]@{
                Created  = $false
                Customer = $existing
            }
        }

        $uri  = "https://restapi.e-conomic.com/customers"
        $body = $customerObject | ConvertTo-Json -Depth 10

        Write-Host "🌐 Economic POST: $uri"
        Write-Host "📤 Payload:"
        Write-Host $body

        try {
            $response = Invoke-RestMethod `
                -Method      Post `
                -Uri         $uri `
                -Headers     $Headers `
                -Body        $body `
                -ContentType "application/json"

            Write-Host "✔️ Customer created successfully"
            Write-Host "   CustomerNumber: $($response.customerNumber)"

            return [pscustomobject]@{
                Created  = $true
                Customer = $response
            }
        }
        catch {
            Write-Host "❌ Failed to create customer"

            if ($_.Exception.Response -and $_.Exception.Response.GetResponseStream) {
                try {
                    $reader = New-Object System.IO.StreamReader($_.Exception.Response.GetResponseStream())
                    $reader.BaseStream.Position = 0
                    $reader.DiscardBufferedData()
                    $errorBody = $reader.ReadToEnd()
                    Write-Host "Economic API response:"
                    Write-Host $errorBody
                }
                catch {
                    Write-Host "Could not read API error response body."
                }
            }

            throw $_
        }
    }

    ##############################################
    # Create Economic Customer from JSON input
    ##############################################
    if (-not [string]::IsNullOrWhiteSpace($data)) {
        try {
            $result = New-EconomicCustomer -Headers $Economic_headers_REST -JsonData $data

            # Store customer number so the finally block can use it in the filename
            $script:ProcessedCustomerNumber = $result.Customer.customerNumber

            if ($result.Created) {
                Send-NewCustomerCreatedMail -Customer $result.Customer
            }
            else {
                Write-Host "📭 No email sent because customer already exists."
            }

            Write-Host ""
            Write-Host "Customer result:"
            $result | ConvertTo-Json -Depth 10
        }
        catch {
            Write-Host "❌ Customer creation failed: $($_.Exception.Message)" -ForegroundColor Red

            $sourceFileInfo = if ($dataFileUsed -and -not [string]::IsNullOrWhiteSpace($dataFile)) { $dataFile } else { "N/A (inline data)" }
            Send-CustomerFailureMail `
                -ErrorMessage $_.Exception.Message `
                -SourceFile   $sourceFileInfo `
                -Payload      $data

            throw
        }

        return
    }
    else {
        Write-Host ""
        Write-Host "⚠️ No data was provided." -ForegroundColor Yellow
        Write-Host "Provide JSON either with -data or through -dataFile." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Example using -data:" -ForegroundColor Cyan
        Write-Host '$customer = @{'
        Write-Host '    name = "Test Customer ApS"'
        Write-Host '    currency = "DKK"'
        Write-Host '    customerGroup = @{ customerGroupNumber = 1 }'
        Write-Host '    paymentTerms = @{ paymentTermsNumber = 1 }'
        Write-Host '    vatZone = @{ vatZoneNumber = 1 }'
        Write-Host '    email = "test@example.com"'
        Write-Host '}'
        Write-Host '$json = $customer | ConvertTo-Json -Depth 10'
        Write-Host '.\Create-ERP-Customer-via-Webhook.ps1 -data $json'
        Write-Host ""
        Write-Host "Example using -dataFile:" -ForegroundColor Cyan
        Write-Host '.\Create-ERP-Customer-via-Webhook.ps1 -dataFile "C:\Temp\payload.json"'
        exit 1
    }
}
finally {
    # Move processed In-file to Out folder
    # Filename format: customer_<customerNumber>_<random5digits>.json
    if (
        $moveDataFileAfterRead -and
        $dataFileUsed -and
        -not [string]::IsNullOrWhiteSpace($dataFile) -and
        (Test-Path -LiteralPath $dataFile)
    ) {
        try {
            if (-not (Test-Path -LiteralPath $OutFolder)) {
                New-Item -ItemType Directory -Path $OutFolder -Force | Out-Null
                Write-Host "Created Out folder: $OutFolder" -ForegroundColor Yellow
            }

            $randomNumber = Get-Random -Minimum 10000 -Maximum 99999
            $fileExt      = [System.IO.Path]::GetExtension($dataFile)
            $destFileName = "customer_$($script:ProcessedCustomerNumber)_$randomNumber$fileExt"
            $destPath     = Join-Path $OutFolder $destFileName

            Move-Item -LiteralPath $dataFile -Destination $destPath -Force -ErrorAction Stop
            Write-Host "✔️ Moved processed file to: $destPath" -ForegroundColor Green
        }
        catch {
            Write-Host "⚠️ Failed to move data file '$dataFile' to Out folder: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}
