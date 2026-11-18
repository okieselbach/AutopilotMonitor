# 01. Executive summary, decision, and gaps

Tenant: **clefsoft.co.uk**. Source reviewed: `okieselbach/AutopilotMonitor` at commit `72f138e` (17 Nov 2026), plus the public docs repo `autopilotmonitor/autopilotmonitor-docs`.

## The short version

Autopilot Monitor is a good fit for your goals. But it is **not built to be self-hosted**. It is the code behind a hosted service run by glueckkanja AG (free Community plan at autopilotmonitor.com). The repo has no deployment templates, and several things are baked in at build time that make a private copy a **fork, rebuild and re-sign** job, not a "deploy".

I built everything I could to make that job repeatable. What is verified and what is not is listed at the bottom of this page.

## Decision: self-host (confirmed by you)

You work for a managed service provider (MSP) and cannot use the hosted evaluation, so self-hosting in your own Azure subscription is the only route. That settles the question I raised earlier. The hosted free plan stays off the table, and the hosted trial is removed from the plan below.

What this means in practice:

- **Use only your own test tenant (clefsoft.co.uk) and test devices.** Do not enrol customer devices or customer tenants into this lab. It is a lab, not a service.
- **Check your employer's policy on forks and AI tools.** The MCP/Claude part sends enrollment data (device names, serials, software lists) to your AI vendor. With lab data only, that is usually fine. Confirm it, or skip Part 1 of doc 06 until you have.
- **Licence (G17).** Backend, web and MCP are AGPL-3.0. A private lab is fine. If you later offer it to customers over the network, you must publish your modified source. Get your employer's legal view first.
- **Cost.** About £5 to £15 a month for Azure in the lab, plus code signing (£8 to £35 a month). Details in doc 03.

## Gap register: things the repo does not tell you

Each gap has the evidence (file in the repo) and what I did about it.

| # | Gap | Evidence | Impact | What this pack does |
|---|---|---|---|---|
| **G1** | **No IaC is published.** `infra/`, `internal/` and `.claude/commands` are *private* git submodules. | `.gitmodules` (`update = none`); `infra/` is an empty folder. Code comments point at `infra/mcp-server.bicep`, `infra/scripts/push/New-VapidKeys.ps1`, `New-TargetInfrastructure.ps1`: all private. | No official template, no role-assignment list, no VAPID key tool. | Rebuilt the Bicep from the code (`bicep/`). It compiles and lints clean. It is a *reconstruction*, not upstream's. |
| **G2** | **Hosts are compiled in.** API, portal, MCP, download, storage and "go" short-link hosts live in `Constants.cs`, web `lib/config.ts`, MCP `src/config.ts`, the SWA CSP, the CI endpoint registry, and the signed scripts. | `Constants.cs` lines 91, 101, 111, 233, 251, 259; `Get-Endpoints.ps1`; `staticwebapp.config.json` `connect-src`. | The agent, scripts and portal all point at upstream until you rebuild. | `scripts/01-set-selfhost-endpoints.mjs`. **Tested on a copy of the real repo:** 38 replacements in 11 files, registry values verified afterwards. |
| **G3** | **Code-signing is pinned to the vendor.** Loader, installer, MSI and agent verify an Authenticode signature whose subject must match `*O=glueckkanja AG*`. | `Constants.CodeSigningPublisherPattern`; `Start-/Install-AutopilotMonitor.ps1`; `Package.wxs`; `PayloadCatalogVerifier.cs`. | Without your own **publicly trusted** code-signing certificate, devices reject everything. A self-signed cert only works on devices that already trust it (test VMs you prepare yourself). | Patcher rewrites the pattern. **Chosen route: self-signed lab certificate** (script 08), trusted only on test VMs you prepare (script 09). Real devices need a trusted certificate later (doc 04, step 8). |
| **G4** | **Agent release pipeline assumes Windows runners, AzureSignTool and a Key Vault certificate.** It also writes `LatestAgentV2Version/Sha256/ExeSha256` and `LatestBootstrapV2ScriptVersion` into the AdminConfiguration table, and keeps build counters in a `versions` blob. | `build-agent.yml`, `build-msi.yml`, `publish-scripts.yml`, `Publish-BootstrapScripts.ps1`. | The backend rejects or ignores agents it does not have a hash for. | Reuse upstream's workflows after patching (they read hosts from `Get-Endpoints.ps1`). Variables list in doc 04. |
| **G5** | **No first admin.** Adding a Global Admin is itself a Global-Admin-only API. New tenants are queued for approval (`AutoApproveNewTenants` defaults to **false**). | `AuthFunction.AddGlobalAdmin`; `AdminConfiguration.cs:475`; `EndpointAccessPolicyCatalog`. | You can sign in but can do nothing. | `scripts/03-seed-platform.mjs` writes 3 table rows. **Tested against the Azurite Table emulator**, including re-run safety (found and fixed a bug where a re-run wiped the pinned object id). |
| **G6** | **Device auth is mTLS with the Intune MDM certificate.** The platform must pass the client cert through as `X-ARR-ClientCert`. | `SecurityValidator.cs:374-380`, `CertificateValidator.cs`. Docs: "TLS inspection breaks it". | Wrong cert mode means every agent upload is refused, or every admin browser gets a certificate prompt. | Bicep sets `clientCertEnabled: true`. **Trade-off:** `Optional` can make browsers prompt for a cert (an Intune-enrolled admin PC may have one). `OptionalInteractiveUser` does not prompt, but forces TLS renegotiation: no HTTP/2, no TLS 1.3, and POST bodies over **100 KB fail with 403** (Microsoft docs). Upstream's choice is in the private repo. **Test both** (doc 05, test T0). |
| **G7** | **The API app registration has hidden requirements:** multi-tenant, `api://<id>` identifier, `access_as_user` scope, `access_as_application` app role, v2 tokens, optional claims `acct` and `idtyp`, SPA and Web redirect URIs for admin consent, MCP callback. | `Program.cs`, `AuthenticationMiddleware.cs`, `ClaimsPrincipalExtensions.cs:70,115`, `AutopilotDeviceValidationConsentFunction.cs:43`, `oauth.ts:87`. | Missing `acct` means guests are not detected. Wrong token version breaks validation. | `scripts/02-New-EntraAppRegistration.ps1`. |
| **G8** | **Not everything can use managed identity.** Storage and App Insights can. The Graph calls use an app **client secret** (`EntraId:ClientSecret`); SignalR needs a **connection string** (it throws on start without it); MCP needs the same secret. | `GraphTokenService.cs:297`, `SignalRNotificationService.cs:30`, `oauth.ts:45`. | "Managed identities everywhere" is not achievable. | Key Vault references for all three; documented as accepted risk, with a rotation runbook. |
| **G9** | **Queue triggers need a special setting** `DataStorage__queueServiceUri`, which the host resolves itself. It cannot be derived from the storage account name. | `local.settings.json.template`. | Queue-driven features (analyze, deletion, backups) silently do nothing. | Set in Bicep. |
| **G10** | **Rules come from a third-party GitHub branch.** Default `GitHub:RulesBaseUrl` is `raw.githubusercontent.com/okieselbach/Autopilot-Monitor/refs/heads/main/rules` (note the *old* repo name). The "reseed" call (Global Admin) pulls rules and CPE maps from there. | `GitHubRuleRepository.cs:27`. | Supply-chain exposure; also the rule tables are empty until you reseed. | Setting exposed in Bicep. Point at your fork, pinned to a commit. Reseed step in doc 04. |
| **G11** | **Portal legal pages name glueckkanja AG as data controller** (Privacy, Terms, About, footer). | `app/privacy/page.tsx`, `app/terms/page.tsx`, `SiteFooter.tsx`. | A fork serving these pages states something false about who processes the data. | Not auto-patched on purpose (needs your own wording). Edit before anyone else uses it. Keep the licence and attribution. |
| **G12** | **Web build depends on another repo and an unrelated URL.** The prebuild step fails in CI without a checkout of the public docs repo (`WHATS_NEW_DOCS_DIR`) and the build requires a "platform stats manifest" URL. | `deploy-web.yml`. | First CI run fails with a confusing prebuild error. | `ci/deploy-web.yml` does both. |
| **G13** | **Front Door is assumed** for `download.` and `go.` aliases and for real client IPs (`FrontDoorProfileId`). | `ClientIpExtractor.cs`, `Get-Endpoints.ps1`. | Without it, rate-limit keys use the platform hop (acceptable for a lab). | Dropped for the lab (about £30 a month saved). Cloudflare covers the download host (Worker) and DNS; the API must **not** be proxied. `go.` links become `<api>/api/bootstrap/go/<code>` or a Cloudflare redirect (doc 04, step 5b). |
| **G14** | **Region mismatch.** Static Web Apps cannot be created in UK South. | Microsoft docs; `swa.bicep`. | None for runtime: content is served from the edge. | SWA control plane in West Europe; everything else UK South. **Verify Flex Consumption is offered in UK South** before you start: `az functionapp list-flexconsumption-locations`. |
| **G15** | **Optional integrations have no switch-off documentation:** email (Mandrill, `Email:ApiKey`), web push (VAPID), NVD API key, Azure Monitor alert webhook (fail-closed without `OpsAlertWebhookSecret`). | `EmailService.cs`, `local.settings.json.template`. | Features quietly absent. | Left off in the lab. Listed in doc 02. |
| **G16** | **Upstream moves fast** ("expect frequent change"; session data may be cleared between major updates). Forks drift. | Docs: requirements-and-access. | Every upstream merge may break your patches. | Patcher is idempotent and config-driven; upgrade strategy in doc 06. |
| **G17** | **Licence.** Backend, web and MCP are AGPL-3.0; agent and shared library are MIT. | `README.md` licence section. | Private lab use is fine. If *other people* use your modified service over the network, you must offer them the source. | Keep the fork public or get legal advice before opening it to others. |
| **G18** | **Hardware whitelist rejects unknown makes.** Default: `Dell*`, `HP*`, `Lenovo*`, `Microsoft Corporation`. | `portal-setup.md`. | Hyper-V VMs report "Microsoft Corporation": already allowed. VMware, QEMU, Parallels are not. | Test plan uses Hyper-V. |

## Delivery plan (assumes one consultant, UK business hours)

| Phase | Work | Effort | Depends on |
|---|---|---|---|
| 1. Decide and prepare | Confirm Flex in UK South; choose signing route; create GitHub fork; confirm licences | 0.5 day | none |
| 2. Landing zone | RG, Bicep pass 1, deployer RBAC | 0.5 day | Azure Owner or (Contributor + User Access Admin) |
| 3. Identity | Entra app, consent, secrets, seed rows | 0.5 day | Global Admin or Privileged Role Admin |
| 4. Fork and rebuild | Patch endpoints, fix guard tests, edit legal pages | 1 day | Phase 2 outputs (hostnames) |
| 5. Signing and agent pipeline | Obtain certificate, wire Key Vault, run agent, MSI and script publish | 1 to 3 days (certificate lead time) | Phase 4 |
| 6. App deploy | Backend, web, MCP, rules reseed | 0.5 day | Phases 3 and 4 |
| 7. Intune | Groups, platform script, MSI for Device Prep | 0.5 day | Phase 5 |
| 8. Test | Test plan, doc 05 (several enrollments, a few days of elapsed time for dynamic groups and ESP timeouts) | 2 days | Phase 7 |
| 9. Harden and hand over | Remove temporary roles, budget, runbook | 0.5 day | Phase 8 |

**Total: about 7 to 10 working days**, of which 1 to 3 are waiting on a code-signing certificate. **Elapsed time:** 2 to 3 weeks if the certificate is a purchased OV certificate with identity validation.

### Milestones
M1 landing zone deployed and `07-Test-Deployment.ps1` API gate passes. M2 first admin can sign in to the portal. M3 signed agent published and `version.json` served. M4 first monitored Autopilot enrollment appears live. M5 MCP answers a question about that session. M6 temporary access removed and budget alert active.

## Open questions I would ask before starting

1. Does your employer's policy allow a personal fork of an AGPL project and sending lab telemetry to an AI vendor (for the MCP test)?
2. Which signing route: purchased OV code-signing certificate, **Azure Artifact Signing** (needs a pipeline change; upstream uses AzureSignTool with Key Vault), or lab-only self-signed trusted on your test VMs?
3. Is `clefsoft.co.uk` an Entra tenant where you hold Global Administrator? Do you have Entra ID P1 (dynamic groups)?
4. Do you own a custom domain for the portal and MCP (vanity URLs, managed certificates), or are default `*.azurestaticapps.net` URLs fine?
5. Will anyone besides you use it? (Decides G11 and G17.)
6. Hybrid Join: do you already have AD DS, Entra Connect and the Intune Connector for AD, or must we build a lab domain controller?
7. Is the Azure subscription a Visual Studio / credit subscription (spend caps, region quotas)?
8. Which devices: Hyper-V VMs only, or real hardware (decides the hardware whitelist and Autopilot hash capture)?
9. Retention and privacy: telemetry includes device and software inventory data. Any internal policy on where that may be stored?
10. Who gets portal access, and do you want it mapped through the `AutopilotMonitor-Admins` group or managed inside the portal?

## What is verified and what is not (read this)

| Item | Status |
|---|---|
| Bicep (`bicep/main.bicep` + modules, `main.bicepparam`) | **Compiled and linted** with Bicep CLI 0.49.2: clean. **Not deployed** (no Azure access from here). API versions and property names come from the compiler's own type definitions. |
| `01-set-selfhost-endpoints.mjs` | **Run against a real clone** of the repo. Output inspected. |
| `03-seed-platform.mjs` | **Run against Azurite** (Table emulator). Row shapes copied from backend entity classes. Not run against real Azure. |
| PowerShell scripts 02, 04, 05, 06, 07 | **Not executed and not parsed.** PowerShell could not be installed here. They use documented SDK cmdlets, include `-WhatIf`, and are marked as untested in their headers. Run `-WhatIf` first and expect to fix small things. |
| GitHub workflows | YAML syntax checked only. Not run. |
| `azure.yaml` (azd) | YAML syntax only. azd not installed. Provision-only by design. |
| Entra app registration contents | Inferred from source. **Not proven against a running instance.** Two inferences are flagged in doc 02 (scope consent type, `EntraAppRolesEnabled`). |
| Costs | **My estimates** from public list prices as I know them, with stated assumptions. Not run through the Azure Pricing Calculator. Check before committing budget. |
