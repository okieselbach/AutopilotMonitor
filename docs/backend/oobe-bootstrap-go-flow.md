---
type: Concept
title: OOBE Bootstrap go-Code Flow — Always-200 Script Endpoint
description: The full lifecycle of the bootstrap short-code — created in the portal, typed as `irm go.autopilotmonitor.com/CODE | iex` at Shift+F10, served by GetBootstrapScriptFunction as an always-HTTP-200 PowerShell script — including the two-line injection defense and the rate-limit story behind Front Door.
resource: src/Backend/AutopilotMonitor.Functions/Functions/Bootstrap/GetBootstrapScriptFunction.cs
tags:
  - backend
  - bootstrap
  - security
  - oobe
timestamp: 2026-07-28T00:00:00+02:00
---

# OOBE Bootstrap go-Code Flow

Bootstrap Sessions let a technician install the agent **before** Intune
enrollment: an admin creates a short-lived session in the portal (Settings →
Bootstrap Sessions), hands the technician a short URL, and the technician types
`irm https://go.autopilotmonitor.com/CODE | iex` into a Shift+F10 console
during OOBE. The URL is deliberately short because it is typed by hand on a
device that has no clipboard.

# Lifecycle

1. **Create** — `POST /api/bootstrap/sessions` (`BootstrapManagerOrGA`).
   `BootstrapSessionService.CreateAsync` mints an unambiguous-charset short
   code + a GUID token, validity clamped 1–168 h. The response's `bootstrapUrl`
   is built from `Constants.BootstrapGoBaseUrl` — the single producer of the
   absolute URL (pinned by `CreateBootstrapSessionUrlShapeTests`).
2. **Serve** — `GET /api/bootstrap/go/{code}` (`GetBootstrapScriptFunction`,
   `PublicAnonymous`). Customers reach it through the Front Door custom domain
   `go.autopilotmonitor.com`, whose `/*` route rewrites onto the API path.
   Validation runs in-process (`ValidateCodeAsync` + the tenant's
   `BootstrapTokenEnabled` gate) — no HTTP hop to the validate endpoint.
3. **Install** — the generated script downloads the agent ZIP from
   `Constants.AgentDownloadBaseUrl`, extracts, and runs
   `--install --no-auth --bootstrap-token … --tenant-id …`. The agent monitors
   from OOBE start, switches to certificate auth after Intune enrollment, and
   self-destructs on completion.

The legacy Next.js `/go/[code]` route (SSR on the SWA) stays alive only for
URLs issued before the migration and is removed with the static-export PR —
see the transition rule in [URL Registry](../url-registry.md).

# Always-200 contract

Every response — success, invalid format, unknown/expired code, disabled
feature, rate limit, internal error — is **HTTP 200 `text/plain`**. The
consumer is `irm | iex`: a non-2xx status would make `irm` throw an opaque
exception on the OOBE console, while a 200 error script surfaces the actual
message via `Write-Host 'ERROR: …'`. Consequences:

- Failures are invisible in status-code metrics; the compensating signal is the
  `LogWarning` per rejection (code + client IP).
- Error messages are capped at 200 chars **before** quote-escaping and
  single-quote-doubled — a hostile upstream message cannot break out of the
  `Write-Host` literal.

A disabled tenant returns the **same generic message** as an unknown code — no
enumeration oracle for whether a code exists.

# Injection defense (two lines)

The script runs as SYSTEM during OOBE, so interpolation is treated as a
security boundary:

1. **`BootstrapScriptValueValidator`** — tenantId/token must be exact canonical
   GUIDs, the download URL must be https on an allow-listed host
   (`AgentDownloadBaseUrl`/`AgentBlobBaseUrl` hosts) with the strict
   `/agent/….zip` path shape, expiry must be future and ≤ 14 days. No accepted
   value can contain `$`, quotes, backticks, spaces, or newlines. Kept even
   though values are produced in-process — defense-in-depth against tampered
   table data.
2. **Single-quoted PS literals** — every interpolated value sits inside
   single-quoted PowerShell strings, which do not expand `$()`, `$var`, or
   backticks.

The template is a C# raw string with `__TOKEN__` placeholders substituted via
`Replace` (never `$`-interpolation — the script is full of PS `$vars`).
`OobeBootstrapScriptGeneratorTests` pins byte parity with the original
TypeScript template through a golden file, so the legacy route and the backend
serve identical scripts during the transition.

# Rate limiting behind Front Door

The endpoint self-limits per client IP (`bootstrap-script:{ip}`, 20/min via
`ClientIpExtractor.GetTrustedClientIp`) — anonymous routes are skipped by
`UserRateLimitMiddleware` and must bring their own limiter. Because traffic
arrives through Front Door, the rightmost X-Forwarded-For hop may resolve to
AFD egress IPs; whether buckets collapse (the shared-bucket 429 incident class)
must be verified post-deploy from two networks. If they do, the fix is an
AFD-aware extraction honoring `X-Azure-ClientIP` only when `X-Azure-FDID`
matches our profile — NOT `GetClientEgressIp`, which trusts the header
unconditionally and is documented as unfit for rate-limit keys.

Front Door route caching is DISABLED for this route: the body inlines the
bearer token, so a cache hit would replay one requester's token to another.
`NoStoreCacheMiddleware` covers the `/api/bootstrap/go/` prefix.

# Citations

- `src/Backend/AutopilotMonitor.Functions/Functions/Bootstrap/GetBootstrapScriptFunction.cs`
- `src/Backend/AutopilotMonitor.Functions/Functions/Bootstrap/OobeBootstrapScriptGenerator.cs`
- `src/Backend/AutopilotMonitor.Functions/Security/BootstrapScriptValueValidator.cs`
- `src/Backend/AutopilotMonitor.Functions.Tests/OobeBootstrapScriptGeneratorTests.cs` — golden parity
- `src/Web/autopilot-monitor-web/app/go/[code]/route.ts` — legacy route (transition only)
- [URL Registry](../url-registry.md) — go-URL registry entries + transition rule
