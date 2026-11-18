// Log Analytics workspace + workspace-based Application Insights.
// The daily cap is the main cost guard: App Insights is the one line item that can run away.

param location string
param workspaceName string
param appInsightsName string
param retentionDays int = 30
@description('Daily ingestion cap in GB. 0 = no cap (do not do this in a lab).')
param dailyCapGb int = 1
param tags object

resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: retentionDays
    workspaceCapping: dailyCapGb > 0 ? { dailyQuotaGb: dailyCapGb } : null
    features: { enableLogAccessUsingOnlyResourcePermissions: true }
  }
}

resource appi 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  tags: tags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: law.id
    IngestionMode: 'LogAnalytics'
    DisableLocalAuth: false // Functions host still ships telemetry with the connection string
  }
}

output workspaceId string = law.id
output workspaceCustomerId string = law.properties.customerId
output appInsightsId string = appi.id
output appInsightsName string = appi.name
output appInsightsConnectionString string = appi.properties.ConnectionString
// "AppId" is what the backend calls APPINSIGHTS_APP_ID (the KQL proxy uses the AI data API).
output appInsightsAppId string = appi.properties.AppId
