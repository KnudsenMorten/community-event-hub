// ===========================================================================
//  main.bicep  -  Community Hub : full Azure infrastructure (Stage 1)
// ===========================================================================
//  Deploys, into one resource group, the complete environment for the
//  evergreen "community-hub" application (see CONTEXT.md):
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
//  Deploy:  see scripts/deploy.sh and docs/RUNBOOK.md
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

@description('SQL administrator login name.')
param sqlAdminLogin string = 'communityhubadmin'

@description('SQL administrator password. Supply at deploy time (deploy.sh prompts / reads it); never commit a value.')
@secure()
param sqlAdminPassword string

@description('Zoho Backstage origin allowed to embed the hub in an iframe (e.g. https://eldk27.expertslive.dk). Empty until confirmed - see CONTEXT.md 5a / open question 13.')
param backstageEmbedOrigin string = ''

@description('Custom hostname this environment will be reached at after the post-deploy DNS + binding step (e.g. test.hub.eldk27.expertslive.dk for dev, hub.eldk27.expertslive.dk for prod). Informational only -- the binding itself is a post-deploy step in docs/RUNBOOK.md §4.2 because the DNS CNAME must exist + be verified before Azure can attach the hostname. Surfaced as an output so the operator sees the exact target without grepping the parameter file.')
param customDomain string = ''

@description('TEST MODE master switch (CommunityHub.Core.Integrations.TestModeOptions.Enabled). When true the integrations perform NO real outbound writes: no Zoho Backstage / Booking calls, no WooCommerce writes, coordinator notifications routed to the test address only. Both dev + prod share the SAME upstream services (Zoho Backstage, Zoho Booking, WooCommerce store) -- TestMode is the safety latch that lets dev READ live data without WRITING. Defaults to true for dev, false for prod.')
param testModeEnabled bool = (environmentName == 'dev')

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
    sqlAdminLogin:    sqlAdminLogin
    sqlAdminPassword: sqlAdminPassword
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
    backstageEmbedOrigin:        backstageEmbedOrigin
    customDomain:                customDomain
    testModeEnabled:             testModeEnabled
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

// NOTE - custom domain (hub.eldk27.expertslive.dk / test.hub.eldk27.expertslive.dk):
//  The custom-domain binding + managed certificate is intentionally NOT in
//  this template. It requires a DNS record (CNAME -> webAppHostname) to exist
//  and be verified FIRST, which cannot happen inside the same deployment.
//  It is a documented post-deploy step in docs/RUNBOOK.md §4.2. The
//  customDomain parameter is informational + exported as an output so the
//  operator sees the exact hostname they need to bind without grepping the
//  parameter file.
//
// NOTE - dual-env design:
//  dev + prod share the SAME upstream services (Zoho Backstage, Zoho Booking,
//  WooCommerce store, Brevo, Company Manager). Only the CEH itself (web app,
//  SQL, storage, custom hostname) is duplicated per env. dev defaults to
//  TestMode (no real outbound writes) so live data can be read for testing
//  without polluting prod-side state. See docs/RUNBOOK.md §1.1.
