using namespace System.Net

param($Request, $TriggerMetadata)

Write-Host "webhook-contact triggered"

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
        $input = $data
    } else {
        $input = $data | ConvertFrom-Json -ErrorAction Stop
    }

    Write-Host "Payload received: $($Request.Body | ConvertTo-Json -Depth 10)"

    # Validate required fields
    if (-not $input.customerNumber) {
        Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
            StatusCode = [HttpStatusCode]::BadRequest
            Body       = "customerNumber is required."
        })
        return
    }
    if (-not $input.contact -or -not $input.contact.name) {
        Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
            StatusCode = [HttpStatusCode]::BadRequest
            Body       = "contact.name is required."
        })
        return
    }

    $customerNumber = $input.customerNumber
    $contact        = $input.contact

    Write-Host "CustomerNumber: $customerNumber"
    Write-Host "Contact name  : $($contact.name)"

    # Duplicate check by email
    if (-not [string]::IsNullOrWhiteSpace($contact.email)) {
        $existingContacts = Invoke-EconomicPagedGet `
            -Uri     "https://restapi.e-conomic.com/customers/$customerNumber/contacts?pagesize=1000" `
            -Headers $Global:Economic_headers_REST

        $existing = $existingContacts | Where-Object {
            $_.email -and $_.email.Trim().ToLower() -eq $contact.email.Trim().ToLower()
        } | Select-Object -First 1

        if ($existing) {
            Write-Host "Contact already exists. ContactNumber: $($existing.customerContactNumber)"
            Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
                StatusCode = [HttpStatusCode]::OK
                Body       = ($existing | ConvertTo-Json -Depth 10)
            })
            return
        }
    }

    # Build contact body
    $bodyObject = @{ name = $contact.name }
    if ($contact.email) { $bodyObject.email = $contact.email }
    if ($contact.phone) { $bodyObject.phone = $contact.phone }
    if ($contact.notes) { $bodyObject.notes = $contact.notes }

    $response = Invoke-RestMethod `
        -Method      Post `
        -Uri         "https://restapi.e-conomic.com/customers/$customerNumber/contacts" `
        -Headers     $Global:Economic_headers_REST `
        -Body        ($bodyObject | ConvertTo-Json -Depth 5) `
        -ContentType "application/json"

    Write-Host "Contact created. ContactNumber: $($response.customerContactNumber)"

    # Send success email
    Send-Mail `
        -Subject "New contact created in e-conomic: $($response.name)" `
        -Body @"
A new contact has been created in e-conomic.

CustomerNumber : $customerNumber
ContactNumber  : $($response.customerContactNumber)
Name           : $($response.name)
Email          : $($response.email)
Phone          : $($response.phone)
Notes          : $($response.notes)
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
        -Subject "FAILED: Contact was NOT created in e-conomic" `
        -Body @"
A contact could NOT be created in e-conomic via the webhook integration.

Time    : $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Error   : $errMsg
Payload : $($Request.Body | ConvertTo-Json -Depth 10)
"@

    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::InternalServerError
        Body       = "Error: $errMsg"
    })
}
