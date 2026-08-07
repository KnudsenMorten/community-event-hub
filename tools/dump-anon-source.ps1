#Requires -Version 5.1
<#
.SYNOPSIS
    Dump the real names / e-mails / company names of the active edition, READ-ONLY, so the
    documentation-screenshot harness can ASSERT its redaction instead of trusting it.

.DESCRIPTION
    §952 / §957. The screenshot harness publishes to a PUBLIC mirror, so it has to know exactly
    which strings on the page are real data. It does not guess:

      names + companies  -> DIMMED, real and legible (§952c -- the operator's explicit instruction)
      e-mail + phone     -> REPLACED, and then asserted absent before the PNG is written

    This is the "did I remember every name?" tool. The answer must never come from memory, so the
    list is dumped from the same database the pictures are taken of.

    🔒 STRICTLY READ-ONLY -- SELECTs only. Safe to run against PROD, and PROD is the target
    (operator 2026-08-07: "we use only prod"). It is NOT covered by the §957 write exception
    because it does not write.

    ⚠️ Dump from the SAME environment you capture. A DEV dump against a PROD capture asserts
    nothing: none of the real strings would be in the list, so every check would pass while every
    real address stayed on the page. The harness cannot detect that -- only this note can.

    Requires: az CLI signed in with SQL access (see CLAUDE.md -- az needs the PEM export).

.EXAMPLE
    pwsh -File tools/dump-anon-source.ps1 -Env prod
    $env:ANON_SOURCE = "$env:TEMP/ceh-anon-source.json"
#>
[CmdletBinding()]
param(
    [ValidateSet('dev', 'prod')]
    [string]$Env = 'prod',

    # Keep it OUT of the repo: this is a list of every real participant's name and e-mail address,
    # and the repo mirrors publicly.
    [string]$OutFile = (Join-Path $env:TEMP 'ceh-anon-source.json')
)
$ErrorActionPreference = 'Stop'

$server = if ($Env -eq 'prod') { 'eldk27hub-sql-prodpdrq.database.windows.net' }
          else                 { 'eldk27hub-sql-devz237e.database.windows.net' }
$dbName = 'eldk27hub-db'

$eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
$token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv 2>$null
$ErrorActionPreference = $eap
if (-not $token) { throw "could not get an AAD access token for SQL (run 'az login' as a DB-authorized identity)." }

$conn = New-Object System.Data.SqlClient.SqlConnection
$conn.ConnectionString = "Server=tcp:$server,1433;Database=$dbName;Encrypt=True;Connection Timeout=30;"
$conn.AccessToken = $token
$conn.Open()

function Get-Column([string]$sql) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    $out = New-Object System.Collections.Generic.List[string]
    $r = $cmd.ExecuteReader()
    try { while ($r.Read()) { if (-not $r.IsDBNull(0)) { $v = ([string]$r.GetValue(0)).Trim(); if ($v) { $out.Add($v) } } } }
    finally { $r.Close() }
    return $out
}

try {
    # Scoped to the ACTIVE edition, matching what the pages actually render. A past edition's
    # participants are not on screen, and padding the list with them only slows the page pass.
    $people = @()
    $people += Get-Column @"
SELECT DISTINCT p.FullName FROM Participants p
JOIN Events e ON e.Id = p.EventId AND e.IsActive = 1
WHERE p.FullName IS NOT NULL AND LEN(LTRIM(RTRIM(p.FullName))) > 0;
"@
    # Sponsor event coordinators are people who appear on sponsor pages but are NOT participant
    # rows -- dumping only Participants left their names undimmed on the sponsor screenshots.
    $people += Get-Column @"
SELECT DISTINCT LTRIM(RTRIM(ISNULL(s.EventCoordinatorFirstName,'') + ' ' + ISNULL(s.EventCoordinatorLastName,'')))
FROM SponsorInfos s
JOIN Events e ON e.Id = s.EventId AND e.IsActive = 1
WHERE s.EventCoordinatorFirstName IS NOT NULL OR s.EventCoordinatorLastName IS NOT NULL;
"@

    $emails = @()
    foreach ($col in 'Email', 'SecondaryEmail', 'AlternateEmail') {
        $emails += Get-Column @"
SELECT DISTINCT p.$col FROM Participants p
JOIN Events e ON e.Id = p.EventId AND e.IsActive = 1
WHERE p.$col IS NOT NULL AND p.$col LIKE '%@%';
"@
    }
    foreach ($col in 'EventCoordinatorEmail', 'ZohoContactEmail') {
        $emails += Get-Column @"
SELECT DISTINCT s.$col FROM SponsorInfos s
JOIN Events e ON e.Id = s.EventId AND e.IsActive = 1
WHERE s.$col IS NOT NULL AND s.$col LIKE '%@%';
"@
    }

    $companies = @()
    foreach ($col in 'CompanyName', 'EventCoordinatorCompanyName') {
        $companies += Get-Column @"
SELECT DISTINCT s.$col FROM SponsorInfos s
JOIN Events e ON e.Id = s.EventId AND e.IsActive = 1
WHERE s.$col IS NOT NULL AND LEN(LTRIM(RTRIM(s.$col))) > 0;
"@
    }
}
finally { $conn.Close() }

$src = [ordered]@{
    people    = @($people    | Where-Object { $_ } | Sort-Object -Unique)
    emails    = @($emails    | Where-Object { $_ } | Sort-Object -Unique)
    companies = @($companies | Where-Object { $_ } | Sort-Object -Unique)
}

$dir = Split-Path -Parent $OutFile
if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$src | ConvertTo-Json -Depth 4 | Set-Content -Path $OutFile -Encoding utf8

Write-Host "Dumped from $($Env.ToUpper()): $($src.people.Count) people, $($src.emails.Count) e-mails, $($src.companies.Count) companies" -ForegroundColor Green
Write-Host "  -> $OutFile" -ForegroundColor Green
# 🔒 An empty people list is a BROKEN DUMP, not a clean database, and the harness guards against
# the empty-regex case -- but silently capturing 96 undimmed screenshots is the worse outcome.
if (-not $src.people.Count) { throw "no people dumped -- the capture would dim nothing. Check the active event and the connection." }
Write-Host "  `$env:ANON_SOURCE = '$OutFile'" -ForegroundColor Yellow
