# 03. Cost estimate and security review

## Part 1. Azure cost estimate

**Read this first.** These are **my estimates**, built from public list prices as I know them, in GBP at roughly £0.78 per US$1. I could not run them through the Azure Pricing Calculator from here. Upstream does not publish how much load one enrollment creates, so the "small" and "medium" columns are order-of-magnitude. **Before you commit budget, put the line items into the Pricing Calculator for UK South.** One price I quote from Microsoft's own docs: Flex Consumption on-demand execution is about **$0.000026 per GB-second** and **$0.40 per million executions**, with a monthly free grant (check the current size on the Functions pricing page).

### Assumptions
| | Lab | Small production | Medium enterprise |
|---|---|---|---|
| Enrollments a month | up to 50 | about 500 | about 5,000 |
| Portal users | 1 to 3 | up to 10 | up to 30 |
| Tenants | 1 (yours) | 1 | 1 |
| Availability | none promised | business hours | zone redundant, always-ready |

### Monthly estimate (GBP, rounded)

| Line item | Lab | Small prod | Medium enterprise | Notes |
|---|---:|---:|---:|---|
| Functions (Flex, on-demand) | 0 to 3 | 5 to 15 | 15 to 40 | Mostly inside the free grant at lab scale. |
| Functions always-ready instance | 0 | 0 (optional ~15 to 20) | 40 to 80 | Zone redundancy needs **at least 2** always-ready (Microsoft docs). |
| Storage ×2 (LRS / ZRS) | 1 to 3 | 3 to 8 | 15 to 40 | Tables and queues are the cost driver, not capacity. |
| Azure SignalR | **0** (Free) | 0 to 39 | 39 to 78 | Free = 20 connections and 20k messages/day. Standard S1 ≈ £39 per unit. |
| Application Insights + Log Analytics | 0 to 5 | 5 to 20 | 60 to 180 | Cap at 1 GB/day in Bicep. First 5 GB a month is free per billing account. Worker telemetry sampling already filtered by the backend. |
| Key Vault | under 1 | under 1 | 2 to 5 | Plus a **separate** signing vault if you use AzureSignTool (below). |
| Static Web App | 0 (Free) | 0 to 7 | 7 | Standard only if you need SLA or more custom domains. |
| ACR Basic (MCP image) | 3 to 4 | 3 to 4 | 15 (Standard) | Premium (private link) is ~£37. |
| Container App (MCP) | 0 to 1 | 1 to 3 | 25 to 40 (min 1 replica) | Scale-to-zero: first request pays about 25 s cold start (upstream measured). |
| Cloudflare (DNS + download Worker) | 0 (Free plan, Workers free tier) | 0 | 0 to 20 | Replaces Front Door in the lab. Does **not** front the API (client-cert auth). |
| Front Door | not used | not used | 30 to 40 (Standard) | Needed only for `go.`-style short links and real client IPs. |
| Private endpoints / VNet | not used | not used | 25 to 60 | Storage ×3 services, Key Vault. |
| Defender for Cloud plans | not used | optional 5 to 10 | 10 to 30 | Storage and Key Vault plans. |
| **Subtotal (Azure)** | **≈ £5 to 15** | **≈ £20 to 100** | **≈ £300 to 650** | Small prod swings on the SignalR tier and the always-ready choice. |

### Costs outside Azure that people forget
| Item | Estimate | Notes |
|---|---|---|
| **Code-signing certificate** (required, G3) | **£0 for the self-signed lab route** (chosen). £8 to £35 a month for a trusted certificate later | OV certificate ~£150 to £400 a year, **or** Azure Artifact Signing ~US$10 a month (Basic). **Artifact Signing needs a pipeline change**: upstream signs with AzureSignTool against a Key Vault certificate. Availability for UK organisations and identity-validation lead time: verify. |
| Signing Key Vault (separate, created by Bicep) | under £1 | Keep separate from the platform vault. |
| GitHub Actions | £0 (public fork) | Private: Windows runner minutes count double. |
| Microsoft 365 test licences | existing | Business Premium ≈ £16 per user per month if you need one. |
| Your time | days | See delivery plan in doc 01. |

### Cost guards already in the template
- Log Analytics **daily cap 1 GB** (Bicep `logDailyCapGb`)
- Function **max instances 10** (`functionMaxInstances`; lowest allowed is 1)
- SignalR **Free**, SWA **Free**, ACR **Basic**, MCP **min replicas 0**
- Optional **budget** with 80 % actual and 100 % forecast alerts (`monthlyBudget`, `budgetAlertEmail`)
- No Front Door, no private endpoints, no geo-redundancy

### Where the lab will hurt first
1. **SignalR Free**: one admin with several tabs open on a busy day can reach 20 connections or 20k messages. Symptom: portal stops live-updating; ops event in the audit log. Fix: change to `Standard_S1` (30 to 60 min DNS change on tier move, per Microsoft docs).
2. **App Insights**: first suspect for a surprise bill. Check *Usage and estimated costs* weekly in month 1.
3. **Cold start**: Function on-demand means the first agent call after idle is slow. Agents retry; not a correctness problem.

### Hosted alternative for comparison
The hosted Community plan: **£0**.

---

## Part 2. Security review

### Baseline you asked for, and the honest status

| Requirement | Status | Detail |
|---|---|---|
| Managed identities | **Partly** | Yes for all storage (Table, Queue, Blob incl. user-delegation SAS), App Insights, Key Vault, ACR pull. **No** for Graph (client secret), SignalR (connection string), MCP OAuth (client secret): see G8. All three are in Key Vault, never in config. |
| Key Vault secrets | Yes | `entra-client-secret`, `signalr-connection-string`, `pagination-signing-key`, `ops-alert-webhook-secret`. RBAC mode, soft delete 14 d, purge protection, audit logs to Log Analytics. |
| Least-privilege RBAC | Yes at resource scope | No subscription-wide data roles for the app. See doc 02 table. Deployer roles are temporary. |
| Conditional Access compatible | Yes, with one caveat | Agents use **mTLS, not Entra tokens**, so CA never sees devices. CA applies to humans signing into the portal and to the MCP sign-in. Do **not** require a *compliant device* for this app if you want to use Claude from a personal browser. |
| MFA enforced | For humans: yes via CA (below). For devices: not applicable | There is no MFA concept for the agent; its trust anchor is the Intune device certificate plus tenant validation. |

### Conditional Access (do this, in report-only first)
Target the enterprise app **Autopilot Monitor (clefsoft)** with **Require multifactor authentication** (or your authentication strength). Include `AutopilotMonitor-Admins`. Exclude your break-glass accounts. Run **report-only** for a week, read the sign-in logs, then enforce. Also keep the tenant's baseline policy for admin roles.

Sketch (untested, PowerShell Graph SDK, report-only):
```powershell
Connect-MgGraph -Scopes 'Policy.ReadWrite.ConditionalAccess','Application.Read.All','Group.Read.All'
$sp  = Get-MgServicePrincipal -Filter "displayName eq 'Autopilot Monitor (clefsoft)'"
$grp = Get-MgGroup -Filter "displayName eq 'AutopilotMonitor-Admins'"
New-MgIdentityConditionalAccessPolicy -BodyParameter @{
  displayName = 'CA - Autopilot Monitor - require MFA'
  state       = 'enabledForReportingButNotEnforced'
  conditions  = @{ applications = @{ includeApplications = @($sp.AppId) }
                   users        = @{ includeGroups = @($grp.Id) } ; clientAppTypes = @('all') }
  grantControls = @{ operator = 'OR'; builtInControls = @('mfa') }
}
```

### Findings (ranked)

| # | Severity | Finding | Fix |
|---|---|---|---|
| S1 | **High** | **Client-cert mode vs browsers** (G6). `Optional` may prompt admin browsers; `OptionalInteractiveUser` caps POST bodies at 100 KB and disables TLS 1.3/HTTP2. A wrong choice either breaks devices or admins. | Test both in T0 before anything else, from an Intune-enrolled admin PC (the one most likely to hold a matching certificate). Start with `Optional` (no renegotiation). If your browser prompts, try `OptionalInteractiveUser` and measure whether any agent upload body exceeds 100 KB. I have **not** verified a browser-side workaround; if both modes fail, the clean fix is to give browsers a different hostname than devices (custom domain via Front Door), which is a design change. |
| S2 | **High** | **Public `agent` container.** The loader downloads anonymously. Anyone can read it; nobody can write. | Accepted. It holds only signed binaries and scripts. Container-level `Blob` access (no listing), shared key off, separate from every other container. Alternative: Front Door with private origin (~£30/mo). |
| S3 | **High** | **Code-signing key is the crown jewel.** Whoever signs can push code to every enrolling device in the tenant. | Dedicated vault, `Key Vault Crypto User` only for the pipeline identity, GitHub `production` environment with required reviewers, OIDC (no stored credentials), branch protection on `main`. |
| S4 | Medium | **Rules fetched from a GitHub branch** at reseed (G10). A compromised branch means a compromised rule set (gather rules can collect registry values, logs, allow-listed commands from devices). | Pin `rulesBaseUrl` to a commit in **your** fork; review diffs on upstream merges. |
| S5 | Medium | **Secrets with a lifetime.** Client secret 6 months; signalr key; random keys. | Calendar the expiry. Rotation runbook in doc 06. Move Graph to a certificate credential only if you change the code (it sends `client_secret`). |
| S6 | Medium | **Everything is on the public internet** (Function, storage data plane, Key Vault). | Fine for a lab. Production variant: private endpoints for storage and Key Vault, VNet integration for the Function, Front Door WAF. The agent and the browser still need a public endpoint on the API. |
| S7 | Medium | **Multi-tenant app registration in a single-tenant world.** Any Entra tenant can sign in and *request* activation. | `AutoApproveNewTenants` is **true** only to activate your own tenant. **Turn it off after your tenant is active** (Portal admin settings, or flip the table row back). Also set *Assignment required* on the enterprise app. |
| S8 | Medium | **TLS inspection / proxies** break device auth silently. | Exclusion list in doc 05. |
| S9 | Low | **Telemetry contains device and software inventory.** | Data stays in your subscription; tenant-delete cascade exists with a 30-day recoverable manifest (upstream docs). Add retention rules for your policy. |
| S10 | Low | **App Insights connection string is public by design** (it ships in the portal bundle). | Expected. Never put an instrumentation *key* with ingestion rights anywhere sensitive. |
| S11 | Low | **Deployer data-plane roles linger.** | Set `deployerObjectId = ''` and redeploy at go-live (M6). |
| S12 | Info | `Security headers`: backend adds `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options`, `Cache-Control: no-store` on credential endpoints; the SWA config adds a strict CSP. | The patcher keeps the CSP `connect-src` in sync with your hosts. Re-check after every upstream merge. |

### What the platform already does well (from the code)
Device sessions are bound to the device certificate (no cross-device writes); every upload is size-capped; SSRF guards on outbound fetches; agent payloads are hash- and catalog-verified at download and runtime; the bootstrap loader refuses unsigned installers; OAuth redirect URIs are on an allow-list with per-client binding; MCP stores no credentials and forwards the user's own token; Global Admin roles are bound to tenant id and object id, so a same-named UPN in another tenant gets nothing.

### Data protection checklist
- [ ] Hardware whitelist reviewed (default allows only Dell, HP, Lenovo, Microsoft)
- [ ] Diagnostics upload: **your own storage** destination, not hosted storage (the portal setup guide offers hosted by default)
- [ ] Software inventory and vulnerability analyzer: decide deliberately, it lists installed software
- [ ] Geo-location detection: leave off if your devices use a proxy in another country
- [ ] Retention period written down
- [ ] Privacy/Terms pages edited (G11)
