using 'main.bicep'

// ===== clefsoft.co.uk lab/prod parameters =====
// Pass 1 = defaults below. Pass 2: set deployMcp = true and entraClientId, re-deploy.
// Final re-run: also set mcpPublicUrl from the pass-2 output.

param workload = 'autopilotmonitor'
param env = 'prod'
param locCode = 'uks'

// Storage account names are global. 'stautopilotmonitorprod' may be taken; set 2 chars to disambiguate.
param uniqueSuffix = ''

param entraClientId = '66facfeb-654b-46e6-b342-5fbe0a5b60af'          // <- app id from scripts/02-New-EntraAppRegistration.ps1
param deployMcp = true           // <- true on pass 2
param mcpImage = 'mcr.microsoft.com/k8se/quickstart:latest' // <- <acr>.azurecr.io/autopilotmonitor-mcp:<tag> after az acr build
param mcpPublicUrl = 'https://ca-autopilotmonitor-mcp-prod.ashybeach-700e1175.uksouth.azurecontainerapps.io'           // <- final re-run

// YOUR fork, pinned to a commit once you trust it. Example shape only:
param rulesBaseUrl = 'https://raw.githubusercontent.com/<your-github-user>/AutopilotMonitor/<commit-sha>/rules'

// Custom domains, once Cloudflare DNS + hostnames exist (docs/04 part 2b). Example:
param extraPortalOrigins = ['https://monitor.clefsoft.co.uk']      // ['https://monitor.clefsoft.co.uk']

param clientCertMode = 'Optional'  // test OptionalInteractiveUser too, see docs/02 gap G6
param functionMaxInstances = 10
param signalRSku = 'Free_F1'
param swaSku = 'Free'
param storageSku = 'Standard_LRS'
param logRetentionDays = 30
param logDailyCapGb = 1

// Your own object id: `az ad signed-in-user show --query id -o tsv`. Temporary, remove after go-live.
param deployerObjectId = 'a96cfd2a-5e48-4923-8097-71bc2cc91f96'

param monthlyBudget = 40
param budgetAlertEmail = 'paul.clephan@clefsoft.co.uk'        // <- set to a real mailbox or the budget is skipped
