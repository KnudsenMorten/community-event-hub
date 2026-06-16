#------------------------------------------------------------------------------------------------
Write-Output "***********************************************************************************************"
Write-Output "Zoho Booking - Duplicate Appointmens Detection (detect >1 master class bookings)"
Write-Output ""
Write-Output "Purpose: This script detects appointments that must be adjusted (double bookings)"
Write-Output ""
Write-Output "Support: Morten Knudsen - mok@expertslive.dk"
Write-Output "***********************************************************************************************"
#------------------------------------------------------------------------------------------------

$ScriptDirectory = $PSScriptRoot
$global:PathScripts = Split-Path -parent $ScriptDirectory

Import-Module "$ScriptDirectory\Secrets.psm1" -Global -force -WarningAction SilentlyContinue
Import_Secrets

#########################
# Variables
#########################

    $EventName        = "ELDK27"
    $ZohoApiDomain = "https://www.zohoapis.eu"

    # SMTP
    $WhatIf = $false
    $SmtpServer  = "smtp-relay.brevo.com"
    $SmtpPort    = 587
    $FromDisplay = "Experts Live Denmark"
    $FromAddress = "info@expertslive.dk"


# ---- Zoho OAuth & REST helpers
function Get-ZohoAccessToken {
  param([string]$ClientId,[string]$ClientSecret,[string]$RefreshToken)
  try {
    (Invoke-RestMethod -Method POST -Uri "https://accounts.zoho.eu/oauth/v2/token" -Body @{
      refresh_token = $RefreshToken
      client_id     = $ClientId
      client_secret = $ClientSecret
      grant_type    = "refresh_token"
    }).access_token
  } catch {
    Write-Host "❌ Failed to get Zoho access token: $($_.Exception.Message)" -ForegroundColor Red
    $null
  }
}


# ---- Get token FIRST
Write-Host "🔑 Getting Zoho access token..."
$ZohoAccessToken = Get-ZohoAccessToken -ClientId $ZohoClientId -ClientSecret $ZohoClientSecret -RefreshToken $ZohoRefreshToken
if (-not $ZohoAccessToken) {
    Write-Host "⚠️ Zoho token unavailable; Zoho operations will be skipped." -ForegroundColor Yellow
    return
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

$culture = [System.Globalization.CultureInfo]::InvariantCulture


###############################
## Master Class Analyze
###############################

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

    # Person who booked >1 master class
        $duplicates = $ActiveAppointments | Group-Object customer_email | Where-Object { $_.Count -gt 1 }

        $duplicates

    $duplicateGroups = $ActiveAppointments | Where-Object { $_.customer_email } | Group-Object customer_email | Where-Object { $_.Count -gt 1 }


    $emailsToSendHtml = foreach ($group in $duplicateGroups) {
        $appointments = $group.Group | Sort-Object iso_start_time
        $customerName = ($appointments | Select-Object -First 1).customer_name
        $customerEmail = $group.Name

        $items = $appointments | ForEach-Object {
            "<li><strong>$($_.service_name)</strong><br/>$($_.start_time) - $($_.end_time)<br/><a href='$($_.summary_url)'>Cancel appointment here in the summary (top, right corner)</a></li>"
        }

    $body = @"
<html>
<body>
<p>Dear $customerName,</p>
<p>We can see that you are currently registered for more than one <strong>Master Class</strong> at <strong>$($EventName) - Experts Live Denmark</strong>.</p>
<p>Please review your bookings below and keep only the class you want to attend and cancel the other(s).</p>
<ul>
$($items -join "`r`n")
</ul>
<p>If you need help, just reply to this email.</p>
<p>Best regards,<br/>Experts Live Denmark</p>
</body>
</html>
"@

    [pscustomobject]@{
        To      = $customerEmail
        Subject = "Issue detected with Master Class booking - $($EventName)"
        Body    = $body
    }
}

foreach ($mail in $emailsToSendHtml) {

    if ($WhatIf) {
        Write-Host "--------------------------------------------" -ForegroundColor Cyan
        Write-Host "WHATIF: Email would be sent" -ForegroundColor Yellow
        Write-Host "To      : $($mail.To)"
        Write-Host "From    : $FromDisplay <$FromAddress>"
        Write-Host "Subject : $($mail.Subject)"
        Write-Host "Body preview:"
        Write-Host $mail.Body
        Write-Host "--------------------------------------------"
    }
    else {
        $from = "$FromDisplay <$FromAddress>"

        if (-not $FromAddress -or -not $mail.To) {
            throw "From/To address missing"
        }

        $securePass = ConvertTo-SecureString $SmtpPass -AsPlainText -Force
        $cred = New-Object System.Management.Automation.PSCredential($SmtpUser, $securePass)

        Write-Host "SMTP: sending -> To=$($mail.To) | From=$from | Server=${SmtpServer}:${SmtpPort} | SSL=True" -ForegroundColor Green

        Send-MailMessage `
            -To $mail.To `
            -From $from `
            -Subject $mail.Subject `
            -Body $mail.Body `
            -SmtpServer $SmtpServer `
            -Port $SmtpPort `
            -Credential $cred `
            -UseSsl `
            -BodyAsHtml
    }
}
