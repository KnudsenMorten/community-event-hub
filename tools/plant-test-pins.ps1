#Requires -Version 5.1
<#
.SYNOPSIS
    Plant short-lived login PINs for the DEV organizer so the Playwright
    admin suite (tests/playwright/admin-mobile.spec.ts) can do a REAL PIN
    login without reading a mailbox.

.DESCRIPTION
    DEV-ONLY. Inserts N LoginPin rows for the organizer participant with a
    PBKDF2 hash of a freshly generated random PIN (same parameters as
    PinService: SHA256, 100k iterations, 16-byte salt, 32-byte digest).

    CreatedAt is set a few minutes in the FUTURE so the planted rows always
    win the "newest redeemable PIN" race against the rows the login form's
    "Send my sign-in code" step creates during the test. Expiry stays ~14
    minutes out, matching the product's 15-minute PIN lifetime, so leftovers
    self-expire. Each successful login consumes one row (single-use).

    Requires: az CLI signed in with Key Vault + SQL firewall access.

.EXAMPLE
    $pin = .\tools\plant-test-pins.ps1
    $env:ADMIN_PIN = $pin
    npx playwright test admin-mobile

.EXAMPLE
    # Plant for a sponsor contact (Role 4) so the sponsor-area sweep can log in:
    $pin = .\tools\plant-test-pins.ps1 -OrganizerEmail sponsor@example.com -Role 4 -Count 2
#>
[CmdletBinding()]
param(
    [string]$OrganizerEmail = 'mok@expertslive.dk',
    [int]$Count = 4,
    # ParticipantRole filter: 0=Organizer (default), 4=Sponsor, 5=Attendee...
    [int]$Role = 0
)
$ErrorActionPreference = 'Stop'

$server = 'eldk27hub-sql-devz237e.database.windows.net'
$dbName = 'eldk27hub-db'
$user   = 'eldk27hubadmin'
$vault  = 'kveldk27hubdevz237e'

# --- Generate the PIN + PBKDF2 hash (mirrors PinService.HashPin) ----------
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$pinBytes = New-Object byte[] 4
$rng.GetBytes($pinBytes)
$pin = ('{0:D6}' -f ([BitConverter]::ToUInt32($pinBytes, 0) % 1000000))

$salt = New-Object byte[] 16
$rng.GetBytes($salt)
$pbkdf2 = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
    [System.Text.Encoding]::UTF8.GetBytes($pin), $salt, 100000,
    [System.Security.Cryptography.HashAlgorithmName]::SHA256)
$hash = "$([Convert]::ToBase64String($salt)):$([Convert]::ToBase64String($pbkdf2.GetBytes(32)))"

# --- Insert the rows --------------------------------------------------------
$eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
$pw = az keyvault secret show --vault-name $vault -n sql-admin-password --query value -o tsv 2>$null
$ErrorActionPreference = $eap
if (-not $pw) { throw "could not read sql-admin-password from $vault" }

$cs = "Server=tcp:$server,1433;Database=$dbName;User ID=$user;Password=$pw;Encrypt=True;Connection Timeout=30;"
$conn = New-Object System.Data.SqlClient.SqlConnection($cs)
$conn.Open()
try {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
DECLARE @pid INT = (SELECT TOP 1 p.Id FROM Participants p
                    JOIN Events e ON e.Id = p.EventId AND e.IsActive = 1
                    WHERE p.Email = @email AND p.IsActive = 1 AND p.Role = @role);
IF @pid IS NULL THROW 50000, 'participant not found for that email + role', 1;
DECLARE @i INT = 0;
WHILE @i < @count
BEGIN
    INSERT INTO LoginPins (ParticipantId, PinHash, CreatedAt, ExpiresAt, FailedAttempts)
    VALUES (@pid, @hash,
            DATEADD(MINUTE, 5, SYSDATETIMEOFFSET()),  -- future: outranks form-requested rows
            DATEADD(MINUTE, 14, SYSDATETIMEOFFSET()),
            0);
    SET @i += 1;
END
SELECT @pid;
"@
    [void]$cmd.Parameters.AddWithValue('@email', $OrganizerEmail)
    [void]$cmd.Parameters.AddWithValue('@hash', $hash)
    [void]$cmd.Parameters.AddWithValue('@count', $Count)
    [void]$cmd.Parameters.AddWithValue('@role', $Role)
    $participantId = $cmd.ExecuteScalar()
    Write-Host "Planted $Count PIN row(s) for participant $participantId ($OrganizerEmail, role $Role) on DEV." -ForegroundColor Green
    Write-Host "PIN (valid ~14 min, single-use each):" -ForegroundColor Green
}
finally { $conn.Close() }

# The PIN is the pipeline output so callers can do $env:ADMIN_PIN = (& script).
$pin
