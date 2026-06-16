# =====================================================================================
# Secrets.psm1  —  Key-Vault-by-name secret bootstrap for the legacy automation scripts
# -------------------------------------------------------------------------------------
# This module replaces the previous plaintext `Import_Secrets` function. It reads EVERY
# credential from Azure Key Vault BY NAME at runtime — no secret VALUE is ever stored in
# this repository (only the secret NAMES and the bootstrap code).
#
# The map below mirrors the CEH "config references secrets by name" convention (DESIGN
# §17). Most names already exist in the event Key Vault; two were added for this tree:
#   - currency-api-key      (One Simple API exchange-rate token)
#   - visualcron-api-password (VisualCron JSON-API monitor login)
#
# USAGE (drop-in compatible with the old call site):
#     Import-Module "$PSScriptRoot\Secrets.psm1" -Global -Force
#     Import_Secrets                 # reads from KV, populates the same $global:* vars
#
# AUTH: the host running these scripts must be signed in to Azure with read access to the
# vault — `Connect-AzAccount` (interactive on the VM, or an Azure Function managed
# identity / a cert-based service principal). No client secrets are used or stored here.
#
# The vault name is operator config, NOT a secret: set $env:ELDK_KEYVAULT_NAME or pass
# -VaultName. It is intentionally not hard-coded to a customer-specific value.
# =====================================================================================

# --- Secret-NAME map (names only; values live in Key Vault) --------------------------
# Logical key -> Key Vault secret name.
$script:SecretNameMap = @{
    # Entra app used by the legacy scripts (app-only Graph / Backstage helpers)
    EntraTenantId            = 'eldk-entra-tenant-id'
    EntraClientId            = 'eldk-entra-client-id'
    EntraClientSecret        = 'eldk-entra-client-secret'

    # e-conomic (ERP) REST API
    EconomicAppSecretToken   = 'economic-app-secret-token'
    EconomicGrantToken       = 'economic-agreement-grant-token'

    # Brevo SMTP relay
    SmtpUser                 = 'brevo-smtp-username'
    SmtpPass                 = 'brevo-smtp-key'

    # WooCommerce REST + Company Manager (WordPress) application password
    ConsumerKey              = 'woocommerce-consumer-key'
    ConsumerSecret           = 'woocommerce-consumer-secret'
    WpUserApi                = 'company-manager-wp-user'
    WpAppPassword            = 'company-manager-wp-app-password'

    # Zoho OAuth (Backstage / Bookings)
    ZohoClientId             = 'zoho-client-id'
    ZohoClientSecret         = 'zoho-client-secret'
    ZohoRefreshToken         = 'zoho-refresh-token'

    # Currency conversion API (added for this tree)
    CurrencyApiKey           = 'currency-api-key'

    # VisualCron JSON-API monitor login password (added for this tree)
    VisualCronApiPassword    = 'visualcron-api-password'
}

# --- Non-secret operator config (NOT in Key Vault) -----------------------------------
# These are ordinary config values, not credentials. Override per environment via env
# vars; the defaults below carry no customer data and no secrets.
function Get-LegacyAutomationConfig {
    [CmdletBinding()]
    param()
    return [pscustomobject]@{
        TenantShortName = $env:ELDK_TENANT_SHORTNAME ; # e.g. an Entra domain short name
        ZohoApiBase     = if ($env:ELDK_ZOHO_API_BASE) { $env:ELDK_ZOHO_API_BASE } else { 'https://www.zohoapis.eu/backstage/v3' }
        ZohoPortalId    = $env:ELDK_ZOHO_PORTAL_ID    ; # Backstage portal id (operator config)
        ZohoEventId     = $env:ELDK_ZOHO_EVENT_ID     ; # Backstage event id  (operator config)
        ZohoBrandId     = $env:ELDK_ZOHO_BRAND_ID     ; # Backstage brand id  (operator config)
    }
}

# --- KV reader -----------------------------------------------------------------------
function Get-KvSecretValue {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$VaultName,
        [Parameter(Mandatory)][string]$SecretName
    )

    # Prefer Az.KeyVault; fall back to the `az` CLI if the module is not present.
    if (Get-Command Get-AzKeyVaultSecret -ErrorAction SilentlyContinue) {
        $s = Get-AzKeyVaultSecret -VaultName $VaultName -Name $SecretName -ErrorAction Stop
        if (-not $s) { throw "Secret '$SecretName' not found in vault '$VaultName'." }
        # Az returns a SecureString in -AsPlainText:$false mode; normalise to plaintext.
        if ($s.SecretValue) {
            $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s.SecretValue)
            try   { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
            finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        }
        return [string]$s.SecretValueText
    }

    if (Get-Command az -ErrorAction SilentlyContinue) {
        $v = az keyvault secret show --vault-name $VaultName --name $SecretName --query value -o tsv
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($v)) {
            throw "Could not read secret '$SecretName' from vault '$VaultName' via az CLI."
        }
        return $v.Trim()
    }

    throw "No Key Vault client available. Install Az.KeyVault or the az CLI, and sign in to Azure."
}

# --- Public bootstrap (drop-in replacement for the old plaintext Import_Secrets) ------
function Import_Secrets {
    [CmdletBinding()]
    param(
        [string]$VaultName = $env:ELDK_KEYVAULT_NAME
    )

    if ([string]::IsNullOrWhiteSpace($VaultName)) {
        throw "Key Vault name not set. Pass -VaultName or set `$env:ELDK_KEYVAULT_NAME (this is operator config, not a secret)."
    }

    Write-Verbose "Loading secrets from Key Vault '$VaultName' by name..."

    # Resolve every mapped secret once.
    $resolved = @{}
    foreach ($key in $script:SecretNameMap.Keys) {
        $resolved[$key] = Get-KvSecretValue -VaultName $VaultName -SecretName $script:SecretNameMap[$key]
    }

    $cfg = Get-LegacyAutomationConfig

    # ---- Populate the SAME global variable surface the old module exposed -----------
    # Tenant / Entra app
    $global:TenantId        = $resolved.EntraTenantId
    $global:ClientId        = $resolved.EntraClientId
    $global:ClientSecret    = $resolved.EntraClientSecret
    $global:TenantShortName = $cfg.TenantShortName

    # e-conomic (ERP) REST headers
    $Global:Economic_headers_REST = @{
        'X-AppSecretToken'      = $resolved.EconomicAppSecretToken
        'X-AgreementGrantToken' = $resolved.EconomicGrantToken
        'Content-Type'          = 'application/json'
        'FollowRelLink'         = '$true'
        'MaximumFollowRelLink'  = '3'
        'charset'               = 'UTF-8'
    }

    # SMTP (Brevo)
    $Global:SmtpUser = $resolved.SmtpUser
    $Global:SmtpPass = $resolved.SmtpPass

    # Webshop (WooCommerce + Company Manager WordPress)
    $Global:ConsumerKey    = $resolved.ConsumerKey
    $Global:ConsumerSecret = $resolved.ConsumerSecret
    $Global:WpUserApi      = $resolved.WpUserApi
    $Global:WpAppPassword  = $resolved.WpAppPassword

    # Zoho Backstage / Bookings
    $global:ZohoApiBase      = $cfg.ZohoApiBase
    $global:ZohoClientId     = $resolved.ZohoClientId
    $global:ZohoClientSecret = $resolved.ZohoClientSecret
    $global:ZohoRefreshToken = $resolved.ZohoRefreshToken
    $global:ZohoPortalId     = $cfg.ZohoPortalId
    $global:ZohoEventId      = $cfg.ZohoEventId
    $global:ZohoBrandId      = $cfg.ZohoBrandId

    # Currency conversion
    $global:Currency_APIKey = $resolved.CurrencyApiKey

    # VisualCron monitor login (consumed by MonitorFix-Webhook-Is-Active.ps1)
    $global:VisualCronApiPassword = $resolved.VisualCronApiPassword

    Write-Verbose "Secret bootstrap complete ($($resolved.Count) secrets loaded from '$VaultName')."
}

Export-ModuleMember -Function Import_Secrets, Get-KvSecretValue, Get-LegacyAutomationConfig
