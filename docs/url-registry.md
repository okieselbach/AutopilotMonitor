---
type: Concept
title: URL Registry — One Place per Component, Guard-Enforced
description: Every well-known own or Microsoft host lives in exactly one registry file per component (Constants.cs, utils/config.ts, src/config.ts); guard tests fail the build on any literal outside them. Includes the agent-download host migration to the download alias.
resource: /src/Shared/AutopilotMonitor.Shared/Constants.cs
tags:
  - architecture
  - urls
  - ci
  - convention
timestamp: 2026-07-23T00:00:00+02:00
---

# URL Registry

Every well-known base URL — own hosts (portal, www, docs, download alias, MCP,
API, both blob accounts) and Microsoft hosts (Graph, Entra login) — is defined
in exactly one registry file per component. Code references the constant; the
literal appears nowhere else.

| Component | Registry | Guard test |
|---|---|---|
| C# (Shared/Backend/Agent) | `AutopilotMonitor.Shared.Constants` (`*BaseUrl`) | `HardcodedUrlGuardTests` |
| Web | `utils/config.ts` (`DOCS_URL`, `SITE_URL`, `PORTAL_URL`, `BOOTSTRAP_GO_URL`, `ENTRA_LOGIN_URL`, `API_URL_PROD`, `BLOB_URL_PROD`, `AGENT_DOWNLOAD_HOSTNAMES`) | `utils/__tests__/hardcodedUrls.guard.test.ts` |
| MCP | `src/config.ts` (`API_BASE_URL`, `ENTRA_LOGIN_BASE_URL`, `DOCS_BASE_URL`) | `src/__tests__/hardcoded-urls.guard.test.ts` |

Derived registries build on these instead of repeating hosts:
`lib/hostRouting.ts` computes `PUBLIC_HOST`/`PORTAL_HOST`/`APEX_HOST` from the
URL constants, and `middleware.ts` imports from it.

# Schema

The guards scan non-test source for the enforced host list and fail with
file:line on any occurrence outside the registry. Comment lines are ignored
(docs may cite URLs). Test files are excluded on purpose: a literal in a test
is an independent oracle — `ValidateBootstrapCodeResponseShapeTests` spells the
full agent download URL out so a registry change must be acknowledged in two
places, backend and portal allow-list, consciously.

Why enforced rather than agreed: the registry existed before (both
`Constants.AgentBlobBaseUrl` and the MCP `config.ts` header document earlier
drift), yet the EU cutover still missed two literal copies of the blob host —
`LatestVersionsService` and `ValidateBootstrapCodeFunction` kept reading the
legacy account, silently kept alive only by the fail-soft legacy mirror in
`build-agent.yml`. A convention that is not checked decays; these guards make
the next stray literal a red build instead of a production surprise.

# Agent-download host migration (2026-07-23)

The two legacy readers above plus the agent's `SelfUpdater` now use
`Constants.AgentDownloadBaseUrl` (`download.autopilotmonitor.com/agent`, Front
Door in front of the current blob origin — verified serving `version.json`,
`version-v2.json` and both ZIPs before the switch). The portal's
`bootstrapValidation.ts` allow-lists BOTH the alias and the legacy blob host
(`AGENT_DOWNLOAD_HOSTNAMES`) because bootstrap scripts already deployed in
customer Intune tenants still carry legacy URLs.

Deploy-order constraint: the web allow-list must be live BEFORE (or with) the
backend that serves alias URLs — an old portal build rejects the alias host and
the bootstrap flow dies client-side. Remove the legacy hostname from the
allow-list only after the legacy account is torn down.

**Front Door caching is a correctness hazard here, not a staleness nuisance.**
The first release after the migration exposed it: the alias kept serving the
previous `version.json` (`X-Cache: TCP_REMOTE_HIT`) while the origin was already
updated — and because manifest and ZIP are independent cache entries, a stale
manifest paired with a fresh ZIP fails the bootstrap's SHA-256 check. Two
defenses, both in place since 2026-07-23: route caching on `/agent/*` is
DISABLED, and every blob upload (CI `Send-Blob`, local `build.ps1`, the
`versions/*.json` manifests) stamps `Cache-Control: no-cache` — all these blobs
rotate in place, even the per-line ZIP. If caching is ever re-enabled, the
headers keep the mismatch class impossible.

Deliberately still on the legacy account: the `HealthCheckService` /
`MaintenanceService` legacy-keepalive probes and the fail-soft mirror in
`build-agent.yml` — they exist precisely to keep already-deployed customer
bootstrap scripts working until the customer migration completes.

# Bootstrap go-URL (2026-07-28)

The OOBE bootstrap short-URL moved from `www.autopilotmonitor.com/go/{code}`
(Next.js route, SSR) to **`go.autopilotmonitor.com/{code}`** — a Front Door
custom domain whose `/*` route rewrites onto `/api/bootstrap/go/{code}` on the
Function App (`GetBootstrapScriptFunction`). Registry entries:
`Constants.BootstrapGoBaseUrl` (C#, the single producer via
`CreateBootstrapSessionFunction`) and `BOOTSTRAP_GO_URL` (web, display-only in
the sessions list); `go.autopilotmonitor.com` is on both guards' enforced-host
lists. `CreateBootstrapSessionUrlShapeTests` pins the produced URL as an
independent oracle — before this, no test guarded the bootstrap URL shape at
all.

Front Door route caching for `bootstrap-go` is DISABLED — same hazard class as
the `/agent/*` route above, sharpened: the script body inlines the bearer
token, so a cache hit would replay one requester's token to another.
`NoStoreCacheMiddleware` additionally stamps `no-store` via the
`/api/bootstrap/go/` prefix.

Transition COMPLETED with the static-export PR (2026-07-29): the legacy
Next.js `/go/[code]` route, `utils/bootstrapValidation.ts`, and
`middleware.ts` are deleted — verified safe because the BootstrapSessions
table held zero active sessions predating the URL cutover (all expired
2026-03). The backend is now the only script producer;
`OobeBootstrapScriptGeneratorTests`' golden file remains as the pin of the
shipped template.

Portal detail routes are query-string based since the same PR
(`/sessions?id=…` etc., builders in `lib/routes.ts`, notification producer
`Constants.PortalSessionUrl`). Legacy PATH-shaped links in sent
Teams/Slack/webhook notifications are rewritten forever by SWA wildcard
rewrites + `components/LegacyPathRedirect.tsx`; `staticwebapp.config.json`
carries the production redirects/headers and is guarded by
`utils/__tests__/swaConfig.guard.test.ts` (registry-backed hosts, no
unsafe-eval, route order).

# Citations

- `src/Shared/AutopilotMonitor.Shared/Constants.cs` — C# registry
- `src/Web/autopilot-monitor-web/utils/config.ts` — web registry
- `src/McpServer/autopilot-monitor-mcp/src/config.ts` — MCP registry
- `src/Backend/AutopilotMonitor.Functions.Tests/HardcodedUrlGuardTests.cs`
- [Version Contract](versioning.md) — the release pipeline this migration protects
