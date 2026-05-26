// ===========================================================================
//  monitoring.bicep  -  Log Analytics + Application Insights
// ---------------------------------------------------------------------------
//  Telemetry for both the web app and the Functions app: request logs,
//  exceptions, the reminder-job run history. Kept deliberately simple - one
//  workspace, one Application Insights resource shared by both apps.
// ===========================================================================

@description('Azure region.')
param location string

@description('Log Analytics workspace name.')
param logAnalyticsName string

@description('Application Insights resource name.')
param appInsightsName string

@description('Tags applied to the resources.')
param tags object

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  tags: tags
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

output appInsightsConnectionString string = appInsights.properties.ConnectionString
output appInsightsName string = appInsights.name
output logAnalyticsId string = logAnalytics.id
