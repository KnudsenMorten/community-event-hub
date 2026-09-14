// ===========================================================================
//  keyvault.bicep  -  Azure Key Vault for the Community Hub
// ---------------------------------------------------------------------------
//  Holds every secret the app and the Functions job need. NOTHING secret is
//  ever placed in the repo or in the JSON config - only the secret *names*.
//  The inventory of expected secrets is in docs/DESIGN.md §17:
//      brevo-smtp-username, brevo-smtp-key,
//      woocommerce-consumer-key, woocommerce-consumer-secret,
//      company-manager-wp-user, company-manager-wp-app-password,
//      sql-admin-password
//  Secret VALUES are set after deployment (see scripts/set-secrets.sh /
//  README.md "Getting started"); this module only provisions the vault + RBAC access.
//
//  Auth model: RBAC (enableRbacAuthorization = true). Modern + audit-friendly.
//  Access policies are NOT used. The web app + Functions app managed
//  identities receive the built-in role "Key Vault Secrets User" (GET + LIST
//  on secrets only) at the vault scope. The operator running set-secrets.sh
//  must hold "Key Vault Secrets Officer" (or higher) on the vault -- granted
//  by set-secrets.sh on its first run if absent.
// ===========================================================================

@description('Azure region for the vault.')
param location string

@description('Globally-unique Key Vault name (3-24 chars, alphanumeric/hyphen).')
param keyVaultName string

@description('Tags applied to the vault.')
param tags object

@description('Principal IDs (managed identities) that get GET/LIST on secrets via the built-in Key Vault Secrets User role - typically the web app and the Functions app.')
param readerPrincipalIds array = []

@description('Tenant ID. Defaults to the deployment tenant. Used by KV for AAD-issued token validation; RBAC role assignments below also live in this tenant.')
param tenantId string = subscription().tenantId

// Built-in "Key Vault Secrets User" role -- GET + LIST on secrets only.
// What the web app + Functions app MIs need.
//
// Built-in role definition GUIDs are the same in every tenant; this is the
// documented id of "Key Vault Secrets User". An earlier note here claimed the
// id differs per tenant -- that came from a mistyped docs value. To confirm
// it in your tenant, compare with the output of
// `az role definition list --name "Key Vault Secrets User"`, e.g.
// (the query below prints just the id). If it ever differs, use
// the value it prints:
//     az role definition list --name "Key Vault Secrets User" \
//       --query "[0].name" -o tsv
// and update the value below (or parameterize -- a future cleanup).
//
// The matching operator role (Key Vault Secrets Officer, GET+LIST+SET+
// DELETE) is granted by scripts/set-secrets.sh on first run, NOT here.
var keyVaultSecretsUserRoleId = '4633458b-17de-408a-b874-0445c86b69e6'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: keyVaultName
  location: location
  tags: tags
  properties: {
    sku: {
      family: 'A'
      name: 'standard'
    }
    tenantId: tenantId
    // RBAC mode -- role assignments below grant access, not the legacy
    // accessPolicies array (which is intentionally omitted).
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 90
    enablePurgeProtection: true
    // Secrets are read by Azure services over the Azure backbone.
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

// Grant each reader principal (web app MI, functions app MI, ...) the
// Key Vault Secrets User role at the vault scope. Name is a deterministic
// GUID of (vault, principal, role) so the assignment is idempotent on
// re-deploy and re-running this module never produces duplicates.
resource readerRoleAssignments 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in readerPrincipalIds: {
    name: guid(vault.id, principalId, keyVaultSecretsUserRoleId)
    scope: vault
    properties: {
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUserRoleId)
      principalId: principalId
      principalType: 'ServicePrincipal'
    }
  }
]

output keyVaultName string = vault.name
output keyVaultUri string = vault.properties.vaultUri
output keyVaultId string = vault.id
