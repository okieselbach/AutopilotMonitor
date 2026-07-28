---
type: Concept
title: Portal Navigation Prefetch & the SWA Runtime Bottleneck
description: Why every navigation Link in the portal sets prefetch={false} — the App Router's viewport prefetch fires one RSC request per visible link, and the Static Web Apps SSR runtime queues them server-side into a multi-minute latency tail. Also records what the follow-up measurement showed about the Standard SKU and the keep-alive side effect.
resource: src/Web/autopilot-monitor-web/components/GlobalSidebar.tsx
tags:
  - web
  - performance
  - nextjs
  - static-web-apps
  - observability
timestamp: 2026-07-27T18:00:00+02:00
---

# Context

The portal is a Next.js 15 App Router application hosted on Azure Static Web Apps. Every page under
the authenticated shell is a client component behind `ProtectedRoute` that fetches its own data from
the Function App (`autopilotmonitor-api-eu`) after mount. The RSC payload a route returns on
navigation therefore carries no page data — only the shell.

`GlobalSidebar` and `Navbar` render the full navigation on every page. With the App Router's default
prefetch behaviour, each visible `<Link>` triggers a background `GET /<route>?_rsc=<hash>` as soon as
it enters the viewport.

# Mechanism

Two properties combine into a portal-wide freeze:

1. **Fan-out.** The navigation is fully rendered on each page load, so a single navigation issues one
   RSC request per visible link — measured at ~10,400 requests over 14 days.
2. **A heavy latency tail on the SSR runtime.** RSC responses are prerendered and edge-cached
   (`x-nextjs-cache: HIT`, `s-maxage=31536000`), so the median is fast. On a cache miss or cold start
   the Static Web Apps managed runtime queues them **server-side**, and the queue drains all at once.

The navigation the user wants is then stuck behind that queue and the tab shows a loading spinner or a
blank page until it drains. Nothing fails — the requests eventually return `200`, which is why the
failure leaves no error trace anywhere.

> **Correction (2026-07-28).** The original version of this document named a third property: that
> `portal.autopilotmonitor.com` negotiates HTTP/1.1 and the prefetches exhaust the six-connection
> browser cap. **That is wrong.** It came from `curl -w '%{http_version}'` on a Windows build of curl
> whose Schannel backend has no HTTP/2 support, so it can only ever report `1.1`. ALPN says otherwise:
> `openssl s_client -alpn h2,http/1.1` negotiates **h2** on the portal host, confirmed by a .NET
> `HttpClient` request returning `HTTP/2.0`. There is no six-connection cap and connection starvation
> was never the mechanism — the queue is server-side. Verify HTTP versions with `openssl s_client
> -alpn` or `HttpClient`, never with this curl build.

# Evidence

Incident of 2026-07-27, 17:25–17:29 CEST. Browser-side dependencies during the freeze:

| Request | Duration | Status |
| --- | --- | --- |
| `/admin/presence?_rsc=…` | 151,461 ms | 200 |
| `/gather-rules?_rsc=…` | 52,283 ms | 200 |
| `/progress?_rsc=…` | 37,878 ms | 200 |
| `/analyze-rules?_rsc=…` | 37,559 ms | 200 |
| `/audit?_rsc=…` | 37,539 ms | 200 |
| Backend API, same window | 1,600–2,200 ms | 200 |

The three ~37.5 s responses complete within 340 ms of each other — a queue draining, not independent
slow requests. The corresponding `browserTimings` row for `/geographic-performance` reports
`networkDuration` = 16 ms against `processingDuration` = 225,095 ms: the network delivered the
document immediately and the browser then waited nearly four minutes for the `load` event.

Fourteen-day distribution of RSC prefetches: n = 10,428, p50 = 91 ms, p95 = 896 ms, max = 234,230 ms,
38 over 10 s, 21 over 30 s.

# Outcome, measured 2026-07-28

Two changes landed within half an hour of each other on 2026-07-27: the SWA plan went Free → Standard
at 16:02:44 UTC, and `prefetch={false}` went live at 16:34:13 UTC. Twenty hours of production
telemetry separate their effects.

**The Standard plan produced no measurable latency gain.** Latency is governed almost entirely by the
gap to the previous request — the SSR runtime cools down — so raw percentiles only measure the traffic
mix. Stratified by that gap, the two eras are indistinguishable (p50, Free → Standard): `<10s`
85 → 95 ms · `10-60s` 100 → 117 ms · `1-5min` 295 → 310 ms · `5-30min` 429 → 441 ms. Tail rates match
too: Free ran 1.64 % over 2 s and 0.374 % over 10 s (n = 10,163); Standard produced 6 and 1 events at
n = 258 against 4.2 and 1.0 expected. `browserTimings.totalDuration` by Mann-Whitney: n = 436 vs 48,
z = −0.24, **p = 0.81**.

**The prefetch fix is what removed the freezes** — worst `browserTimings.totalDuration` fell from
225,277 ms to 4,856 ms, and the 30-second events stopped.

**It also removed an accidental keep-alive.** The prefetch storm kept the SSR runtime warm: 85 % of
requests used to arrive less than 10 s apart, now 46 % do. On an identical never-cacheable route
(`/sessions/{id}`) the median rose from 87 ms to 211 ms and p90 from 465 ms to 1,480 ms. After 517
minutes idle, one request took 11,441 ms and was abandoned (`resultCode 0`). The trade — minute-long
freezes gone, ~150 ms added to a cold navigation — is worth it, but it is a trade.

Do not benchmark this with a synthetic burst. Firing requests in quick succession warms the runtime
and measures the best case: in a 25-round `curl --parallel-max 6` run, round 1 came in at p50 288 ms
and rounds 4–25 at p50 113 ms.

> **Endgame (2026-07-29).** The cold-start class was eliminated at the root: the portal became a
> full static export (`output: 'export'`) — the SWA-managed SSR runtime no longer exists, so there
> is nothing left to cool down. One escalation drove this: on 2026-07-28 a cold start exceeded the
> SWA's 45-second function limit and the landing page served a hard "Backend call failure" 500.
> The availability test that bridged the gap as a keep-alive stays as plain uptime monitoring.

# Diagnosing this class of problem

The backend Application Insights resource is the wrong place to look — a freeze appears there as an
*absence* of requests, never as an error. Browser telemetry lives in a **separate** Application
Insights resource addressed by `AUTOPILOT_MONITOR_WEB_APPINSIGHTS_ID`, which the MCP
`query_backend_logs` tool does not reach. Query it through
`.claude/commands/scripts/query-appinsights.sh` with that ID exported, and read `dependencies`
(client-side fetches, including RSC prefetches) and `browserTimings` (the network/processing split of
a document load).

# Contract

Navigation `<Link>` elements in `GlobalSidebar` and `Navbar` set `prefetch={false}`. The prefetched
RSC payload buys nothing for these routes, so the only remaining cost of a click is the route's JS
chunk, which the browser caches after first use.

Do not remove the prop to "speed up navigation" without first re-measuring the tail in the web
Application Insights resource — the prefetch is cheap only while the runtime is warm.

# Citations

* `src/Web/autopilot-monitor-web/components/GlobalSidebar.tsx` — nav link rendering, rationale comment
* `src/Web/autopilot-monitor-web/components/Navbar.tsx` — top-bar and notification links
* [SWA and Oryx node pinning](../architecture.md) — hosting model for the web app
