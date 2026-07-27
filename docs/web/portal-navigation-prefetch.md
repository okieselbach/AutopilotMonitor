---
type: Concept
title: Portal Navigation Prefetch & the SWA Runtime Bottleneck
description: Why every navigation Link in the portal sets prefetch={false} — the App Router's viewport prefetch fires one RSC request per visible link against an HTTP/1.1 Static Web Apps runtime, and its latency tail starves the connection pool that serves the document and the route chunks.
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

Three properties combine into a portal-wide freeze:

1. **Fan-out.** The navigation is fully rendered on each page load, so a single navigation issues one
   RSC request per visible link — measured at ~10,400 requests over 14 days.
2. **Shared origin over HTTP/1.1.** `portal.autopilotmonitor.com` serves the document, the JS route
   chunks *and* the RSC payloads, and it negotiates HTTP/1.1 — not HTTP/2. Browsers cap concurrent
   connections per origin at six.
3. **A heavy latency tail on the SSR runtime.** RSC responses are prerendered and edge-cached
   (`x-nextjs-cache: HIT`, `s-maxage=31536000`), so the median is fast. On a cache miss or cold start
   the Static Web Apps managed runtime queues them.

When the tail hits, the in-flight prefetches hold all six connections. The document request and the
route chunks of the page the user actually wants then have no socket available, and the tab shows a
loading spinner or a blank page until the queue drains. Nothing fails — the requests eventually
return `200`, which is why the failure leaves no error trace anywhere.

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
