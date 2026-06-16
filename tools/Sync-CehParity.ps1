#requires -version 5.1
<#
.SYNOPSIS
    Make a CommunityHub environment's data match the canonical dev seed.

.DESCRIPTION
    Re-runnable parity tool. It brings any environment (dev or prod) to the
    SAME data shape from a single source of truth -- `scripts/seed-eldk27.sql`:

      * the ELDK27 Event row,
      * the four real organizers (mok / mb / kea / mlh @expertslive.dk,
        IsTestUser = 0),
      * the seeded sponsor + speaker sample tasks,
      * the four role-coverage test users plus the example masterclass speaker,
        all flagged IsTestUser = 1 so prod-vs-test state is distinguishable and
        go-live cleanup can remove them with `WHERE [IsTestUser] = 1`.

    The ONLY intended dev/prod difference is the email flow (dev/local redirects
    every outbound mail to one test inbox; prod uses a send allowlist). That
    difference is environment configuration, not data, so this tool does not
    touch it -- the tool's only write is the canonical data seed below.

    Idempotent: re-running inserts nothing twice and re-asserts IsTestUser /
    IsActive on the seeded rows (so a run after the IsTestUser migration tags
    rows that pre-dated the column).

    Secret hygiene: no server name, vault name or password is hard-coded for
    prod. Supply the SQL admin password via -SqlPassword, the
    CEH_SQL_ADMIN_PASSWORD env var, or -KeyVault <name> (read at runtime from
    that env's Key Vault secret `sql-admin-password` via the az CLI). The
    target server / database / user are parameters with no prod default.

.PARAMETER Env
    Logical environment label used only for messages (dev | prod).

.PARAMETER Server
    Target Azure SQL server FQDN, e.g. <name>.database.windows.net.

.PARAMETER Database
    Target database name.

.PARAMETER User
    SQL admin login.

.PARAMETER KeyVault
    Optional Key Vault name to read the `sql-admin-password` secret from
    (requires an az CLI session with Key Vault read + SQL firewall access).

.PARAMETER SqlPassword
    SQL admin password (overrides KeyVault / env var). Prefer KeyVault or the
    CEH_SQL_ADMIN_PASSWORD env var so the secret is never typed on a command line.

.PARAMETER ApplyMigrations
    Run `dotnet ef database update` against the target first so the schema
    (including the IsTestUser column) exists before seeding.

.PARAMETER WhatIf
    Show what would run without connecting or changing anything.

.EXAMPLE
    # DEV (password from the dev Key Vault):
    ./tools/Sync-CehParity.ps1 -Env dev `
        -Server <devsql>.database.windows.net -Database <devdb> -User <admin> `
        -KeyVault <devkv> -ApplyMigrations

.EXAMPLE
    # PROD (password from the prod Key Vault), preview first:
    ./tools/Sync-CehParity.ps1 -Env prod `
        -Server <prodsql>.database.windows.net -Database <proddb> -User <admin> `
        -KeyVault <prodkv> -WhatIf
    ./tools/Sync-CehParity.ps1 -Env prod `
        -Server <prodsql>.database.windows.net -Database <proddb> -User <admin> `
        -KeyVault <prodkv> -ApplyMigrations
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('dev', 'prod')]
    [string]$Env = 'dev',

    [Parameter(Mandatory = $true)]
    [string]$Server,

    [Parameter(Mandatory = $true)]
    [string]$Database,

    [Parameter(Mandatory = $true)]
    [string]$User,

    [string]$KeyVault,

    [string]$SqlPassword,

    [switch]$ApplyMigrations
)

$ErrorActionPreference = 'Stop'

$repoRoot  = Split-Path -Parent $PSScriptRoot
$seedFile  = Join-Path $repoRoot 'scripts/seed-eldk27.sql'
$coreProj  = Join-Path $repoRoot 'src/CommunityHub.Core'
$startProj = Join-Path $repoRoot 'src/CommunityHub'

if (-not (Test-Path $seedFile)) { throw "seed file not found: $seedFile" }

# --- Resolve the SQL admin password ---------------------------------------
if ([string]::IsNullOrWhiteSpace($SqlPassword)) {
    $SqlPassword = $env:CEH_SQL_ADMIN_PASSWORD
}
if ([string]::IsNullOrWhiteSpace($SqlPassword) -and $KeyVault) {
    Write-Host "Reading sql-admin-password from Key Vault '$KeyVault'..."
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    $SqlPassword = az keyvault secret show --vault-name $KeyVault -n sql-admin-password --query value -o tsv 2>$null
    $ErrorActionPreference = $eap
}
if ([string]::IsNullOrWhiteSpace($SqlPassword)) {
    throw "No SQL password. Supply -SqlPassword, set CEH_SQL_ADMIN_PASSWORD, or pass -KeyVault <name>."
}

Write-Host "Parity target: [$Env] $Server / $Database (user $User)" -ForegroundColor Cyan

# --- 1. Schema: apply EF migrations (optional) -----------------------------
if ($ApplyMigrations) {
    if ($PSCmdlet.ShouldProcess("$Server/$Database", "dotnet ef database update")) {
        $cs = "Server=tcp:$Server,1433;Initial Catalog=$Database;User ID=$User;Password=$SqlPassword;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
        Write-Host "Applying EF migrations (schema includes the IsTestUser column)..."
        # Pass the connection string to the design-time/runtime context via env.
        $prev = $env:Sql__ConnectionStringTemplate
        try {
            $env:Sql__ConnectionStringTemplate = $cs
            & dotnet ef database update --project $coreProj --startup-project $startProj
            if ($LASTEXITCODE -ne 0) { throw "dotnet ef database update failed (exit $LASTEXITCODE)" }
        } finally {
            $env:Sql__ConnectionStringTemplate = $prev
        }
    }
} else {
    Write-Host "Skipping migrations (-ApplyMigrations not set). Assuming the IsTestUser column already exists." -ForegroundColor Yellow
}

# --- 2. Data: apply the canonical seed -------------------------------------
$seedSql = Get-Content -LiteralPath $seedFile -Raw

if (-not $PSCmdlet.ShouldProcess("$Server/$Database", "apply scripts/seed-eldk27.sql")) {
    Write-Host "WhatIf: would apply $seedFile to $Server/$Database." -ForegroundColor Yellow
    return
}

Add-Type -AssemblyName 'System.Data'
$cs = "Server=tcp:$Server,1433;Initial Catalog=$Database;User ID=$User;Password=$SqlPassword;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;"
$conn = New-Object System.Data.SqlClient.SqlConnection $cs
$conn.Open()
try {
    # The seed is a single batch (no GO separators); run it as one command.
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = $seedSql
    $cmd.CommandTimeout = 120
    [void]$cmd.ExecuteNonQuery()
    Write-Host "Applied seed-eldk27.sql." -ForegroundColor Green

    # --- 3. Verify -----------------------------------------------------------
    $verify = $conn.CreateCommand()
    $verify.CommandText = @"
DECLARE @e INT = (SELECT Id FROM Events WHERE Code = N'ELDK27');
SELECT
    (SELECT COUNT(*) FROM Participants WHERE EventId = @e AND IsTestUser = 0) AS Organizers,
    (SELECT COUNT(*) FROM Participants WHERE EventId = @e AND IsTestUser = 1) AS TestUsers,
    (SELECT COUNT(*) FROM Tasks        WHERE EventId = @e)                    AS Tasks;
"@
    $r = $verify.ExecuteReader()
    if ($r.Read()) {
        Write-Host ("Verified: Organizers={0}  TestUsers={1}  Tasks={2}" -f $r['Organizers'], $r['TestUsers'], $r['Tasks']) -ForegroundColor Green
        if ([int]$r['Organizers'] -lt 4) {
            Write-Warning "Expected 4 organizers (mok/mb/kea/mlh@expertslive.dk) tagged IsTestUser=0."
        }
        if ([int]$r['TestUsers'] -lt 4) {
            Write-Warning "Expected at least 4 test users tagged IsTestUser=1."
        }
    }
    $r.Close()
}
finally {
    $conn.Close()
}

Write-Host "Parity sync complete for [$Env]. Data now matches the canonical dev seed (email flow stays env-specific)." -ForegroundColor Cyan
