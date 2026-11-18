// Autopilot Monitor, self-hosted. Resource-group scope.
//
// Two passes (see docs/04):
//   Pass 1: deployMcp=false, entraClientId=''           -> core platform + empty Key Vault
//   (scripts create the Entra app and the secrets)
//   Pass 2: deployMcp=true,  entraClientId='<app id>'   -> sets the client id, adds MCP
//
// The upstream repo does NOT ship infrastructure code: its infra/ folder is a private git
// submodule. Everything here is inferred from the source (Program.cs, Constants.cs, middleware,
// Dockerfile, workflows). Treat it as a reviewed reconstruction, not as upstream's own template.

targetScope = 'resourceGroup'

@description('Short workload name used in resource names.')
param workload string = 'autopilotmonitor'
@allowed(['dev', 'test', 'prod'])
param env string = 'prod'
param location string = resourceGroup().location
@description('Region short code used in the resource group name only.')
param locCode string = 'uks'
@description('Appended to globally-unique names to avoid collisions. Max 2 chars (storage names are limited to 24).')
@maxLength(2)
param uniqueSuffix string = ''

@description('Entra application (client) id of the multi-tenant API app. Empty on pass 1.')
param entraClientId string = ''
@description('Deploy the MCP server (Container App + ACR). Needs the Key Vault secret and entraClientId first.')
param deployMcp bool = false
@description('MCP container image. Placeholder until you have built the real one into ACR.')
param mcpImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
@description('Public URL of the MCP server (output "mcpPublicUrl" of pass 2). Feed it back in on a final re-run so the backend health check points at YOUR MCP server, not upstream.')
param mcpPublicUrl string = ''

@description('Where "reseed" pulls rules from. Use YOUR fork (raw URL up to /rules), ideally pinned to a commit.')
param rulesBaseUrl string = 'https://raw.githubusercontent.com/okieselbach/AutopilotMonitor/refs/heads/main/rules'

@allowed(['Optional', 'OptionalInteractiveUser', 'Required'])
param clientCertMode string = 'Optional'
@minValue(1)
param functionMaxInstances int = 10
@allowed(['Free_F1', 'Standard_S1'])
param signalRSku string = 'Free_F1'
@allowed(['Free', 'Standard'])
param swaSku string = 'Free'
@allowed(['Standard_LRS', 'Standard_ZRS', 'Standard_GRS', 'Standard_RAGRS'])
param storageSku string = 'Standard_LRS'
param logRetentionDays int = 30
param logDailyCapGb int = 1

@description('Object id of the person running the deployment scripts. Gets TEMPORARY data-plane roles (see docs/03). Remove afterwards.')
param deployerObjectId string = ''

@description('Custom portal origins (e.g. https://autopilot.clefsoft.co.uk) added to Function CORS and SignalR CORS next to the default SWA host.')
param extraPortalOrigins array = []

@description('Monthly budget in your billing currency. 0 disables the budget.')
param monthlyBudget int = 0
param budgetAlertEmail string = ''
param budgetStartDate string = utcNow('yyyy-MM-01')

var tags = {
  workload: workload
  environment: env
  managedBy: 'bicep'
  purpose: 'autopilot-monitor-selfhost'
}

// ---------------------------------------------------------------- names
var sfx = uniqueSuffix
var dashSfx = empty(uniqueSuffix) ? '' : '-${uniqueSuffix}'
var nLaw = 'log-${workload}-${env}-${locCode}'
var nAppi = 'appi-${workload}-${env}-${locCode}'
var nDataStorage = take('st${workload}${env}${sfx}', 24) // stautopilotmonitorprod
var nHostStorage = take('st${workload}fn${env}${sfx}', 24) // stautopilotmonitorfnprod
var nKv = take('kv-${workload}-${env}${dashSfx}', 24) // kv-autopilotmonitor-prod (24 chars: no room for a suffix)
var nKvSign = take('kv-${workload}-sign${dashSfx}', 24) // kv-autopilotmonitor-sign: code-signing certificate ONLY
var nSignalR = 'sigr-${workload}-${env}${dashSfx}'
var nPlan = 'asp-${workload}-${env}-${locCode}'
var nFunc = 'func-${workload}-${env}${dashSfx}'
var nSwa = 'swa-${workload}-${env}${dashSfx}'
var nAcr = take('acr${workload}${env}${sfx}', 50) // acrautopilotmonitorprod
var nCaEnv = 'cae-${workload}-${env}-${locCode}'
var nCa = take('ca-${workload}-mcp-${env}', 32)
var nMcpId = 'id-${workload}-mcp-${env}-${locCode}'

// ---------------------------------------------------------------- core
module monitoring 'modules/monitoring.bicep' = {
  name: 'monitoring'
  params: {
    location: location
    workspaceName: nLaw
    appInsightsName: nAppi
    retentionDays: logRetentionDays
    dailyCapGb: logDailyCapGb
    tags: tags
  }
}

module storage 'modules/storage.bicep' = {
  name: 'storage'
  params: {
    location: location
    dataStorageName: nDataStorage
    hostStorageName: nHostStorage
    skuName: storageSku
    tags: tags
  }
}

module keyvault 'modules/keyvault.bicep' = {
  name: 'keyvault'
  params: {
    location: location
    name: nKv
    workspaceId: monitoring.outputs.workspaceId
    tags: tags
  }
}

// Separate vault for the code-signing certificate: whoever can sign can ship code to every enrolling device.
module keyvaultSign 'modules/keyvault.bicep' = {
  name: 'keyvault-sign'
  params: {
    location: location
    name: nKvSign
    workspaceId: monitoring.outputs.workspaceId
    tags: tags
  }
}

module swa 'modules/swa.bicep' = {
  name: 'swa'
  params: {
    name: nSwa
    sku: swaSku
    tags: tags
  }
}

var portalOrigin = 'https://${swa.outputs.defaultHostName}'
var corsOrigins = concat([portalOrigin], extraPortalOrigins)

module signalr 'modules/signalr.bicep' = {
  name: 'signalr'
  params: {
    location: location
    name: nSignalR
    skuName: signalRSku
    portalOrigins: corsOrigins
    tags: tags
  }
}

// McpServerUrl: the backend only uses it for a health probe. If left empty the code falls back to
// upstream's hosted MCP URL (Constants.McpServerBaseUrl), so a lab health page would report on the wrong server.
module func 'modules/functionapp.bicep' = {
  name: 'functionapp'
  params: {
    location: location
    planName: nPlan
    functionAppName: nFunc
    tags: tags
    hostStorageName: storage.outputs.hostStorageName
    deployContainerName: storage.outputs.deployContainerName
    dataStorageName: storage.outputs.dataStorageName
    keyVaultName: keyvault.outputs.name
    appInsightsConnectionString: monitoring.outputs.appInsightsConnectionString
    appInsightsAppId: monitoring.outputs.appInsightsAppId
    appInsightsName: monitoring.outputs.appInsightsName
    entraClientId: entraClientId
    corsOrigins: corsOrigins
    mcpPublicUrl: mcpPublicUrl
    rulesBaseUrl: rulesBaseUrl
    clientCertMode: clientCertMode
    maxInstances: functionMaxInstances
  }
}

module mcp 'modules/mcp.bicep' = if (deployMcp) {
  name: 'mcp'
  params: {
    location: location
    tags: tags
    acrName: nAcr
    environmentName: nCaEnv
    appName: nCa
    identityName: nMcpId
    keyVaultName: keyvault.outputs.name
    workspaceName: nLaw
    apiBaseUrl: 'https://${func.outputs.defaultHostName}'
    entraClientId: entraClientId
    image: mcpImage
    entraAuthority: '${environment().authentication.loginEndpoint}${tenant().tenantId}'
  }
}

// ---------------------------------------------------------------- deployer (temporary) data-plane access
// Shared-key access is disabled, so whoever seeds tables / uploads the agent / writes secrets needs
// real data-plane roles. Remove this assignment after go-live: set deployerObjectId back to ''.
resource dataStorageRef 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: nDataStorage }
resource kvRef 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: nKv }

var deployerDataRoles = [
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe' // Storage Blob Data Contributor
]
resource deployerData 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for r in (empty(deployerObjectId) ? [] : deployerDataRoles): {
  name: guid(dataStorageRef.id, deployerObjectId, r)
  scope: dataStorageRef
  properties: {
    principalId: deployerObjectId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', r)
  }
  dependsOn: [storage]
}]

resource deployerKv 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerObjectId)) {
  name: guid(kvRef.id, deployerObjectId, 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7')
  scope: kvRef
  properties: {
    principalId: deployerObjectId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b86a8fe4-44ce-4948-aee5-eccb2c155cd7') // Key Vault Secrets Officer
  }
  dependsOn: [keyvault]
}

resource kvSignRef 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: nKvSign }
resource deployerKvSign 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(deployerObjectId)) {
  name: guid(kvSignRef.id, deployerObjectId, 'a4417e6f-fecd-4de8-b567-7b0420556985')
  scope: kvSignRef
  properties: {
    principalId: deployerObjectId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a4417e6f-fecd-4de8-b567-7b0420556985') // Key Vault Certificates Officer (import the signing cert)
  }
  dependsOn: [keyvaultSign]
}

module budget 'modules/budget.bicep' = if (monthlyBudget > 0 && !empty(budgetAlertEmail)) {
  name: 'budget'
  params: {
    name: 'budget-${workload}-${env}'
    amount: monthlyBudget
    startDate: budgetStartDate
    alertEmail: budgetAlertEmail
  }
}

// ---------------------------------------------------------------- outputs (consumed by scripts/ and CI)
output resourceGroup string = resourceGroup().name
output functionAppName string = func.outputs.functionAppName
output apiBaseUrl string = 'https://${func.outputs.defaultHostName}'
output portalUrl string = portalOrigin
output staticWebAppName string = swa.outputs.name
output keyVaultName string = keyvault.outputs.name
output signingKeyVaultName string = keyvaultSign.outputs.name
output signingKeyVaultUri string = keyvaultSign.outputs.uri
output dataStorageName string = storage.outputs.dataStorageName
output dataBlobEndpoint string = storage.outputs.dataBlobEndpoint
output agentContainerUrl string = '${storage.outputs.dataBlobEndpoint}agent'
output versionsContainerUrl string = '${storage.outputs.dataBlobEndpoint}versions'
output statsContainerUrl string = '${storage.outputs.dataBlobEndpoint}stats'
output signalRName string = signalr.outputs.name
output appInsightsName string = monitoring.outputs.appInsightsName
output functionPrincipalId string = func.outputs.principalId
output mcpPublicUrl string = deployMcp ? mcp!.outputs.publicUrl : ''
output acrName string = deployMcp ? mcp!.outputs.acrName : nAcr
output containerAppName string = nCa
