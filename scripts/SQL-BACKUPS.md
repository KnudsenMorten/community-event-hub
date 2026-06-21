# SQL database backups — CEH (ELDK27)

Both CEH databases are **Azure SQL Database**, so they already have Microsoft-managed
**automatic backups (PITR)**: full weekly + differential every 12 h + transaction-log every
5–10 min, written to **geo-redundant (GRS)** backup storage. PITR lets you restore to *any
point* within the short-term-retention window (finer than 6-hourly).

| DB | Server | Edition | PITR short-term retention | LTR | Backup redundancy |
|----|--------|---------|---------------------------|-----|-------------------|
| `eldk27hub-db` (DEV)  | `eldk27hub-sql-devz237e`  | Standard S0 | **14 days** | none (non-prod) | Geo |
| `eldk27hub-db` (PROD) | `eldk27hub-sql-prodpdrq`  | Basic       | **35 days** | **weekly, 8 weeks** | Geo |

> Retention and LTR above were configured 2026-06-15 (previously both were 7 days, no LTR).

## Portable 6-hourly BACPAC export (operator request)

On top of PITR, `Export-SqlBacpac.ps1` writes a self-contained **`.bacpac`** every 6 hours to the
`sqlbackups` container of the environment storage account, keeping 7 days of files (28 exports).
A BACPAC is portable: it can be restored into *any* SQL Server / Azure SQL, on any subscription.

Storage targets (private to the CEH sub):
- DEV:  `steldk27hubdevz237e` → container `sqlbackups`
- PROD: `steldk27hubprodpdrq` → container `sqlbackups`

The Azure SQL export service connects to the logical server over its **public** endpoint, which
is why this works for CEH (both servers have `PublicNetworkAccess=Enabled` + an
`AllowAzureServices` firewall rule) and the storage accounts allow the export worker
(`defaultAction=Allow`, shared-key access on). Export uses **SQL auth** — the admin password is
read at runtime from each environment Key Vault secret `sql-admin-password` (never logged).

### Schedule it (Azure Automation, recommended — no Automation account exists yet)

```bash
SUB=772440e1-adf8-4fbe-82f9-bb977b55bc8b
RG=rg-eldk27hub-prod                 # repeat for rg-eldk27hub-dev
AA=aa-eldk27hub-backups
az automation account create -g $RG -n $AA --subscription $SUB -l westeurope
# Grant the Automation account's system-assigned identity:
#   - 'SQL DB Contributor' on the SQL server (to submit exports)
#   - 'Storage Account Contributor' (or Blob Data Contributor + key) on the storage account
#   - 'Key Vault Secrets User' on the env Key Vault (to read sql-admin-password)
# Import Az.Accounts, Az.Sql, Az.Storage, Az.KeyVault modules into the account (PS 7.2 runtime).
az automation runbook create -g $RG --automation-account-name $AA \
   -n Export-SqlBacpac --type PowerShell72
az automation runbook replace-content -g $RG --automation-account-name $AA \
   -n Export-SqlBacpac --content @scripts/Export-SqlBacpac.ps1
# 6-hour schedule (00/06/12/18 UTC):
az automation schedule create -g $RG --automation-account-name $AA \
   -n every-6h --frequency Hour --interval 6 --start-time <next-UTC-quarter-hour>
az automation job-schedule create -g $RG --automation-account-name $AA \
   --runbook-name Export-SqlBacpac --schedule-name every-6h
```
Pass the runbook parameters (`-TenantId -SubscriptionId -ApplicationId -CertificateThumbprint
-ResourceGroupName -ServerName -DatabaseName -StorageAccountName -KeyVaultName`) via the
job-schedule `--parameters`. Use the CEH deploy SPN cert if you prefer SPN auth, or switch the
script's `Connect-AzAccount` to `-Identity` to use the Automation account's managed identity.

## Restore procedures

**PITR (fastest, finest granularity — use this for accidental data loss):**
```powershell
Restore-AzSqlDatabase -FromPointInTimeBackup `
  -ResourceGroupName rg-eldk27hub-prod -ServerName eldk27hub-sql-prodpdrq `
  -TargetDatabaseName eldk27hub-db-restored `
  -ResourceId (Get-AzSqlDatabase -ResourceGroupName rg-eldk27hub-prod -ServerName eldk27hub-sql-prodpdrq -DatabaseName eldk27hub-db).ResourceID `
  -PointInTime '2026-06-15T12:00:00Z'
```

**LTR (PROD weekly archive, up to 8 weeks back):**
```powershell
$ltr = Get-AzSqlDatabaseLongTermRetentionBackup -Location westeurope -ServerName eldk27hub-sql-prodpdrq -DatabaseName eldk27hub-db
Restore-AzSqlDatabase -FromLongTermRetentionBackup -ResourceId $ltr[0].ResourceId `
  -ResourceGroupName rg-eldk27hub-prod -ServerName eldk27hub-sql-prodpdrq -TargetDatabaseName eldk27hub-db-ltr
```

**Import a BACPAC (portable, into any server):**
```powershell
New-AzSqlDatabaseImport -ResourceGroupName rg-eldk27hub-prod -ServerName eldk27hub-sql-prodpdrq `
  -DatabaseName eldk27hub-db-imported -Edition Standard -ServiceObjectiveName S0 -DatabaseMaxSizeBytes 2GB `
  -StorageKeyType StorageAccessKey -StorageKey '<storage-key>' `
  -StorageUri 'https://steldk27hubprodpdrq.blob.core.windows.net/sqlbackups/<file>.bacpac' `
  -AdministratorLogin eldk27hubadmin -AdministratorLoginPassword (Read-Host -AsSecureString)
# or locally:  SqlPackage /a:Import /tf:<file>.bacpac /tsn:<server> /tdn:<db> /AccessToken:<token>
```

> Hygiene note: DEV SQL server has a stale firewall rule `claude-test-temp`
> (same IP as `dev-machine-mok`) that can be removed —
> `az sql server firewall-rule delete -g rg-eldk27hub-dev -s eldk27hub-sql-devz237e -n claude-test-temp`.
