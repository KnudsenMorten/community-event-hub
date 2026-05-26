// ===========================================================================
//  storage.bicep  -  Azure Blob Storage for the Community Hub
// ---------------------------------------------------------------------------
//  Holds runtime-uploaded files: speaker manual, volunteer handbook, sponsor
//  booth artwork, venue map, the logo. The database stores only the URLs -
//  never file bytes. One private container; the app issues short-lived SAS
//  URLs (or streams via the app) so files are not world-readable.
// ===========================================================================

@description('Azure region for the storage account.')
param location string

@description('Storage account name (globally unique, 3-24 chars, lowercase letters and numbers only).')
param storageAccountName string

@description('Tags applied to the resources.')
param tags object

@description('Name of the blob container for uploaded files.')
param uploadsContainerName string = 'uploads'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    accessTier: 'Hot'
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

resource uploadsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: uploadsContainerName
  properties: {
    // Private - no anonymous access. The app brokers downloads.
    publicAccess: 'None'
  }
}

output storageAccountName string = storageAccount.name
output uploadsContainerName string = uploadsContainer.name
output blobEndpoint string = storageAccount.properties.primaryEndpoints.blob
