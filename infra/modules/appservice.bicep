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
param dotnetVersion string = 'DOTNETCORE|10.0'

@description('Key Vault URI - used to build @Microsoft.KeyVault secret references.')
param keyVaultUri string

@description('SQL connection-string template (no credentials) from the sql module.')
param sqlConnectionStringTemplate string

@description('Blob endpoint from the storage module.')
param blobEndpoint string

@description('Application Insights connection string for telemetry.')
param appInsightsConnectionString string

@description('§392 — the Application Insights ARM RESOURCE ID (not the connection string). Surfaced as Telemetry__AppInsightsResourceId so the organizer Platform-health page can RUN Logs queries against the component; the connection string only says where telemetry goes. Empty disables the page with an explanation rather than failing.')
param appInsightsResourceId string = ''

@description('The Zoho Backstage origin allowed to embed the hub in an iframe (frame-ancestors CSP). Empty = embedding disabled until set.')
param backstageEmbedOrigin string = ''

@description('Custom hostname the operator will bind post-deploy (e.g. dev.hub.your-event.example). Surfaced as the Hub__CustomDomain app setting so the running app can emit it in absolute URLs / cookie domain hints. Binding itself is manual -- see README.md "Getting started".')
param customDomain string = ''

@description('RETIRED (§330) — the old outbound-email allowlist floor. SUPERSEDED by §234: audience control is RINGS ONLY and nothing in the app reads Email__OnlySendTo any more, so this module no longer emits it. The parameter is kept (unused) so existing parameter files and pipelines that still pass it do not fail; remove it once none do.')
param emailOnlySendTo string = ''

@description('TEST MODE master switch -- when true, integrations perform NO real outbound writes (no Zoho Backstage / Booking calls, no WooCommerce writes, coordinator notifications routed to TestCoordinatorEmail). Surfaced as the TestMode__Enabled app setting; the .NET app binds this via TestModeOptions. Defaults are set in main.bicep based on environmentName (true for dev, false for prod).')
param testModeEnabled bool

@description('§340-H MASTER SWITCH for outbound writes to third-party systems (Zoho Backstage, e-conomic, LinkedIn, SharePoint). Surfaced as Integrations__AllowExternalWrites. prod true / dev false, set in main.bicep from environmentName. The .NET default is FALSE so an unconfigured host never reaches a third party; an organizer can override per edition on the Settings page.')
param allowExternalWrites bool

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
        // Outbound-email safety. §330 NOTE: the 2026-06-16 allowlist directive was
        // SUPERSEDED by §234 — audience control is RINGS ONLY, enforced per recipient in
        // BrevoEmailSender, plus Email__KillSwitch and the per-hour ceiling (§326av). The
        // Email__OnlySendTo setting this module used to emit is READ BY NOTHING, so
        // emitting it made a redeploy look like it restored a protection that no longer
        // exists — worse than emitting nothing. Removed.
        // RedirectAllTo stays pinned EMPTY here: prod must never fan all mail into one
        // mailbox. DEV sets it deliberately as a live app setting.
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
        // §392: the READ side of telemetry — the organizer's Platform-health page runs KQL
        // against this component as the app's managed identity (see the Monitoring Reader
        // assignment below). The connection string above is write-only plumbing and cannot
        // be used to query, which is why this is a second, separate setting.
        //
        // SLOT WARNING (the §347 lesson, and the reason this belongs in Bicep at all): app
        // settings SWAP WITH THE SLOT unless marked sticky. Set by hand on one slot only,
        // this page works until the next swap and then silently stops — so it is emitted
        // here, from the template, for whichever site this module deploys.
        {
          name: 'Telemetry__AppInsightsResourceId'
          value: appInsightsResourceId
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
        // docs/DESIGN.md §4. Empty until the embedding origin is set.
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
        // §340-H: the environment half of the external-write switch. dev false /
        // prod true. The web app reaches Zoho too (sponsor provisioning, the
        // organizer push buttons), so it is gated identically to the Jobs host.
        {
          name: 'Integrations__AllowExternalWrites'
          value: string(allowExternalWrites)
        }
        // 🔴 §1037 — THE PER-SYSTEM CEILING (operator 2026-08-10). The single switch above could
        // only say "every system" or "no system", so DEV had to be "no system" — while what he
        // needs is DEV exercising ERP and SharePoint and never touching Zoho:
        //   "dev must newer WRITE to zoho, but it is allowed to read from zoho. dev is allowed to
        //    readwrite to erp. ... dev is not allowed to publish on linkedin. dev is allowed to
        //    write to sharepoint (as it has its own separate path)."
        // 🔒 A system NOT named here falls back to Integrations__AllowExternalWrites, so prod keeps
        // writing everywhere and an unconfigured host still writes nothing.
        {
          name: 'Integrations__ExternalWrites__Zoho'
          value: string(allowExternalWrites)
        }
        {
          name: 'Integrations__ExternalWrites__LinkedIn'
          value: string(allowExternalWrites)
        }
        {
          name: 'Integrations__ExternalWrites__Erp'
          value: 'true'
        }
        {
          name: 'Integrations__ExternalWrites__SharePoint'
          value: 'true'
        }
        // 🔴 §1041 — THE WEBSHOP (Company Manager / WordPress on the public site). Added after a
        // DEV run reported 53 billing updates against the LIVE webshop: CompanyManagerClient had
        // never been wired to the write guard at all, so any host with the credentials could write.
        // DEV and PROD share ONE Company Manager, so this follows allowExternalWrites (dev false).
        {
          name: 'Integrations__ExternalWrites__Webshop'
          value: string(allowExternalWrites)
        }
      ]
    }
  }
}

// §392: the web app's MI must be able to READ the Application Insights component, or the
// Platform-health page renders its "could not read telemetry" message forever. Built-in role
// 'Monitoring Reader' id 43d0d8ad-25c7-4714-9337-8ba259a9fe05 — read-only by definition, so this
// grants the ability to see telemetry and nothing else.
//
// Scoped to the RESOURCE GROUP rather than the component: this module does not own the AI resource
// (monitoring.bicep does), and a cross-module resource scope would need it passed in as an existing
// reference. Monitoring Reader at RG scope is still read-only, and the RG holds only this platform's
// own resources.
//
// Deterministic GUID name so a redeploy is idempotent. Conditional on the resource id being set, so
// an environment that deliberately runs without the page does not get a stray assignment.
var monitoringReaderRoleId = '43d0d8ad-25c7-4714-9337-8ba259a9fe05'

resource webAppMonitoringReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(appInsightsResourceId)) {
  name: guid(resourceGroup().id, webApp.id, monitoringReaderRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringReaderRoleId)
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output webAppName string = webApp.name
output webAppHostname string = webApp.properties.defaultHostName
output webAppPrincipalId string = webApp.identity.principalId
output appServicePlanId string = appServicePlan.id
