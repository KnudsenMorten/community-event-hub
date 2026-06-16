param(
    [string]$data,
    [string]$dataFile
)

#------------------------------------------------------------------------------------------------
Write-Output "***********************************************************************************************"
Write-Output "Create Contact in Economic via Webhook"
Write-Output ""
Write-Output "Support: Morten Knudsen - mok@expertslive.dk"
Write-Output "***********************************************************************************************"
#------------------------------------------------------------------------------------------------

$datafile  = "C:\Scripts-ELDK-Automation\Integration\Webshop-Contact\In\contact.json"
$OutFolder = "C:\Scripts-ELDK-Automation\Integration\Webshop-Contact\Out"

$moveDataFileAfterRead         = $true
$dataFileUsed                  = $false
$script:ProcessedCustomerNumber = "unknown"   # populated after API call; used in finally block
$script:ProcessedContactNumber  = "unknown"   # populated after API call; used in finally block

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

    function Send-NewContactCreatedMail {
        param(
            [Parameter(Mandatory = $true)]
            [int]$CustomerNumber,

            [Parameter(Mandatory = $true)]
            [psobject]$Contact
        )

        try {
            $securePass = ConvertTo-SecureString $Global:SmtpPass -AsPlainText -Force
            $cred       = New-Object System.Management.Automation.PSCredential($Global:SmtpUser, $securePass)
            $from       = "$FromDisplay <$FromAddress>"
            $subject    = "New contact created in e-conomic: $($Contact.name)"

            $body = @"
A new contact has been created in e-conomic.

CustomerNumber:
$CustomerNumber

ContactNumber:
$($Contact.customerContactNumber)

Name:
$($Contact.name)

Email:
$($Contact.email)

Phone:
$($Contact.phone)

Notes:
$($Contact.notes)

Time:
$(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@

            Write-Host "Sending new-contact mail to $AlertTo" -ForegroundColor Green

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
            Write-Host "FAILED to send new-contact mail: $($_.Exception.Message)" -ForegroundColor Red
        }
    }

    function Send-ContactFailureMail {
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
            $subject    = "❌ FAILED: Contact was NOT created in e-conomic"

            $body = @"
A contact could NOT be created in e-conomic via the webhook integration.

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

    function Get-ExistingContact {
        param(
            [Parameter(Mandatory)]
            [hashtable]$Headers,

            [Parameter(Mandatory)]
            [int]$CustomerNumber,

            [Parameter(Mandatory)]
            [string]$Email
        )

        Write-Host "🔎 Checking if contact already exists for customer $CustomerNumber with email: $Email" -ForegroundColor Cyan

        $existingContacts = Invoke-EconomicPagedGet `
            -Uri     "https://restapi.e-conomic.com/customers/$CustomerNumber/contacts?pagesize=1000" `
            -Headers $Headers

        $match = $existingContacts | Where-Object {
            $_.email -and $_.email.Trim().ToLower() -eq $Email.Trim().ToLower()
        } | Select-Object -First 1

        return $match
    }

    function New-EconomicCustomerContact {
        param(
            [Parameter(Mandatory)]
            [hashtable]$Headers,

            [Parameter(Mandatory)]
            [int]$CustomerNumber,

            [Parameter(Mandatory)]
            [psobject]$Contact
        )

        if (-not $Contact.name) {
            throw "Contact 'name' is required."
        }

        # Duplicate check by email (when email is provided)
        if (-not [string]::IsNullOrWhiteSpace($Contact.email)) {
            $existing = Get-ExistingContact `
                -Headers        $Headers `
                -CustomerNumber $CustomerNumber `
                -Email          $Contact.email

            if ($existing) {
                Write-Host "⚠️ Contact already exists under customer $CustomerNumber with email '$($Contact.email)'. Skipping creation." -ForegroundColor Yellow
                Write-Host "   Existing ContactNumber: $($existing.customerContactNumber)" -ForegroundColor Yellow

                return [pscustomobject]@{
                    Created = $false
                    Contact = $existing
                }
            }
        }
        else {
            Write-Host "⚠️ No email provided — skipping duplicate check." -ForegroundColor Yellow
        }

        $uri = "https://restapi.e-conomic.com/customers/$CustomerNumber/contacts"

        $bodyObject = @{ name = $Contact.name }

        if ($Contact.email) { $bodyObject.email = $Contact.email }
        if ($Contact.phone) { $bodyObject.phone = $Contact.phone }
        if ($Contact.notes) { $bodyObject.notes = $Contact.notes }

        $body = $bodyObject | ConvertTo-Json -Depth 5

        Write-Host "Creating contact under customer $CustomerNumber" -ForegroundColor Green
        Write-Host "Payload sent to e-conomic:" -ForegroundColor Cyan
        Write-Host $body

        $response = Invoke-RestMethod `
            -Method      Post `
            -Uri         $uri `
            -Headers     $Headers `
            -Body        $body `
            -ContentType "application/json"

        Write-Host "✔️ Contact created:" -ForegroundColor Green
        Write-Host "   ContactNumber: $($response.customerContactNumber)" -ForegroundColor Green

        return [pscustomobject]@{
            Created = $true
            Contact = $response
        }
    }

    ##############################################
    # Create Economic Contact from JSON input
    ##############################################
    if (-not [string]::IsNullOrWhiteSpace($data)) {
        try {
            $trimmedData = $data.Trim()
            $input = $trimmedData | ConvertFrom-Json -ErrorAction Stop
        }
        catch {
            throw "Invalid JSON payload. Error: $($_.Exception.Message)"
        }

        if (-not $input.customerNumber) {
            throw "customerNumber is required."
        }

        if (-not $input.contact) {
            throw "contact object is required."
        }

        Write-Host "Parsed customerNumber : $($input.customerNumber)" -ForegroundColor Green
        Write-Host "Parsed contact.name   : $($input.contact.name)" -ForegroundColor Green
        Write-Host "Parsed contact.email  : $($input.contact.email)" -ForegroundColor Green
        Write-Host "Parsed contact.phone  : $($input.contact.phone)" -ForegroundColor Green
        Write-Host "Parsed contact.notes  : $($input.contact.notes)" -ForegroundColor Green

        # Store customer number now so it's available in finally even if creation fails
        $script:ProcessedCustomerNumber = $input.customerNumber

        try {
            $contactResult = New-EconomicCustomerContact `
                -Headers        $Economic_headers_REST `
                -CustomerNumber $input.customerNumber `
                -Contact        $input.contact

            # Store contact number for the Out filename
            $script:ProcessedContactNumber = $contactResult.Contact.customerContactNumber

            if ($contactResult.Created) {
                Send-NewContactCreatedMail `
                    -CustomerNumber $input.customerNumber `
                    -Contact        $contactResult.Contact
            }
            else {
                Write-Host "📭 No email sent because contact already exists." -ForegroundColor Yellow
            }

            Write-Host ""
            Write-Host "Result:" -ForegroundColor Green
            $contactResult | ConvertTo-Json -Depth 10
        }
        catch {
            Write-Host "❌ Contact creation failed: $($_.Exception.Message)" -ForegroundColor Red

            $sourceFileInfo = if ($dataFileUsed -and -not [string]::IsNullOrWhiteSpace($dataFile)) { $dataFile } else { "N/A (inline data)" }
            Send-ContactFailureMail `
                -ErrorMessage $_.Exception.Message `
                -SourceFile   $sourceFileInfo `
                -Payload      $data

            throw
        }
    }
    else {
        Write-Host "No JSON payload received." -ForegroundColor Yellow
        exit 1
    }
}
finally {
    # Move processed In-file to Out folder
    # Filename format: contact_<customerNumber>_<contactNumber>_<random5digits>.json
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
            $destFileName = "contact_$($script:ProcessedCustomerNumber)_$($script:ProcessedContactNumber)_$randomNumber$fileExt"
            $destPath     = Join-Path $OutFolder $destFileName

            Move-Item -LiteralPath $dataFile -Destination $destPath -Force -ErrorAction Stop
            Write-Host "✔️ Moved processed file to: $destPath" -ForegroundColor Green
        }
        catch {
            Write-Host "⚠️ Failed to move data file '$dataFile' to Out folder: $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}
