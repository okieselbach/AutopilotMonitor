// MCP server: Node 22 container on Azure Container Apps, scale-to-zero, plus ACR Basic.
//
// Deployed in the SECOND pass only (deployMcp = true) because:
//   - Container Apps resolves Key Vault secret references at creation time; the secret must exist.
//   - The image must exist in ACR (a placeholder image is used until you build the real one).
//
// MCP_PUBLIC_URL is derived from the environment's default domain inside this template, so no
// separate "stage 1 / stage 2" URL pinning is needed. The server refuses to boot in production
// without it (see src/config.ts), which is why it is always set.

param location string
param tags object
param acrName string
param environmentName string
param appName string
param identityName string
param keyVaultName string
param workspaceName string

param apiBaseUrl string
param entraClientId string
@description('Placeholder until the real image is pushed to ACR (az acr build), then pass the real reference.')
param image string = 'mcr.microsoft.com/k8se/quickstart:latest'
@description('Entra authority for the MCP sign-in. Upstream default is /organizations (any tenant). For a private instance pin it to your own tenant: https://login.microsoftonline.com/<tenant-id>. The backend still enforces roles either way; this is defence in depth.')
param entraAuthority string = ''
param minReplicas int = 0
param maxReplicas int = 2

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource law 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = { name: workspaceName }

resource uami 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: identityName
  location: location
  tags: tags
}

resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(acr.id, uami.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: acr
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d') // AcrPull
  }
}

resource kvUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(kv.id, uami.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: kv
  properties: {
    principalId: uami.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User
  }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: law.properties.customerId
        sharedKey: law.listKeys().primarySharedKey
      }
    }
  }
}

var publicUrl = 'https://${appName}.${env.properties.defaultDomain}'

var baseEnv = [
  { name: 'AUTOPILOT_API_URL', value: apiBaseUrl }
  { name: 'MCP_PUBLIC_URL', value: publicUrl }
  { name: 'AUTOPILOT_ENTRA_CLIENT_ID', value: entraClientId }
  { name: 'AUTOPILOT_ENTRA_CLIENT_SECRET', secretRef: 'entra-client-secret' }
]
// Hosts allowed as OAuth redirect targets: the default (claude.ai, chatgpt.com, vscode.dev, loopback) is
// already right. Only add MCP_ALLOWED_REDIRECT_HOSTS to ALLOW a self-hosted AI client.
var authorityEnv = empty(entraAuthority) ? [] : [
  { name: 'AUTOPILOT_ENTRA_AUTHORITY', value: entraAuthority }
]

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${uami.id}': {} }
  }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        { server: acr.properties.loginServer, identity: uami.id }
      ]
      secrets: [
        {
          name: 'entra-client-secret'
          keyVaultUrl: '${kv.properties.vaultUri}secrets/entra-client-secret'
          identity: uami.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'mcp'
          image: image
          resources: { cpu: json('0.25'), memory: '0.5Gi' }
          env: concat(baseEnv, authorityEnv)
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [
          { name: 'http', http: { metadata: { concurrentRequests: '20' } } }
        ]
      }
    }
  }
  dependsOn: [acrPull, kvUser]
}

output acrName string = acr.name
output acrLoginServer string = acr.properties.loginServer
output appName string = app.name
output publicUrl string = publicUrl
output identityPrincipalId string = uami.properties.principalId
