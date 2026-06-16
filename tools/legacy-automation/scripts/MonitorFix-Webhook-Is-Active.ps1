# ─── Secrets & module bootstrap ──────────────────────────────────────────────
$ScriptDirectory      = $PSScriptRoot
$global:PathScripts   = Split-Path -Parent $ScriptDirectory
Import-Module "$ScriptDirectory\Secrets.psm1" -Global -Force -WarningAction SilentlyContinue
Import_Secrets
# Secrets module exposes SMTP credentials — adjust variable names below
# to match what your Secrets.psm1 actually exports (e.g. $global:SmtpUser)

# ─── Configuration ────────────────────────────────────────────────────────────
$baseUrl   = "https://localhost:8002/VisualCron/json"
$username  = "monitor"
# Password is read from Key Vault by Import_Secrets (secret name: visualcron-api-password).
# NEVER hard-code the VisualCron API password here.
$password  = $global:VisualCronApiPassword
if ([string]::IsNullOrWhiteSpace($password)) {
    throw "VisualCron API password not loaded. Ensure Import_Secrets ran (it reads 'visualcron-api-password' from Key Vault)."
}

# Exact group name as it appears in VisualCron
$groupName = "EXPERTS LIVE DENMARK AUTOMATION"

# Delay after activating a stopped trigger
$DelaySecondsAfterFix = 2

# HTTP REST trigger ports to monitor for CLOSE_WAIT socket leaks
$MonitoredPorts = @(9992, 9993, 9994)

# If CLOSE_WAIT count exceeds this threshold, skip reactivation and send alert
# instead — restarting VisualCron from inside a VisualCron job kills the job itself
$CloseWaitAlertThreshold = 100

# ─── Email configuration (Brevo SMTP relay) ───────────────────────────────────
$SmtpServer  = "smtp-relay.brevo.com"
$SmtpPort    = 587
$SmtpUseSsl  = $true
$FromDisplay = "Experts Live Denmark"
$FromAddress = "info@expertslive.dk"
$MailFrom    = "$FromDisplay <$FromAddress>"
$AlertTo     = @("mok@expertslive.dk")
# SMTP user/password come from Secrets module loaded above
# Expected exports: $global:SmtpUser and $global:SmtpPass
# If your Secrets.psm1 uses different names, update the Send-AlertEmail function below
# ─────────────────────────────────────────────────────────────────────────────

# ─── Trust self-signed VisualCron certificate ────────────────────────────────
if (-not ([System.Management.Automation.PSTypeName]'TrustAllCertsPolicy').Type) {
    Add-Type @"
using System.Net;
using System.Security.Cryptography.X509Certificates;
public class TrustAllCertsPolicy : ICertificatePolicy {
    public bool CheckValidationResult(
        ServicePoint srvPoint, X509Certificate certificate,
        WebRequest request, int certificateProblem) {
        return true;
    }
}
"@
}
[System.Net.ServicePointManager]::CertificatePolicy = New-Object TrustAllCertsPolicy

# ─── Helpers ─────────────────────────────────────────────────────────────────
function Get-CloseWaitInfo {
    param([int[]]$Ports)
    $all    = Get-NetTCPConnection -ErrorAction SilentlyContinue
    $result = @{}
    foreach ($port in $Ports) {
        $conns = $all | Where-Object { $_.LocalPort -eq $port -and $_.State -eq "CloseWait" }
        $result[$port] = @{
            Count     = $conns.Count
            RemoteIPs = ($conns | Select-Object -ExpandProperty RemoteAddress -Unique) -join ', '
        }
    }
    return $result
}

function Send-AlertEmail {
    param(
        [string]$Subject,
        [string]$Body
    )
    try {
        # Credentials from Secrets module — update names if yours differ
        $smtpUser = $global:SmtpUser
        $smtpKey  = $global:SmtpPass

        $securePass = ConvertTo-SecureString $smtpKey -AsPlainText -Force
        $credential = New-Object System.Management.Automation.PSCredential($smtpUser, $securePass)

        Send-MailMessage `
            -SmtpServer $SmtpServer `
            -Port       $SmtpPort `
            -UseSsl `
            -Credential $credential `
            -From       $MailFrom `
            -To         $AlertTo `
            -Subject    $Subject `
            -Body       $Body `
            -BodyAsHtml $false `
            -ErrorAction Stop

        Write-Output "  [EMAIL] Alert sent to: $($AlertTo -join ', ')"
    } catch {
        Write-Output "  [EMAIL] Failed to send alert: $_"
    }
}

# ─── Main ────────────────────────────────────────────────────────────────────
try {

    # Step 1: Check CLOSE_WAIT socket counts
    Write-Output "Checking CLOSE_WAIT socket counts on ports: $($MonitoredPorts -join ', ')"
    $cwInfo  = Get-CloseWaitInfo -Ports $MonitoredPorts
    $cwTotal = 0
    foreach ($k in $cwInfo.Keys) { $cwTotal += $cwInfo[$k].Count }

    $cwSummary = $cwInfo.Keys | Sort-Object | ForEach-Object {
        "  Port $_ : $($cwInfo[$_].Count) CLOSE_WAIT" + $(
            if ($cwInfo[$_].Count -gt 0) { " (from: $($cwInfo[$_].RemoteIPs))" } else { " - clean" }
        )
    }
    $cwSummary | ForEach-Object { Write-Output $_ }

    # Step 2: If CLOSE_WAIT is critically high — alert and exit
    # DO NOT restart the VisualCron service from inside a VisualCron job.
    # That kills the running job and leaves the system in an unknown state.
    # Run Restart-VisualCron.ps1 manually or via Windows Task Scheduler instead.
    if ($cwTotal -ge $CloseWaitAlertThreshold) {
        Write-Output ""
        Write-Output "CLOSE_WAIT total ($cwTotal) >= alert threshold ($CloseWaitAlertThreshold)."
        Write-Output "Sending alert email — manual intervention required."

        $hostname  = $env:COMPUTERNAME
        $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

        $emailBody = @"
VisualCron - Problem with connection draining detected
======================================================
Server    : $hostname
Time      : $timestamp
Ports     : $($MonitoredPorts -join ', ')
Sockets   : $cwTotal connections stuck in draining state (threshold: $CloseWaitAlertThreshold)

Per-port breakdown:
$($cwSummary -join "`r`n")

The server is experiencing a buildup of connections that are not draining
properly from the App Gateway to the VisualCron webhook listener.

ACTION: Please temporarily restart the VisualCron server to resolve this.
Run the following script on $hostname :
  C:\Scripts-ELDK-Automation\Restart-VisualCron.ps1

Note: Do not run this from inside VisualCron - it must be run directly on the server.
"@

        Send-AlertEmail `
            -Subject "[$hostname] VisualCron: Connection draining issue detected - server restart required" `
            -Body    $emailBody

        exit 0
    }

    # Step 3: Normal trigger reactivation via API
    Write-Output ""
    Write-Output "CLOSE_WAIT count ($cwTotal) within normal range. Proceeding with trigger check."
    Write-Output "Logging on to VisualCron API..."

    $logon = Invoke-RestMethod `
        -Uri "$baseUrl/logon?username=$username&password=$password&expire=600" `
        -Method Get `
        -ErrorAction Stop

    if (-not $logon.token) { throw "No token returned from VisualCron logon." }
    $token = $logon.token
    Write-Output "Logon succeeded."

    Write-Output "Retrieving all jobs..."
    $jobs = Invoke-RestMethod `
        -Uri "$baseUrl/Job/List?token=$token" `
        -Method Get `
        -ErrorAction Stop

    if (-not $jobs) { throw "No jobs returned from Job/List." }

    $groupJobs = $jobs | Where-Object { $_.group -eq $groupName }
    if (-not $groupJobs) {
        Write-Output "No jobs found in group: $groupName"
        return
    }

    Write-Output "Found $($groupJobs.Count) job(s) in group '$groupName'."

    $fixedTriggers  = 0
    $activeTriggers = 0

    foreach ($job in $groupJobs) {
        Write-Output ""
        Write-Output "Checking job: $($job.name) (ID: $($job.id))"

        $triggers = Invoke-RestMethod `
            -Uri "$baseUrl/Trigger/List?token=$token&jobId=$($job.id)" `
            -Method Get `
            -ErrorAction Stop

        if (-not $triggers) { Write-Output "  No triggers found."; continue }

        foreach ($trigger in $triggers) {
            $triggerType = [string]$trigger.triggertype
            $triggerDesc = [string]$trigger.description
            $triggerId   = [string]$trigger.id
            $isActive    = [bool]$trigger.active

            $isHttpRestTrigger =
                ($triggerType -match 'HTTP|REST') -or
                ($triggerDesc -match 'HTTP|REST|Webhook')

            if (-not $isHttpRestTrigger) {
                Write-Output "  Skipping non-HTTP trigger: ID=$triggerId Type='$triggerType'"
                continue
            }


            Write-Output "  HTTP trigger: ID=$triggerId Active=$isActive Desc='$triggerDesc'"

            if (-not $isActive) {
                Write-Output "  Trigger is inactive - reactivating via API..."
                $activateResult = Invoke-RestMethod `
                    -Uri "$baseUrl/Trigger/Activate?token=$token&jobId=$($job.id)&triggerId=$triggerId" `
                    -Method Get `
                    -ErrorAction SilentlyContinue

                Write-Output "  Activation result: $activateResult"
                Start-Sleep -Seconds $DelaySecondsAfterFix
                $fixedTriggers++
            } else {
                Write-Output "  Trigger already active."
                $activeTriggers++
            }
        }
    }

    Write-Output ""
    Write-Output "Summary: $activeTriggers trigger(s) already active, $fixedTriggers trigger(s) reactivated."

    # Step 4: Send informational alert if any triggers needed reactivation
    if ($fixedTriggers -gt 0) {
        $hostname  = $env:COMPUTERNAME
        $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"

        $emailBody = @"
VisualCron - Webhook triggers reactivated automatically
=======================================================
Server    : $hostname
Time      : $timestamp
Fixed     : $fixedTriggers webhook trigger(s) were found inactive and have been reactivated
Active    : $activeTriggers trigger(s) were already running normally
Group     : $groupName

The monitor job detected and fixed the inactive triggers automatically.
No action is required at this time.

If this notification appears frequently it may indicate a recurring
connection draining problem between the App Gateway and VisualCron.
"@

        Send-AlertEmail `
            -Subject "[$hostname] VisualCron: $fixedTriggers webhook trigger(s) self-healed" `
            -Body    $emailBody
    }
}
catch {
    Write-Error $_
}
finally {
    if ($token) {
        try {
            Invoke-RestMethod -Uri "$baseUrl/logoff?token=$token" -Method Get | Out-Null
            Write-Output "Logged off."
        } catch {
            Write-Warning "Logoff failed: $($_.Exception.Message)"
        }
    }
}
