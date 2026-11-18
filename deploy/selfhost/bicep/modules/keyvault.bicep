// Key Vault: RBAC mode, soft delete + purge protection. Secrets are NOT created here:
// their values come from the Entra app script (client secret) and the init script (random keys),
// so they never pass through a template parameter or the deployment history.

param location string
param name string
param workspaceId string
param tags object

resource kv 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 14
    enablePurgeProtection: true
    publicNetworkAccess: 'Enabled'
    networkAcls: { defaultAction: 'Allow', bypass: 'AzureServices' }
  }
}

resource diag 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-law'
  scope: kv
  properties: {
    workspaceId: workspaceId
    logs: [
      { category: 'AuditEvent', enabled: true }
    ]
  }
}

output id string = kv.id
output name string = kv.name
output uri string = kv.properties.vaultUri
