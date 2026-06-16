// ===========================================================================
//  sql.bicep  -  Azure SQL server + database for the Community Hub
// ---------------------------------------------------------------------------
//  Stores all structured data: crew profiles, roles, hotel bookings, dinner
//  signups, volunteer shifts, tasks, sponsor task assignments, the Events
//  table (one row per edition - ELDK27, ELDK28...) and SentReminders.
//  GDPR-relevant: the server is private-by-default (no public Azure access),
//  TLS 1.2 enforced. The web app and Functions reach it over a firewall rule
//  that allows Azure services only.
// ===========================================================================

@description('Azure region for the SQL server.')
param location string

@description('SQL logical server name (globally unique, lowercase).')
param sqlServerName string

@description('SQL database name.')
param sqlDatabaseName string

@description('Tags applied to the resources.')
param tags object

@description('SQL administrator login name. Optional: the server now authenticates app traffic via Entra managed identity (AAD-only). A SQL login is only needed if Azure-AD-only auth is disabled.')
param sqlAdminLogin string = 'communityhubadmin'

@description('SQL administrator password. Optional: only required when azureADOnlyAuthentication is false. Passed at deploy time from Key Vault or a secure parameter - never hard-coded.')
@secure()
param sqlAdminPassword string = ''

@description('Entra (Azure AD) admin group for the SQL server. Members can connect as SQL admin via Entra auth. Defaults to the ELDK SQL Admins group.')
param aadAdminLogin string = 'ELDK SQL Admins'

@description('Object id (sid) of the Entra admin group.')
param aadAdminObjectId string = '27338212-954e-41c4-95ce-71c2778991e9'

@description('Entra tenant id the admin principal belongs to.')
param aadAdminTenantId string = subscription().tenantId

@description('Enforce Entra-only (Azure AD-only) authentication on the SQL server - disables SQL login+password. The app + Functions authenticate passwordlessly via their managed identities.')
param azureADOnlyAuthentication bool = true

@description('Database SKU. Defaults to a small General Purpose serverless tier suitable for a low-traffic crew portal.')
param databaseSku object = {
  name: 'GP_S_Gen5_1'
  tier: 'GeneralPurpose'
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: sqlServerName
  location: location
  tags: tags
  properties: {
    // SQL login is only populated when AAD-only auth is OFF. With AAD-only ON
    // (the default) the app + Functions authenticate via managed identity and
    // no SQL password exists.
    administratorLogin: azureADOnlyAuthentication ? null : sqlAdminLogin
    administratorLoginPassword: azureADOnlyAuthentication ? null : sqlAdminPassword
    version: '12.0'
    minimalTlsVersion: '1.2'
    // No public endpoint exposure beyond the explicit firewall rule below.
    publicNetworkAccess: 'Enabled'
    // Entra (Azure AD) admin: the ELDK SQL Admins group. Set inline so the
    // admin exists at create time (avoids a separate apply ordering issue).
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'Group'
      login: aadAdminLogin
      sid: aadAdminObjectId
      tenantId: aadAdminTenantId
      azureADOnlyAuthentication: azureADOnlyAuthentication
    }
  }
}

// Explicit Azure-AD-only authentication child resource. Redundant with the
// inline `administrators.azureADOnlyAuthentication` above but kept so the
// setting is unambiguous and survives a server-properties-only update.
resource sqlAadOnly 'Microsoft.Sql/servers/azureADOnlyAuthentications@2023-08-01-preview' = if (azureADOnlyAuthentication) {
  parent: sqlServer
  name: 'Default'
  properties: {
    azureADOnlyAuthentication: true
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: sqlDatabaseName
  location: location
  tags: tags
  sku: databaseSku
  properties: {
    collation: 'SQL_Latin1_General_CP1_CI_AS'
    // Serverless auto-pause keeps cost low for an event portal that is
    // idle most of the year. Raise / remove for steady traffic.
    autoPauseDelay: 60
    minCapacity: json('0.5')
    zoneRedundant: false
  }
}

// Allow other Azure services (the App Service + Functions) to reach the DB.
// The 0.0.0.0 rule is the Azure-internal "allow Azure services" convention,
// NOT a public-internet opening.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output sqlServerName string = sqlServer.name
output sqlServerFqdn string = sqlServer.properties.fullyQualifiedDomainName
output sqlDatabaseName string = sqlDatabase.name
// Connection string WITHOUT credentials - the app composes the final string
// using the SQL password pulled from Key Vault at runtime.
output sqlConnectionStringTemplate string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;'
