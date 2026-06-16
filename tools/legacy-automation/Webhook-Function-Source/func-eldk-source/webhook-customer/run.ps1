using namespace System.Net

param($Request, $TriggerMetadata)

Write-Host "webhook-customer triggered"

# ─── Helpers ─────────────────────────────────────────────────────────────────
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

function Invoke-EconomicPagedGet {
    param([string]$Uri, [hashtable]$Headers)
    $collection = @()
    $resp = Invoke-RestMethod -Method Get -Headers $Headers -Uri $Uri
    if ($resp.collection) { $collection += $resp.collection }
    while ($resp.pagination -and $resp.pagination.nextPage) {
        $resp = Invoke-RestMethod -Method Get -Headers $Headers -Uri $resp.pagination.nextPage
        if ($resp.collection) { $collection += $resp.collection }
    }
    return $collection
}

# ─── Main ─────────────────────────────────────────────────────────────────────
try {
    # Read JSON body
    $data = $Request.Body
    if ($data -is [hashtable] -or $data -is [System.Management.Automation.PSCustomObject]) {
        $customerObject = $data
    } else {
        $customerObject = $data | ConvertFrom-Json -ErrorAction Stop
    }

    Write-Host "Payload received: $($Request.Body | ConvertTo-Json -Depth 10)"

    # Validate required fields
    $missing = @()
    if (-not $customerObject.name)                                                          { $missing += "name" }
    if (-not $customerObject.currency)                                                      { $missing += "currency" }
    if (-not ($customerObject.customerGroup -and $customerObject.customerGroup.customerGroupNumber)) { $missing += "customerGroup.customerGroupNumber" }
    if (-not ($customerObject.paymentTerms -and $customerObject.paymentTerms.paymentTermsNumber))    { $missing += "paymentTerms.paymentTermsNumber" }
    if (-not ($customerObject.vatZone -and $customerObject.vatZone.vatZoneNumber))          { $missing += "vatZone.vatZoneNumber" }

    if ($missing.Count -gt 0) {
        $msg = "Missing required field(s): $($missing -join ', ')"
        Write-Host $msg
        Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
            StatusCode = [HttpStatusCode]::BadRequest
            Body       = $msg
        })
        return
    }

    $customerName = $customerObject.name.Trim()
    Write-Host "Checking if customer exists: $customerName"

    # Duplicate check by name
    $existingCustomers = Invoke-EconomicPagedGet `
        -Uri     "https://restapi.e-conomic.com/customers?pagesize=1000" `
        -Headers $Global:Economic_headers_REST

    $existing = $existingCustomers | Where-Object {
        $_.name -and $_.name.Trim().ToLower() -eq $customerName.ToLower()
    } | Select-Object -First 1

    if ($existing) {
        Write-Host "Customer already exists. CustomerNumber: $($existing.customerNumber)"
        Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
            StatusCode = [HttpStatusCode]::OK
            Body       = ($existing | ConvertTo-Json -Depth 10)
        })
        return
    }

    # Create customer
    $body     = $customerObject | ConvertTo-Json -Depth 10
    $response = Invoke-RestMethod `
        -Method      Post `
        -Uri         "https://restapi.e-conomic.com/customers" `
        -Headers     $Global:Economic_headers_REST `
        -Body        $body `
        -ContentType "application/json"

    Write-Host "Customer created. CustomerNumber: $($response.customerNumber)"

    # Send success email
    Send-Mail `
        -Subject "New customer created in e-conomic: $($response.name)" `
        -Body @"
A new customer has been created in e-conomic.

CustomerNumber : $($response.customerNumber)
Name           : $($response.name)
Email          : $($response.email)
Address        : $($response.address)
Zip            : $($response.zip)
City           : $($response.city)
Country        : $($response.country)
Phone          : $($response.telephoneAndFaxNumber)
CVR            : $($response.corporateIdentificationNumber)
Website        : $($response.website)
Time           : $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@

    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::OK
        Body       = ($response | ConvertTo-Json -Depth 10)
    })

} catch {
    $errMsg = $_.Exception.Message
    Write-Host "ERROR: $errMsg"

    Send-Mail `
        -Subject "FAILED: Customer was NOT created in e-conomic" `
        -Body @"
A customer could NOT be created in e-conomic via the webhook integration.

Time    : $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Error   : $errMsg
Payload : $($Request.Body | ConvertTo-Json -Depth 10)
"@

    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::InternalServerError
        Body       = "Error: $errMsg"
    })
}
