// Autopilot-Monitor — Log Analytics workspace transformation (ingestion-time filter)
//
// Two ingestion-time filters. (2) Successful Azure SignalR REST dependencies emitted by the
// HOST process for the SignalR output binding (SDKVersion rdddsc:*) — the worker-side
// StorageDependencyFilterProcessor never sees those. Failed SignalR calls are kept.
// (1) Drops the redundant HOST-side HTTP request record from AppRequests so that exactly one
// canonical request row remains: the worker-side copy emitted by RequestTelemetryMiddleware
// (Properties.Source == 'WorkerMiddleware', carries TenantId/UserId/CorrelationId and is
// sampling-exempt per item). Non-HTTP host invocations (timer/queue triggers) have an empty
// Url and are kept — the worker middleware never sees them.
//
// Why ingestion-time: the host request item is emitted by the Functions host process and
// cannot be suppressed from worker code or host.json (verified 2026-06-09, see Program.cs).
// Live numbers 2026-08-23 (7d): host copy 274k rows / 323 MB, worker copy 189k rows / 208 MB.
//
// Cost note: transformation filtering is free up to 50% of the table's incoming volume; the
// portion beyond that (here ~11% of AppRequests) is billed at the (cheap) data-processing rate.
//
// Apply (two steps; the link is a CLI update = GET+PUT, which keeps retention/SKU intact):
//   az deployment group create -g rg-autopilotmonitor-prd-gwc \
//     --template-file infra/appinsights-workspace-transforms.bicep \
//     --query properties.outputs.dcrId.value -o tsv
//   az monitor log-analytics workspace update -g rg-autopilotmonitor-prd-gwc -n AutopilotMonitor \
//     --data-collection-rule <dcrId-from-above>
//
// Verify after ~20 min (ingestion latency):
//   AppRequests | where TimeGenerated > ago(15m) | where AppRoleName == 'autopilotmonitor-api-eu'
//   | summarize rows=count() by source=tostring(Properties.Source), hasUrl=isnotempty(Url)
//   → only (WorkerMiddleware, true) and ('', false) buckets remain.
//
// Rollback: az monitor log-analytics workspace update -g <rg> -n AutopilotMonitor
//   --set properties.defaultDataCollectionRuleResourceId=null   (then delete the DCR)

@description('Log Analytics workspace that backs the workspace-based Application Insights resources.')
param workspaceName string = 'AutopilotMonitor'

param location string = resourceGroup().location

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: workspaceName
}

resource transforms 'Microsoft.Insights/dataCollectionRules@2023-03-11' = {
  name: 'dcr-autopilotmonitor-ws-transforms'
  location: location
  kind: 'WorkspaceTransforms'
  properties: {
    description: 'AppRequests: keep the enriched worker request record (Properties.Source == WorkerMiddleware) and non-HTTP host invocations (empty Url); drop the host HTTP duplicate.'
    destinations: {
      logAnalytics: [
        {
          workspaceResourceId: workspace.id
          name: 'ws'
        }
      ]
    }
    dataFlows: [
      {
        streams: [ 'Microsoft-Table-AppRequests' ]
        destinations: [ 'ws' ]
        transformKql: 'source | where tostring(Properties.Source) == \'WorkerMiddleware\' or isempty(Url)'
      }
      {
        // Successful Azure SignalR REST calls (group add/remove per connection) are emitted by
        // the HOST process for the SignalR output binding (SDKVersion rdddsc:*), so the worker's
        // StorageDependencyFilterProcessor never sees them. Failed SignalR calls are kept.
        streams: [ 'Microsoft-Table-AppDependencies' ]
        destinations: [ 'ws' ]
        transformKql: 'source | where not(Success == true and Target endswith \'.service.signalr.net\')'
      }
    ]
  }
}

// A workspace has exactly one transformation DCR, activated by linking it as the workspace's
// defaultDataCollectionRuleResourceId — deliberately NOT modelled here (see Apply above).

output dcrId string = transforms.id
