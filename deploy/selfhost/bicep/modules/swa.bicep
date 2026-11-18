// Portal: Azure Static Web Apps. The Next.js app is a pure static export (output: 'export'),
// so there is no SSR runtime and Free tier is enough for a lab.
// The SWA "location" is only where its management plane lives; content is served globally.

param name string
@description('SWA metadata regions are limited (westus2, centralus, eastus2, westeurope, eastasia). UK South is not one of them.')
@allowed(['westeurope', 'westus2', 'centralus', 'eastus2', 'eastasia'])
param location string = 'westeurope'
@allowed(['Free', 'Standard'])
param sku string = 'Free'
param tags object

resource swa 'Microsoft.Web/staticSites@2023-12-01' = {
  name: name
  location: location
  tags: union(tags, { 'azd-service-name': 'web' })
  sku: { name: sku, tier: sku }
  properties: {
    // No repository link: we deploy the pre-built export with the SWA CLI + deployment token.
    stagingEnvironmentPolicy: 'Disabled'
    allowConfigFileUpdates: true
  }
}

output name string = swa.name
output defaultHostName string = swa.properties.defaultHostname
