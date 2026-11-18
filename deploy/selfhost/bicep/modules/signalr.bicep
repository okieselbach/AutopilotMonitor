// Real-time updates for the portal. Serverless mode: the backend uses the Management SDK
// (ServiceManager) and the negotiate endpoint, there is no hub server connection.
//
// Free_F1 = 20 concurrent connections, 20,000 messages/day. Fine for a lab; one busy admin
// screen on a big enrollment day will hit it. Standard_S1 = 1,000 connections, 1M msgs/day per unit.
// NOTE: Free <-> Standard changes the public IP and can cause 30-60 minutes of DNS propagation.

param location string
param name string
@allowed(['Free_F1', 'Standard_S1'])
param skuName string = 'Free_F1'
param portalOrigins array
param tags object

resource sig 'Microsoft.SignalRService/signalR@2024-03-01' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuName == 'Free_F1' ? 'Free' : 'Standard'
    capacity: 1
  }
  kind: 'SignalR'
  properties: {
    features: [
      { flag: 'ServiceMode', value: 'Serverless' }
    ]
    cors: { allowedOrigins: portalOrigins }
    tls: { clientCertEnabled: false }
    publicNetworkAccess: 'Enabled'
    disableLocalAuth: false // backend reads a connection string (AzureSignalRConnectionString); no MI path in code
  }
}

output id string = sig.id
output name string = sig.name
output hostName string = sig.properties.hostName
