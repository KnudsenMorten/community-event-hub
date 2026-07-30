<#
.SYNOPSIS
  Post-deploy smoke probe for the welcome/e-mail send pipeline (REQUIREMENTS §255).

.DESCRIPTION
  Two modes against the dev or prod SQL database (deployment-SPN AAD token, same
  auth as tools/deploy-app.ps1):

  PASSIVE (default) — asserts the pipeline is not stuck: every ring-eligible,
  active, welcome-eligible participant either has a welcome ledger row or was
  created less than -GraceMinutes ago. Also prints the latest EmailLogs /
  SentReminders activity so "is anything sending at all?" is one command.

  -Fire — active end-to-end probe: clears the welcome ledger + stamps for the
  given -Emails (defaults to the operator's Ring-1 test accounts), then polls
  SentReminders for fresh welcome rows until -TimeoutMinutes. PASS when every
  reset account has been re-welcomed. Sends REAL mail to those accounts — they
  must be operator-controlled mailboxes. Rings are never modified.

.NOTES
  Exit code 0 = PASS, 1 = FAIL/stuck. Run after every deploy (hosted-smoke gate).
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')] [string] $Env = 'prod',
    [switch] $Fire,
    [string[]] $Emails = @(
        'mok@mortenknudsen.net',        # speaker
        'knudsen_morten@hotmail.com',   # crew
        'mok@2linkit.net',              # crew
        'mortenknudsen1974@gmail.com'   # 2-day attendee (MC-selection welcome)
    ),
    [int] $GraceMinutes = 30,
    [int] $TimeoutMinutes = 20,
    [string] $AzureConfigDir = 'C:\work\.azcfg-eldk-deploy'
)

$ErrorActionPreference = 'Stop'
$env:AZURE_CONFIG_DIR = $AzureConfigDir

$server = if ($Env -eq 'prod') { 'eldk27hub-sql-prodpdrq.database.windows.net' }
          else                 { 'eldk27hub-sql-devz237e.database.windows.net' }
$db = 'eldk27hub-db'

function Get-SqlToken {
    # SQL AAD tokens need the trailing-slash audience; without it the login is
    # rejected as '<token-identified principal>'.
    az account get-access-token --resource 'https://database.windows.net/' --query accessToken -o tsv
}

function Invoke-Db([string] $Query) {
    Invoke-Sqlcmd -ServerInstance $server -Database $db -AccessToken (Get-SqlToken) -Query $Query
}

$emailList = ($Emails | ForEach-Object { "N'" + $_.Replace("'", "''") + "'" }) -join ','

Write-Host ">> Welcome-pipeline smoke [$Env] against $server" -ForegroundColor Cyan

if ($Fire) {
    Write-Host ">> FIRE: resetting welcome state for $($Emails.Count) probe account(s) (real mail will be sent)..."
    Invoke-Db @"
DECLARE @ev INT = (SELECT TOP 1 Id FROM Events WHERE IsActive = 1);
DELETE FROM SentReminders WHERE EventId = @ev AND ReminderType = N'welcome' AND RecipientEmail IN ($emailList);
UPDATE Participants SET WelcomeWithLoginSentAt = NULL WHERE EventId = @ev AND IsActive = 1 AND Email IN ($emailList);
UPDATE Attendees SET MasterClassInviteSentAt = NULL WHERE EventId = @ev AND MirrorState = 0 AND TicketStatus = 1 AND Email IN ($emailList);
"@ | Out-Null

    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    do {
        Start-Sleep -Seconds 60
        $rows = @(Invoke-Db @"
DECLARE @ev INT = (SELECT TOP 1 Id FROM Events WHERE IsActive = 1);
SELECT RecipientEmail FROM SentReminders
WHERE EventId = @ev AND ReminderType = N'welcome' AND RecipientEmail IN ($emailList)
UNION
SELECT a.Email FROM Attendees a
WHERE a.EventId = @ev AND a.MirrorState = 0 AND a.TicketStatus = 1
  AND a.Email IN ($emailList) AND a.MasterClassInviteSentAt IS NOT NULL;
"@)
        $done = @($rows | ForEach-Object { $_.RecipientEmail }) | Sort-Object -Unique
        Write-Host (">> {0:HH:mm:ss} re-welcomed {1}/{2}: {3}" -f (Get-Date), $done.Count, $Emails.Count, ($done -join ', '))
    } until ($done.Count -ge $Emails.Count -or (Get-Date) -gt $deadline)

    if ($done.Count -ge $Emails.Count) { Write-Host '>> PASS — every probe account was re-welcomed.' -ForegroundColor Green; exit 0 }
    $missing = $Emails | Where-Object { $_ -notin $done }
    Write-Host ">> FAIL — not re-welcomed within $TimeoutMinutes min: $($missing -join ', ')" -ForegroundColor Red
    exit 1
}

# PASSIVE probe.
$stuck = @(Invoke-Db @"
DECLARE @ev INT = (SELECT TOP 1 Id FROM Events WHERE IsActive = 1);
SELECT p.Email, p.Role, p.Ring, CONVERT(varchar(19), p.CreatedAt, 120) AS CreatedAt
FROM Participants p
WHERE p.EventId = @ev AND p.IsActive = 1 AND p.Ring <= 1 AND p.Role <> 0  -- organizers get no welcome by design
  AND p.CreatedAt < DATEADD(minute, -$GraceMinutes, SYSUTCDATETIME())
  AND NOT EXISTS (SELECT 1 FROM SentReminders s
                  WHERE s.EventId = @ev AND s.ReminderType = N'welcome' AND s.RecipientEmail = p.Email)
  -- 2-day attendee welcomes stamp the mirror row (MC-selection invite), not the reminder ledger
  AND NOT EXISTS (SELECT 1 FROM Attendees a
                  WHERE a.EventId = @ev AND a.MirrorState = 0 AND a.TicketStatus = 1
                    AND a.Email = p.Email AND a.MasterClassInviteSentAt IS NOT NULL);
"@)

Write-Host '>> Latest send activity:'
Invoke-Db @"
DECLARE @ev INT = (SELECT TOP 1 Id FROM Events WHERE IsActive = 1);
SELECT TOP 5 CONVERT(varchar(19), SentAt, 120) AS SentAt, ToEmail, Success FROM EmailLogs WHERE EventId = @ev ORDER BY SentAt DESC;
"@ | Format-Table -AutoSize | Out-String -Width 160 | Write-Host

if ($stuck.Count -eq 0) {
    Write-Host ">> PASS — no ring-eligible participant is stuck without a welcome (grace $GraceMinutes min)." -ForegroundColor Green
    exit 0
}
Write-Host ">> FAIL — $($stuck.Count) participant(s) eligible but never welcomed:" -ForegroundColor Red
$stuck | Format-Table -AutoSize | Out-String -Width 160 | Write-Host
exit 1
