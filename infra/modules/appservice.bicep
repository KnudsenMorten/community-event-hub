// ===========================================================================
//  appservice.bicep  -  Linux App Service hosting the Community Hub web app
// ---------------------------------------------------------------------------
//  Serves all crew-facing pages (PIN login, role hub, hotel/dinner/volunteer
//  forms, tasks, sponsor module). .NET on Linux. A system-assigned managed
//  identity is created so the app can read Key Vault secrets without storing
//  credentials. App settings reference Key Vault via @Microsoft.KeyVault(...).
// ===========================================================================

@description('Azure region.')
param location string

@description('App Service plan name.')
param appServicePlanName string

@description('Web app name (becomes <name>.azurewebsites.net).')
param webAppName string

@description('Tags applied to the resources.')
param tags object

@description('App Service plan SKU. B1 is a low-cost starting point; scale up for the event.')
param planSku string = 'B1'

@description('.NET runtime version on Linux.')
param dotnetVersion string = 'DOTNETCORE|8.0'

@description('Key Vault URI - used to build @Microsoft.KeyVault secret references.')
param keyVaultUri string

@description('SQL connection-string template (no credentials) from the sql module.')
param sqlConnectionStringTemplate string

@description('Blob endpoint from the storage module.')
param blobEndpoint string

@description('Application Insights connection string for telemetry.')
param appInsightsConnectionString string

@description('The Zoho Backstage origin allowed to embed the hub in an iframe (frame-ancestors CSP). Empty = embedding disabled until set.')
param backstageEmbedOrigin string = ''

@description('Custom hostname the operator will bind post-deploy (e.g. test.hub.eldk27.expertslive.dk). Surfaced as the Hub__CustomDomain app setting so the running app can emit it in absolute URLs / cookie domain hints. Binding itself is manual -- see docs/RUNBOOK.md §4.2.')
param customDomain string = ''

@description('SAFE outbound-email allowlist FLOOR persisted in infra so a redeploy can never re-open mail to real recipients (operator directive 2026-06-16: never mail anyone outside the allowlist, dev AND prod). Defaults to the @expertslive.dk organiser domain only; the operator''s personal test addresses are added on top as a LIVE app setting (never committed here, public mirror). The app fails closed if this is empty.')
param emailOnlySendTo string = '@expertslive.dk'

@description('TEST MODE master switch -- when true, integrations perform NO real outbound writes (no Zoho Backstage / Booking calls, no WooCommerce writes, coordinator notifications routed to TestCoordinatorEmail). Surfaced as the TestMode__Enabled app setting; the .NET app binds this via TestModeOptions. Defaults are set in main.bicep based on environmentName (true for dev, false for prod).')
param testModeEnabled bool

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  sku: {
    name: planSku
  }
  kind: 'linux'
  properties: {
    reserved: true // required for Linux
  }
}

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: location
  tags: tags
  // System-assigned identity -> used for Key Vault access policies.
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: dotnetVersion
      alwaysOn: true
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        // Outbound-email safety (operator directive 2026-06-16): allowlist is the
        // SOLE gate, redirect is OFF, and the app fails closed on an empty list.
        // Persisted here so a redeploy can't drop them and re-open mail to real
        // speakers / sponsors / volunteers. Personal test addresses are layered
        // on as a live app setting (not committed -- public mirror).
        {
          name: 'Email__OnlySendTo'
          value: emailOnlySendTo
        }
        {
          name: 'Email__RedirectAllTo'
          value: ''
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
          name: 'Storage__BlobEndpoint'
          value: blobEndpoint
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        // NOTE: no Sql__AdminPassword is emitted. The app authenticates to
        // Azure SQL passwordlessly via its system-assigned managed identity
        // (the connection string in Program.cs appends
        // `Authentication=Active Directory Managed Identity;` when no SQL
        // password is configured). The MI is granted db_datareader /
        // db_datawriter / db_ddladmin on the database. SQL login+password is a
        // local-dev-only fallback and never set in Azure.
        // The Backstage origin allowed to embed the hub. The app uses this to
        // emit `Content-Security-Policy: frame-ancestors <origin>` and to
        // issue the session cookie as SameSite=None inside that embed. See
        // CONTEXT.md section 5a. Empty until the real Backstage domain is set.
        {
          name: 'Embedding__BackstageOrigin'
          value: backstageEmbedOrigin
        }
        // The custom hostname the operator will bind post-deploy. Empty until
        // the bind happens. The app uses this to emit absolute URLs (email
        // links, OAuth redirects) at the public hostname rather than the raw
        // <name>.azurewebsites.net default.
        {
          name: 'Hub__CustomDomain'
          value: customDomain
        }
        // TEST MODE master switch. When true, integrations perform NO real
        // outbound writes (no Zoho Backstage / Booking calls, no WooCommerce
        // writes, coordinator notifications routed to the test address only).
        // dev defaults to true, prod to false -- the value is set in
        // main.bicep based on environmentName, never assumed here. The
        // .NET app binds this via TestModeOptions (Integrations namespace).
        {
          name: 'TestMode__Enabled'
          value: string(testModeEnabled)
        }
      ]
    }
  }
}

output webAppName string = webApp.name
output webAppHostname string = webApp.properties.defaultHostName
output webAppPrincipalId string = webApp.identity.principalId
output appServicePlanId string = appServicePlan.id
