// ===========================================================================
//  main.bicep  -  Community Hub : full Azure infrastructure (Stage 1)
// ===========================================================================
//  Deploys, into one resource group, the complete environment for the
//  evergreen "community-hub" application (see docs/DESIGN.md §11):
//      - Log Analytics + Application Insights        (monitoring.bicep)
//      - Key Vault                                   (keyvault.bicep)
//      - Azure SQL server + database                 (sql.bicep)
//      - Storage account + uploads container         (storage.bicep)
//      - Linux App Service plan + web app            (appservice.bicep)
//      - Functions app (timer-triggered scheduler)   (functions.bicep)
//
//  Multi-environment: the same template deploys `dev` and `prod` - pass
//  environmentName. Resource names are suffixed per environment so both can
//  coexist. The app itself is evergreen (one code base, an Events table row
//  per edition); the year never appears in infrastructure, only in the DNS
//  hostname and user-facing labels.
//
//  Deploy:  see scripts/deploy.sh and README.md "Getting started"
//  Scope:   resource group (create the RG first - deploy.sh does this).
// ===========================================================================

targetScope = 'resourceGroup'

// --- Parameters ------------------------------------------------------------

@description('Deployment environment. Drives resource-name suffixes and lets dev + prod coexist.')
@allowed([ 'dev', 'prod' ])
param environmentName string

@description('Azure region for all resources. Default West Europe - EU data residency, low latency to Copenhagen, full service availability.')
param location string = 'westeurope'

@description('Short base name for resources. Keep lowercase, no spaces.')
param baseName string = 'communityhub'

// SQL admin login + password parameters were removed: the SQL server is
// Azure-AD-only (managed-identity auth) and no SQL login or password is
// provisioned. sql.bicep keeps optional, defaulted sqlAdminLogin/Password
// params only as a fallback for azureADOnlyAuthentication=false, which this
// template never enables. Emitting a Sql__Admin* app setting made the app take
// the SQL-auth path and Migrate() failed 500 against the AAD-only server.

// §961 — the Entra SQL admin group moved UP to here (and on into the per-environment parameters
// files, which are denylisted from the public mirror). It used to be a hardcoded default inside
// modules/sql.bicep, which published a real Entra group name + object id AND silently baked our
// admin group into anyone else's deployment of this template.
@description('Entra (Azure AD) group that administers the SQL server. Supply your own — members can connect as SQL admin via Entra auth.')
param sqlAadAdminLogin string

@description('Object id (sid) of the Entra group named in sqlAadAdminLogin.')
param sqlAadAdminObjectId string

@description('Zoho Backstage origin allowed to embed the hub in an iframe (e.g. https://your-event-portal.example). Empty = the hub cannot be framed.')
param backstageEmbedOrigin string = ''

@description('Custom hostname this environment will be reached at after the post-deploy DNS + binding step (e.g. dev.hub.your-event.example for dev, hub.your-event.example for prod). Informational only -- the binding itself is a post-deploy step (README.md "Getting started") because the DNS CNAME must exist + be verified before Azure can attach the hostname. Surfaced as an output so the operator sees the exact target without grepping the parameter file.')
param customDomain string = ''

@description('TEST MODE master switch (CommunityHub.Core.Integrations.TestModeOptions.Enabled). When true the integrations perform NO real outbound writes: no Zoho Backstage / Booking calls, no WooCommerce writes, coordinator notifications routed to the test address only. Both dev + prod share the SAME upstream services (Zoho Backstage, Zoho Booking, WooCommerce store) -- TestMode is the safety latch that lets dev READ live data without WRITING. Defaults to true for dev, false for prod.')
param testModeEnabled bool = (environmentName == 'dev')

@description('§340-H MASTER SWITCH for outbound WRITES to third-party systems (Zoho Backstage, e-conomic, LinkedIn, SharePoint), surfaced as Integrations__AllowExternalWrites on BOTH hosts. This is the environment half of the guarantee that DEV cannot change external systems: as the testModeEnabled description above states, dev and prod share the SAME upstream Zoho / WooCommerce, and TestMode covered only the exhibitor + ERP seams -- the Zoho session and speaker pushes were gated solely by per-edition feature flags, which are an EDITION concept, not an environment one. Defaults to true for prod, false for dev. An organizer can override it per edition on the Settings page ("except if i specifically enable so it can do this").')
param allowExternalWrites bool = (environmentName != 'dev')

// --- Naming ----------------------------------------------------------------
//  A short suffix keeps globally-unique names (Key Vault, SQL, Storage) within
//  length limits while staying readable. uniqueString keeps them collision-safe.

var suffix = '${environmentName}${uniqueString(resourceGroup().id)}'
var shortSuffix = substring(suffix, 0, 8)

var names = {
  logAnalytics:        '${baseName}-log-${environmentName}'
  appInsights:         '${baseName}-ai-${environmentName}'
  keyVault:            'kv${baseName}${shortSuffix}'          // <=24 chars
  sqlServer:           '${baseName}-sql-${shortSuffix}'
  sqlDatabase:         '${baseName}-db'
  storageAccount:      'st${baseName}${shortSuffix}'          // <=24, a-z0-9
  functionsStorage:    'stfn${baseName}${shortSuffix}'        // separate acct
  appServicePlan:      '${baseName}-plan-${environmentName}'
  webApp:              '${baseName}-web-${shortSuffix}'
  functionsPlan:       '${baseName}-fnplan-${environmentName}'
  functionsApp:        '${baseName}-fn-${shortSuffix}'
}

var tags = {
  project:     'community-hub'
  environment: environmentName
  managedBy:   'bicep'
}

// --- Monitoring (first - other modules consume its connection string) ------

module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location:         location
    logAnalyticsName: names.logAnalytics
    appInsightsName:  names.appInsights
    tags:             tags
  }
}

// --- Key Vault -------------------------------------------------------------
//  Created before the apps, but the apps' managed identities are not known
//  until they exist. The grant of GET/LIST to those identities is done by a
//  second, post-app module pass (keyvaultAccess) below.

module keyVault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location:            location
    keyVaultName:        names.keyVault
    tags:                tags
    readerPrincipalIds:  []   // populated after the apps exist (see below)
  }
}

// --- SQL -------------------------------------------------------------------

module sql 'modules/sql.bicep' = {
  name: 'sql'
  params: {
    location:         location
    sqlServerName:    names.sqlServer
    sqlDatabaseName:  names.sqlDatabase
    tags:             tags
    aadAdminLogin:    sqlAadAdminLogin
    aadAdminObjectId: sqlAadAdminObjectId
    // No sqlAdminLogin / sqlAdminPassword passed: the server is Azure-AD-only
    // and authenticates app traffic via managed identity. sql.bicep's optional
    // login/password params stay defaulted (unused while azureADOnlyAuthentication=true).
  }
}

// --- Storage (uploaded files) ----------------------------------------------

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location:           location
    storageAccountName: names.storageAccount
    tags:               tags
  }
}

// --- Web app ---------------------------------------------------------------

module appService 'modules/appservice.bicep' = {
  name: 'appservice'
  params: {
    location:                    location
    appServicePlanName:          names.appServicePlan
    webAppName:                  names.webApp
    tags:                        tags
    keyVaultUri:                 keyVault.outputs.keyVaultUri
    sqlConnectionStringTemplate: sql.outputs.sqlConnectionStringTemplate
    blobEndpoint:                storage.outputs.blobEndpoint
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    // §392: the READ side of telemetry. The connection string above says where telemetry
    // GOES; the resource id is what the organizer Platform-health page QUERIES.
    appInsightsResourceId:       monitoring.outputs.appInsightsId
    backstageEmbedOrigin:        backstageEmbedOrigin
    customDomain:                customDomain
    testModeEnabled:             testModeEnabled
    allowExternalWrites:         allowExternalWrites
  }
}

// --- Functions app (scheduler) ---------------------------------------------

module functions 'modules/functions.bicep' = {
  name: 'functions'
  params: {
    location:                    location
    functionsAppName:            names.functionsApp
    functionsPlanName:           names.functionsPlan
    functionsStorageAccountName: names.functionsStorage
    tags:                        tags
    keyVaultUri:                 keyVault.outputs.keyVaultUri
    sqlConnectionStringTemplate: sql.outputs.sqlConnectionStringTemplate
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    // §340-D: the Jobs host is where the TestMode swaps actually happen, so it must
    // receive the SAME explicit value the web app gets -- dev true / prod false. It
    // was never passed here, so BOTH Functions apps fell back to the .NET default:
    // prod silently stubbed Zoho exhibitor + e-conomic writes, and dev depended on
    // that default for its safety rather than on a stated value.
    testModeEnabled:             testModeEnabled
    allowExternalWrites:         allowExternalWrites
  }
}

// --- Key Vault access for the two managed identities -----------------------
//  A second deployment of the keyvault module, now that the web app and
//  Functions app exist and their principalIds are known. Re-running the
//  module is idempotent - it just updates the access policies.

module keyVaultAccess 'modules/keyvault.bicep' = {
  name: 'keyvault-access'
  params: {
    location:           location
    keyVaultName:       names.keyVault
    tags:               tags
    readerPrincipalIds: [
      appService.outputs.webAppPrincipalId
      functions.outputs.functionsAppPrincipalId
    ]
  }
}

// --- Outputs ---------------------------------------------------------------

output webAppHostname string = appService.outputs.webAppHostname
output customDomain string = customDomain
output testModeEnabled bool = testModeEnabled
output functionsAppName string = functions.outputs.functionsAppName
output keyVaultName string = keyVault.outputs.keyVaultName
output sqlServerFqdn string = sql.outputs.sqlServerFqdn
output storageBlobEndpoint string = storage.outputs.blobEndpoint
output appInsightsName string = monitoring.outputs.appInsightsName

// NOTE - custom domain (e.g. hub.your-event.example / dev.hub.your-event.example):
//  The custom-domain binding + managed certificate is intentionally NOT in
//  this template. It requires a DNS record (CNAME -> webAppHostname) to exist
//  and be verified FIRST, which cannot happen inside the same deployment.
//  It is a documented post-deploy step (README.md "Getting started"). The
//  customDomain parameter is informational + exported as an output so the
//  operator sees the exact hostname they need to bind without grepping the
//  parameter file.
//
// NOTE - dual-env design:
//  dev + prod share the SAME upstream services (Zoho Backstage, Zoho Booking,
//  WooCommerce store, Brevo, Company Manager). Only the CEH itself (web app,
//  SQL, storage, custom hostname) is duplicated per env. dev defaults to
//  TestMode (no real outbound writes) so live data can be read for testing
//  without polluting prod-side state. See docs/DESIGN.md §11.
