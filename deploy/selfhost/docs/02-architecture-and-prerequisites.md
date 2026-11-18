# 02. Architecture, Azure services and prerequisites

Everything here is read from the source code. Where I had to infer something, it says **(inferred)**.

## Architecture

```
                                  ┌──────────────────────── clefsoft.co.uk (Microsoft 365 / Entra / Intune) ───────────────────────┐
                                  │                                                                                                │
   Autopilot device (OOBE)        │   Intune: platform script (Start-AutopilotMonitor.ps1)  / MSI LOB app (Device Prep)             │
   ┌────────────────────┐ 1.script│   Entra: app registration "Autopilot Monitor (clefsoft)"  (multi-tenant, api://<id>)            │
   │ Start-…ps1 (loader)│◄────────┤   Groups: AutopilotMonitor-Devices, Autopilot-Pilot, AutopilotMonitor-Admins                    │
   │  verifies signature│         │   Graph (app perms): DeviceManagementServiceConfig.Read.All  (+ optional add-ons)               │
   │ Install-…ps1       │         └───────────────▲────────────────────────────────────────────────────▲───────────────────────────┘
   │  verifies catalog  │                         │ 5. Graph app token (client secret)                  │ 6. user sign-in (MSAL, PKCE)
   │ Agent.exe (.NET)   │                         │    validates device in YOUR tenant                  │    + MFA via Conditional Access
   └─────────┬──────────┘                         │                                                     │
             │ 2. download agent ZIP (anonymous)  │                                                     │
             ▼                                    │                                                     │
   ┌───────────────────────────────────── Azure subscription · rg-autopilotmonitor-prod-uks · UK South ───────────────────────────┐
   │                                              │                                                     │                         │
   │  ┌─────────────────────────────┐   3. mTLS   │   ┌──────────────────────────────────┐   4. REST    │   ┌──────────────────┐ │
   │  │ Storage (data)              │  device cert│   │ Function App (Flex Consumption)  │◄─────────────┼───┤ Static Web App   │ │
   │  │  Tables: sessions, events,  │◄────────────┼──►│ func-autopilotmonitor-prod       │   + SignalR  │   │ (Next.js export) │ │
   │  │   tenants, rules, admins …  │  MI (RBAC)  │   │ .NET 10 isolated, system MI      │   negotiate  │   └──────────────────┘ │
   │  │  Queues: analyze, deletion, │             │   │ clientCertEnabled (X-ARR-ClientCert)            │                         │
   │  │   backup, offboarding …     │             │   │ Timers: maintenance 2h, sweep 4h, 12h           │   ┌──────────────────┐   │
   │  │  Blobs: agent/ (PUBLIC),    │             │   └───┬───────────────┬──────────────┬───────────────┘   │ Azure SignalR    │   │
   │  │   versions/, stats/, diag.. │             │       │ KV refs       │ KQL (MI)     │ conn string       │ Serverless, Free │   │
   │  └─────────────────────────────┘             │       ▼               ▼              ▼                   └──────────────────┘   │
   │  ┌─────────────────────────────┐             │  ┌──────────┐  ┌───────────────┐  ┌─────────────────────────┐                   │
   │  │ Storage (host/deploy)       │             │  │Key Vault │  │ App Insights  │  │ Container App (MCP)     │◄── 7. Claude, VS Code│
   │  │ Flex package, host state    │             │  │ secrets  │  │ + Log Analytics│ │ Node 22, scale-to-zero  │    OAuth 2.1 + PKCE │
   │  └─────────────────────────────┘             │  └──────────┘  └───────────────┘  │ + ACR Basic, UAMI       │    (Entra sign-in)  │
   │                                              │                                    └──────────┬──────────────┘                   │
   │                                              │              forwards the USER's token (no stored creds) to the API             │
   └──────────────────────────────────────────────┴──────────────────────────────────────────────────────────────────────────────────┘

   Build/release (GitHub):  fork ─► Actions ─► Bicep (infra) · dotnet publish (backend) · next build (web) · docker build (MCP)
                                     └──► agent: Windows runner · AzureSignTool · Key Vault signing cert ─► blob agent/ + AdminConfiguration row
```

### How one enrollment flows (from the code)

1. Intune delivers the **loader** early in ESP. It checks it is really mid-enrollment (no user profile, uptime under 12 h, no previous run), downloads and **verifies the installer's signature**, then the agent ZIP and its **signed catalog**.
2. The agent **backfills** Intune Management Extension logs from byte zero, then streams events.
3. Each call to `/api/agent/*` is authenticated by the **Intune MDM client certificate** (chain checked against embedded Intune root certificates, so it works in any tenant), tied to the Entra **tenant id** on the device.
4. The backend checks the device is **registered in your tenant** (Graph call, client-credential token for *your* tenant) and that the manufacturer is on the **hardware whitelist**. Until a validation method is switched on, **all agent data is rejected**.
5. Events land in Table Storage; queues fan out analysis (rules engine), vulnerability correlation and deletion work; SignalR pushes the live view.
6. On completion the agent uploads a final batch (optionally a diagnostics package straight to blob by short-lived user-delegation SAS) and **removes itself**, leaving a registry marker so it never reinstalls.

## Azure services required

| Service | Required? | Why (evidence) | Lab SKU |
|---|---|---|---|
| **Azure Functions** | Yes | Backend API + timers + queue triggers. `AutopilotMonitor.Functions.csproj`: `net10.0`, isolated worker, `Microsoft.Azure.Functions.Worker.Extensions.*`. | **Flex Consumption FC1**, Linux. .NET 10 on Linux is not supported on classic Consumption (Microsoft docs). |
| **Storage account (data)** | Yes | Tables, Queues, Blobs. `TableStorageService`, `QueueClientFactory`, `BlobStorageService` all read `AzureStorageAccountName`. | StorageV2, Standard_LRS, shared key **off** |
| **Storage account (host)** | Yes (Flex) | Deployment package + `AzureWebJobsStorage`. Kept separate for blast radius. | Standard_LRS |
| **Azure SignalR Service** | **Yes** (hard fail) | `SignalRNotificationService` throws if `AzureSignalRConnectionString` is missing. Serverless mode, Management SDK. | **Free_F1** (20 connections, 20k msgs/day). Upstream defaults assume Standard S1. |
| **Application Insights + Log Analytics** | Strongly advised | Worker telemetry, Operator "raw logs" KQL proxy, MCP usage overview. Backend filters storage dependency noise to save cost. | Workspace-based, **1 GB/day cap** |
| **Key Vault** | Yes (by my design) | Holds 4 secrets. Not required by the code, which only reads app settings. KV references keep secrets out of config. | Standard, RBAC, purge protection |
| **Static Web Apps** | Yes | Portal is a pure static export (`output: 'export'`). | Free |
| **Container Apps + ACR** | Only for MCP | `Dockerfile`, `deploy-mcp.yml`. Scale-to-zero. | ACR Basic; 0.25 vCPU / 0.5 GiB |
| **Front Door** | No (lab) | Used upstream for `download.` and `go.` aliases and client-IP trust. | **Skipped** |
| **Azure Monitor alerts** | Optional | Webhook endpoint `ops/alert-webhook` (secret query string, fail-closed). | none |
| **Email (Mandrill)** | Optional | `Email:ApiKey`; default sender is upstream's domain. | none |
| **Web Push (VAPID)** | Optional | Needs a VAPID key tool that lives in the private repo. | none |

## Microsoft 365 / Entra / Intune requirements

### Entra ID: the app registration (exact)

Created by `scripts/02-New-EntraAppRegistration.ps1`. One registration does three jobs: portal sign-in, API audience, and the MCP server's OAuth client.

| Setting | Value | Why |
|---|---|---|
| Sign-in audience | `AzureADMultipleOrgs` | JwtBearer authority is `organizations`. |
| Application ID URI | `api://<appId>` | Audiences trusted are `<id>` and `api://<id>`. |
| Exposed scope | `access_as_user` | Portal and MCP request `api://<id>/access_as_user`. |
| App roles | `access_as_application` (Application); `Admin`, `Operator`, `Viewer` (User) | Unattended MCP principals; optional Entra-role to portal-role mapping. |
| Access token version | 2 | Validation parameters use v2 issuer. **(inferred)** |
| Optional claims (access token) | `acct`, `idtyp` | Guest-account detection; app-vs-user detection. |
| SPA redirect | portal origin | MSAL uses `window.location.origin`. |
| Web redirects | `<portal>/settings/tenant/autopilot`, `<portal>/settings` | Admin-consent return paths hard-coded in `RegisteredConsentRedirectPaths`. |
| Web redirect | `<mcp>/oauth/callback` | MCP authorization-code flow (confidential client, secret). |
| Client secret | 6 months, in Key Vault | Used by the backend to get Graph tokens and by MCP. |
| Graph **application** permission (required) | `DeviceManagementServiceConfig.Read.All` | Device validation (Autopilot, Corporate Identifier, Device Association). |
| Graph application (optional) | `DeviceManagementManagedDevices.Read.All` | **Intune Enrollment Validation**: easiest for Device Prep if you pre-register nothing. |
| Graph application (optional) | `DeviceManagementScripts.Read.All`, `CloudPC.Read.All` | Script display names; Windows 365. Not needed for the lab. |
| Graph delegated | `User.Read` | Profile photo. |

Admin consent: needs **Global Administrator or Privileged Role Administrator** (Microsoft: only those can grant Graph application permissions). The script grants it in your own tenant; `scope type = Admin` is my choice **(inferred)**, consistent with the docs saying users cannot consent to `access_as_user`.

**Enterprise application settings to review:** *Assignment required = Yes* (only members of `AutopilotMonitor-Admins` can sign in at all), then assign that group. This is a stronger gate than the portal's own roles. Without assignment-required, any account in your tenant can reach the portal login and gets the roleless Progress Portal view.

**Authentication requirements for humans:** work or school accounts only. Guests and personal accounts cannot activate a tenant (docs; `IsExternalIdentity` in code).

### Intune

| Item | Detail |
|---|---|
| Autopilot | Devices registered (hardware hash), deployment profile assigned, ESP policy. Windows 10/11 x64 or Windows 11 ARM64. |
| Platform script | `Start-AutopilotMonitor.ps1`: run as SYSTEM **No** logged-on creds; signature check **No**; 64-bit **Yes**. Assigned to `AutopilotMonitor-Devices` (pilot: `Autopilot-Pilot`). |
| Device Preparation | Deploy the **MSI line-of-business app** as *Required* to the **device group selected in the Device Preparation policy**. A platform script only runs after Microsoft 365 Apps. |
| Validation method | Classic Autopilot: Autopilot Device Validation. Device Prep: Device Association, Corporate Identifier, or Intune Enrollment Validation. |
| Enrollment restrictions | Block personal Windows enrollment if you use Intune Enrollment Validation (it admits *any* device your tenant lets enroll). |
| Network | Outbound HTTPS 443 only. **No TLS inspection** on the API host (breaks client-cert auth). |

### RBAC recommendation

| Who | Where | Role |
|---|---|---|
| You (deployer), **temporary** | Subscription or RG | Owner, or Contributor + User Access Administrator |
| You (deployer), **temporary** | Data storage account | Storage Table Data Contributor, Storage Blob Data Contributor (Bicep `deployerObjectId`) |
| You (deployer), **temporary** | Key Vault | Key Vault Secrets Officer |
| GitHub OIDC identity | Resource group | Contributor (infra) is too broad for app deploys. Use a **second** identity with *Website Contributor* on the Function app only, and *Storage Blob Data Contributor* on the `agent` and `versions` containers. |
| Function app (system MI) | Data storage | Table Data Contributor, Queue Data Contributor, Queue Data Message Processor, Blob Data Contributor |
| Function app (system MI) | Host storage | Blob Data Owner, Queue Data Contributor, Table Data Contributor |
| Function app (system MI) | Key Vault | Key Vault Secrets User |
| Function app (system MI) | App Insights | Monitoring Reader |
| MCP identity (user-assigned) | ACR / Key Vault | AcrPull / Key Vault Secrets User |
| Portal people | Entra group `AutopilotMonitor-Admins`, plus Settings > Access Management in the portal | Admin / Operator / Viewer |

## Licensing

| Need | Minimum | Notes |
|---|---|---|
| Intune + Autopilot | Intune Plan 1 (in M365 Business Premium, E3, E5) | Business Premium is the cheapest bundle that has everything below. |
| Entra ID P1 | in Business Premium / E3 / E5 | **Dynamic device groups**, group-based app assignment, Conditional Access. |
| Windows | Windows 10/11 Pro or higher | Autopilot needs Pro or higher. |
| Hybrid Join test | AD DS, Entra Connect, **Intune Connector for AD** | Server licences or evaluation media. |
| Azure | Pay-as-you-go or credit subscription | Costs in doc 03. |
| Code signing | OV code-signing certificate **or** Azure Artifact Signing | See doc 04 step 8. |
| GitHub | Free (public fork) | Windows runner minutes cost double on private repos. |
| Autopilot Monitor itself | Free (AGPL/MIT) | Hosted Community plan also free. |

## Prerequisites checklist (tick before Phase 2)

**Accounts and access**
- [ ] Global Administrator (or Privileged Role Administrator + Application Administrator) in clefsoft.co.uk
- [ ] Azure subscription; Owner (or Contributor + User Access Administrator) on it
- [ ] Microsoft Entra ID P1 confirmed
- [ ] GitHub account; fork of `okieselbach/AutopilotMonitor`; Actions enabled
- [ ] A named person to receive budget alerts

**Tooling on your workstation**
- [ ] Azure CLI 2.60+ (`az version`), Bicep (`az bicep install`)
- [ ] PowerShell 7.2+ and `Microsoft.Graph` modules: `Install-Module Microsoft.Graph.Authentication, Microsoft.Graph.Applications, Microsoft.Graph.Groups, Microsoft.Graph.Identity.SignIns -Scope CurrentUser`
- [ ] Node 22 LTS and git; .NET 10 SDK only if you build locally
- [ ] Docker is **not** needed (`az acr build` builds in Azure)

**Decisions**
- [ ] Signing route chosen (G3)
- [ ] `az functionapp list-flexconsumption-locations` shows `uksouth` (G14)
- [ ] Naming suffix chosen if `stautopilotmonitorprod` is taken
- [ ] Hardware for tests decided (Hyper-V Gen2 VMs recommended)

**Network**
- [ ] No TLS inspection between test devices and the API host
- [ ] Test devices can reach `*.azurewebsites.net`, `*.blob.core.windows.net`, `login.microsoftonline.com`
