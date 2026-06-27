#Requires -Version 5.1
<#
.SYNOPSIS
    Stand up a LOCAL, synthetic "Demo Community Conf" instance for headless
    screenshot capture (docs/img). LOCAL-ONLY: never touches the Azure dev/prod
    databases. No real customer or personal data.

.DESCRIPTION
    1. Applies tools/seed-demo.sql to a local SQL Server (default .\SQLEXPRESS,
       database CommunityHubDemo) using Windows / integrated auth. The web app
       must have created the schema first (it auto-applies EF migrations on
       startup; point Sql:ConnectionStringTemplate at the same DB and run it).
    2. Plants short-lived login PINs (PBKDF2: SHA256, 100k iterations, 16-byte
       salt, 32-byte digest — identical to PinService) for each role account so
       the Playwright login flow can sign in with a real PIN. CreatedAt is set a
       few minutes in the FUTURE so a planted PIN out-ranks the row the login
       form creates, and ExpiresAt ~14 min out so leftovers self-expire.

    Prints the email/role/PIN table so a capture run can export them as the
    SPEAKER_PIN / VOLUNTEER_PIN / ... env vars the Playwright harness reads.

.EXAMPLE
    .\tools\seed-demo.ps1
.EXAMPLE
    .\tools\seed-demo.ps1 -Server '.\SQLEXPRESS' -Database CommunityHubDemo
#>
[CmdletBinding()]
param(
    [string]$Server   = '.\SQLEXPRESS',
    [string]$Database  = 'CommunityHubDemo',
    [int]$Count = 4
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName 'System.Data'

$seedSql = Join-Path $PSScriptRoot 'seed-demo.sql'
if (-not (Test-Path $seedSql)) { throw "seed file not found: $seedSql" }

$cs = "Server=$Server;Database=$Database;Trusted_Connection=True;TrustServerCertificate=True;Encrypt=False;"
$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()
try {
    # --- 1. Apply the synthetic data seed (idempotent) --------------------------
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = (Get-Content -LiteralPath $seedSql -Raw)
    $cmd.CommandTimeout = 120
    [void]$cmd.ExecuteNonQuery()
    Write-Host "Applied seed-demo.sql to $Server/$Database." -ForegroundColor Green

    # --- 2. Plant login PINs for each role account -----------------------------
    $roles = @(
        @{ Email='organizer@democonf.example.com'; Role=0; Label='Organizer' },
        @{ Email='speaker@democonf.example.com';   Role=1; Label='Speaker'   },
        @{ Email='volunteer@democonf.example.com'; Role=3; Label='Volunteer' },
        @{ Email='sponsor@democonf.example.com';   Role=4; Label='Sponsor'   },
        @{ Email='attendee@democonf.example.com';  Role=5; Label='Attendee'  }
    )

    $results = foreach ($r in $roles) {
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $pinBytes = New-Object byte[] 4; $rng.GetBytes($pinBytes)
        $pin = ('{0:D6}' -f ([BitConverter]::ToUInt32($pinBytes,0) % 1000000))
        $salt = New-Object byte[] 16; $rng.GetBytes($salt)
        $pbkdf2 = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
            [System.Text.Encoding]::UTF8.GetBytes($pin), $salt, 100000,
            [System.Security.Cryptography.HashAlgorithmName]::SHA256)
        $hash = "$([Convert]::ToBase64String($salt)):$([Convert]::ToBase64String($pbkdf2.GetBytes(32)))"

        $c = $conn.CreateCommand()
        $c.CommandText = @"
DECLARE @pid INT = (SELECT TOP 1 p.Id FROM Participants p
                    JOIN Events e ON e.Id=p.EventId AND e.IsActive=1
                    WHERE p.Email=@email AND p.IsActive=1 AND p.Role=@role);
IF @pid IS NULL THROW 50000,'participant not found',1;
DECLARE @i INT=0;
WHILE @i<@count BEGIN
    INSERT INTO LoginPins (ParticipantId,PinHash,CreatedAt,ExpiresAt,FailedAttempts)
    VALUES (@pid,@hash,DATEADD(MINUTE,5,SYSDATETIMEOFFSET()),DATEADD(MINUTE,14,SYSDATETIMEOFFSET()),0);
    SET @i+=1; END
SELECT @pid;
"@
        [void]$c.Parameters.AddWithValue('@email',$r.Email)
        [void]$c.Parameters.AddWithValue('@role',$r.Role)
        [void]$c.Parameters.AddWithValue('@hash',$hash)
        [void]$c.Parameters.AddWithValue('@count',$Count)
        $partId = $c.ExecuteScalar()
        [pscustomobject]@{ Role=$r.Label; Email=$r.Email; ParticipantId=$partId; Pin=$pin }
    }
    Write-Host "Planted $Count PIN row(s) per role (valid ~14 min, single-use each)." -ForegroundColor Green
    $results | Format-Table -AutoSize
}
finally { $conn.Close() }
