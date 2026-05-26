#requires -version 5.1
<#
    seed-dev.ps1
    ------------------------------------------------------------------
    Seeds the dev database with one Event row + 5 test Participants
    (one per role). Idempotent: re-runs UPDATE in place. Reads the
    SQL password from the env var ELDKHUB_SQL_ADMIN_PASSWORD (the
    same one deploy.sh / set-secrets.sh use).
#>

param(
    [string]$Server   = 'eldk27hub-sql-devz237e.database.windows.net',
    [string]$Database = 'eldk27hub-db',
    [string]$User     = 'eldk27hubadmin'
)

$ErrorActionPreference = 'Stop'

$Password = $env:ELDKHUB_SQL_ADMIN_PASSWORD
if ([string]::IsNullOrWhiteSpace($Password)) {
    throw "ELDKHUB_SQL_ADMIN_PASSWORD env var is not set."
}

Add-Type -AssemblyName 'System.Data'

$cs = "Server=tcp:$Server,1433;Initial Catalog=$Database;User ID=$User;Password=$Password;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()

function Exec([string]$Sql) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $Sql
    $cmd.CommandTimeout = 60
    [void]$cmd.ExecuteNonQuery()
}

function Scalar([string]$Sql) {
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $Sql
    $cmd.CommandTimeout = 60
    return $cmd.ExecuteScalar()
}

# --- Event ---------------------------------------------------------------
Exec @"
IF NOT EXISTS (SELECT 1 FROM Events WHERE Code = 'ELDK27')
BEGIN
    INSERT INTO Events
        (CommunityName, Code, DisplayName, StartDate, EndDate, PreDayDate,
         VenueName, HubHostname, IsActive, LockDate, CreatedAt)
    VALUES
        (N'Experts Live Denmark', N'ELDK27',
         N'Experts Live Denmark 2027',
         '2027-05-26', '2027-05-27', '2027-05-25',
         N'Copenhagen',
         N'eldk27.eventhub.expertslive.dk',
         1, NULL, SYSDATETIMEOFFSET());
END
ELSE
BEGIN
    UPDATE Events
       SET CommunityName = N'Experts Live Denmark',
           DisplayName   = N'Experts Live Denmark 2027',
           IsActive      = 1
     WHERE Code = 'ELDK27';
END
"@

$eventId = [int](Scalar "SELECT Id FROM Events WHERE Code = 'ELDK27'")
Write-Host "Event 'ELDK27' = Id $eventId"

# --- Participants --------------------------------------------------------
$people = @(
    @{ Email='mok@expertslive.dk';            Name='MOK (Organizer)';  Role=0 }, # Organizer
    @{ Email='mok@mortenknudsen.net';         Name='MOK (Speaker)';    Role=1 }, # Speaker
    @{ Email='mortenknudsen1974@gmail.com';   Name='MOK (Attendee)';   Role=5 }, # Attendee
    @{ Email='mok@2linkit.net';               Name='MOK (Sponsor)';    Role=4 }, # Sponsor
    @{ Email='knudsen_morten@hotmail.com';    Name='MOK (Volunteer)';  Role=3 }  # Volunteer
)

foreach ($p in $people) {
    $emailEsc = $p.Email -replace "'", "''"
    $nameEsc  = $p.Name  -replace "'", "''"
    $role     = $p.Role
    Exec @"
IF NOT EXISTS (SELECT 1 FROM Participants WHERE EventId = $eventId AND Email = N'$emailEsc')
BEGIN
    INSERT INTO Participants (EventId, Email, FullName, Phone, Role, SponsorCompanyId, IsActive, CreatedAt)
    VALUES ($eventId, N'$emailEsc', N'$nameEsc', NULL, $role, NULL, 1, SYSDATETIMEOFFSET());
END
ELSE
BEGIN
    UPDATE Participants
       SET FullName = N'$nameEsc',
           Role     = $role,
           IsActive = 1
     WHERE EventId = $eventId AND Email = N'$emailEsc';
END
"@
    Write-Host ("  seeded: {0,-35} role={1}" -f $p.Email, $p.Role)
}

$conn.Close()
Write-Host "Done."
