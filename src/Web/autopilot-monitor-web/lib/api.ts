/**
 * Typed API URL builder
 * All backend endpoint URLs are defined here for type-safety and maintainability.
 */
import { API_BASE_URL } from "@/utils/config";

function qs(params: Record<string, string | undefined>): string {
  const p = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    if (value !== undefined && value !== "") p.set(key, value);
  }
  const str = p.toString();
  return str ? `?${str}` : "";
}

export const api = {
  // ── Auth ──────────────────────────────────────────────────────────────────
  auth: {
    me: () => `${API_BASE_URL}/api/auth/me`,
  },

  // ── Sessions ──────────────────────────────────────────────────────────────
  sessions: {
    list: (
      tenantId?: string,
      days?: number,
      opts?: { pageSize?: number; continuation?: string },
    ) =>
      `${API_BASE_URL}/api/sessions${qs({
        tenantId,
        days: days?.toString(),
        pageSize: opts?.pageSize?.toString(),
        continuation: opts?.continuation,
      })}`,
    get: (sessionId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}${qs({ tenantId })}`,
    events: (
      sessionId: string,
      tenantId?: string,
      opts?: { pageSize?: number; continuation?: string },
    ) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/events${qs({
        tenantId,
        pageSize: opts?.pageSize?.toString(),
        continuation: opts?.continuation,
      })}`,
    delete: (sessionId: string, tenantId: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}${qs({ tenantId })}`,
    analysis: (sessionId: string, tenantId?: string, reanalyze?: boolean) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/analysis${qs({ tenantId, ...(reanalyze ? { reanalyze: "true" } : {}) })}`,
    vulnerabilityReport: (sessionId: string, tenantId?: string, rescan?: boolean) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/vulnerability-report${qs({ tenantId, ...(rescan ? { rescan: "true" } : {}) })}`,
    markFailed: (sessionId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/mark-failed${qs({ tenantId })}`,
    markSucceeded: (sessionId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/mark-succeeded${qs({ tenantId })}`,
    report: (sessionId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/report${qs({ tenantId })}`,
    quickSearch: (q: string) =>
      `${API_BASE_URL}/api/search/quick${qs({ q })}`,
    // NOTE: lives under /api/stats/sessions (NOT /api/sessions/stats) — Azure
    // Functions' router lets a literal "sessions/stats" be eaten by the sibling
    // "sessions/{sessionId}" function, which then 404s on "stats" not parsing
    // as a GUID. Symmetric path used for the GA variant in globalSessions.stats.
    stats: (opts?: { days?: number }) =>
      `${API_BASE_URL}/api/stats/sessions${qs({ days: opts?.days?.toString() })}`,
  },

  // ── Inspector v1 (global admin only — Plan §M6) ───────────────────────────
  inspector: {
    signals: (sessionId: string, opts?: { tenantId?: string; maxResults?: number }) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/signals${qs({ tenantId: opts?.tenantId, maxResults: opts?.maxResults?.toString() })}`,
    decisionGraph: (sessionId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/decision-graph${qs({ tenantId })}`,
    reducerVerification: (sessionId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/sessions/${sessionId}/reducer-verification${qs({ tenantId })}`,
  },

  // ── Global Sessions (global admin) ────────────────────────────────────────
  globalSessions: {
    list: (
      tenantId?: string,
      days?: number,
      opts?: { pageSize?: number; continuation?: string },
    ) =>
      `${API_BASE_URL}/api/global/sessions${qs({
        tenantId,
        days: days?.toString(),
        pageSize: opts?.pageSize?.toString(),
        continuation: opts?.continuation,
      })}`,
    stats: (opts?: { tenantId?: string; days?: number }) =>
      `${API_BASE_URL}/api/global/stats/sessions${qs({
        tenantId: opts?.tenantId,
        days: opts?.days?.toString(),
      })}`,
  },

  // ── Config ────────────────────────────────────────────────────────────────
  config: {
    all: () => `${API_BASE_URL}/api/config/all`,
    tenant: (tenantId: string) => `${API_BASE_URL}/api/config/${tenantId}`,
    featureFlags: (tenantId: string) => `${API_BASE_URL}/api/config/${tenantId}/feature-flags`,
    autopilotConsentUrl: (tenantId: string, redirectUri: string) =>
      `${API_BASE_URL}/api/config/${tenantId}/autopilot-device-validation/consent-url${qs({ redirectUri })}`,
    autopilotConsentStatus: (tenantId: string) =>
      `${API_BASE_URL}/api/config/${tenantId}/autopilot-device-validation/consent-status`,
    autopilotConsentFailure: (tenantId: string) =>
      `${API_BASE_URL}/api/config/${tenantId}/autopilot-device-validation/consent-failure`,
    autopilotConsentSuccess: (tenantId: string) =>
      `${API_BASE_URL}/api/config/${tenantId}/autopilot-device-validation/consent-success`,
    testNotification: (tenantId: string) =>
      `${API_BASE_URL}/api/config/${tenantId}/test-notification`,
    latestVersions: (opts?: { refresh?: boolean }) =>
      `${API_BASE_URL}/api/config/latest-versions${qs({ refresh: opts?.refresh ? "true" : undefined })}`,
  },

  // ── Global Config (global admin) ──────────────────────────────────────────
  globalConfig: {
    get: () => `${API_BASE_URL}/api/global/config`,
    tenant: (tenantId: string) => `${API_BASE_URL}/api/global/config/${tenantId}`,
  },

  // ── Tenants ───────────────────────────────────────────────────────────────
  tenants: {
    admins: (tenantId: string) =>
      `${API_BASE_URL}/api/tenants/${tenantId}/admins`,
    admin: (tenantId: string, adminUpn: string) =>
      `${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}`,
    adminAction: (tenantId: string, adminUpn: string, action: string) =>
      `${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}/${action}`,
    adminPermissions: (tenantId: string, adminUpn: string) =>
      `${API_BASE_URL}/api/tenants/${tenantId}/admins/${encodeURIComponent(adminUpn)}/permissions`,
    offboard: (tenantId: string) =>
      `${API_BASE_URL}/api/tenants/${tenantId}/offboard`,
  },

  // ── Devices ───────────────────────────────────────────────────────────────
  devices: {
    blocked: (tenantId: string) =>
      `${API_BASE_URL}/api/devices/blocked${qs({ tenantId })}`,
    block: () => `${API_BASE_URL}/api/devices/block`,
    unblock: (serialNumber: string, tenantId: string) =>
      `${API_BASE_URL}/api/devices/block/${encodeURIComponent(serialNumber)}${qs({ tenantId })}`,
    allBlocked: () => `${API_BASE_URL}/api/global/devices/blocked`,
  },

  // ── Versions ──────────────────────────────────────────────────────────────
  versions: {
    blocked: () => `${API_BASE_URL}/api/versions/blocked`,
    block: () => `${API_BASE_URL}/api/versions/block`,
    unblock: (pattern: string) =>
      `${API_BASE_URL}/api/versions/block/${encodeURIComponent(pattern)}`,
  },

  // ── Rules ─────────────────────────────────────────────────────────────────
  rules: {
    analyze: () => `${API_BASE_URL}/api/rules/analyze`,
    analyzeRule: (ruleId: string) =>
      `${API_BASE_URL}/api/rules/analyze/${encodeURIComponent(ruleId)}`,
    analyzeRuleFromTemplate: (ruleId: string) =>
      `${API_BASE_URL}/api/rules/analyze/${encodeURIComponent(ruleId)}/create-from-template`,
    globalAnalyze: (tenantId?: string) =>
      `${API_BASE_URL}/api/global/rules/analyze${qs({ tenantId })}`,
    gather: (tenantId?: string) =>
      `${API_BASE_URL}/api/rules/gather${qs({ tenantId })}`,
    gatherRule: (ruleId: string, tenantId?: string) =>
      `${API_BASE_URL}/api/rules/gather/${ruleId}${qs({ tenantId })}`,
    globalGather: (tenantId?: string) =>
      `${API_BASE_URL}/api/global/rules/gather${qs({ tenantId })}`,
    reseedFromGitHub: (type: "analyze" | "gather" | "ime" | "all") =>
      `${API_BASE_URL}/api/rules/reseed-from-github${qs({ type })}`,
    imeLogPatterns: () => `${API_BASE_URL}/api/rules/ime-log-patterns`,
    imeLogPattern: (patternId: string) =>
      `${API_BASE_URL}/api/rules/ime-log-patterns/${encodeURIComponent(patternId)}${qs({ global: "true" })}`,
  },

  // ── Metrics ───────────────────────────────────────────────────────────────
  metrics: {
    usage: (tenantId?: string) =>
      `${API_BASE_URL}/api/metrics/usage${qs({ tenantId })}`,
    globalUsage: (tenantId?: string) =>
      `${API_BASE_URL}/api/global/metrics/usage${qs({ tenantId })}`,
    app: (tenantId: string, days: number) =>
      `${API_BASE_URL}/api/metrics/app${qs({ tenantId, days: String(days) })}`,
    globalApp: (days: number, tenantId?: string) =>
      `${API_BASE_URL}/api/global/metrics/app${qs({ days: String(days), tenantId })}`,
    geographic: (tenantId: string, days: number, groupBy: string) =>
      `${API_BASE_URL}/api/metrics/geographic${qs({ tenantId, days: String(days), groupBy })}`,
    globalGeographic: (days: number, groupBy: string, tenantId?: string) =>
      `${API_BASE_URL}/api/global/metrics/geographic${qs({ days: String(days), groupBy, tenantId })}`,
    // ?full=1 — UI needs the full LocationSessionRow shape for the page table; default
    // is now lean (~15 fields/row) so MCP-driven callers fit many sessions in a single
    // response.
    geographicSessions: (tenantId: string, days: number, groupBy: string, locationKey: string) =>
      `${API_BASE_URL}/api/metrics/geographic/sessions${qs({ tenantId, days: String(days), groupBy, locationKey, full: '1' })}`,
    globalGeographicSessions: (days: number, groupBy: string, locationKey: string) =>
      `${API_BASE_URL}/api/global/metrics/geographic/sessions${qs({ days: String(days), groupBy, locationKey, full: '1' })}`,
    platform: (opts?: { limit?: number; days?: number }) =>
      `${API_BASE_URL}/api/global/metrics/platform${qs({
        limit: opts?.limit?.toString(),
        days: opts?.days?.toString(),
      })}`,
    ruleStats: (startDate?: string, endDate?: string, ruleType?: string) =>
      `${API_BASE_URL}/api/metrics/rule-stats${qs({ startDate, endDate, ruleType })}`,
    globalRuleStats: (startDate?: string, endDate?: string, ruleType?: string, tenantId?: string) =>
      `${API_BASE_URL}/api/global/metrics/rule-stats${qs({ startDate, endDate, ruleType, tenantId })}`,
    sla: (tenantId?: string, months?: number, fresh?: boolean) =>
      `${API_BASE_URL}/api/metrics/sla${qs({ tenantId, months: months?.toString(), fresh: fresh ? "1" : undefined })}`,
    globalSla: (tenantId: string, months?: number, fresh?: boolean) =>
      `${API_BASE_URL}/api/global/metrics/sla${qs({ tenantId, months: months?.toString(), fresh: fresh ? "1" : undefined })}`,
  },

  // ── Apps Dashboard ────────────────────────────────────────────────────────
  apps: {
    list: (tenantId: string, days: number) =>
      `${API_BASE_URL}/api/apps/list${qs({ tenantId, days: String(days) })}`,
    analytics: (tenantId: string, appName: string, days: number) =>
      `${API_BASE_URL}/api/apps/${encodeURIComponent(appName)}/analytics${qs({ tenantId, days: String(days) })}`,
    sessions: (
      tenantId: string,
      appName: string,
      days: number,
      status: "all" | "failed" | "succeeded" = "all",
      offset = 0,
      limit = 50,
      model?: string,
      version?: string
    ) =>
      `${API_BASE_URL}/api/apps/${encodeURIComponent(appName)}/sessions${qs({
        tenantId,
        days: String(days),
        status,
        offset: String(offset),
        limit: String(limit),
        model,
        version,
      })}`,

    // Global Admin variants — tenantId is optional:
    //   - undefined → aggregates across ALL tenants
    //   - provided → returns per-tenant view for any tenant (GA override)
    globalList: (days: number, tenantId?: string) =>
      `${API_BASE_URL}/api/global/apps/list${qs({ days: String(days), tenantId })}`,
    globalAnalytics: (appName: string, days: number, tenantId?: string) =>
      `${API_BASE_URL}/api/global/apps/${encodeURIComponent(appName)}/analytics${qs({ days: String(days), tenantId })}`,
    globalSessions: (
      appName: string,
      days: number,
      status: "all" | "failed" | "succeeded" = "all",
      offset = 0,
      limit = 50,
      tenantId?: string,
      model?: string,
      version?: string
    ) =>
      `${API_BASE_URL}/api/global/apps/${encodeURIComponent(appName)}/sessions${qs({
        days: String(days),
        status,
        offset: String(offset),
        limit: String(limit),
        tenantId,
        model,
        version,
      })}`,
  },

  // ── Diagnostics ───────────────────────────────────────────────────────────
  diagnostics: {
    downloadUrl: (tenantId: string, blobName: string) =>
      `${API_BASE_URL}/api/diagnostics/download-url${qs({ tenantId, blobName })}`,
  },

  // ── Progress ──────────────────────────────────────────────────────────────
  progress: {
    sessions: (tenantId: string) =>
      `${API_BASE_URL}/api/progress/sessions${qs({ tenantId })}`,
    sessionEvents: (sessionId: string, tenantId: string) =>
      `${API_BASE_URL}/api/progress/sessions/${sessionId}/events${qs({ tenantId })}`,
  },

  // ── Bootstrap ─────────────────────────────────────────────────────────────
  bootstrap: {
    sessions: (tenantId?: string) =>
      `${API_BASE_URL}/api/bootstrap/sessions${qs({ tenantId })}`,
    session: (code: string, tenantId: string) =>
      `${API_BASE_URL}/api/bootstrap/sessions/${code}${qs({ tenantId })}`,
  },

  // ── Reports ───────────────────────────────────────────────────────────────
  reports: {
    list: (opts?: { tenantId?: string; pageSize?: number; continuation?: string }) =>
      `${API_BASE_URL}/api/global/session-reports${qs({
        tenantId: opts?.tenantId,
        pageSize: opts?.pageSize?.toString(),
        continuation: opts?.continuation,
      })}`,
    downloadUrl: (blobName: string) =>
      `${API_BASE_URL}/api/global/session-reports/download-url${qs({ blobName })}`,
    note: (reportId: string) =>
      `${API_BASE_URL}/api/global/session-reports/${reportId}/note`,
  },

  // ── Diag Files Reports (no session context) ───────────────────────────────
  diagFilesReports: {
    submit: () => `${API_BASE_URL}/api/diag-files-reports`,
  },

  // ── Distress Reports ──────────────────────────────────────────────────────
  distressReports: {
    list: () => `${API_BASE_URL}/api/global/distress-reports`,
  },

  // ── Session Cascade Deletion (Global-Admin Session Cleanup page) ──────────
  sessionDeletions: {
    list: (state: "Preparing" | "Queued" | "Running" | "Poisoned", strandedSinceMinutes?: number) =>
      `${API_BASE_URL}/api/global/session-deletions${qs({
        state,
        strandedSinceMinutes: strandedSinceMinutes?.toString(),
      })}`,
    // Cross-tenant GA-only routes live under /api/global/* — /api/admin/* collides with the
    // Azure Functions runtime's mTLS-gated admin paths.
    //
    // Reads the persisted manifest + progress blob for an in-flight / poisoned cascade.
    // This is what the Session Cleanup page uses — NOT the dry-run preview below.
    storedManifest: (
      sessionId: string,
      tenantId: string,
      manifestId: string,
      mode: "summary" | "full" | "download" = "summary",
    ) =>
      `${API_BASE_URL}/api/global/sessions/${encodeURIComponent(sessionId)}/deletion-manifest${qs({
        tenantId,
        manifestId,
        mode,
      })}`,
    // Dry-run builder that enumerates current data — only meaningful BEFORE a cascade starts.
    // The Session Cleanup page should use storedManifest instead.
    preview: (sessionId: string, tenantId: string, mode: "summary" | "full" | "download" = "summary") =>
      `${API_BASE_URL}/api/global/sessions/${encodeURIComponent(sessionId)}/delete/preview${qs({ tenantId, mode })}`,
    restore: (sessionId: string) =>
      `${API_BASE_URL}/api/global/sessions/${encodeURIComponent(sessionId)}/restore`,
    // File-browser endpoint: enumerates every persisted manifest blob for one tenant, grouped
    // by sessionId, sorted by recency. Powers the Restore Browser tab so an operator can pick
    // a (session, manifest) without knowing the manifestId in advance.
    tenantManifests: (tenantId: string, sessionFilter?: string) =>
      `${API_BASE_URL}/api/global/tenants/${encodeURIComponent(tenantId)}/deletion-manifests${qs({ sessionId: sessionFilter })}`,

    // Cheap hierarchy-listing endpoint: returns the set of tenant IDs that currently have at
    // least one persisted snapshot blob. Powers the Restore Browser "only tenants with restore
    // data" checkbox so the dropdown can hide tenants that have nothing to restore.
    tenantsWithManifests: () =>
      `${API_BASE_URL}/api/global/tenants-with-deletion-manifests`,
  },

  // ── Hardware Rejection Insights (tenant-scoped, from distress data) ──────
  distress: {
    hardwareRejected: () => `${API_BASE_URL}/api/audit/hardware-rejected`,
  },

  // ── Ops Events ───────────────────────────────────────────────────────────
  opsEvents: {
    list: (
      category?: string,
      opts?: { dateFrom?: string; dateTo?: string; pageSize?: number; continuation?: string },
    ) =>
      `${API_BASE_URL}/api/global/ops-events${qs({
        category,
        dateFrom: opts?.dateFrom,
        dateTo: opts?.dateTo,
        pageSize: opts?.pageSize?.toString(),
        continuation: opts?.continuation,
      })}`,
  },

  // ── Notifications ─────────────────────────────────────────────────────────
  notifications: {
    list: () => `${API_BASE_URL}/api/global/notifications`,
    dismiss: (id: string) => `${API_BASE_URL}/api/global/notifications/${id}/dismiss`,
    dismissAll: () => `${API_BASE_URL}/api/global/notifications/dismiss-all`,
    // Tenant-scoped persistent notifications (bell). TenantId resolved server-side from JWT.
    tenantList: () => `${API_BASE_URL}/api/notifications`,
    tenantDismiss: (id: string) => `${API_BASE_URL}/api/notifications/${id}/dismiss`,
    tenantDismissAll: () => `${API_BASE_URL}/api/notifications/dismiss-all`,
  },

  // ── Audit ─────────────────────────────────────────────────────────────────
  audit: {
    logs: (opts?: { dateFrom?: string; dateTo?: string; pageSize?: number; continuation?: string }) =>
      `${API_BASE_URL}/api/audit/logs${qs({
        dateFrom: opts?.dateFrom,
        dateTo: opts?.dateTo,
        pageSize: opts?.pageSize?.toString(),
        continuation: opts?.continuation,
      })}`,
    globalLogs: (opts?: { dateFrom?: string; dateTo?: string; pageSize?: number; continuation?: string }) =>
      `${API_BASE_URL}/api/global/audit/logs${qs({
        dateFrom: opts?.dateFrom,
        dateTo: opts?.dateTo,
        pageSize: opts?.pageSize?.toString(),
        continuation: opts?.continuation,
      })}`,
  },

  // ── Customs Archive (PR3.B Tenant offboarding archive) ────────────────────
  // Response types defined below (CustomsArchive*).
  customsArchive: {
    listRuns: (opts?: { tenantId?: string }) =>
      `${API_BASE_URL}/api/global/customs-archive${qs({ tenantId: opts?.tenantId })}`,
    listEntries: (tenantId: string, historyRowKey: string) =>
      `${API_BASE_URL}/api/global/customs-archive/${encodeURIComponent(tenantId)}/${encodeURIComponent(historyRowKey)}`,
    getEntry: (tenantId: string, historyRowKey: string, archiveRowKey: string) =>
      `${API_BASE_URL}/api/global/customs-archive/${encodeURIComponent(tenantId)}/${encodeURIComponent(historyRowKey)}/${encodeURIComponent(archiveRowKey)}`,
    deleteEntry: (tenantId: string, historyRowKey: string, archiveRowKey: string) =>
      `${API_BASE_URL}/api/global/customs-archive/${encodeURIComponent(tenantId)}/${encodeURIComponent(historyRowKey)}/${encodeURIComponent(archiveRowKey)}`,
    deleteRun: (tenantId: string, historyRowKey: string) =>
      `${API_BASE_URL}/api/global/customs-archive/${encodeURIComponent(tenantId)}/${encodeURIComponent(historyRowKey)}`,
  },

  // ── Feedback ──────────────────────────────────────────────────────────────
  feedback: {
    status: () => `${API_BASE_URL}/api/feedback/status`,
    submit: () => `${API_BASE_URL}/api/feedback`,
    all: () => `${API_BASE_URL}/api/feedback/all`,
  },

  // ── Preview ───────────────────────────────────────────────────────────────
  preview: {
    whitelist: () => `${API_BASE_URL}/api/preview/whitelist`,
    whitelistTenant: (tenantId: string) =>
      `${API_BASE_URL}/api/preview/whitelist/${tenantId}`,
    sendWelcomeEmail: (tenantId: string) =>
      `${API_BASE_URL}/api/preview/send-welcome-email/${tenantId}`,
    notificationEmail: () => `${API_BASE_URL}/api/preview/notification-email`,
    notificationEmailTenant: (tenantId: string) =>
      `${API_BASE_URL}/api/preview/notification-email/${tenantId}`,
  },

  // ── Vulnerability ─────────────────────────────────────────────────────────
  vulnerability: {
    sync: () => `${API_BASE_URL}/api/vulnerability/sync`,
    syncMsrc: () => `${API_BASE_URL}/api/vulnerability/sync-msrc`,
    syncReseed: (type: string) =>
      `${API_BASE_URL}/api/vulnerability/sync${qs({ reseed: type })}`,
    cpeMappings: () => `${API_BASE_URL}/api/vulnerability/cpe-mappings`,
    cpeMapping: () => `${API_BASE_URL}/api/vulnerability/cpe-mapping`,
    cpeAutoResolve: () => `${API_BASE_URL}/api/vulnerability/cpe-mapping/auto-resolve`,
    unmatchedSoftware: (skip?: number, take?: number) =>
      `${API_BASE_URL}/api/vulnerability/unmatched-software${qs({
        skip: skip !== undefined ? String(skip) : undefined,
        take: take !== undefined ? String(take) : undefined,
      })}`,
    ignoredSoftware: () => `${API_BASE_URL}/api/vulnerability/ignored-software`,
  },

  // ── MCP Users ─────────────────────────────────────────────────────────────
  mcpUsers: {
    list: () => `${API_BASE_URL}/api/global/mcp-users`,
    add: () => `${API_BASE_URL}/api/global/mcp-users`,
    remove: (upn: string) =>
      `${API_BASE_URL}/api/global/mcp-users/${encodeURIComponent(upn)}`,
    enable: (upn: string) =>
      `${API_BASE_URL}/api/global/mcp-users/${encodeURIComponent(upn)}/enable`,
    disable: (upn: string) =>
      `${API_BASE_URL}/api/global/mcp-users/${encodeURIComponent(upn)}/disable`,
    check: () => `${API_BASE_URL}/api/global/mcp-users/check`,
    setUsagePlan: (upn: string) =>
      `${API_BASE_URL}/api/global/mcp-users/${encodeURIComponent(upn)}/usage-plan`,
  },

  // ── MCP Usage ──────────────────────────────────────────────────────────────
  mcpUsage: {
    me: (dateFrom?: string, dateTo?: string) =>
      `${API_BASE_URL}/api/metrics/mcp-usage/me${qs({ dateFrom, dateTo })}`,
    user: (userId: string, dateFrom?: string, dateTo?: string) =>
      `${API_BASE_URL}/api/metrics/mcp-usage/user/${encodeURIComponent(userId)}${qs({ dateFrom, dateTo })}`,
    global: (tenantId?: string, dateFrom?: string, dateTo?: string) =>
      `${API_BASE_URL}/api/global/metrics/mcp-usage${qs({ tenantId, dateFrom, dateTo })}`,
    daily: (tenantId?: string, dateFrom?: string, dateTo?: string) =>
      `${API_BASE_URL}/api/global/metrics/mcp-usage/daily${qs({ tenantId, dateFrom, dateTo })}`,
    planTiers: () => `${API_BASE_URL}/api/global/config/plan-tiers`,
  },

  // ── Maintenance ───────────────────────────────────────────────────────────
  maintenance: {
    trigger: (date?: string) =>
      `${API_BASE_URL}/api/maintenance/trigger${qs({ date })}`,
  },

  // ── Health ────────────────────────────────────────────────────────────────
  health: {
    detailed: () => `${API_BASE_URL}/api/health/detailed`,
  },

  // ── Realtime (SignalR) ────────────────────────────────────────────────────
  realtime: {
    hub: () => `${API_BASE_URL}/api/realtime`,
    joinGroup: () => `${API_BASE_URL}/api/realtime/groups/join`,
    leaveGroup: () => `${API_BASE_URL}/api/realtime/groups/leave`,
  },
};

// ── Customs Archive response types ────────────────────────────────────────
// Shared by /admin/customs-archive (list runs) and
// /admin/customs-archive/[tenantId]/[historyRowKey] (list/inspect entries).
export interface CustomsArchiveRunSummary {
  partitionKey: string;
  tenantId: string;
  historyRowKey: string;
  archivedAt: string;
  gatherRulesCount: number;
  analyzeRulesCount: number;
  imeLogPatternsCount: number;
}

export interface CustomsArchiveListRunsResponse {
  success: boolean;
  count: number;
  runs: CustomsArchiveRunSummary[];
}

export interface CustomsArchiveEntrySummary {
  partitionKey: string;
  rowKey: string;
  originalTable: string;
  originalRowKey: string;
  archivedAt: string;
  entityJsonPreview: string;
}

export interface CustomsArchiveListEntriesResponse {
  success: boolean;
  count: number;
  entries: CustomsArchiveEntrySummary[];
}

export interface CustomsArchiveFullEntry extends CustomsArchiveEntrySummary {
  tenantId: string;
  originalPartitionKey: string;
  entityJson: string;
  historyRowKey: string;
  archivedBy: string;
}
