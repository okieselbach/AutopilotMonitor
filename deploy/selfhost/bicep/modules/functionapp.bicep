// Backend API: Azure Functions, Flex Consumption (Linux), .NET 10 isolated.
//
// Why Flex Consumption: .NET 10 on Linux is not supported on the classic Consumption plan, and
// the backend source itself comments on Flex scale-controller behaviour. Scale-to-zero billing.
//
// Authentication of devices = mutual TLS with the Intune MDM client certificate. That is why
// clientCertEnabled is on. See docs/02 (gap G6) for the Optional vs OptionalInteractiveUser trade-off.

param location string
param planName string
param functionAppName string
param tags object

param hostStorageName string
param deployContainerName string
param dataStorageName string
param keyVaultName string
param appInsightsConnectionString string
param appInsightsAppId string
param appInsightsName string

@description('Entra app (client) id of the multi-tenant API app. Empty on the first pass.')
param entraClientId string = ''
@description('Origins allowed to call the API from a browser (the portal).')
param corsOrigins array
param mcpPublicUrl string = ''
param rulesBaseUrl string

@allowed(['Optional', 'OptionalInteractiveUser', 'Required'])
param clientCertMode string = 'Optional'
@description('Max on-demand instances. Lowest allowed is 1. Cost guard for a lab.')
@minValue(1)
param maxInstances int = 10
@allowed([512, 2048, 4096])
param instanceMemoryMB int = 2048

var kvUri = 'https://${keyVaultName}${environment().suffixes.keyvaultDns}'
// Key Vault reference syntax: the platform resolves these with the app's managed identity.
// Secrets are created by scripts/03-Initialize-Platform.ps1 and 02-New-EntraAppRegistration.ps1.
var kvRef = '@Microsoft.KeyVault(SecretUri=${kvUri}/secrets/{0}/)'

resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: hostStorageName }
resource dataStorage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: dataStorageName }
resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource appi 'Microsoft.Insights/components@2020-02-02' existing = { name: appInsightsName }

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: planName
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { name: 'FC1', tier: 'FlexConsumption' }
  properties: { reserved: true }
}

var baseSettings = [
  // Functions host storage over identity (no keys, shared key is disabled on the account)
  { name: 'AzureWebJobsStorage__accountName', value: hostStorageName }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }

  // ---- Product data account (Managed Identity path in TableStorageService / BlobStorageService / QueueClientFactory)
  { name: 'AzureStorageAccountName', value: dataStorageName }
  // [QueueTrigger] connection: identity form, resolved by the Functions host itself
  { name: 'DataStorage__queueServiceUri', value: 'https://${dataStorageName}.queue.${environment().suffixes.storage}' }

  // ---- Entra (multi-tenant API app)
  { name: 'EntraId__ClientId', value: entraClientId }
  { name: 'EntraId__ClientSecret', value: format(kvRef, 'entra-client-secret') }

  // ---- Secrets (Key Vault references)
  { name: 'AzureSignalRConnectionString', value: format(kvRef, 'signalr-connection-string') }
  { name: 'PaginationTokenSigningKey', value: format(kvRef, 'pagination-signing-key') }
  { name: 'OpsAlertWebhookSecret', value: format(kvRef, 'ops-alert-webhook-secret') }

  // ---- KQL proxy / MCP usage overview (optional features; harmless when unset)
  { name: 'APPINSIGHTS_APP_ID', value: appInsightsAppId }

  // ---- Misc
  { name: 'McpServerUrl', value: mcpPublicUrl }
  // Where "reseed from GitHub" pulls rules, ime patterns and CPE maps from. Defaults upstream to a
  // third-party main branch. Point it at YOUR fork, ideally a pinned commit (supply-chain control).
  { name: 'GitHub__RulesBaseUrl', value: rulesBaseUrl }
]

resource func 'Microsoft.Web/sites@2024-04-01' = {
  name: functionAppName
  location: location
  tags: union(tags, { 'azd-service-name': 'api' })
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    clientCertEnabled: true
    clientCertMode: clientCertMode
    keyVaultReferenceIdentity: 'SystemAssigned'
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${hostStorage.properties.primaryEndpoints.blob}${deployContainerName}'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      scaleAndConcurrency: {
        maximumInstanceCount: maxInstances
        instanceMemoryMB: instanceMemoryMB
      }
      runtime: { name: 'dotnet-isolated', version: '10.0' }
    }
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: false // client-cert + HTTP/2 do not mix when renegotiation is in play
      cors: { allowedOrigins: corsOrigins, supportCredentials: false }
      appSettings: baseSettings
    }
  }
}

// ---------------------------------------------------------------- RBAC (least privilege, resource scope)

// Host storage: what the Functions host needs for AzureWebJobsStorage__accountName + Flex deployment package
var hostRoles = [
  'b7e6dc6d-f1e8-4753-8033-0f276bb0955b' // Storage Blob Data Owner
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88' // Storage Queue Data Contributor
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor
]
resource hostRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for r in hostRoles: {
  name: guid(hostStorage.id, func.id, r)
  scope: hostStorage
  properties: {
    principalId: func.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', r)
  }
}]

// Data storage: tables (data), queues (workers + triggers), blobs (agent, diagnostics, backups; user-delegation SAS)
var dataRoles = [
  '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3' // Storage Table Data Contributor
  '974c5e8b-45b9-4653-ba55-5f855dd0fb88' // Storage Queue Data Contributor
  '8a0f0c08-91a1-4084-bc3d-661d67233fed' // Storage Queue Data Message Processor (queue triggers)
  'ba92f5b4-2d11-453d-a403-e96b0029c9fe' // Storage Blob Data Contributor (includes generateUserDelegationKey)
]
resource dataRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for r in dataRoles: {
  name: guid(dataStorage.id, func.id, r)
  scope: dataStorage
  properties: {
    principalId: func.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', r)
  }
}]

resource kvRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, func.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: kv
  properties: {
    principalId: func.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User
  }
}

// KQL proxy (Global Admin "raw logs") reads App Insights through the data API with the app's MI
resource aiRbac 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appi.id, func.id, '43d0d8ad-25c7-4714-9337-8ba259a9fe05')
  scope: appi
  properties: {
    principalId: func.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '43d0d8ad-25c7-4714-9337-8ba259a9fe05') // Monitoring Reader
  }
}

output functionAppName string = func.name
output defaultHostName string = func.properties.defaultHostName
output principalId string = func.identity.principalId
