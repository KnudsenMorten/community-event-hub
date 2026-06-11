#Requires -Version 5.1
<#
.SYNOPSIS
    One-time upgrade to zero-downtime slot-swap deploys for prod.

.DESCRIPTION
    COSTS MONEY: deployment slots need the Standard tier (S1) or higher; the
    prod plan is B1 today. S1 is roughly 4-5x the B1 price -- run this only
    after deciding the spend is worth it. The slot itself shares the plan's
    compute (no extra instance cost).

    What it does:
      1. Upgrades the prod App Service plan to S1.
      2. Creates a 'staging' slot cloning production configuration.
      3. Enables the slot's system-assigned managed identity and grants it
         get/list on the Key Vault behind the app's KeyVault references
         (Sql__AdminPassword, Email__*, SponsorLeads__GlobalSecret). WITHOUT
         this the slot crashes at startup -- the slot identity is a DIFFERENT
         principal from production's.
      4. Marks the KV-reference settings as slot-STICKY? No -- they must NOT
         be sticky: both slots use the same secrets, so settings travel with
         the swap harmlessly.

    After this, deploy-app.ps1 automatically switches to the
    deploy-to-slot -> warm -> swap flow, and rollback-app.ps1 becomes an
    instant swap-back.

.EXAMPLE
    .\tools\enable-slot-deploys.ps1            # prod
    .\tools\enable-slot-deploys.ps1 -WhatIf
#>
[CmdletBinding()]
param([switch]$WhatIf)

$ErrorActionPreference = 'Stop'
function Invoke-Az {
    param([Parameter(Mandatory)][string[]]$AzArgs)
    $eap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $out = az @AzArgs 2>$null } finally { $ErrorActionPreference = $eap }
    $script:AzExit = $LASTEXITCODE
    return $out
}
$rg  = 'rg-eldk27hub-prod'
$app = 'eldk27hub-web-prodpdrq'

$planId = Invoke-Az @('webapp','show','-g',$rg,'-n',$app,'--query','appServicePlanId','-o','tsv')
$planName = ($planId -split '/')[-1]
$sku = Invoke-Az @('appservice','plan','show','-g',$rg,'-n',$planName,'--query','sku.name','-o','tsv')
Write-Host "Plan: $planName (current SKU: $sku)"

if ($WhatIf) {
    Write-Host "[WHATIF] Would: upgrade $planName to S1; create 'staging' slot on $app (config cloned); enable slot MSI; grant it get/list on the app's Key Vault."
    return
}

if ($sku -notmatch '^(S|P)') {
    Write-Host ">> Upgrading plan to S1 (slots require Standard+)..." -ForegroundColor Yellow
    [void](Invoke-Az @('appservice','plan','update','-g',$rg,'-n',$planName,'--sku','S1','--output','none'))
    if ($script:AzExit -ne 0) { throw "plan upgrade failed." }
}

Write-Host ">> Creating 'staging' slot (cloning production config)..." -ForegroundColor Cyan
[void](Invoke-Az @('webapp','deployment','slot','create','-g',$rg,'-n',$app,'--slot','staging','--configuration-source',$app,'--output','none'))
if ($script:AzExit -ne 0) { throw "slot create failed." }

Write-Host ">> Enabling slot managed identity..." -ForegroundColor Cyan
$slotPrincipal = Invoke-Az @('webapp','identity','assign','-g',$rg,'-n',$app,'--slot','staging','--query','principalId','-o','tsv')

# Find the vault behind the app's KeyVault references and grant the slot identity.
$kvQuery = "[?contains(value, 'VaultName=') || contains(value, 'vault.azure.net')].value | [0]"
$kvRef = Invoke-Az @('webapp','config','appsettings','list','-g',$rg,'-n',$app,'--query',$kvQuery,'-o','tsv')
$vault = $null
if ($kvRef -match 'VaultName=([^;)]+)') { $vault = $Matches[1] }
elseif ($kvRef -match 'https://([^.]+)\.vault\.azure\.net') { $vault = $Matches[1] }
if (-not $vault) {
    Write-Host "WARNING: could not derive the Key Vault name from the app settings -- grant the slot identity ($slotPrincipal) get/list on the vault manually." -ForegroundColor Yellow
} else {
    Write-Host ">> Granting slot identity get/list on vault '$vault'..." -ForegroundColor Cyan
    # Try RBAC first (vault may use RBAC authorization); fall back to access policy.
    $vaultId = Invoke-Az @('keyvault','show','-n',$vault,'--query','id','-o','tsv')
    $rbac = Invoke-Az @('keyvault','show','-n',$vault,'--query','properties.enableRbacAuthorization','-o','tsv')
    if ($rbac -eq 'true') {
        [void](Invoke-Az @('role','assignment','create','--assignee-object-id',$slotPrincipal,'--assignee-principal-type','ServicePrincipal','--role','Key Vault Secrets User','--scope',$vaultId,'--output','none'))
    } else {
        [void](Invoke-Az @('keyvault','set-policy','-n',$vault,'--object-id',$slotPrincipal,'--secret-permissions','get','list','--output','none'))
    }
}

Write-Host ">> Done. Verify the slot starts: az webapp browse -g $rg -n $app --slot staging" -ForegroundColor Green
Write-Host ">> From now on, deploy-app.ps1 -Env prod uses deploy->warm->swap automatically." -ForegroundColor Green
