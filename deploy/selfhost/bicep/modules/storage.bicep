// Two storage accounts, on purpose:
//   data  = the product's Table/Queue/Blob data (tables are created by the app at start-up).
//   host  = Functions host + Flex deployment package. Nothing public, nothing of ours in it.
//
// Shared-key access is OFF on both. The backend uses Managed Identity (AzureStorageAccountName)
// and, for diagnostics uploads, user-delegation SAS (it detects MI and switches mode).
//
// The data account needs ONE public thing: the "agent" container. The signed bootstrap
// scripts download the agent ZIP anonymously (irm ... | iex style), so it must be readable
// without credentials. It is blob-level anonymous read only: no listing, no write.

param location string
param dataStorageName string
param hostStorageName string
@allowed(['Standard_LRS', 'Standard_ZRS', 'Standard_GRS', 'Standard_RAGRS'])
param skuName string = 'Standard_LRS'
param deployContainerName string = 'app-package'
param tags object

resource data 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: dataStorageName
  location: location
  tags: tags
  sku: { name: skuName }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowBlobPublicAccess: true // required for the public "agent" container only
    allowCrossTenantReplication: false
    publicNetworkAccess: 'Enabled' // agents on the internet reach this; see docs for the private-endpoint variant
    accessTier: 'Hot'
  }
}

resource dataBlob 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: data
  name: 'default'
  properties: {
    deleteRetentionPolicy: { enabled: true, days: 7 }
    containerDeleteRetentionPolicy: { enabled: true, days: 7 }
  }
}

// Public read, blob level (no container listing)
resource agentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: dataBlob
  name: 'agent'
  properties: { publicAccess: 'Blob' }
}

// Private containers the CI/deploy workflows write to (version counters/manifests, stats snapshot).
resource versionsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: dataBlob
  name: 'versions'
  properties: { publicAccess: 'None' }
}

resource statsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: dataBlob
  name: 'stats'
  properties: { publicAccess: 'None' }
}

resource host 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: hostStorageName
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    allowBlobPublicAccess: false
    allowCrossTenantReplication: false
  }
}

resource hostBlob 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: host
  name: 'default'
}

resource deployContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: hostBlob
  name: deployContainerName
  properties: { publicAccess: 'None' }
}

output dataStorageId string = data.id
output dataStorageName string = data.name
output dataBlobEndpoint string = data.properties.primaryEndpoints.blob
output dataTableEndpoint string = data.properties.primaryEndpoints.table
output dataQueueEndpoint string = data.properties.primaryEndpoints.queue
output hostStorageId string = host.id
output hostStorageName string = host.name
output hostBlobEndpoint string = host.properties.primaryEndpoints.blob
output deployContainerName string = deployContainer.name
