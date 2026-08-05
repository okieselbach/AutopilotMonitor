---
type: concept
title: MCP Tool Telemetry
description: The three telemetry layers for judging MCP tool quality — backend App Insights via X-MCP-Tool-Name, the per-user usage table, and the MCP_TOOL_LOGGING stderr lines that carry the signals the backend cannot see (soft errors, result size vs. cap, zero-hit searches, Zod rejections).
resource: src/McpServer/autopilot-monitor-mcp/src/telemetry.ts
tags: [mcp, telemetry, observability, tool-quality]
timestamp: 2026-08-05
---

# Schema

Three layers answer "are the MCP tools doing their job, and are tools missing?":

1. **Backend App Insights** — every tool handler runs inside `withToolTelemetry`
   (`src/telemetry.ts`), which propagates the tool name via AsyncLocalStorage so
   `apiFetch` stamps `X-MCP-Tool-Name` on each backend request.
   `RequestTelemetryMiddleware` copies it into the request telemetry as
   `McpToolName`: per-tool call volume, latency percentiles and HTTP failure
   rates by KQL. Blind spot: tools that never call the backend, and handler-level
   soft errors (see layer 3).
2. **Usage table** — `McpQuotaEnforcementMiddleware.TrackUsage` counts per
   user/day under the key `toolname:endpoint`; surfaced by the `get_api_usage`
   tool. Counts only, every call counts as success.
3. **stderr JSON lines** (`MCP_TOOL_LOGGING=true`, queryable via Container App
   Logs) — the layer that carries what the other two structurally cannot:
   - `tool_call`: duration, `isError` **including soft errors** (handlers never
     throw — `toolError` *returns* `isError: true`, so backend-only telemetry
     records those calls as successes), `resultChars` + `overCap` (result
     exceeds the tool's `anthropic/maxResultSizeChars` hint → the host truncates
     it; a frequently-overCap tool needs tighter defaults or projections), a
     size-bounded args summary, and the caller scope (ga/delegated/tenant).
     This is the ONLY telemetry for backend-free tools: `search_docs`,
     `validate_rule`, `get_resource`, the local ranking of `search_knowledge`.
   - `search_zero_hit`: query text (capped) whenever `search_events` /
     `search_knowledge` / `search_docs` return zero results — the most direct
     "missing docs / missing knowledge / missing tool?" demand signal.
   - `tool_call_rejected`: JSON-RPC-level rejections (Zod argument validation,
     unknown tool name). The SDK answers these *before* any handler runs, so
     they are captured by wrapping `res.end` in the `/mcp` route (stateless
     mode guarantees one JSON-RPC request per POST; `enableJsonResponse` yields
     a single buffered body). This is the strongest "the schema or description
     confuses the model" signal and was previously invisible everywhere.

The env flag defaults to `true` in `infra/mcp-server.bicep` (documented desired
state — the template is not routinely deployed; the live flag is set with
`az containerapp update --set-env-vars MCP_TOOL_LOGGING=true`).

# Examples

Count zero-hit queries to find unmet demand (Log Analytics):

```kusto
ContainerAppConsoleLogs_CL
| where Log_s has '"type":"search_zero_hit"'
| extend d = parse_json(Log_s)
| summarize count() by tostring(d.tool), tostring(d.query)
```

Per-tool soft-error and truncation rates:

```kusto
ContainerAppConsoleLogs_CL
| where Log_s has '"type":"tool_call"'
| extend d = parse_json(Log_s)
| summarize calls = count(), errors = countif(d.isError == true),
            overCap = countif(d.overCap == true), p95ms = percentile(tolong(d.durationMs), 95)
  by tool = tostring(d.tool)
```

# Citations

- `src/McpServer/autopilot-monitor-mcp/src/telemetry.ts` — wrapper + log emitters
- `src/McpServer/autopilot-monitor-mcp/src/index.ts` — `/mcp` rejection sniffer
- `src/Backend/AutopilotMonitor.Functions/Middleware/RequestTelemetryMiddleware.cs` — `McpToolName` stamping
- `src/Backend/AutopilotMonitor.Functions/Middleware/McpQuotaEnforcementMiddleware.cs` — usage-table counting
