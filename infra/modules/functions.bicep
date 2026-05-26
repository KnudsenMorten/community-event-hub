// ===========================================================================
//  functions.bicep  -  Azure Functions app: the Community Hub scheduler
// ---------------------------------------------------------------------------
//  An App Service web app cannot run on a timer, so a separate Functions app
//  (FlexConsumption plan, timer triggers) runs the daily jobs:
//      reminderJob       - evaluates due reminders, sends via Brevo, dedups
//                          against SentReminders (idempotent; a missed run
//                          self-heals on the next run)
//      woocommercePull   - fetches new completed WooCommerce sponsor orders
//  Has its own system-assigned managed identity so it, too, reads Key Vault.
//
//  PLAN: FlexConsumption (FC1). Replaces the legacy Y1 Consumption SKU which
//  has stamp-affinity issues on freshly-created resource groups when paired
//  with Linux (the RG sometimes lands on a stamp pool with no Linux-Dynamic
//  workers -- "Linux dynamic workers are not available in resource group
//  ..." -- with no way to influence stamp assignment short of deleting the
//  RG and rolling the dice, which is incompatible with KV purge protection).
//  FlexConsumption is the modern Microsoft-recommended replacement, has
//  Linux supported by design, scales to zero, pay-per-execution, and uses
//  a deployment blob container with identity-based access for code drop.
// ===========================================================================

@description('Azure region.')
param location string

@description('Functions app name.')
param functionsAppName string

@description('FlexConsumption plan name for the Functions app.')
param functionsPlanName string

@description('Tags applied to the resources.')
param tags object

@description('Storage account name - Functions requires an associated storage account for its own runtime state + the deployment package blob container.')
param functionsStorageAccountName string

@description('Key Vault URI for secret references.')
param keyVaultUri string

@description('SQL connection-string template (no credentials).')
param sqlConnectionStringTemplate string

@description('Application Insights connection string.')
param appInsightsConnectionString string

@description('Max scale-out instance count. FlexConsumption default ceiling is 100; lower for cost-bounded envs.')
param maximumInstanceCount int = 40

@description('Per-instance memory in MB. FlexConsumption supports 512 / 2048 / 4096 -- 2048 is the standard sweet spot.')
@allowed([ 512, 2048, 4096 ])
param instanceMemoryMB int = 2048

// Functions runtime needs its own storage account (separate from the app's
// uploads storage) for triggers, logs, durable state, AND -- new in
// FlexConsumption -- the deployment package blob container.
resource functionsStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: functionsStorageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    // Identity-based access for the deployment container needs blob service
    // enabled (always on for StorageV2 but explicit for clarity).
    allowSharedKeyAccess: true
  }
}

// FlexConsumption deployment-package blob container. The function app's MI
// reads the zip from here at scale-out / cold-start to bootstrap the runtime.
resource functionsStorageBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: functionsStorage
  name: 'default'
  properties: {}
}

resource deploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: functionsStorageBlobService
  name: 'app-package'
  properties: {
    publicAccess: 'None'
  }
}

resource functionsPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: functionsPlanName
  location: location
  tags: tags
  sku: {
    name: 'FC1'
    tier: 'FlexConsumption'
  }
  kind: 'functionapp,linux'
  properties: {
    reserved: true // required for Linux
  }
}

resource functionsApp 'Microsoft.Web/sites@2023-12-01' = {
  name: functionsAppName
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: functionsPlan.id
    httpsOnly: true
    // FlexConsumption-specific config block. Runtime + scale + deployment
    // package source all live here (NOT in siteConfig as on Y1).
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${functionsStorage.properties.primaryEndpoints.blob}${deploymentContainer.name}'
          authentication: {
            type: 'SystemAssignedIdentity'
          }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maximumInstanceCount
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: {
        name: 'dotnet-isolated'
        version: '8.0'
      }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      // FlexConsumption no longer uses FUNCTIONS_WORKER_RUNTIME or
      // FUNCTIONS_EXTENSION_VERSION (replaced by functionAppConfig.runtime).
      // AzureWebJobsStorage still required for runtime state -- using shared
      // key here for simplicity; can be flipped to identity-based later by
      // dropping AzureWebJobsStorage and adding AzureWebJobsStorage__accountName.
      appSettings: [
        {
          name: 'AzureWebJobsStorage'
          value: 'DefaultEndpointsProtocol=https;AccountName=${functionsStorage.name};EndpointSuffix=${environment().suffixes.storage};AccountKey=${functionsStorage.listKeys().keys[0].value}'
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        {
          name: 'KeyVault__Uri'
          value: keyVaultUri
        }
        {
          name: 'Sql__ConnectionStringTemplate'
          value: sqlConnectionStringTemplate
        }
        {
          name: 'Sql__AdminPassword'
          value: '@Microsoft.KeyVault(VaultName=${last(split(keyVaultUri, '/'))};SecretName=sql-admin-password)'
        }
      ]
    }
  }
}

// Grant the function app's MI Storage Blob Data Contributor on the deployment
// container so it can read the code package at cold-start. Built-in role
// 'Storage Blob Data Contributor' id ba92f5b4-2d11-453d-a403-e96b0029c9fe.
// Deterministic GUID name so re-deploy is idempotent.
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource functionsAppStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(functionsStorage.id, functionsApp.id, storageBlobDataContributorRoleId)
  scope: functionsStorage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: functionsApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output functionsAppName string = functionsApp.name
output functionsAppPrincipalId string = functionsApp.identity.principalId
