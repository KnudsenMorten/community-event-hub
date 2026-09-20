<#
.SYNOPSIS
    Capture or apply a Community Event Hub environment's FEATURE BASELINE — which features are
    on or off, and which ring each is released to — as a versionable JSON file.

.DESCRIPTION
    A fresh hub runs perfectly well with no baseline at all: any feature with no stored row falls
    back to its built-in catalog default (advanced features default to OFF). This script exists for
    the case that default does not cover — REPEATABILITY:

      * standing up a second environment (test / staging) that must match an existing one,
      * a community forking the platform and wanting a known starting posture,
      * answering "what is this environment actually set to?" after months of ad-hoc toggling.

    Without it, the only way to set the posture is the organizer Settings page, one switch at a
    time, across dozens of features.

    THREE MODES, and only one of them writes:

      -Export            Read the current posture into a JSON file. Writes nothing to the database.
      -Apply  -Path x    Make the environment match the file.
      -Apply  -WhatIf    Print exactly what -Apply would change, and write nothing.

.NOTES
    ── SAFETY, AND ITS LIMITS ────────────────────────────────────────────────────────────────────
    * NEVER DELETES. Only inserts and updates. A feature present in the database but absent from
      the file is listed and LEFT ALONE — the file is a baseline, not an authority on everything.
    * IDEMPOTENT. Re-running an apply that has already been applied changes nothing and says so.
    * ONE WRITER AT A TIME. The organizer Settings page writes this same table. Do not run an
      apply while somebody is editing settings in the UI; the last writer wins and neither is told.

    🔴 ENABLING A FEATURE CAN START SENDING E-MAIL. Many hub features send mail as part of their
       normal operation, so applying a baseline to a LIVE environment can put real mail in front of
       real people within minutes.

       ⚠️ This script CANNOT tell you which features those are. The mapping from feature to mail
       lives in the application's own template catalog, not in the database, so a standalone script
       would have to duplicate it — and a duplicate that drifts is worse than no answer at all.
       What it does instead is refuse to enable anything unless you pass -AllowEnable, and print
       every feature it is about to switch ON so you see the list before it happens.

       ⇒ On a live environment: run -WhatIf first. Always.

    ── AUTHENTICATION ────────────────────────────────────────────────────────────────────────────
    Pass -ConnectionString for full control, or -Server/-Database plus ONE of:

      -SqlUser / -SqlPassword   a SQL login.
      -AccessToken              an Entra token for https://database.windows.net/, e.g.
                                az account get-access-token --resource https://database.windows.net/ `
                                    --query accessToken -o tsv

    ⚠️ There is deliberately no "just use my Windows/Entra login" mode. This script talks to SQL
    through System.Data.SqlClient, which does NOT support the `Authentication=Active Directory ...`
    connection-string keywords — it fails with "Keyword not supported: 'authentication'", which
    reads like a broken script rather than a missing credential. Asking for a token explicitly is
    one more step and one less confusing failure.

.PARAMETER EventId
    The edition (Event row) whose posture to read or write. Features are stored per edition.

.EXAMPLE
    ./Set-FeatureBaseline.ps1 -Export -Server <your-sql-host> -Database hubdb `
        -EventId 1 -Path baseline.json
    Capture the current posture so it can be committed and reviewed.

.EXAMPLE
    ./Set-FeatureBaseline.ps1 -Apply -Path baseline.json -Server <your-sql-host> `
        -Database hubdb -EventId 1 -WhatIf
    Show what would change. Writes nothing. Do this first.

.EXAMPLE
    ./Set-FeatureBaseline.ps1 -Apply -Path baseline.json -Server <your-sql-host> `
        -Database hubdb -EventId 1 -AllowEnable
    Apply it, including switching features ON.
#>

#Requires -Version 7.0

[CmdletBinding(DefaultParameterSetName = 'Export')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Export')]
    [switch]$Export,

    [Parameter(Mandatory, ParameterSetName = 'Apply')]
    [switch]$Apply,

    # The baseline file: written by -Export, read by -Apply.
    [Parameter(Mandatory)]
    [string]$Path,

    [int]$EventId = 1,

    [string]$ConnectionString,
    [string]$Server,
    [string]$Database,
    [string]$SqlUser,
    [string]$SqlPassword,
    # An Entra access token for https://database.windows.net/ (alternative to integrated auth).
    [string]$AccessToken,

    # Stamped into LastUpdatedByEmail so the audit trail shows the change came from this script.
    [string]$StampAs = 'feature-baseline-script',

    # Required before any feature is switched ON. See the e-mail warning in .NOTES.
    [switch]$AllowEnable,

    # With -Apply: print the plan and write nothing.
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Ring values, matching the application's Ring enum. Kept here only to make the file readable —
# the script stores and compares the integer, so an added ring does not break it.
$RingNames = @{ 0 = 'Ring0'; 1 = 'Ring1'; 2 = 'Ring2'; 3 = 'Broad' }
function Format-Ring([int]$v) { if ($RingNames.ContainsKey($v)) { "$($RingNames[$v]) ($v)" } else { "Ring $v" } }

# ---------------------------------------------------------------------------------------------
# Connection
# ---------------------------------------------------------------------------------------------
function New-SqlConnection {
    if ($ConnectionString) {
        $cs = $ConnectionString
    }
    else {
        if (-not $Server -or -not $Database) {
            throw "Provide -ConnectionString, or both -Server and -Database."
        }
        $cs = "Server=tcp:$Server,1433;Initial Catalog=$Database;Encrypt=True;" +
              "TrustServerCertificate=False;Connect Timeout=30;"
        if ($SqlUser) {
            if (-not $SqlPassword) { throw "-SqlUser was given without -SqlPassword." }
            $cs += "User ID=$SqlUser;Password=$SqlPassword;"
        }
        elseif (-not $AccessToken) {
            # 🔒 Fail HERE with an actionable message rather than letting SqlClient throw
            # "Keyword not supported: 'authentication'" further down. System.Data.SqlClient has no
            # Entra connection-string mode, so there is no silent fallback to offer.
            throw "No credential. Pass -SqlUser/-SqlPassword, or -AccessToken, or a full " +
                  "-ConnectionString. For Entra: " +
                  "az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv"
        }
    }

    $conn = [System.Data.SqlClient.SqlConnection]::new($cs)
    if ($AccessToken) { $conn.AccessToken = $AccessToken }
    $conn.Open()
    return $conn
}

function Invoke-Reader($conn, [string]$sql, [hashtable]$p = @{}) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    foreach ($k in $p.Keys) {
        $val = $p[$k]
        [void]$cmd.Parameters.AddWithValue("@$k", $(if ($null -eq $val) { [DBNull]::Value } else { $val }))
    }
    $rows = @()
    $r = $cmd.ExecuteReader()
    try {
        while ($r.Read()) {
            $row = [ordered]@{}
            for ($i = 0; $i -lt $r.FieldCount; $i++) {
                $row[$r.GetName($i)] = $(if ($r.IsDBNull($i)) { $null } else { $r.GetValue($i) })
            }
            $rows += [pscustomobject]$row
        }
    }
    finally { $r.Close() }
    return , $rows
}

function Invoke-NonQuery($conn, [string]$sql, [hashtable]$p = @{}) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $sql
    foreach ($k in $p.Keys) {
        $val = $p[$k]
        [void]$cmd.Parameters.AddWithValue("@$k", $(if ($null -eq $val) { [DBNull]::Value } else { $val }))
    }
    return $cmd.ExecuteNonQuery()
}

function Get-CurrentPosture($conn) {
    Invoke-Reader $conn @'
SELECT FeatureKey, Enabled, ReleasedToRing, ReleasedToRingOverride
FROM   FeatureSettings
WHERE  EventId = @eventId
ORDER  BY FeatureKey
'@ @{ eventId = $EventId }
}

function Assert-EventExists($conn) {
    $rows = Invoke-Reader $conn 'SELECT Id FROM Events WHERE Id = @id' @{ id = $EventId }
    if ($rows.Count -eq 0) {
        throw "No Event row with Id $EventId in this database. Features are stored per edition, " +
              "so there is nothing to baseline. Check -EventId."
    }
}

# ---------------------------------------------------------------------------------------------
# EXPORT
# ---------------------------------------------------------------------------------------------
if ($Export) {
    $conn = New-SqlConnection
    try {
        Assert-EventExists $conn
        $rows = Get-CurrentPosture $conn
    }
    finally { $conn.Dispose() }

    $doc = [ordered]@{
        '_schema'      = 'ceh/feature-baseline@1'
        '_doc'         = 'Feature posture for one edition. Applied with Set-FeatureBaseline.ps1 ' +
                         '-Apply. A feature absent from this file is left untouched; nothing is ever deleted.'
        '_exportedUtc' = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        '_ringValues'  = 'ReleasedToRing: 0=Ring0, 1=Ring1, 2=Ring2, 3=Broad'
        'features'     = @($rows | ForEach-Object {
            [ordered]@{
                key                    = $_.FeatureKey
                enabled                = [bool]$_.Enabled
                releasedToRing         = [int]$_.ReleasedToRing
                releasedToRingOverride = $(if ($null -eq $_.ReleasedToRingOverride) { $null } else { [int]$_.ReleasedToRingOverride })
            }
        })
    }

    $doc | ConvertTo-Json -Depth 6 | Set-Content -Path $Path -Encoding utf8
    Write-Host ">> Exported $($rows.Count) stored feature setting(s) for event $EventId to $Path"
    if ($rows.Count -eq 0) {
        Write-Host ">> NOTE: no stored rows. That is normal for a fresh environment — every feature"
        Write-Host "         is running on its built-in catalog default. An empty baseline is a valid"
        Write-Host "         starting point; add entries for the features you want pinned."
    }
    return
}

# ---------------------------------------------------------------------------------------------
# APPLY
# ---------------------------------------------------------------------------------------------
if (-not (Test-Path $Path)) { throw "Baseline file not found: $Path" }

$baseline = Get-Content -Path $Path -Raw | ConvertFrom-Json
if (-not $baseline.PSObject.Properties.Name.Contains('features')) {
    throw "$Path has no 'features' array — is it a baseline file? Expected schema ceh/feature-baseline@1."
}

$wanted = @($baseline.features)
if ($wanted.Count -eq 0) {
    Write-Host ">> The baseline lists no features. Nothing to do."
    return
}

$dupes = $wanted | Group-Object key | Where-Object Count -gt 1
if ($dupes) {
    throw "The baseline lists these keys more than once, so the result would depend on order: " +
          ($dupes.Name -join ', ')
}

# 🔒 Validate the WHOLE file before opening a connection. A bad baseline should cost nothing and
# touch nothing — and an error that arrives after the connection reads like a database problem.
for ($n = 0; $n -lt $wanted.Count; $n++) {
    $f = $wanted[$n]
    if (-not $f.PSObject.Properties.Name.Contains('key') -or [string]::IsNullOrWhiteSpace($f.key)) {
        throw "Baseline entry #$($n + 1) has no 'key'."
    }
    if ($null -ne $f.releasedToRing -and ([int]$f.releasedToRing -lt 0)) {
        throw "Baseline entry '$($f.key)' has a negative releasedToRing."
    }
}

$conn = New-SqlConnection
try {
    Assert-EventExists $conn
    $current = @{}
    foreach ($row in Get-CurrentPosture $conn) { $current[$row.FeatureKey] = $row }

    $toInsert = @(); $toUpdate = @(); $unchanged = @(); $willEnable = @()

    foreach ($f in $wanted) {

        $ring = $(if ($null -ne $f.releasedToRing) { [int]$f.releasedToRing } else { 3 })
        $override = $(if ($null -ne $f.releasedToRingOverride) { [int]$f.releasedToRingOverride } else { $null })
        $enabled = [bool]$f.enabled

        if (-not $current.ContainsKey($f.key)) {
            $toInsert += [pscustomobject]@{ Key = $f.key; Enabled = $enabled; Ring = $ring; Override = $override }
            if ($enabled) { $willEnable += $f.key }
        }
        else {
            $c = $current[$f.key]
            $cOverride = $(if ($null -eq $c.ReleasedToRingOverride) { $null } else { [int]$c.ReleasedToRingOverride })
            $same = ([bool]$c.Enabled -eq $enabled) -and ([int]$c.ReleasedToRing -eq $ring) -and ($cOverride -eq $override)
            if ($same) {
                $unchanged += $f.key
            }
            else {
                $toUpdate += [pscustomobject]@{
                    Key = $f.key; Enabled = $enabled; Ring = $ring; Override = $override
                    WasEnabled = [bool]$c.Enabled; WasRing = [int]$c.ReleasedToRing
                }
                if ($enabled -and -not [bool]$c.Enabled) { $willEnable += $f.key }
            }
        }
    }

    $notInFile = @($current.Keys | Where-Object { $_ -notin ($wanted.key) })

    # --- the plan ---------------------------------------------------------------------------
    Write-Host ""
    Write-Host "Feature baseline -> event $EventId"
    Write-Host "  file      : $Path"
    Write-Host "  create    : $($toInsert.Count)"
    Write-Host "  update    : $($toUpdate.Count)"
    Write-Host "  unchanged : $($unchanged.Count)"
    Write-Host "  in the database but NOT in the file (left untouched): $($notInFile.Count)"
    foreach ($k in ($notInFile | Sort-Object)) { Write-Host "      $k" }

    if ($toInsert) {
        Write-Host ""
        Write-Host "  CREATE"
        foreach ($i in ($toInsert | Sort-Object Key)) {
            Write-Host ("      {0,-42} enabled={1,-5} ring={2}" -f $i.Key, $i.Enabled, (Format-Ring $i.Ring))
        }
    }
    if ($toUpdate) {
        Write-Host ""
        Write-Host "  UPDATE"
        foreach ($u in ($toUpdate | Sort-Object Key)) {
            Write-Host ("      {0,-42} enabled {1} -> {2}   ring {3} -> {4}" -f `
                $u.Key, $u.WasEnabled, $u.Enabled, (Format-Ring $u.WasRing), (Format-Ring $u.Ring))
        }
    }

    if ($willEnable) {
        Write-Host ""
        Write-Host "  !! WOULD SWITCH ON $($willEnable.Count) FEATURE(S):" -ForegroundColor Yellow
        foreach ($k in ($willEnable | Sort-Object)) { Write-Host "      $k" -ForegroundColor Yellow }
        Write-Host "     Some hub features SEND E-MAIL as part of normal operation. On a live" -ForegroundColor Yellow
        Write-Host "     environment this can put real mail in front of real people within minutes." -ForegroundColor Yellow
    }

    if ($WhatIf) {
        Write-Host ""
        Write-Host ">> -WhatIf: nothing was written."
        return
    }

    if ($willEnable -and -not $AllowEnable) {
        Write-Host ""
        throw "Refusing to switch features ON without -AllowEnable (see the list above). " +
              "Re-run with -WhatIf to review, then add -AllowEnable when you are satisfied."
    }

    if (-not $toInsert -and -not $toUpdate) {
        Write-Host ""
        Write-Host ">> Already matches the baseline. Nothing written."
        return
    }

    # --- write ------------------------------------------------------------------------------
    # One transaction: a half-applied baseline is a posture nobody chose.
    $tx = $conn.BeginTransaction()
    try {
        $now = [DateTimeOffset]::UtcNow
        foreach ($i in $toInsert) {
            $cmd = $conn.CreateCommand(); $cmd.Transaction = $tx
            $cmd.CommandText = @'
INSERT INTO FeatureSettings (EventId, FeatureKey, Enabled, ReleasedToRing, ReleasedToRingOverride,
                             UpdatedAt, LastUpdatedByEmail)
VALUES (@eventId, @key, @enabled, @ring, @override, @now, @by)
'@
            [void]$cmd.Parameters.AddWithValue('@eventId', $EventId)
            [void]$cmd.Parameters.AddWithValue('@key', $i.Key)
            [void]$cmd.Parameters.AddWithValue('@enabled', $i.Enabled)
            [void]$cmd.Parameters.AddWithValue('@ring', $i.Ring)
            [void]$cmd.Parameters.AddWithValue('@override', $(if ($null -eq $i.Override) { [DBNull]::Value } else { $i.Override }))
            [void]$cmd.Parameters.AddWithValue('@now', $now)
            [void]$cmd.Parameters.AddWithValue('@by', $StampAs)
            [void]$cmd.ExecuteNonQuery()
        }
        foreach ($u in $toUpdate) {
            $cmd = $conn.CreateCommand(); $cmd.Transaction = $tx
            $cmd.CommandText = @'
UPDATE FeatureSettings
SET    Enabled = @enabled, ReleasedToRing = @ring, ReleasedToRingOverride = @override,
       UpdatedAt = @now, LastUpdatedByEmail = @by
WHERE  EventId = @eventId AND FeatureKey = @key
'@
            [void]$cmd.Parameters.AddWithValue('@eventId', $EventId)
            [void]$cmd.Parameters.AddWithValue('@key', $u.Key)
            [void]$cmd.Parameters.AddWithValue('@enabled', $u.Enabled)
            [void]$cmd.Parameters.AddWithValue('@ring', $u.Ring)
            [void]$cmd.Parameters.AddWithValue('@override', $(if ($null -eq $u.Override) { [DBNull]::Value } else { $u.Override }))
            [void]$cmd.Parameters.AddWithValue('@now', $now)
            [void]$cmd.Parameters.AddWithValue('@by', $StampAs)
            [void]$cmd.ExecuteNonQuery()
        }
        $tx.Commit()
    }
    catch {
        $tx.Rollback()
        throw
    }

    Write-Host ""
    Write-Host ">> Applied. Created $($toInsert.Count), updated $($toUpdate.Count)."
    Write-Host ">> The application reads these live — no restart or redeploy is needed."
}
finally { $conn.Dispose() }
