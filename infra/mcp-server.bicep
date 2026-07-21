// Autopilot-Monitor MCP Server — Azure Container App Infrastructure
//
// Reconciled against the live deployment on 2026-07-21 (rg-autopilotmonitor-prd-gwc,
// Germany West Central). The template had drifted behind two changes made directly
// in Azure: the EU cutover (registry/API renamed) and the ACR hardening (admin user
// disabled, image pull moved to the app's system-assigned identity).
//
// !! BEFORE RE-DEPLOYING THIS TEMPLATE, READ THIS !!
// The live app carries an ingress custom-domain binding for mcp.autopilotmonitor.com
// (SNI-enabled, with a managed certificate). Custom domains are NOT modelled below,
// because the certificate is bound out-of-band. A full template deployment would
// therefore REMOVE that binding and take the public MCP endpoint offline.
// For routine changes (CPU/memory, env vars, image) prefer the surgical CLI form:
//   az containerapp update -n autopilotmonitor-mcp -g rg-autopilotmonitor-prd-gwc --cpu 0.5 --memory 1.0Gi
// Treat this file as the documented desired state and as a rebuild-from-scratch
// recipe — not as a routinely-applied deployment.
//
// On a FROM-SCRATCH deploy the AcrPull assignment below is created only after the
// Container App exists (it needs the app's principalId), so the very first revision
// can fail to pull its image. Re-run the deployment, or restart the revision, once
// the role assignment has propagated. This does not affect updates to a live app.
//
// Deploy (two-stage; required to set the OAuth public URL safely):
//   1. First deploy without mcpPublicUrl:
//      az deployment group create \
//        --resource-group <rg-name> \
//        --template-file infra/mcp-server.bicep \
//        --parameters apiUrl=https://autopilotmonitor-api.azurewebsites.net \
//                     entraClientSecret=<secret-value>
//      This emits `containerAppUrl` as an output (e.g. https://autopilotmonitor-mcp.<env-suffix>.<region>.azurecontainerapps.io).
//
//   2. Re-deploy with that URL pinned, so the OAuth proxy never trusts caller-
//      supplied X-Forwarded-Host / Host headers when building issuer / WWW-
//      Authenticate / redirect_uri metadata:
//        --parameters mcpPublicUrl=<containerAppUrl from step 1>
//
//   When mcpPublicUrl is set, the MCP server uses it verbatim and ignores any
//   forwarded-headers from the request. Without it, the server logs a startup
//   warning and falls back to header-derived URLs (acceptable for local dev,
//   not for production).

@description('Azure region for all resources')
param location string = resourceGroup().location

@description('Name of the Container App')
param containerAppName string = 'autopilotmonitor-mcp'

@description('Name of the Container App Environment')
param environmentName string = 'autopilotmonitor-env'

@description('Name of the Log Analytics Workspace (dedicated for Container Apps)')
param logAnalyticsName string = 'autopilotmonitor-container-logs'

@description('Name of the Azure Container Registry')
param acrName string = 'autopilotmonitoreuacr'

@description('Backend API URL')
param apiUrl string = 'https://autopilotmonitor-api-eu.azurewebsites.net'

@description('Container image tag')
param imageTag string = 'latest'

@description('Entra ID app registration client_id used for the OAuth proxy. Public value (appears in JWT aud claim); not a secret.')
param entraClientId string = '1a400946-62c1-4ab4-aa37-f730ac89704d'

@secure()
@description('Entra ID client secret for OAuth token exchange')
param entraClientSecret string = ''

@description('Comma-separated allowlist of redirect_uri hosts for the OAuth proxy. Loopback is always allowed. Empty = built-in default (Anthropic, OpenAI, Google).')
param mcpAllowedRedirectHosts string = ''

@description('Public URL of the MCP server, used verbatim for OAuth issuer / WWW-Authenticate / redirect metadata. REQUIRED in production: the container refuses to boot without it (host-spoofing defense — see config.ts). Set on the second deploy with the value of the containerAppUrl output from the first deploy. The stage-1 deploy is safe because minReplicas=0 means no container runs until this is pinned. Empty falls back to X-Forwarded-Host headers only outside production (NODE_ENV != production).')
param mcpPublicUrl string = ''

@secure()
// NOTE: avoid characters outside cp1252 here (this one used to say a Unicode
// >=). `az bicep build --stdout` writes through the Windows console codec and
// dies with a UnicodeEncodeError on anything it cannot map. Em dashes are fine.
@description('Base64-encoded HMAC key (>=32 bytes after decode) for signing the OAuth proxy state. Naming and format match the backend\'s PaginationTokenSigningKey for consistency. Empty = generate per-instance random key on startup (acceptable for single-replica deployments since state has 10 min lifetime). Generate with PowerShell: [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))')
param oAuthStateSigningKey string = ''

@description('Enable structured tool-call logging to stderr (queryable via Container App Logs)')
param toolLoggingEnabled bool = false

// --- Azure Container Registry ---

// Admin user is DISABLED: a registry-wide username/password pair is a static,
// non-rotating credential that would also have to live in a Container App secret.
// The app pulls with its system-assigned identity + AcrPull instead (see below).
resource acr 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: acrName
  location: location
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
  }
}

// AcrPull for the Container App's system-assigned identity — this is what replaces
// the admin credentials. Without it the app cannot pull its own image.
//
// NOTE: this assignment exists on the live registry but could not be enumerated
// during reconciliation (the CLI session lacks Microsoft.Authorization read), so it
// is reproduced here from the documented pattern rather than read back from Azure.
// Verify with:
//   az role assignment list --scope <acr-id> --assignee <containerApp principalId>
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  scope: acr
  name: guid(acr.id, containerAppName, acrPullRoleId)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
    principalId: containerApp.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

// --- Log Analytics Workspace (required by Container App Environment) ---

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    sku: {
      name: 'PerGB2018'
    }
    retentionInDays: 30
  }
}

// --- Container App Environment ---

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey: logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

// --- Container App ---

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: containerAppName
  location: location
  // System-assigned identity: the credential the app uses to pull from the ACR
  // (see the AcrPull assignment above). Replaces the former admin-user secret.
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      // 'system' = pull with the app's system-assigned identity. No username, no
      // password secret — nothing to rotate and nothing to leak from the app config.
      registries: [
        {
          server: acr.properties.loginServer
          identity: 'system'
        }
      ]
      secrets: [
        {
          name: 'entra-client-secret'
          value: entraClientSecret
        }
        {
          name: 'oauth-state-signing-key'
          value: oAuthStateSigningKey
        }
      ]
      activeRevisionsMode: 'Single'
    }
    template: {
      containers: [
        {
          name: 'mcp-server'
          image: '${acr.properties.loginServer}/${containerAppName}:${imageTag}'
          // 0.5 vCPU is deliberate, not a default. The corpus embeddings are
          // precomputed at image build time, so steady-state search is cheap —
          // but a scale-to-zero cold start still loads the ~23 MB ONNX embedding
          // model to embed the incoming QUERY, and that is seconds of pure CPU on
          // the critical path of the first search. Doubling the vCPU roughly
          // halves it. Container Apps only accepts fixed CPU/memory pairs, so the
          // memory moves with it. Do not "optimize" this back to 0.25.
          resources: {
            cpu: json('0.5')
            memory: '1.0Gi'
          }
          env: [
            { name: 'AUTOPILOT_API_URL', value: apiUrl }
            { name: 'PORT', value: '8080' }
            { name: 'AUTOPILOT_ENTRA_CLIENT_ID', value: entraClientId }
            { name: 'AUTOPILOT_ENTRA_CLIENT_SECRET', secretRef: 'entra-client-secret' }
            { name: 'MCP_ALLOWED_REDIRECT_HOSTS', value: mcpAllowedRedirectHosts }
            { name: 'MCP_PUBLIC_URL', value: mcpPublicUrl }
            { name: 'OAuthStateSigningKey', secretRef: 'oauth-state-signing-key' }
            { name: 'MCP_TOOL_LOGGING', value: toolLoggingEnabled ? 'true' : 'false' }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              periodSeconds: 30
              initialDelaySeconds: 10
            }
            {
              type: 'Readiness'
              httpGet: {
                path: '/health'
                port: 8080
              }
              periodSeconds: 10
              initialDelaySeconds: 5
            }
          ]
        }
      ]
      scale: {
        minReplicas: 0
        maxReplicas: 1
        rules: [
          {
            name: 'http-scaler'
            http: {
              metadata: {
                concurrentRequests: '10'
              }
            }
          }
        ]
      }
    }
  }
}

// --- Outputs ---

output containerAppUrl string = 'https://${containerApp.properties.configuration.ingress.fqdn}'
output mcpEndpoint string = 'https://${containerApp.properties.configuration.ingress.fqdn}/mcp'
output acrLoginServer string = acr.properties.loginServer
