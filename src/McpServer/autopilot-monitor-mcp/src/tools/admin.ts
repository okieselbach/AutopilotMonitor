import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { apiFetch, buildQuery, followNextLink, pickGlobalOrTenantPath, scanUntilMatch } from '../client.js';
import { withToolTelemetry } from '../telemetry.js';
import { getResourceContent, assertKnownEventType } from '../resource-catalog.js';
import { READ_ONLY, READ_ONLY_OPEN, MAX_RESULT_SIZE_CHARS, toolResultText, SessionIdSchema } from './shared.js';
import { toolError } from './error-handler.js';

/**
 * Non-sensitive tenant fields safe to surface to the model. Keep-list (not
 * deny-list) on purpose: /api/config/all returns FULL tenant configs including
 * secrets (Teams/webhook URLs, the diagnostics blob SAS URL, branding image
 * URLs). A keep-list means any future config field — including a new secret —
 * is dropped by default rather than leaking by omission.
 */
export const TENANT_SAFE_FIELDS: ReadonlySet<string> = new Set([
  'tenantId', 'domainName', 'planTier', 'disabled', 'disabledReason',
  'onboardedAt', 'onboardedBy', 'lastUpdated', 'dataRetentionDays',
]);

/**
 * Normalize the /api/config/all payload (bare array or common envelope) into a
 * list of tenants projected down to TENANT_SAFE_FIELDS. Exported for unit tests
 * — this is the security boundary that keeps tenant secrets out of model context.
 */
export function extractTenantList(data: unknown): Record<string, unknown>[] {
  const rows: unknown[] = Array.isArray(data)
    ? data
    : ((data as { configurations?: unknown[]; tenants?: unknown[] })?.configurations
      ?? (data as { tenants?: unknown[] })?.tenants
      ?? []);
  return rows.map((row) => {
    const projected: Record<string, unknown> = {};
    if (row && typeof row === 'object') {
      for (const [key, value] of Object.entries(row as Record<string, unknown>)) {
        if (TENANT_SAFE_FIELDS.has(key)) projected[key] = value;
      }
    }
    return projected;
  });
}

/**
 * Infer the geographic grouping level from a locationKey's segment count, so the
 * backend (which matches by reconstructing the same delimited key for a given
 * groupBy) resolves country/region keys — not just city keys. Mirrors
 * GetGeographicMetricsFunction.GetLocationKey:
 *   country → "US"                       (1 segment)
 *   region  → "Saxony, DE"               (2 segments)
 *   city    → "Falkenstein, Saxony, DE"  (3 segments)
 * Best-effort fallback for the raw-locationKey path; the structured
 * country/region/city path is the robust drilldown.
 */
export function inferGeoGroupBy(locationKey: string): 'country' | 'region' | 'city' {
  const segments = locationKey.split(',').length;
  return segments >= 3 ? 'city' : segments === 2 ? 'region' : 'country';
}

/**
 * Translate get_geographic_sessions location inputs into backend query params.
 * A raw locationKey wins (carries an inferred groupBy so non-city keys resolve);
 * otherwise structured country/region/city are forwarded verbatim and matched
 * against the actual Geo* fields server-side. Returns {} when neither is given.
 */
export function buildGeoLocationParams(input: {
  locationKey?: string; country?: string; region?: string; city?: string;
}): Record<string, string | undefined> {
  const { locationKey, country, region, city } = input;
  if (locationKey) {
    return { locationKey, groupBy: inferGeoGroupBy(locationKey) };
  }
  if (country) {
    return { country, region: region || undefined, city: city || undefined };
  }
  return {};
}

/**
 * Echo the effective window + session cap for get_platform_metrics. The backend
 * analyzes only the newest `sessionLimit` sessions inside the window (default
 * 100), so on busy installs a wider `days` changes nothing — `truncated` flags
 * when the cap was hit (more sessions than analyzed likely exist). Prefers the
 * backend's clamped echo over the requested values.
 */
export function platformWindowEcho(
  raw: { windowDays?: number; sessionLimit?: number },
  requested: { days: number; limit: number },
  sessionsAnalyzed: number,
): { windowDays: number; sessionLimit: number; truncated: boolean } {
  const sessionLimit = raw.sessionLimit ?? requested.limit;
  return {
    windowDays: raw.windowDays ?? requested.days,
    sessionLimit,
    truncated: sessionsAnalyzed >= sessionLimit,
  };
}

/**
 * Page client-side over an MCP-usage payload. The mcp-usage endpoints return the
 * full set in one shot (no server-side cursor): the global per-record breakdown
 * alone can run to thousands of rows and overflow the inline response budget. We
 * slice whichever array the payload carries ("records" for the per-user and
 * global breakdowns, "summaries" for the daily view) and echo every other
 * top-level field (tenantId/userId/usagePlan) verbatim. The "continuation"
 * carries an integer offset (usage-offset:N); each page re-reads the same set,
 * so raise pageSize to amortize round-trips on a full sweep. Mirrors the
 * get_geographic_sessions client-side pager. Exported for unit testing.
 */
export function paginateUsage(
  data: unknown,
  pageSize: number,
  continuation?: string,
): Record<string, unknown> {
  if (!data || typeof data !== 'object') return { records: [], totalCount: 0, count: 0, offset: 0, nextLink: null };
  const obj = data as Record<string, unknown>;
  const arrayKey = Array.isArray(obj.records) ? 'records' : Array.isArray(obj.summaries) ? 'summaries' : null;
  if (!arrayKey) return obj; // unexpected shape — pass through untouched rather than mangle it
  const all = obj[arrayKey] as unknown[];
  const m = continuation ? /^usage-offset:(\d+)$/.exec(continuation) : null;
  const offset = m ? parseInt(m[1], 10) : 0;
  const slice = all.slice(offset, offset + pageSize);
  const nextOffset = offset + pageSize;
  const nextLink = nextOffset < all.length ? `usage-offset:${nextOffset}` : null;
  const { records: _records, summaries: _summaries, ...rest } = obj;
  return {
    ...rest,
    totalCount: all.length,
    count: slice.length,
    offset,
    [arrayKey]: slice,
    nextLink,
  };
}

export function registerAdminTools(server: McpServer, ga: boolean): void {
  // Tool 11: get_api_usage — Global Admin only; not registered for normal users
  // (the `if (ga)` guards the whole single server.registerTool(...) statement).
  if (ga) server.registerTool(
    'get_api_usage',
    {
      title: 'API Usage',
      description:
        'Get API/MCP usage statistics. Shows request counts per endpoint per day. ' +
        'Use to monitor platform usage, identify heavy users, or debug rate limiting. ' +
        'Use userId for a specific user, daily for aggregated summaries, or neither for global per-record breakdown. ' +
        'The daily and global breakdowns are Global Admin only; the per-user (userId) view also works for a ' +
        'Tenant Admin querying a user within their own tenant. ' +
        'The global per-record breakdown spans every user/endpoint and can run to thousands of rows, so results ' +
        'are paged: the default pageSize is 200 and the response carries a "nextLink" when more remain — pass that ' +
        'whole string back as "continuation" to get the next slice, and stop when nextLink is absent. Narrow with ' +
        'dateFrom/dateTo (default is the full retention window) or daily=true for a compact summary.',
      inputSchema: {
        userId: z.string().optional().describe('Specific user object ID to query usage for'),
        tenantId: z.string().optional().describe('Filter usage by tenant ID'),
        dateFrom: z.string().optional().describe('Start date (YYYY-MM-DD)'),
        dateTo: z.string().optional().describe('End date (YYYY-MM-DD)'),
        daily: z.boolean().optional().default(false).describe('Return daily aggregated summary instead of per-endpoint breakdown'),
        pageSize: z.coerce.number().int().min(1).max(2000).optional().default(200)
          .describe('Rows to return per call (1-2000, default 200). Follow nextLink for more; raise it for full sweeps.'),
        continuation: z.string().optional()
          .describe('Pass the whole nextLink string from the prior response to fetch the next slice.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_api_usage', async () => {
      try {
        let data: unknown;
        const params: Record<string, string | undefined> = { tenantId: args.tenantId, dateFrom: args.dateFrom, dateTo: args.dateTo };
        if (args.userId) {
          data = await apiFetch(`/api/metrics/mcp-usage/user/${encodeURIComponent(args.userId)}${buildQuery(params)}`);
        } else if (args.daily) {
          data = await apiFetch(`/api/global/metrics/mcp-usage/daily${buildQuery(params)}`);
        } else {
          data = await apiFetch(`/api/global/metrics/mcp-usage${buildQuery(params)}`);
        }
        return toolResultText(paginateUsage(data, args.pageSize, args.continuation), MAX_RESULT_SIZE_CHARS.adminStream);
      } catch (error: unknown) {
        return toolError('get_api_usage', args, error);
      }
    })
  );

  // Tool 12: get_geographic_metrics
  server.registerTool(
    'get_geographic_metrics',
    {
      title: 'Geographic Metrics',
      description:
        'Get geographic distribution of enrollments — where devices are enrolling from, with performance comparisons. ' +
        'Shows per-location: session counts, success rates, avg/median/p95 duration, throughput, and outlier detection. ' +
        (ga ? 'Omit tenantId for cross-tenant view (Global Admin). ' : '') +
        'Use get_geographic_sessions to drill into a specific location. ' +
        'days accepts any value 1-365 (e.g. 5, 7, 12, 30, 90).',
      inputSchema: {
        tenantId: z.string().optional().describe(ga ? 'Tenant ID. Omit for cross-tenant view (Global Admin only).' : 'Optional tenant ID. Defaults to your tenant.'),
        days: z.coerce.number().int().min(1).max(365).optional().default(30)
          .describe('Time range in days (1-365). Defaults to 30.'),
        groupBy: z.enum(['country', 'region', 'city']).optional().default('city')
          .describe('Geographic grouping level (default: "city")'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_geographic_metrics', async () => {
      try {
        const { tenantId, ...rest } = args;
        const params: Record<string, string | number | undefined> = { ...rest };
        if (tenantId) params.tenantId = tenantId;
        const prefix = pickGlobalOrTenantPath('/api/global/metrics', '/api/metrics');
        const data = await apiFetch(`${prefix}/geographic${buildQuery(params)}`);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('get_geographic_metrics', args, error);
      }
    })
  );

  // Tool 13: get_geographic_sessions
  server.registerTool(
    'get_geographic_sessions',
    {
      title: 'Geographic Sessions',
      description:
        'Drill into a specific geographic location and list its enrollment sessions (lean rows). ' +
        'Preferred: pass the structured country / region / city you saw in get_geographic_metrics ' +
        '(country alone lists the whole country; add region, then city, to narrow). ' +
        'Alternatively pass locationKey verbatim from a get_geographic_metrics row (e.g. "US", ' +
        '"Saxony, DE", or "Falkenstein, Saxony, DE") — any grouping level resolves. ' +
        'A busy location can hold thousands of sessions, so results are paged: the default pageSize is 50 ' +
        'and the response carries a "nextLink" when more remain — pass that whole string back as "continuation" ' +
        'to get the next slice, and stop when nextLink is absent. (Pagination is applied to the location result ' +
        'set, so raise pageSize for fewer round-trips when you need a full sweep.)',
      inputSchema: {
        locationKey: z.string().optional().describe('Location key copied verbatim from a get_geographic_metrics row (e.g. "US", "Saxony, DE", "Falkenstein, Saxony, DE"). Any grouping level works. If provided, country/region/city are ignored.'),
        country: z.string().optional().describe('Country filter, matched against the session GeoCountry exactly as shown by get_geographic_metrics (typically a 2-letter code, e.g. "DE", "US", "CH"). Used when locationKey is not provided; lists the whole country on its own.'),
        region: z.string().optional().describe('Region/state filter (e.g. "Saxony", "North Carolina"). Optional; used with country to narrow.'),
        city: z.string().optional().describe('City filter (e.g. "Falkenstein"). Optional; used with country to narrow.'),
        tenantId: z.string().optional().describe(ga ? 'Tenant ID. Omit for cross-tenant view (Global Admin only).' : 'Optional tenant ID. Defaults to your tenant.'),
        days: z.coerce.number().int().min(1).max(365).optional().default(30)
          .describe('Time range in days (1-365). Defaults to 30.'),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(50)
          .describe('Sessions to return per call (1-1000, default 50). Follow nextLink for more; raise it for full sweeps.'),
        continuation: z.string().optional()
          .describe('Pass the whole nextLink string from the prior response to fetch the next slice.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_geographic_sessions', async () => {
      try {
        const { tenantId, locationKey, country, region, city, pageSize, continuation, ...rest } = args;
        const params: Record<string, string | number | undefined> = {
          ...rest,
          ...buildGeoLocationParams({ locationKey, country, region, city }),
        };
        if (tenantId) params.tenantId = tenantId;
        const prefix = pickGlobalOrTenantPath('/api/global/metrics', '/api/metrics');
        const data = await apiFetch(`${prefix}/geographic/sessions${buildQuery(params)}`) as
          { success?: boolean; sessions?: unknown[]; totalCount?: number };

        // The location-sessions endpoint filters in-memory and returns the full
        // set in one shot (no server-side cursor). A single busy location can
        // exceed the client's inline response budget, so we page client-side:
        // the "continuation" carries an integer offset into the location result
        // set. (Each page re-fetches the location set; raise pageSize to amortize.)
        const all = Array.isArray(data?.sessions) ? data.sessions : [];
        const m = continuation ? /^geo-offset:(\d+)$/.exec(continuation) : null;
        const offset = m ? parseInt(m[1], 10) : 0;
        const slice = all.slice(offset, offset + pageSize);
        const nextOffset = offset + pageSize;
        const nextLink = nextOffset < all.length ? `geo-offset:${nextOffset}` : null;

        return toolResultText(
          {
            success: data?.success ?? true,
            totalCount: data?.totalCount ?? all.length,
            count: slice.length,
            offset,
            sessions: slice,
            nextLink,
          },
          MAX_RESULT_SIZE_CHARS.sessions);
      } catch (error: unknown) {
        return toolError('get_geographic_sessions', args, error);
      }
    })
  );

  // Tool 14: get_platform_metrics — Global Admin only; not registered for normal users.
  if (ga) server.registerTool(
    'get_platform_metrics',
    {
      title: 'Platform Metrics',
      description:
        'Get aggregated platform-level agent performance metrics across recent sessions. ' +
        'Returns: avg/max/p95 CPU, memory (working set, private bytes), network (bytes up/down, latency, requests), ' +
        'top sessions by CPU/memory, and per-agent-version breakdown. Global Admin only. ' +
        'Only the newest maxSessions sessions inside the window are analyzed (default 100), so on a ' +
        'busy install widening days alone may not change the result — raise maxSessions to widen the ' +
        'sample. The response echoes sessionLimit and a truncated flag when the cap was hit. ' +
        'days accepts any value 1-365 (e.g. 5, 7, 12, 30, 90).',
      inputSchema: {
        days: z.coerce.number().int().min(1).max(365).optional().default(30)
          .describe('Time window in days (1-365). Defaults to 30.'),
        maxSessions: z.coerce.number().int().min(1).max(2000).optional().default(100)
          .describe('Newest N sessions in the window to analyze (1-2000, default 100). Raise for a wider sample on busy installs.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_platform_metrics', async () => {
      try {
        type SessionMetric = {
          sessionId: string; tenantId: string; deviceName?: string; model?: string; status?: string;
          agentVersion?: string; snapshotCount: number;
          avgCpu: number; maxCpu: number; avgWorkingSet: number; maxWorkingSet: number;
          avgPrivateBytes: number; avgLatency: number;
          totalBytesUp: number; totalBytesDown: number; totalRequests: number;
        };
        const raw = await apiFetch(`/api/global/metrics/platform${buildQuery({ days: args.days, limit: args.maxSessions })}`) as
          { sessions?: SessionMetric[]; windowDays?: number; sessionLimit?: number };
        const sessions = raw?.sessions ?? [];
        const requested = { days: args.days, limit: args.maxSessions };
        if (sessions.length === 0) {
          return toolResultText(
            { ...platformWindowEcho(raw, requested, 0), sessionsAnalyzed: 0, message: 'No performance data available' },
            MAX_RESULT_SIZE_CHARS.small);
        }
        const windowEcho = platformWindowEcho(raw, requested, sessions.length);

        const avg = (arr: number[]) => arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : 0;
        const p95 = (arr: number[]) => { const s = [...arr].sort((a, b) => a - b); return s[Math.floor(s.length * 0.95)] ?? 0; };
        const round = (n: number) => Math.round(n * 100) / 100;

        const cpus = sessions.map(s => s.avgCpu);
        const maxCpus = sessions.map(s => s.maxCpu);
        const ws = sessions.map(s => s.avgWorkingSet);
        const pb = sessions.map(s => s.avgPrivateBytes);
        const lat = sessions.filter(s => s.avgLatency > 0).map(s => s.avgLatency);

        const topCpu = [...sessions].sort((a, b) => b.maxCpu - a.maxCpu).slice(0, 5).map(s => ({
          sessionId: s.sessionId, device: s.deviceName, model: s.model, maxCpu: round(s.maxCpu), avgCpu: round(s.avgCpu),
        }));

        const topMem = [...sessions].sort((a, b) => b.avgWorkingSet - a.avgWorkingSet).slice(0, 5).map(s => ({
          sessionId: s.sessionId, device: s.deviceName, model: s.model, avgWorkingSetMB: round(s.avgWorkingSet),
        }));

        const byVersion: Record<string, { count: number; avgCpu: number[]; avgMem: number[] }> = {};
        for (const s of sessions) {
          const v = s.agentVersion ?? 'unknown';
          if (!byVersion[v]) byVersion[v] = { count: 0, avgCpu: [], avgMem: [] };
          byVersion[v].count++;
          byVersion[v].avgCpu.push(s.avgCpu);
          byVersion[v].avgMem.push(s.avgWorkingSet);
        }
        const versionBreakdown = Object.entries(byVersion).map(([version, d]) => ({
          version, sessions: d.count, avgCpu: round(avg(d.avgCpu)), avgMemMB: round(avg(d.avgMem)),
        })).sort((a, b) => b.sessions - a.sessions);

        const summary = {
          windowDays: windowEcho.windowDays,
          sessionLimit: windowEcho.sessionLimit,
          truncated: windowEcho.truncated,
          sessionsAnalyzed: sessions.length,
          cpu: { avgPercent: round(avg(cpus)), maxPercent: round(Math.max(...maxCpus)), p95Percent: round(p95(maxCpus)) },
          memory: {
            avgWorkingSetMB: round(avg(ws)), maxWorkingSetMB: round(Math.max(...ws)), p95WorkingSetMB: round(p95(ws)),
            avgPrivateBytesMB: round(avg(pb)),
          },
          network: {
            totalBytesUp: sessions.reduce((a, s) => a + s.totalBytesUp, 0),
            totalBytesDown: sessions.reduce((a, s) => a + s.totalBytesDown, 0),
            totalRequests: sessions.reduce((a, s) => a + s.totalRequests, 0),
            avgLatencyMs: round(avg(lat)),
          },
          topSessionsByCpu: topCpu,
          topSessionsByMemory: topMem,
          agentVersionBreakdown: versionBreakdown,
        };
        return toolResultText(summary, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('get_platform_metrics', args, error);
      }
    })
  );

  // Tool 15: get_usage_metrics
  server.registerTool(
    'get_usage_metrics',
    {
      title: 'Usage Metrics',
      description:
        (ga
          ? 'Get usage statistics. Omit tenantId for the cross-tenant platform overview (Global Admin), or pass tenantId to filter it to a single tenant. '
          : 'Get usage statistics for your tenant: session volumes, feature adoption, success rate, active users. ') +
        'days accepts any value 1-365 (e.g. 5, 7, 12, 30, 90).',
      inputSchema: {
        tenantId: z.string().optional().describe(ga ? 'Filter the platform-wide view to a single tenant (Global Admin only). Omit for the whole platform.' : 'Optional; ignored — usage is scoped to your tenant.'),
        days: z.coerce.number().int().min(1).max(365).optional().default(30)
          .describe('Time window in days (1-365). Defaults to 30. Sessions.Total / Tenants.Total reflect this window.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_usage_metrics', async () => {
      try {
        const { tenantId, days } = args;
        // GA → /api/global/metrics/usage (tenantId is a filter); Tenant-Admin → /api/metrics/usage
        // (JWT-scoped; tenantId ignored). Routing by role unlocks the MemberRead tenant endpoint
        // for non-GA callers instead of a blanket 403.
        const path = pickGlobalOrTenantPath('/api/global/metrics/usage', '/api/metrics/usage');
        const data = await apiFetch(`${path}${buildQuery({ tenantId, days })}`);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('get_usage_metrics', args, error);
      }
    })
  );

  // Tool: list_tenants — reuses the GlobalAdminOnly /api/config/all endpoint.
  // The endpoint returns FULL configs (incl. secrets); extractTenantList strips
  // them down to TENANT_SAFE_FIELDS before anything reaches the model.
  // Global Admin only; not registered for normal users.
  if (ga) server.registerTool(
    'list_tenants',
    {
      title: 'List Tenants',
      description:
        'List onboarded tenants with their identity, plan tier, and lifecycle status (onboarded/disabled dates). ' +
        'Global Admin only. Use this to discover tenant IDs for the tenantId parameter of other tools when running ' +
        'cross-tenant investigations. Returns only non-sensitive fields — secrets (webhook URLs, SAS URLs) are stripped ' +
        'server-side. Tenants are sorted by tenantId and returned in pages (default 100). For lean ID discovery pass ' +
        '`fields=tenantId,domainName` — the projection is applied server-side and is echoed in nextLink, so it carries ' +
        'across every page automatically. ' +
        'Pagination: when "nextLink" is present, more tenants are available — call again and pass that whole string ' +
        'back as "continuation". Stop when nextLink is absent.',
      inputSchema: {
        fields: z.string().optional()
          .describe('Comma-separated subset of safe fields to return (e.g. "tenantId,domainName" for lean ID discovery). ' +
                    'tenantId is always included. Default: all safe fields. Applied server-side; unknown/secret keys are ignored.'),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(100)
          .describe('Page size (1-1000, default 100). Tenants are sorted by tenantId; follow nextLink to fetch more.'),
        continuation: z.string().optional()
          .describe('Either the opaque "continuation" value from a prior response or the full nextLink path — both are accepted; the latter is preferred so backend-echoed query params (pageSize, fields) round-trip correctly.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('list_tenants', async () => {
      try {
        const { fields, pageSize, continuation } = args;
        // /api/config/all supports opt-in pagination: passing pageSize switches the
        // backend from its legacy unpaginated bare array to a secret-stripped
        // { count, tenants, nextLink } envelope, with fields= projected server-side
        // and echoed in nextLink. extractTenantList re-applies the keep-list as
        // defense-in-depth (harmless pass-through once the backend has projected).
        const path = followNextLink('/api/config/all', { pageSize, fields }, continuation);
        const data = await apiFetch(path) as { count?: number; tenants?: unknown[]; nextLink?: string | null };
        const tenants = extractTenantList({ tenants: data?.tenants ?? [] });
        return toolResultText(
          { count: tenants.length, tenants, nextLink: data?.nextLink ?? null },
          MAX_RESULT_SIZE_CHARS.adminStream);
      } catch (error: unknown) {
        return toolError('list_tenants', args, error);
      }
    })
  );

  // Tool 16: get_audit_logs
  server.registerTool(
    'get_audit_logs',
    {
      title: 'Audit Logs',
      description:
        'Get audit trail of administrative actions: config changes, device blocks, user management, report submissions. ' +
        (ga ? 'Omit tenantId for cross-tenant audit log (Global Admin). ' : '') +
        'Forensics: dateFrom / dateTo (ISO 8601 UTC) bound the search window exactly; without either, the backend defaults to the last 30 days. ' +
        'Narrow further with exact-match field filters — action (e.g. "config_updated", "device_blocked"), performedBy (actor UPN), ' +
        'entityType (e.g. "TenantConfiguration", "Device"), entityId (the affected entity\'s id). All are applied server-side and ' +
        'are case-sensitive equality matches; combine them to answer questions like "every action alice@contoso.com took on this device". ' +
        'Pagination: when "nextLink" is present in the response, more entries are available — call this tool again and pass the ' +
        'whole nextLink string (e.g. "/api/global/audit/logs?pageSize=...&continuation=...&dateFrom=...&dateTo=...") as ' +
        '"continuation". The tool follows it verbatim so the backend-defaulted date window AND any field filters round-trip ' +
        'correctly (otherwise a follow-up call would compute a fresh "now" and the token fingerprint would mismatch). Stop when nextLink is absent.',
      inputSchema: {
        tenantId: z.string().optional().describe(ga ? 'Tenant ID for tenant-scoped audit log. Omit for cross-tenant view (Global Admin only).' : 'Optional tenant ID. Defaults to your tenant.'),
        dateFrom: z.string().optional().describe('ISO 8601 UTC timestamp — inclusive lower bound of the audit window.'),
        dateTo: z.string().optional().describe('ISO 8601 UTC timestamp — inclusive upper bound of the audit window.'),
        action: z.string().optional().describe('Exact-match filter on the action (e.g. "config_updated", "device_blocked", "deletion_started").'),
        performedBy: z.string().optional().describe('Exact-match filter on the actor UPN that performed the action (e.g. "alice@contoso.com").'),
        entityType: z.string().optional().describe('Exact-match filter on the affected entity type (e.g. "TenantConfiguration", "Device", "User").'),
        entityId: z.string().optional().describe('Exact-match filter on the affected entity id (e.g. a tenantId, deviceId, or report id).'),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(200)
          .describe('Page size (1-1000, default 200). Returns this many entries per call; follow nextLink to fetch more.'),
        continuation: z.string().optional()
          .describe('Either the opaque "continuation" value from a prior response or the full nextLink path — both are accepted; the latter is preferred so backend-echoed query params (incl. resolved dateFrom/dateTo and field filters) round-trip correctly.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_audit_logs', async () => {
      try {
        const { tenantId, dateFrom, dateTo, action, performedBy, entityType, entityId, pageSize, continuation } = args;
        const basePath = pickGlobalOrTenantPath('/api/global/audit/logs', '/api/audit/logs');
        const path = followNextLink(
          basePath,
          { tenantId, dateFrom, dateTo, action, performedBy, entityType, entityId, pageSize },
          continuation,
        );
        const data = await apiFetch(path);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.adminStream);
      } catch (error: unknown) {
        return toolError('get_audit_logs', args, error);
      }
    })
  );

  // Tool 17: get_ops_events — Global Admin only; not registered for normal users.
  if (ga) server.registerTool(
    'get_ops_events',
    {
      title: 'Operational Events',
      description:
        'Get operational events for platform monitoring. Shows consent flow results, maintenance runs, security blocks, ' +
        'tenant offboards, agent timeouts, and blob storage health. Global Admin only. ' +
        'Use category to narrow results (Consent, Maintenance, Security, Tenant, Agent, SLA). ' +
        'Forensics: dateFrom / dateTo (ISO 8601 UTC) bound the search window exactly; without either, the backend defaults to the last 30 days. ' +
        'Pagination: when "nextLink" is present in the response, more events are available — call this tool again and pass the ' +
        'whole nextLink string (e.g. "/api/global/ops-events?pageSize=...&continuation=...&dateFrom=...&dateTo=...") as ' +
        '"continuation". The tool follows it verbatim so the backend-defaulted date window round-trips correctly (otherwise ' +
        'a follow-up call would compute a fresh "now" and the token fingerprint would mismatch). Stop when nextLink is absent.',
      inputSchema: {
        category: z.string().optional().describe('Filter by category: Consent, Maintenance, Security, Tenant, Agent, SLA'),
        tenantId: z.string().optional().describe('Optional — filter events to a single tenant. Omit for cross-tenant view.'),
        dateFrom: z.string().optional().describe('ISO 8601 UTC timestamp — inclusive lower bound of the window.'),
        dateTo: z.string().optional().describe('ISO 8601 UTC timestamp — inclusive upper bound of the window.'),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(200)
          .describe('Page size (1-1000, default 200). Returns this many events per call; follow nextLink to fetch more.'),
        continuation: z.string().optional()
          .describe('Either the opaque "continuation" value from a prior response or the full nextLink path — both are accepted; the latter is preferred so backend-echoed query params (incl. resolved dateFrom/dateTo) round-trip correctly.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_ops_events', async () => {
      try {
        const { category, tenantId, dateFrom, dateTo, pageSize, continuation } = args;
        const path = followNextLink(
          '/api/global/ops-events',
          { category, tenantId, dateFrom, dateTo, pageSize },
          continuation,
        );
        const data = await apiFetch(path);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.adminStream);
      } catch (error: unknown) {
        return toolError('get_ops_events', args, error);
      }
    })
  );

  // Tool 18: list_session_reports — Global Admin only; not registered for normal users.
  if (ga) server.registerTool(
    'list_session_reports',
    {
      title: 'List Session Reports',
      description:
        'List session reports submitted by tenant admins. Reports contain user comments, screenshots, and agent logs for troubleshooting. ' +
        'Global Admin only — returns reports across all tenants by default; pass tenantId to filter to one tenant. ' +
        'This endpoint is fully paginated — there is no truncation. The default pageSize=200 is tuned for typical ' +
        'interactive queries; raise it (up to 1000) for bulk pulls. Pass the whole nextLink string as "continuation" so ' +
        'all backend-echoed query params round-trip correctly.',
      inputSchema: {
        tenantId: z.string().optional().describe('Optional — filter to a single tenant. Omit for cross-tenant view.'),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(200)
          .describe('Page size (1-1000, default 200). Returns this many reports per call; follow nextLink to fetch more.'),
        continuation: z.string().optional()
          .describe('Either the opaque "continuation" value from a prior response or the full nextLink path — both are accepted; the latter is preferred so backend-echoed query params round-trip correctly.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('list_session_reports', async () => {
      try {
        const { tenantId, pageSize, continuation } = args;
        const path = followNextLink(
          '/api/global/session-reports',
          { tenantId, pageSize },
          continuation,
        );
        const data = await apiFetch(path);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.adminStream);
      } catch (error: unknown) {
        return toolError('list_session_reports', args, error);
      }
    })
  );

  // ── Raw Data Tools ────────────────────────────────────────────────────

  // Tool 18: query_raw_events
  server.registerTool(
    'query_raw_events',
    {
      title: 'Query Raw Events',
      description:
        'RAW CROSS-SESSION EVENT QUERY (fallback for broader scope). ' +
        'Query raw enrollment events with flexible filters across sessions. ' +
        (ga ? 'Omit tenantId for cross-tenant search (Global Admin), or specify tenantId for single-tenant. ' : '') +
        'Use this when search_events does not cover the time range or session scope you need, ' +
        'or when you need exact event-type filtering across many sessions. ' +
        'Returns the LITERAL stored Events rows — every column verbatim, PascalCase, incl. ' +
        'PartitionKey/RowKey/Timestamp. It is deliberately UNENRICHED: "DataJson" is the raw stored ' +
        'string (not parsed), Severity/Phase are the raw ints, and there are no decoded Win32/NTSTATUS ' +
        'error meanings. For the enriched/structured stream (parsed Data, decoded error text) use ' +
        'get_session_events or search_events instead. ' +
        'eventType is validated against the event_types catalog — a typo is rejected with a clear error, not a silent ' +
        'empty result. When you filter, the tool auto-scans forward past empty pages, so a returned "count": 0 with no ' +
        '"nextLink" means truly no matches, while "moreToScan": true means the per-call scan budget was hit (pass ' +
        'nextLink as "continuation" to keep scanning). ' +
        'This endpoint is fully paginated — there is no truncation. The default pageSize=200 is tuned for typical ' +
        'interactive queries; raise it (up to 1000) for forensics-grade exact recall. For broad analysis, use ' +
        'pageSize=1000 and follow nextLink repeatedly until absent. Pass the whole nextLink string as "continuation" ' +
        'so all backend-echoed query params round-trip correctly. Note: pageSize is the index-scan cadence — a single ' +
        'indexed session can contribute multiple events, so total events per page may exceed pageSize. ' +
        'For COUNTING / AGGREGATION pass a lean `fields=` projection (e.g. `fields=EventType,Severity,Timestamp`) — ' +
        'a pure pass-through over the real column names that drops the heavy `DataJson` payload (a single ' +
        'app_install_failed event can be tens of KB), so responses stay small; PartitionKey + RowKey are always ' +
        'kept. ' +
        (ga ? 'When querying by sessionId you may omit tenantId — it is auto-resolved from the session (Global Admin).' : ''),
      inputSchema: {
        tenantId: z.string().optional().describe(ga ? 'Tenant ID. Omit for cross-tenant search, or to auto-resolve from a sessionId query (Global Admin only).' : 'Optional tenant ID. Defaults to your tenant.'),
        sessionId: SessionIdSchema.optional().describe('Filter to a specific session'),
        eventType: z.string().optional().describe('Event type filter (e.g. "app_install_failed", "error_detected")'),
        severity: z.enum(['Info', 'Warning', 'Error', 'Critical']).optional(),
        source: z.string().optional().describe('Filter by event source/app name (substring match)'),
        startedAfter: z.string().optional().describe('ISO 8601 datetime — only events after this'),
        startedBefore: z.string().optional().describe('ISO 8601 datetime — only events before this'),
        fields: z.string().optional()
          .describe('Comma-separated pass-through projection over the literal stored column names (case-insensitive); narrows the row but never drops a real column. PartitionKey + RowKey are always kept. Stored columns: PartitionKey, RowKey, Timestamp, EventId, SessionId, EventType, Severity (int), Source, Phase (int), Message, Sequence, DataJson (raw string), ReceivedAt, OriginalTimestamp, TimestampClamped, CausedByTransitionStepIndex, CausedBySignalOrdinal. Omit for the full raw row.'),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(200)
          .describe('Page size (1-1000, default 200). Controls index-scan depth per call; follow nextLink for more.'),
        continuation: z.string().optional()
          .describe('Either the opaque "continuation" value from a prior response or the full nextLink path — both are accepted; the latter is preferred so backend-echoed query params round-trip correctly.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('query_raw_events', async () => {
      try {
        const { tenantId, sessionId, eventType, severity, source, startedAfter, startedBefore, fields, pageSize, continuation } = args;
        if (eventType) assertKnownEventType(eventType);
        const basePath = pickGlobalOrTenantPath('/api/global/raw/events', '/api/raw/events');
        const path = followNextLink(
          basePath,
          { tenantId, sessionId, eventType, severity, source, startedAfter, startedBefore, fields, pageSize },
          continuation,
        );
        // severity/source (and eventType/time on the single-session path) are post-filtered
        // in-memory, so a page can be empty while matches sit further ahead. Auto-exhaust
        // forward so the model isn't misled by an empty-but-continuable page.
        const data = await scanUntilMatch(path, basePath);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.events);
      } catch (error: unknown) {
        return toolError('query_raw_events', args, error);
      }
    })
  );

  // Tool 19: query_raw_sessions
  server.registerTool(
    'query_raw_sessions',
    {
      title: 'Query Raw Sessions',
      description:
        'Returns the LITERAL stored SessionsIndex rows — every column verbatim, PascalCase, incl. ' +
        'PartitionKey/RowKey/Timestamp (e.g. OsEdition, OsDisplayVersion, ImeAgentVersion, GeoRegion/City/Loc, ' +
        'FailureSource, CurrentPhaseDetail, LastEventAt, ResumedAt, StalledAt, PlatformScriptCount, DeletionState, ...). ' +
        'For the curated/typed view (camelCase summary, durationSeconds, deviceProperties filtering) use ' +
        'search_sessions or get_session instead. ' +
        (ga ? 'Specify tenantId for a specific tenant, or omit for cross-tenant access (Global Admin only). ' : '') +
        'For COUNTING / AGGREGATION pass a lean `fields=Status,AgentVersion,StartedAt` (or similar) — a pure pass-through ' +
        'over the real column names that avoids the response cap fat raw rows trip; PartitionKey + RowKey are always kept. ' +
        'For VERSION sweeps use `agentVersionPrefix=2.0.` instead of one call per build. ' +
        'This endpoint is fully paginated — there is no truncation. Default pageSize=200; raise it (up to 1000) for bulk pulls. ' +
        'Pass the whole nextLink string as "continuation" so all backend-echoed query params round-trip correctly.',
      inputSchema: {
        tenantId: z.string().optional().describe(ga ? 'Tenant ID to query. Omit for cross-tenant access (Global Admin only).' : 'Optional tenant ID. Defaults to your tenant.'),
        status: z.enum(['InProgress', 'Pending', 'Stalled', 'Succeeded', 'Failed']).optional(),
        startedAfter: z.string().optional().describe('ISO 8601 datetime'),
        startedBefore: z.string().optional().describe('ISO 8601 datetime'),
        serialNumber: z.string().optional(),
        agentVersion: z.string().optional().describe('Monitor Agent version (exact match, e.g. "2.0.626")'),
        agentVersionPrefix: z.string().optional()
          .describe('Monitor Agent version prefix (e.g. "2.0." matches every 2.0.x build). Mutually exclusive with agentVersion.'),
        imeAgentVersion: z.string().optional().describe('IME Agent version (exact match, e.g. "1.23.456.789")'),
        imeAgentVersionPrefix: z.string().optional()
          .describe('IME Agent version prefix (e.g. "1.23."). Mutually exclusive with imeAgentVersion.'),
        manufacturer: z.string().optional().describe('Hardware manufacturer (exact match)'),
        model: z.string().optional().describe('Hardware model (exact match)'),
        enrollmentType: z.enum(['v1', 'v2']).optional().describe('Enrollment rail'),
        deviceName: z.string().optional().describe('Device name (prefix match)'),
        osBuild: z.string().optional().describe('OS build (prefix match, e.g. "26100")'),
        geoCountry: z.string().optional().describe('Geo country code (exact match, e.g. "DE")'),
        isPreProvisioned: z.boolean().optional().describe('Filter pre-provisioned (white-glove) sessions'),
        isHybridJoin: z.boolean().optional().describe('Filter hybrid AAD-join sessions'),
        fields: z.string().optional().describe('Comma-separated pass-through projection over the literal stored column names (case-insensitive, PascalCase, e.g. "Status,OsEdition,ImeAgentVersion,GeoCity"); narrows the row but never drops a real column. PartitionKey + RowKey are always kept. Omit for the full raw row.'),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(200)
          .describe('Page size (1-1000, default 200). Returns this many sessions per call; follow nextLink to fetch more.'),
        continuation: z.string().optional()
          .describe('Either the opaque "continuation" value from a prior response or the full nextLink path — both are accepted; the latter is preferred so backend-echoed query params round-trip correctly.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('query_raw_sessions', async () => {
      try {
        const { tenantId, status, startedAfter, startedBefore, serialNumber, agentVersion, agentVersionPrefix,
          imeAgentVersion, imeAgentVersionPrefix, manufacturer, model, enrollmentType, deviceName, osBuild,
          geoCountry, isPreProvisioned, isHybridJoin, fields, pageSize, continuation } = args;
        const basePath = pickGlobalOrTenantPath('/api/global/raw/sessions', '/api/raw/sessions');
        const path = followNextLink(
          basePath,
          { tenantId, status, startedAfter, startedBefore, serialNumber, agentVersion, agentVersionPrefix,
            imeAgentVersion, imeAgentVersionPrefix, manufacturer, model, enrollmentType, deviceName, osBuild,
            geoCountry, isPreProvisioned, isHybridJoin, fields, pageSize },
          continuation,
        );
        const data = await apiFetch(path);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.sessions);
      } catch (error: unknown) {
        return toolError('query_raw_sessions', args, error);
      }
    })
  );

  // ── Admin Diagnostic Tools ────────────────────────────────────────────
  // Security note: query_table and query_backend_logs accept arbitrary OData/KQL
  // expressions. This is by design — these are trusted-admin-only diagnostic tools
  // gated by GlobalAdmin RBAC on the backend. No client-side filter allowlist is
  // needed because the backend enforces the same permission boundary.

  // Tool 20: list_tables — Global Admin only; not registered for normal users.
  if (ga) server.registerTool(
    'list_tables',
    {
      title: 'List Tables',
      description: 'List all available Azure Table Storage tables that can be queried via query_table. Global Admin only.',
      inputSchema: {},
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('list_tables', async () => {
      try {
        const data = await apiFetch('/api/global/raw/tables');
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('list_tables', args, error);
      }
    })
  );

  // Tool 21: query_table — Global Admin only; not registered for normal users.
  if (ga) server.registerTool(
    'query_table',
    {
      title: 'Query Table',
      description:
        'Query any Azure Table Storage table directly with OData filters. Global Admin only. ' +
        'Use list_tables to see available tables. Useful for inspecting TenantConfiguration, RuleResults, or any raw data ' +
        'where no specialized tool exists. ' +
        'For COUNTING / AGGREGATION queries pass `fields=PartitionKey,RowKey,Status,AgentVersion` (or similar lean subset) ' +
        'to drop unneeded columns client-side — full TableEntity rows can be 1KB+ each and trip the response cap quickly. ' +
        'This endpoint is fully paginated — there is no truncation. Default pageSize=200; raise it (up to 1000) for ' +
        'full-table dumps. Pass the whole nextLink string as "continuation" so all backend-echoed query params round-trip ' +
        'correctly.',
      inputSchema: {
        tableName: z.string().describe('Table name (e.g. "Sessions", "Events", "RuleResults", "TenantConfiguration")'),
        partitionKey: z.string().optional().describe('Filter by exact partition key (usually TenantId)'),
        rowKeyPrefix: z.string().optional().describe('Filter by row key prefix'),
        filter: z.string().optional().describe('OData filter expression (e.g. "Status eq \'Failed\'")'),
        fields: z.string().optional()
          .describe('Comma-separated column names to keep (e.g. "PartitionKey,RowKey,Status"). Other columns are dropped ' +
                    'client-side after fetch. Useful for aggregation/counting on wide tables. Always includes PartitionKey ' +
                    'and RowKey for cursor stability.'),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(200)
          .describe('Page size (1-1000, default 200). Returns this many rows per call; follow nextLink to fetch more.'),
        continuation: z.string().optional()
          .describe('Either the opaque "continuation" value from a prior response or the full nextLink path — both are accepted; the latter is preferred so backend-echoed query params round-trip correctly.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('query_table', async () => {
      try {
        const { tableName, partitionKey, rowKeyPrefix, filter, fields, pageSize, continuation } = args;
        const basePath = `/api/global/raw/tables/${encodeURIComponent(tableName)}`;
        const path = followNextLink(
          basePath,
          { partitionKey, rowKeyPrefix, filter, pageSize },
          continuation,
        );
        const data = await apiFetch(path) as { table?: string; count?: number; entities?: Record<string, unknown>[]; nextLink?: string | null };

        // Client-side projection — TableEntity columns are dynamic so the backend
        // can't help. Always retain PartitionKey + RowKey (cursor stability +
        // identity) and add Timestamp by default since it's universally useful.
        if (fields && Array.isArray(data?.entities)) {
          const fieldSet = new Set(
            fields.split(',').map((f) => f.trim()).filter(Boolean).concat(['PartitionKey', 'RowKey']),
          );
          data.entities = data.entities.map((row) => {
            const projected: Record<string, unknown> = {};
            for (const key of Object.keys(row)) {
              if (fieldSet.has(key)) projected[key] = row[key];
            }
            return projected;
          });
        }
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.rawTable);
      } catch (error: unknown) {
        return toolError('query_table', args, error);
      }
    })
  );

  // Tool 22: query_backend_logs — Global Admin only; not registered for normal users.
  if (ga) server.registerTool(
    'query_backend_logs',
    {
      title: 'Query Backend Logs',
      description:
        'Query backend Application Insights logs using KQL. Global Admin only. ' +
        'Use for debugging backend issues, tracing requests by correlation ID, and platform diagnostics.',
      inputSchema: {
        query: z.string().describe('KQL query (e.g. "traces | where message contains \'error\' | take 50")'),
        timespan: z.string().optional().default('PT1H').describe('ISO 8601 duration (default: PT1H = last 1 hour). Examples: PT30M, PT6H, P1D'),
      },
      annotations: READ_ONLY_OPEN,
    },
    async (args) => withToolTelemetry('query_backend_logs', async () => {
      try {
        const data = await apiFetch('/api/global/raw/logs', {
          method: 'POST',
          body: JSON.stringify({ query: args.query, timespan: args.timespan }),
        });
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.events);
      } catch (error: unknown) {
        return toolError('query_backend_logs', args, error);
      }
    })
  );

  // Tool: get_rule_stats
  server.registerTool(
    'get_rule_stats',
    {
      title: 'Rule Statistics',
      description:
        'Get rule firing statistics for analyze and gather rules. Shows which rules fire most often, ' +
        'their hit rates (fires/evaluations), and daily trends. Use to identify commonly triggered rules, ' +
        'optimize rule definitions, or understand tenant-specific failure patterns. ' +
        (ga ? 'Without tenantId returns global stats (cross-tenant). With tenantId returns tenant-specific stats. ' : '') +
        'NOTE: default window is 30 days × every rule × per-day trend rows — for 20+ rules the response ' +
        'easily exceeds 70 KB and can trip the response cap. Pass a tighter `startDate`/`endDate` window ' +
        '(7 days is usually plenty) and/or `ruleType` filter to keep responses lean.',
      inputSchema: {
        tenantId: z.string().optional().describe(ga ? 'Filter by tenant ID. Omit for global (cross-tenant) stats.' : 'Optional tenant ID. Defaults to your tenant.'),
        ruleType: z.enum(['analyze', 'gather']).optional().describe('Filter by rule type'),
        startDate: z.string().optional().describe('Start date (YYYY-MM-DD). Defaults to 30 days ago.'),
        endDate: z.string().optional().describe('End date (YYYY-MM-DD). Defaults to today.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_rule_stats', async () => {
      try {
        const params: Record<string, string | undefined> = {
          startDate: args.startDate,
          endDate: args.endDate,
          ruleType: args.ruleType,
        };
        if (args.tenantId) params.tenantId = args.tenantId;
        const prefix = pickGlobalOrTenantPath('/api/global/metrics', '/api/metrics');
        const data = await apiFetch(`${prefix}/rule-stats${buildQuery(params)}`);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('get_rule_stats', args, error);
      }
    })
  );

  // Tool: get_vulnerability_summary
  server.registerTool(
    'get_vulnerability_summary',
    {
      title: 'Vulnerability Summary',
      description:
        'Get a fleet-wide vulnerability exposure summary aggregated from detected CVEs: total affected devices, ' +
        'distinct CVE count, KEV (CISA Known-Exploited) count, a severity breakdown, and the top CVEs ranked by how ' +
        'many devices they affect. ' +
        (ga ? 'Omit tenantId for a cross-tenant overview (Global Admin; also returns affected tenant count); pass tenantId to scope to one tenant. ' : '') +
        'Use this to answer "how exposed is the fleet / this ' +
        'tenant?" and "which CVEs affect the most devices?" — for the device list of a single CVE use search_sessions_by_cve. ' +
        'If "truncated" is true, the underlying index scan hit its cap and counts are a lower bound (narrow with tenantId). ' +
        'Requires vulnerability scanning to be enabled (an empty summary means no findings, not necessarily "not affected").',
      inputSchema: {
        tenantId: z.string().optional().describe(ga ? 'Tenant ID. Omit for cross-tenant overview (Global Admin only).' : 'Optional tenant ID. Defaults to your tenant.'),
        days: z.coerce.number().int().min(1).max(365).optional().default(30)
          .describe('Time window in days (1-365, default 30). Filters CVEs by when they were detected.'),
        topN: z.coerce.number().int().min(1).max(100).optional().default(20)
          .describe('How many top CVEs to return, ranked by affected device count (1-100, default 20).'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_vulnerability_summary', async () => {
      try {
        const { tenantId, days, topN } = args;
        const prefix = pickGlobalOrTenantPath('/api/global/metrics/vulnerability', '/api/metrics/vulnerability');
        const data = await apiFetch(`${prefix}${buildQuery({ tenantId, days, topN })}`);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('get_vulnerability_summary', args, error);
      }
    })
  );

  // Tool: get_resource — returns the same content as the MCP `resources` would,
  // but via a regular tool call. Workaround for clients (e.g. Claude Code's HTTP-MCP
  // bridge in stateless mode) that don't expose `resources/list` reliably.
  server.registerTool(
    'get_resource',
    {
      title: 'Get Catalog Resource',
      description:
        'Returns the contents of a named static catalog resource. Use this when ' +
        'the host MCP client cannot list/read MCP-protocol resources (common with ' +
        'stateless HTTP MCP servers). Available names:\n' +
        '  - "event_types": catalog of valid eventType strings for search_sessions_by_event\n' +
        '  - "device_properties": catalog of dot-notation keys for the deviceProperties filter on search_sessions',
      inputSchema: {
        name: z.enum(['event_types', 'device_properties']).describe('Resource name'),
      },
      annotations: READ_ONLY_OPEN,
    },
    async (args) => withToolTelemetry('get_resource', async () => {
      try {
        const data = getResourceContent(args.name);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('get_resource', args, error);
      }
    })
  );
}
