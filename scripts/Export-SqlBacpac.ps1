<#
.SYNOPSIS
    6-hourly portable BACPAC export of an Azure SQL database to a backups blob container,
    with retention pruning. Cert-only auth; SQL admin password pulled from Key Vault at runtime.

.DESCRIPTION
    Designed to run as an Azure Automation runbook (PowerShell 7.x runtime) on a 6-hour
    schedule, OR ad-hoc from mgmt1. It:
      1. Connects to the tenant as the deploy SPN using a certificate (no secrets, no device-code).
      2. Reads the SQL admin password from the environment Key Vault.
      3. Submits New-AzSqlDatabaseExport to write <db>-<utcstamp>.bacpac into the 'sqlbackups' container.
      4. Waits for completion, then prunes blobs older than -RetentionDays (default 7).

    The Azure SQL export service connects to the logical server over its public endpoint, so the
    server must have PublicNetworkAccess=Enabled + an 'AllowAzureServices' firewall rule, and the
    target storage account must allow the export worker (shared-key access). CEH satisfies both.

.NOTES
    Auth model matches CLAUDE.md: SPN + certificate; client id / thumbprint / tenant id read from
    kv-automatit-dev on mgmt1, OR supplied directly when run as a runbook with a Run-As-style cert.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $SubscriptionId,
    [Parameter(Mandatory)] [string] $ApplicationId,
    [Parameter(Mandatory)] [string] $CertificateThumbprint,
    [Parameter(Mandatory)] [string] $ResourceGroupName,
    [Parameter(Mandatory)] [string] $ServerName,
    [Parameter(Mandatory)] [string] $DatabaseName,
    [Parameter(Mandatory)] [string] $StorageAccountName,
    [Parameter(Mandatory)] [string] $KeyVaultName,
    [string] $SqlAdminLogin       = 'eldk27hubadmin',
    [string] $SqlPasswordSecret   = 'sql-admin-password',
    [string] $ContainerName       = 'sqlbackups',
    [int]    $RetentionDays       = 7,
    [int]    $TimeoutMinutes      = 30
)

$ErrorActionPreference = 'Stop'
Disable-AzContextAutosave -Scope Process | Out-Null

Connect-AzAccount -ServicePrincipal -ApplicationId $ApplicationId -Tenant $TenantId `
    -CertificateThumbprint $CertificateThumbprint -SubscriptionId $SubscriptionId -WarningAction SilentlyContinue | Out-Null

# --- SQL admin password (Key Vault, never logged) ---
$sqlPwd = Get-AzKeyVaultSecret -VaultName $KeyVaultName -Name $SqlPasswordSecret -AsPlainText
$sqlPwdSecure = ConvertTo-SecureString $sqlPwd -AsPlainText -Force

# --- storage context + container ---
$key = (Get-AzStorageAccountKey -ResourceGroupName $ResourceGroupName -Name $StorageAccountName)[0].Value
$stCtx = New-AzStorageContext -StorageAccountName $StorageAccountName -StorageAccountKey $key
if (-not (Get-AzStorageContainer -Name $ContainerName -Context $stCtx -ErrorAction SilentlyContinue)) {
    New-AzStorageContainer -Name $ContainerName -Context $stCtx -Permission Off | Out-Null
}

# --- submit export ---
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss')
$blob  = "$DatabaseName-$stamp.bacpac"
$uri   = "https://$StorageAccountName.blob.core.windows.net/$ContainerName/$blob"
Write-Output "[$(Get-Date -Format o)] Exporting $ServerName/$DatabaseName -> $uri"

$exp = New-AzSqlDatabaseExport -ResourceGroupName $ResourceGroupName -ServerName $ServerName `
    -DatabaseName $DatabaseName -StorageKeyType StorageAccessKey -StorageKey $key -StorageUri $uri `
    -AdministratorLogin $SqlAdminLogin -AdministratorLoginPassword $sqlPwdSecure

$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
do {
    Start-Sleep -Seconds 20
    $status = Get-AzSqlDatabaseImportExportStatus -OperationStatusLink $exp.OperationStatusLink
    Write-Output "[$(Get-Date -Format o)] status=$($status.Status) $($status.ErrorMessage)"
} while ($status.Status -notin 'Succeeded','Failed' -and (Get-Date) -lt $deadline)

if ($status.Status -ne 'Succeeded') {
    throw "BACPAC export did not succeed: $($status.Status) $($status.ErrorMessage)"
}
Write-Output "[$(Get-Date -Format o)] Export succeeded: $blob"

# --- retention prune ---
$cutoff = (Get-Date).ToUniversalTime().AddDays(-$RetentionDays)
$old = Get-AzStorageBlob -Container $ContainerName -Context $stCtx |
    Where-Object { $_.Name -like "$DatabaseName-*.bacpac" -and $_.LastModified.UtcDateTime -lt $cutoff }
foreach ($b in $old) {
    Write-Output "[$(Get-Date -Format o)] Pruning old backup: $($b.Name) ($($b.LastModified))"
    Remove-AzStorageBlob -Container $ContainerName -Blob $b.Name -Context $stCtx -Force
}
Write-Output "[$(Get-Date -Format o)] Done. Kept backups newer than $cutoff (UTC)."
