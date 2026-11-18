# 06. MCP and AI integration, operational runbook, upgrade strategy

## Part 1. MCP and AI integration

### Can the MCP server be self-hosted?
**Yes.** The server is in the repo (`src/McpServer/autopilot-monitor-mcp`, Node 22, AGPL-3.0). It has a Dockerfile and everything it needs is set by environment variables. It is a thin, **stateless** layer: it signs the user in through Entra, then forwards the user's own token to your backend API. It stores no credentials. The backend does all authorization, so the AI assistant sees exactly what that person's portal role allows.

**Design (in `bicep/modules/mcp.bicep`):** Azure Container Apps (0.25 vCPU, 0.5 GiB, scale to zero), image from ACR Basic built with `az acr build`, user-assigned identity for ACR pull and Key Vault, client secret via Key Vault reference.

| Env var | Value | Why |
|---|---|---|
| `AUTOPILOT_API_URL` | your API URL | **Required in production** (server refuses to start, to avoid sending tokens to the wrong host). |
| `MCP_PUBLIC_URL` | your MCP URL | **Required in production.** It becomes the OAuth issuer and redirect base. Bicep derives it from the environment's default domain. |
| `AUTOPILOT_ENTRA_CLIENT_ID` | the app id | Same registration as the portal. Scope requested: `api://<id>/access_as_user openid profile offline_access`. |
| `AUTOPILOT_ENTRA_CLIENT_SECRET` | Key Vault ref | Without it the token exchange runs as a public client (the server logs a warning). |
| `AUTOPILOT_ENTRA_AUTHORITY` | `https://login.microsoftonline.com/<tenantId>` | **My addition, set by default in Bicep.** Upstream default is `/organizations` (any tenant). For a private instance, pin to your tenant. |
| `MCP_ALLOWED_REDIRECT_HOSTS` | (leave unset) | Default list already allows Claude (`claude.ai/api/mcp/auth_callback`), ChatGPT, VS Code (`vscode.dev/redirect`) and loopback. Set it only to add a self-hosted AI client. |

The image also bakes in a copy of the public docs (for the `search_docs` tool) and the rules (for `search_knowledge`). **Editing docs does not update a running server**; rebuild the image. The server's `/health` reports the docs commit so you can see staleness.

### Authentication requirements, step by step
1. Client (Claude, VS Code) discovers `/.well-known/oauth-protected-resource/mcp`, registers itself (a Client ID Metadata Document or dynamic registration; callback must be on the allow-list or loopback).
2. The server redirects the user to **Entra** (`<authority>/oauth2/v2.0/authorize`) with PKCE. **MFA and Conditional Access apply here**, because it is a normal interactive sign-in to your enterprise app.
3. Entra returns a code to `<mcp>/oauth/callback` (Web redirect URI on your app). The server exchanges it using the client secret.
4. The client calls `/mcp` with the resulting **access token**; the server passes it to the API. The API checks audience (`api://<id>`), tenant, and the person's **role in the tenant** (Admin/Operator/Viewer). **No role means no MCP.**
5. Unattended automation instead uses a service principal holding the `access_as_application` role, added as a member in Settings > Access Management; always read-only.

### Claude Desktop
Two ways. The first is the supported one.

**Option 1: custom connector (recommended).** *Claude > Settings (Customize) > Connectors > Add custom connector.* Name `Autopilot Monitor`, URL `https://<your-mcp>/mcp`. A browser opens for the Microsoft sign-in (use your **work** account), then returns to claude.ai.
- Important: remote connectors are called **from Anthropic's cloud**, not from your PC. Your MCP server must therefore be reachable from the internet (it is, on Container Apps). Your data then flows to Anthropic under *your* agreement with them.
- On Team or Enterprise plans an owner adds the connector once; members each sign in with their own account.
- The browser needs **two sign-ins**: claude.ai with your AI account, and Microsoft with your Autopilot Monitor account. If they are different accounts and the connect hangs, sign into claude.ai in your default browser first (upstream's most common support issue).

**Option 2: local bridge (stdio to remote), untested by me.** Uses the `mcp-remote` helper (Node required) and a loopback callback, which the server always allows. In `claude_desktop_config.json`:
```json
{
  "mcpServers": {
    "autopilot-monitor": {
      "command": "npx",
      "args": ["-y", "mcp-remote", "https://<your-mcp>/mcp"]
    }
  }
}
```
Restart Claude Desktop. Pin the `mcp-remote` version once it works (do not run `latest` in a tenant that holds device data).

### VS Code (GitHub Copilot)
Workspace `.vscode/mcp.json`, or *MCP: Open User Configuration*:
```json
{
  "servers": {
    "autopilot-monitor": {
      "type": "http",
      "url": "https://<your-mcp>/mcp",
      "headers": { "X-MCP-Pretty": "1" }
    }
  }
}
```
`X-MCP-Pretty` is optional (indented output costs 15 to 45 % more tokens; use it only when you read raw results). Claude Code CLI: `claude mcp add --transport http autopilot-monitor https://<your-mcp>/mcp`.

### Verify
Ask: *"List all available tools from Autopilot Monitor."* Expect 20+ tools (`search_sessions`, `get_session_summary`, `get_session_events`, `get_session_diagnostics`, `get_app_install_metrics`, `get_time_attribution`, `search_knowledge`, `lookup_error_code`, `search_docs`, `query_raw_events` and more). The `test_*` rule-authoring tools are listed for the Admin role only.

### Quotas and plans on a self-hosted install (unknown)
The backend applies per-minute rate limits plus **daily and monthly MCP budgets by plan**; anything not provably Pro falls back to **Community** (`McpQuotaService`, `FeatureEntitlementCatalog`). I did not find the Community numbers or a self-host override in the public source. **Check Configuration > Reporting > MCP Usage** after your first use. If you hit a budget, a Global Admin can change the tenant's plan/override in the admin area (verify the screen exists in your build).

### Example troubleshooting prompts (map to the test plan)
- *"Show all failed enrollments in the last 24 hours and group them by failure reason."*
- *"Summarize session `<id>` and tell me what caused it to fail."* → built-in prompt **investigate-failed-session**
- *"Go deeper on session `<id>`: download the diagnostics package and search the IME and agent logs."* → **debug-session** (needs a client that can unpack files, such as Claude Code)
- *"Which apps cause the most ESP delays this week, and what is the p95 install time for each?"* → `get_app_install_metrics`, `get_time_attribution`
- *"Win32 app `<name>` failed on my pilot devices. What exit codes, how often, and is the detection rule the problem?"* (T3)
- *"Where does enrollment time go in my user-driven enrollments, and which blocking app costs the most?"*
- *"Compare Hyper-V Gen2 sessions against physical hardware: failure rate and median duration."* → `get_geographic_metrics`/`get_device_history`
- *"Which devices needed more than one attempt this month, and what failed the first time?"*
- *"For my Device Preparation sessions, show phase durations and which resource blocked phase 3."* (T6)
- *"Explain error 0x80070643 / 0x87D1041C / 0x80180014 and what to try first."* → `lookup_error_code`
- *"Draft an analyze rule for the 1603 failure pattern in session `<id>`, validate it, dry-run it."* → **author-rule** (Admin role)
- *"Which devices are affected by CVE-2024-30078?"* → `search_sessions_by_cve` (needs software inventory + vulnerability analyzer enabled)

### Risks specific to MCP
- **Data egress to your AI vendor** (device names, serials, software lists). Your decision, your agreement.
- **Prompt injection from telemetry**: device-originated strings (log lines, app names) reach the model. The tools are read-only, which limits impact; do not add write-capable tools without review.
- **Token scope**: with the Azure CLI pre-authorized (step 3 switch) any CLI login by an allowed user can mint API tokens. Turn it off after setup if you do not need it.

---

## Part 2. Operational runbook

| ID | Task | Frequency | How | Expected / notes |
|---|---|---|---|---|
| **R1** | Health check | Daily in pilot, weekly after | `07-Test-Deployment.ps1`; portal *Audit log and system health*; ops events | All PASS. Poison queues empty. |
| **R2** | Cost check | Weekly in month 1, then monthly | Cost analysis (RG); Log Analytics *Usage* | Under budget. App Insights is the usual cause of spikes. |
| **R3** | **Rotate Entra client secret** | Every 5 months (secret lives 6) | `./02-New-EntraAppRegistration.ps1 -TenantId … -PortalUrl … -KeyVaultName … -RotateSecretOnly`; then `az functionapp restart`; restart MCP revision | Graph token calls keep working. Delete the old secret in Entra after confirming. Expired secret = **all device validation fails** (Graph returns `invalid_client`), so alert on the Key Vault secret `expires` attribute. |
| **R4** | Rotate random secrets | Yearly or on suspicion | `./04-Initialize-Secrets.ps1 -Rotate …` | Invalidates outstanding pagination cursors only (users re-load a page). |
| **R5** | Rotate SignalR key | On suspicion | `az signalr key renew …` then `./04-Initialize-Secrets.ps1` | Live view reconnects. |
| **R6** | Recover a deleted secret/vault | As needed | `az keyvault secret recover` / `az keyvault recover`; purge protection is on, so **names stay reserved for 14 days** | Remember this when choosing a new name (the 24-character Key Vault name has no room for a suffix, see the naming table in doc 04). |
| **R7** | Deploy a backend or web change | Per release | Actions: *Deploy backend* / *Deploy web* | Workflow waits for `/api/health` or `/version.json` to show the commit. |
| **R8** | **Roll back backend** | Incident | Re-run the deploy workflow on the previous commit | Flex Consumption has **no deployment slots** (Microsoft docs); rollback = redeploy. Keep the last good artifact for 30 days. |
| **R9** | Publish a new agent | Per agent release | Build Agent workflow (`publish_as_stable`) | Check `version.json` and the three `LatestAgentV2*` columns. Existing in-flight agents self-update where supported; devices fetch the current agent on every new enrollment. |
| **R10** | Renew code-signing certificate | Before expiry (alert 60 days) | Import the new cert to the signing vault; update `CODE_SIGN_CERTIFICATE_NAME` | Publisher **subject pattern** (`*O=…*`) is unchanged, so already-deployed loaders keep working (upstream design). Re-publish agent, scripts and MSI. |
| **R11** | Backups | Continuous | App has a backup subsystem for critical tables (blob container `critical-table-backups`; Global Admin endpoints under `/api/global/backups`). Blob soft delete 7 days is on. | **Gap: Table Storage has no soft delete or point-in-time restore.** The app's backups cover configuration tables, not necessarily full session history. Decide your RPO; consider scheduled `azcopy` export of tables to another account. Upstream docs say tenant-delete recovery is **operator-assisted within 30 days**. |
| **R12** | Poison queues | Weekly | `az storage queue list … ` look for `*-poison` with messages | Non-empty: read the message, check ops events, fix, re-queue or delete. |
| **R13** | **No sessions appearing** | On report | doc 05 troubleshooting | Order: validation enabled, whitelist, registration, guards, client cert/TLS inspection. |
| **R14** | Add a hardware vendor | As needed | Portal: Settings > Hardware Whitelist (or accept from the "turned away" list) | |
| **R15** | Add a portal user | As needed | Entra group + Settings > Access Management | Table role wins over claim; a **disabled** row is an explicit deny. |
| **R16** | Tenant offboarding / wipe | Rare | Portal admin; cascade worker deletes sessions, blobs, config; recoverable manifest 30 days | Irreversible after the window. Ticket and sign-off first. |
| **R17** | Change client-cert mode | Rare | Bicep param `clientCertMode` + deploy | Re-run T0 and T1 after. |
| **R18** | Rebuild after a host change | Rare | Patcher + tests + re-sign + republish | **Every device that already has an old agent keeps pointing at the old host.** Plan the cutover. |

### Alerts worth creating (Azure Monitor)
Function 5xx rate; Key Vault secret near expiry (Event Grid or a weekly script); Storage throttling; Log Analytics daily cap reached; budget forecast. The app also has an **ops event** store and an Azure Monitor alert webhook (`/api/ops/alert-webhook?secret=…`, secret from Key Vault) if you want alerts to land in the portal.

### Change control (irreversible actions)
Treat these as change-record items with a backout: client-cert mode change; app registration changes; signing-certificate change; host rename; tenant offboarding; purge of any storage account (**no recovery**). Disable-then-soak applies to the platform script: unassign for a week before deleting the Intune object.

---

## Part 3. Future upgrade strategy

### Tracking upstream
1. Add the original as `upstream`: `git remote add upstream https://github.com/okieselbach/AutopilotMonitor.git`.
2. **Monthly** (upstream moves fast; their docs warn of frequent change), on a branch: `git fetch upstream && git merge upstream/main`.
3. Re-run the **patcher** (idempotent). The registry design means conflicts are concentrated in a few files.
4. Re-run the tests; update the guard expectations only if the *structure* changed.
5. Read `docs` changelogs (platform and agent) in the public docs repo for breaking items.
6. Deploy to a **`dev` environment first**: `env = 'dev'` in Bicep, separate resource group, separate Entra app registration (cheap: Free tiers). Run T1 and T3. Then promote.
7. Pin everything you consume at runtime: `rulesBaseUrl` to a commit; `mcp-remote` version; `mcp-docs` commit baked into the image.

### Compatibility facts that make upgrades safer
- Agents download the **current agent on every enrollment**, so version skew between devices and backend is short-lived. The backend can block agent versions and has a kill switch.
- Table schemas are additive in practice (rows read with defaults), but there is **no migration tooling** published; take an app backup before big merges (R11).
- Flex Consumption cannot change plan in place (Microsoft docs): a hosting change is a new Function app plus redeploy, and because device agents have the API host baked in, that is an agent rebuild. Choose a **custom domain** for the API before the pilot if you expect to move hosting.

### Runtime end-of-life to plan for
- **.NET 10** (LTS) is fine for years; check each Functions runtime support notice.
- **Node 22** (web build and MCP image) reaches end of life in 2027: plan to move the web build, MCP Dockerfile and workflows to the next LTS before then. *(Date from memory, please verify on nodejs.org.)*
- **Azure CLI, Bicep, GitHub Actions** versions: let Dependabot (already configured upstream) open PRs on your fork.

### Hardening roadmap (when it stops being a lab)
1. Custom domains for portal, API and MCP; one host per purpose.
2. Front Door (Standard) in front of the API and `agent` container: private blob origin, WAF, real client IPs (`FrontDoorProfileId`), `go.` short links.
3. Private endpoints for storage and Key Vault; Function VNet integration; keep the API itself public (devices and browsers need it).
4. Move Graph auth from client secret to a certificate credential (needs a code change).
5. Zone-redundant storage and always-ready instances (min 2 with zone redundancy).
6. A second Entra app registration for dev; separate signing identity per environment.
7. Scheduled Table exports for DR; test restore twice a year.
8. SignalR Standard S1 when Free throttles.
9. If you later serve other tenants (MSP): the platform has a delegated-admin model and plans; review licensing (G17) and privacy pages (G11) first.
