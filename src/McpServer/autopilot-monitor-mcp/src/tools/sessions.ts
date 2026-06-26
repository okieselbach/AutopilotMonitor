import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { z } from 'zod';
import { apiFetch, ApiError, buildQuery, enforceDelegatedTenant, enforceDelegatedTenantForPage, followNextLink, pickGlobalOrTenantPath, scanUntilMatch } from '../client.js';
import { withToolTelemetry } from '../telemetry.js';
import { READ_ONLY, MAX_RESULT_SIZE_CHARS, toolResultText, SessionIdSchema, isBenignHealthDetectionReport, tenantIdDescription } from './shared.js';
import { toolError } from './error-handler.js';
import { assertKnownEventType, assertKnownDevicePropertyKeys } from '../resource-catalog.js';
import { interpolateAnalysisResults } from '../interpolate-rule-template.js';
import { API_BASE_URL } from '../config.js';
import { DIAG_ZIP_MAP } from '../diag-zip-map.js';

// ── Session summary constants ───────────────────────────────────────────

// Exported so a drift test (event-types-drift.test.ts) can assert every member
// is a real Constants.EventTypes value — a phantom here silently degrades
// get_session_summary (noise leaks in, real events never key-rank).
export const EXCLUDED_EVENT_TYPES = new Set([
  'performance_snapshot', 'agent_metrics_snapshot',
  'performance_collector_stopped', 'agent_metrics_collector_stopped',
  'gather_result', 'gather_rules_collection_completed',
  'software_inventory_analysis', 'security_audit',
  'device_location', 'ntp_time_check', 'ime_agent_version',
]);
export const KEY_EVENT_TYPES = new Set([
  'phase_transition', 'esp_phase_changed', 'enrollment_type_detected',
  'app_install_started', 'app_install_completed', 'app_install_failed', 'app_install_skipped',
  'app_tracking_summary', 'error_detected',
  'enrollment_complete', 'enrollment_failed', 'completion_check',
  'desktop_arrived', 'hello_policy_detected', 'waiting_for_hello', 'hello_completion_timeout',
  'agent_started', 'agent_shutdown', 'agent_shutting_down', 'agent_trace',
  'script_started', 'script_completed', 'script_failed', 'vulnerability_report',
]);
// Phase-defining events promoted to the top of the triage timeline. Module-level
// (not handler-local) so the same drift test can validate it.
export const PHASE_EVENT_TYPES = new Set([
  'phase_transition', 'esp_phase_changed', 'enrollment_type_detected',
  'enrollment_complete', 'enrollment_failed', 'desktop_arrived',
]);
// Permissive ISO-8601 guard: rejects unparseable junk (which the backend would
// silently treat as no filter) while still accepting the date-only and
// timezone-offset forms the backend honors.
const IsoDateString = z.string().refine(
  (s) => !Number.isNaN(Date.parse(s)),
  { message: 'Must be a parseable ISO 8601 date/datetime, e.g. "2024-01-15" or "2024-01-15T00:00:00Z".' },
);
const SEVERITY_RANK: Record<string, number> = { Trace: -1, Debug: 0, Info: 1, Warning: 2, Error: 3, Critical: 4 };
// Phase labels MUST mirror the backend EnrollmentPhase enum and the web's
// phaseConstants.ts (the product's source of truth). The only per-enrollment
// difference is phase 3: "Apps (Device)" on V1 vs "App Installation" on V2.
// -1 = Unknown (events without an explicit phase), 99 = Failed (terminal).
const V1_PHASE_NAMES: Record<number, string> = {
  [-1]: 'Unknown', 0: 'Start', 1: 'Device Preparation', 2: 'Device Setup', 3: 'Apps (Device)',
  4: 'Account Setup', 5: 'Apps (User)', 6: 'Finalizing Setup', 7: 'Complete', 99: 'Failed',
};
const V2_PHASE_NAMES: Record<number, string> = { ...V1_PHASE_NAMES, 3: 'App Installation' };
const phaseName = (phase: unknown, enrollmentType: unknown): string => {
  const map = enrollmentType === 'v2' ? V2_PHASE_NAMES : V1_PHASE_NAMES;
  const n = Number(phase);
  return map[n] ?? String(phase ?? 'Unknown');
};

// ── Registration ────────────────────────────────────────────────────────

export function registerSessionTools(server: McpServer, ga: boolean, delegated: boolean = false): void {
  // Tool 1: search_sessions
  server.registerTool(
    'search_sessions',
    {
      title: 'Search Sessions',
      description:
        'Search enrollment sessions' +
        (ga ? '. Omit tenantId for cross-tenant search (Global Admin), or specify tenantId for single-tenant' : ' in your tenant') + '. ' +
        'Basic properties (status, serial number, manufacturer, model, etc.) filter on the session index. ' +
        'Use deviceProperties for any device hardware/config filter — keys use "eventType.propertyName" notation. ' +
        'Consult the device_properties catalog (call get_resource(name="device_properties")) for available keys. ' +
        'Examples: {"tpm_status.specVersion": "2.0"}, {"hardware_spec.ramTotalGB": ">=8"}, {"secureboot_status.uefiSecureBootEnabled": "True"}. ' +
        'Array values are searched as substring match (e.g. disks containing "NVMe"). ' +
        'For COUNTING / AGGREGATION queries (e.g. "how many V2 enrollments?", "how many failed in last 7 days?") pass ' +
        '`fields=sessionId,status,agentVersion,startedAt` (or a similar lean subset): full SessionSummary objects are ~1.5KB ' +
        'each and can trip the response cap before pagination would normally deliver the answer. With projection a 100-session ' +
        'aggregate fits in <10KB. ' +
        'For VERSION sweeps use `agentVersionPrefix=2.0.` or `imeAgentVersionPrefix=1.23.` instead of one call per build — ' +
        'matches every patch in the line in a single response. ' +
        'deviceProperties key prefixes are validated against the event_types catalog — a typo is rejected with a clear ' +
        'error, not a silent empty result. For deviceProperties / serial / geo / time filters the tool auto-scans ' +
        'forward past empty pages, so a returned "count": 0 with no "nextLink" means truly no matches, while ' +
        '"moreToScan": true means the per-call scan budget was hit (pass nextLink as "continuation" to keep scanning). ' +
        'This endpoint is fully paginated — there is no truncation. Default pageSize=200 is tuned for interactive queries; ' +
        'raise it (up to 1000) for full sweeps. Pass the whole nextLink string as "continuation" so all backend-echoed ' +
        'query params round-trip correctly.',
      inputSchema: {
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated, 'Tenant ID. Omit for cross-tenant search (Global Admin only).', 'Optional tenant ID. Defaults to your tenant.')),
        status: z.enum(['InProgress', 'Pending', 'Stalled', 'Succeeded', 'Failed']).optional()
          .describe('Enrollment status filter. Pending = White Glove pre-provisioning done, awaiting user enrollment; ' +
                    'Stalled = no progress for a while (non-terminal, can heal back to InProgress).'),
        serialNumber: z.string().optional().describe('Device serial number (exact match)'),
        deviceName: z.string().optional().describe('Device name (prefix match, e.g. "DESKTOP-")'),
        manufacturer: z.string().optional().describe('Hardware manufacturer (e.g. "Microsoft", "Dell", "HP")'),
        model: z.string().optional().describe('Hardware model (e.g. "Surface Pro 9")'),
        osBuild: z.string().optional().describe('OS build number prefix (e.g. "26100")'),
        enrollmentType: z.enum(['v1', 'v2']).optional().describe('Autopilot enrollment type'),
        isPreProvisioned: z.boolean().optional().describe('Filter by White Glove / pre-provisioned enrollment'),
        isHybridJoin: z.boolean().optional().describe('Filter by Hybrid Azure AD Join'),
        geoCountry: z.string().optional().describe('Country of enrollment (2-letter ISO code, e.g. "DE", "US")'),
        startedAfter: IsoDateString.optional().describe('ISO 8601 datetime — only sessions started after this'),
        startedBefore: IsoDateString.optional().describe('ISO 8601 datetime — only sessions started before this'),
        agentVersion: z.string().optional().describe('Monitor Agent version (exact match, e.g. "2.0.626")'),
        agentVersionPrefix: z.string().optional()
          .describe('Monitor Agent version prefix (e.g. "2.0." matches every 2.0.x build). Mutually exclusive with agentVersion (exact wins).'),
        imeAgentVersion: z.string().optional().describe('IME Agent version (exact match, e.g. "1.23.456.789")'),
        imeAgentVersionPrefix: z.string().optional()
          .describe('IME Agent version prefix (e.g. "1.23." matches every 1.23.x build). Mutually exclusive with imeAgentVersion.'),
        rebootCountMin: z.coerce.number().int().min(0).optional()
          .describe('Minimum number of reboots observed during enrollment (>=). Use to find "machines with many reboots", ' +
                    'e.g. rebootCountMin=5. Only populated for v2 enrollments; sessions that predate the field are excluded.'),
        rebootCountMax: z.coerce.number().int().min(0).optional()
          .describe('Maximum number of reboots observed during enrollment (<=).'),
        fields: z.string().optional()
          .describe('Comma-separated lean projection (e.g. "sessionId,status,agentVersion,startedAt"). ' +
                    'Use for counting / aggregation to avoid the response cap. Available: sessionId, tenantId, status, ' +
                    'serialNumber, manufacturer, model, deviceName, osBuild, osName, startedAt, completedAt, ' +
                    'durationSeconds, currentPhase, failureReason, eventCount, enrollmentType, isPreProvisioned, ' +
                    'isUserDriven, isHybridJoin, agentVersion, imeAgentVersion, geoCountry, rebootCount.'),
        deviceProperties: z.record(z.string(), z.string()).optional().describe(
          'Dynamic device property filters. Keys use "eventType.propertyName" dot notation. ' +
          'See the device_properties catalog (call get_resource(name="device_properties")) for all available keys and types. ' +
          'Values: exact match by default. Prefix with >=, <=, >, < for numeric ranges (e.g. ">=8"). ' +
          'Trailing "*" is a prefix wildcard (e.g. {"hardware_spec.cpuArchitecture": "ARM*"} matches ARM + ARM64). ' +
          'Booleans: use "True" or "False". Arrays: substring match in any element.'
        ),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(200)
          .describe('Page size (1-1000, default 200). Returns this many sessions per call; follow nextLink to fetch more.'),
        continuation: z.string().optional()
          .describe('Either the opaque "continuation" value from a prior response or the full nextLink path — both are accepted; the latter is preferred so backend-echoed query params round-trip correctly.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('search_sessions', async () => {
      try {
        const { deviceProperties, tenantId: rawTenantId, pageSize, continuation, ...rest } = args;
        // Delegated (MSP): require a managed tenantId (no aggregate); a page-2 call carries it inside the
        // continuation nextLink. No-op for GA/Reader/tenant users.
        const tenantId = enforceDelegatedTenantForPage(rawTenantId, continuation);
        // GA → /api/global/search/sessions (tenantId is filter); Tenant-Admin → /api/search/sessions (JWT-bound).
        const basePath = pickGlobalOrTenantPath('/api/global/search/sessions', '/api/search/sessions');
        // followNextLink handles full nextLink paths verbatim. For first-page calls
        // we still need to layer in deviceProperties as `prop.<key>` query params,
        // which followNextLink doesn't know about — so build the param record and
        // delegate the URL assembly to it.
        const queryParams: Record<string, string | number | boolean | undefined | null> = { ...rest, tenantId, pageSize };
        if (deviceProperties) {
          // Reject typo'd key prefixes (e.g. "tmp_status.x") so a bad filter is a
          // clear error, not a silent count:0 indistinguishable from a real miss.
          assertKnownDevicePropertyKeys(Object.keys(deviceProperties));
          for (const [key, value] of Object.entries(deviceProperties)) {
            queryParams[`prop.${key}`] = value;
          }
        }
        const path = followNextLink(basePath, queryParams, continuation);
        // deviceProperties + scan-path filters (serial, geo, time, …) are post-filtered
        // in-memory by the backend, so a page can be empty yet still carry a nextLink.
        // Auto-exhaust forward so the model never sees a misleading empty-but-continuable page.
        const data = await scanUntilMatch(path, basePath);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.sessions);
      } catch (error: unknown) {
        return toolError('search_sessions', args, error);
      }
    })
  );

  // Tool 2: search_sessions_by_event
  server.registerTool(
    'search_sessions_by_event',
    {
      title: 'Search Sessions by Event',
      description:
        'Find sessions that contain a specific event type (e.g. app install failure, phase transitions, errors). ' +
        (ga ? 'Omit tenantId for cross-tenant search (Global Admin). ' : '') +
        'Check the event_types catalog (call get_resource(name="event_types")) for valid eventType values. ' +
        'Use this to answer: which devices had a failed Teams install, which sessions had an error in DeviceSetup phase. ' +
        'This endpoint is fully paginated — there is no truncation. The default pageSize=200 is tuned for typical ' +
        'interactive queries; raise it (up to 1000) for full sweeps. For broad analysis, use pageSize=1000 and follow ' +
        'nextLink repeatedly until absent. Pass the whole nextLink string as "continuation" so all backend-echoed query ' +
        'params round-trip correctly.',
      inputSchema: {
        eventType: z.string().describe('Event type string — see event_types catalog (call get_resource(name="event_types")) for valid values (e.g. "app_install_failed", "enrollment_failed")'),
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated, 'Tenant ID. Omit for cross-tenant search (Global Admin only).', 'Optional tenant ID. Defaults to your tenant.')),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(200)
          .describe('Page size (1-1000, default 200). Returns this many sessions per call; follow nextLink to fetch more.'),
        continuation: z.string().optional()
          .describe('Either the opaque "continuation" value from a prior response or the full nextLink path — both are accepted; the latter is preferred so backend-echoed query params round-trip correctly.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('search_sessions_by_event', async () => {
      try {
        const { eventType, tenantId: rawTenantId, pageSize, continuation } = args;
        const tenantId = enforceDelegatedTenantForPage(rawTenantId, continuation);
        // eventType is the sole filter and is applied server-side (EventTypeIndex OData),
        // so an empty page never carries a nextLink — no auto-exhaust needed. But validate
        // the type so a typo is a clear error rather than a silent empty result.
        assertKnownEventType(eventType);
        const basePath = pickGlobalOrTenantPath('/api/global/search/sessions-by-event', '/api/search/sessions-by-event');
        const path = followNextLink(
          basePath,
          { eventType, tenantId, pageSize },
          continuation,
        );
        const data = await apiFetch(path);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.indexSessions);
      } catch (error: unknown) {
        return toolError('search_sessions_by_event', args, error);
      }
    })
  );

  // Tool 3: get_session
  server.registerTool(
    'get_session',
    {
      title: 'Get Session',
      description: 'Get full details of a single enrollment session including all device metadata. Set includeAnalysis=true to also get AI rule analysis results explaining why the session failed and remediation suggestions.',
      inputSchema: {
        sessionId: SessionIdSchema.describe('Session UUID'),
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated, 'Tenant ID. If omitted, auto-resolved from the session (Global Admin can access any tenant).', 'Tenant ID. If omitted, auto-resolved from the session.')),
        includeAnalysis: z.boolean().optional().default(false).describe('Include rule analysis results (failure explanations and remediation steps)'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_session', async () => {
      try {
        const { sessionId, tenantId: rawTenantId, includeAnalysis } = args;
        // Delegated (MSP): the session lives in a managed tenant — require + validate it (the backend
        // rescue authorizes /api/sessions/{id}?tenantId=<managed>). No-op for GA/Reader/tenant users.
        const tenantId = enforceDelegatedTenant(rawTenantId);
        const q = buildQuery({ tenantId } as Record<string, string | undefined>);
        const sessionPromise = apiFetch(`/api/sessions/${sessionId}${q}`);
        if (!includeAnalysis) {
          return toolResultText({ session: await sessionPromise, analysis: null }, MAX_RESULT_SIZE_CHARS.small);
        }
        // Fetch session + analysis in parallel (was sequential). Substitute
        // {{token}} placeholders so the raw explanation/remediation text never
        // leaks literal {{reason}}/{{appName}}/…. Swallow ONLY 404 (analysis may
        // not exist yet) — a 403/500 is a real failure that must surface.
        const analysisPromise = apiFetch(`/api/sessions/${sessionId}/analysis${q}`)
          .then(interpolateAnalysisResults)
          .catch((err: unknown) => {
            if (err instanceof ApiError && err.status === 404) return null;
            throw err;
          });
        const [sessionData, analysisData] = await Promise.all([sessionPromise, analysisPromise]);
        return toolResultText({ session: sessionData, analysis: analysisData }, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('get_session', args, error);
      }
    })
  );

  // Tool 3b: get_session_diagnostics
  server.registerTool(
    'get_session_diagnostics',
    {
      title: 'Get Session Diagnostics (Agent Log ZIP)',
      description:
        'Returns a short-lived, ready-to-use download URL for the agent DIAGNOSTICS ZIP of a session ' +
        '(agent logs, DecisionCore journal/signals, IME logs, final-status.json), plus a "zipMap" ' +
        'describing the archive layout. This is the highest-value source for root-causing why an ' +
        'enrollment went wrong: correlate the on-device agent log against the backend Events table.\n\n' +
        'CLIENT REQUIREMENT: this needs a client that can download files and run local file/shell tools ' +
        '(e.g. Claude Code or another agentic client). A pure chat client (Claude Desktop, claude.ai web) ' +
        'has no local filesystem and CANNOT unzip the binary archive — there this tool only yields a ' +
        'download link a human could open manually, not an automated analysis.\n\n' +
        'HOW TO USE: download the ZIP from "downloadUrl" — NO auth header needed, it is a short-lived ' +
        'signed ticket (~10 min) — then unzip and analyze it LOCALLY. The backend never unzips or parses ' +
        'it; you process it on your side and enrich with get_session_events / query_raw_events / ' +
        'search_knowledge. Read files in zipMap priority order; AppWorkload*.log can be hundreds of MB → ' +
        'grep, never read whole.\n\n' +
        'If "available" is false there is no uploaded diagnostics package (upload mode may be Off or ' +
        'OnFailure on a successful session) — proceed with backend telemetry only. ' +
        'Tenant admins get their own tenant\'s diagnostics; ' +
        (ga ? 'Global Admins can pass tenantId to target any tenant.' : 'tenantId is optional and defaults to your tenant.'),
      inputSchema: {
        sessionId: SessionIdSchema.describe('Session UUID'),
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated, 'Tenant ID. If omitted, auto-resolved from the session (Global Admin can access any tenant).', 'Tenant ID. If omitted, auto-resolved from the session.')),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_session_diagnostics', async () => {
      try {
        const { sessionId, tenantId: rawTenantId } = args;
        const tenantId = enforceDelegatedTenant(rawTenantId);
        const q = buildQuery({ tenantId } as Record<string, string | undefined>);
        // The backend wraps the session in a { success, session } envelope (GetSessionFunction);
        // fall back to the raw object so both shapes work (mirrors get_session_summary).
        const sessionResp = await apiFetch(`/api/sessions/${sessionId}${q}`) as Record<string, unknown>;
        const session = (sessionResp.session ?? sessionResp) as Record<string, unknown>;

        const blobName = typeof session.diagnosticsBlobName === 'string' ? session.diagnosticsBlobName : '';
        // tenantId for the ticket: explicit arg → session's tenantId → (tenant user) JWT default.
        const resolvedTenantId = tenantId
          ?? (typeof session.tenantId === 'string' ? session.tenantId : undefined);

        if (!blobName) {
          return toolResultText({
            available: false,
            sessionId,
            reason:
              'No diagnostics package was uploaded for this session. This is often expected — the ' +
              'tenant\'s diagnostics upload mode may be Off, or OnFailure on a session that succeeded. ' +
              'Proceed with backend telemetry (get_session_summary / get_session_events).',
          }, MAX_RESULT_SIZE_CHARS.small);
        }

        // Mint a download ticket. MemberRead + cross-tenant scoping enforced backend-side;
        // ?tenantId= is the GA filter / tenant-user validation. blobName travels in the body.
        const ticketPath = `/api/diagnostics/download-ticket${buildQuery({ tenantId: resolvedTenantId } as Record<string, string | undefined>)}`;
        const ticket = await apiFetch(ticketPath, {
          method: 'POST',
          body: JSON.stringify({ blobName }),
        }) as { url?: string; expiresAt?: string; blobName?: string; destination?: string; sizeBytes?: number | null };

        if (!ticket?.url) {
          return toolError('get_session_diagnostics', args,
            new Error('Backend did not return a download URL for the diagnostics ticket.'));
        }

        const downloadUrl = ticket.url.startsWith('http') ? ticket.url : `${API_BASE_URL}${ticket.url}`;

        return toolResultText({
          available: true,
          sessionId,
          tenantId: resolvedTenantId,
          blobName: ticket.blobName ?? blobName,
          destination: ticket.destination,
          sizeBytes: ticket.sizeBytes ?? null,
          downloadUrl,
          expiresAt: ticket.expiresAt,
          instructions:
            'Download the ZIP from downloadUrl with NO auth header (it carries a short-lived signed ' +
            'ticket). Unzip it locally and analyze on your side — the backend does not parse it. ' +
            'Read files per zipMap.files priority; AppWorkload*.log can be huge → grep only. Then ' +
            'correlate the agent log timeline against backend events (get_session_events / ' +
            'query_raw_events) and look up rules/patterns via search_knowledge. The download URL ' +
            'expires at expiresAt — re-call this tool for a fresh one if needed.',
          zipMap: DIAG_ZIP_MAP,
        }, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('get_session_diagnostics', args, error);
      }
    })
  );

  // Tool 4: get_session_events
  server.registerTool(
    'get_session_events',
    {
      title: 'Get Session Events',
      description:
        'RAW EVENT RETRIEVAL (fallback when ranked search misses). ' +
        'Returns up to pageSize events from a single session. Filter by eventType, severity, or source (app name). ' +
        'Use this when search_events returns incomplete results and you need the full unfiltered event stream, ' +
        'or for root cause analysis when you need every event in chronological sequence. ' +
        'If you omit tenantId, the backend auto-resolves it from the session' + (ga ? ' (Global Admin can access any tenant)' : '') + '. ' +
        'eventType is validated against the event_types catalog — a typo is rejected with a clear error, not a silent ' +
        'empty result. When you filter, the tool auto-scans forward past empty pages, so a returned "count": 0 is ' +
        'meaningful: with no "nextLink" it means truly no matching events; with "moreToScan": true it means the ' +
        'per-call scan budget was hit before a match (pass nextLink as "continuation" to keep scanning). ' +
        'Pagination: if the response includes "nextLink", more events are available — call this tool again and pass the ' +
        'whole nextLink string (e.g. "/api/sessions/{id}/events?pageSize=...&continuation=...&tenantId=...") as ' +
        '"continuation". The tool follows it verbatim so query params the backend echoes (tenantId, ' +
        'filters, etc.) round-trip correctly. Stop when the response no longer contains a nextLink. Sessions with ' +
        'thousands of events are fully reachable across multiple calls.',
      inputSchema: {
        sessionId: SessionIdSchema.describe('Session UUID'),
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated, 'Tenant ID. If omitted, auto-resolved from the session.', 'Tenant ID. If omitted, auto-resolved from the session.')),
        eventType: z.string().optional().describe('Filter to only events of this type'),
        severity: z.enum(['Info', 'Warning', 'Error', 'Critical']).optional(),
        source: z.string().optional().describe('Filter by event source/app name (e.g. "MicrosoftTeams")'),
        fields: z.string().optional()
          .describe('Comma-separated lean projection (e.g. "eventType,severity,timestamp,message"). Drops the heavy "data" payload unless "data" is listed. Valid keys: eventId, sessionId, tenantId, eventType, severity, source, phase, phaseName, timestamp, receivedAt, message, sequence, rowKey, originalTimestamp, timestampClamped, causedByTransitionStepIndex, causedBySignalOrdinal, data.'),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(200)
          .describe('Page size (1-1000, default 200). The endpoint returns this many events per call; follow nextLink to fetch more.'),
        continuation: z.string().optional()
          .describe('Either the opaque "continuation" value from a prior response or the full nextLink path — both are accepted; the latter is preferred so query params the backend echoes round-trip correctly.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_session_events', async () => {
      try {
        const { sessionId, tenantId: rawTenantId, pageSize, continuation, eventType, severity, source, fields } = args;
        const tenantId = enforceDelegatedTenantForPage(rawTenantId, continuation);
        if (eventType) assertKnownEventType(eventType);
        const basePath = `/api/sessions/${sessionId}/events`;
        const path = followNextLink(
          basePath,
          { tenantId, pageSize, eventType, severity, source, fields },
          continuation,
        );
        // eventType/severity/source are post-filtered in-memory over the session's
        // event partition, so a page can be empty while matches sit on a later page.
        // Auto-exhaust forward so the model isn't misled by an empty-but-continuable page.
        const data = await scanUntilMatch(path, basePath);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.events);
      } catch (error: unknown) {
        return toolError('get_session_events', args, error);
      }
    })
  );

  // Tool 5: get_session_summary
  server.registerTool(
    'get_session_summary',
    {
      title: 'Get Session Summary',
      description:
        'Get a concise, structured summary of an enrollment session optimized for analysis. ' +
        'Returns: session overview (status, duration, device, enrollment config), ' +
        'key events timeline (errors, warnings, phase transitions, app installs — noise filtered out, ' +
        'capped at 50 most-relevant entries; stats.keyEventsTruncated indicates if more were dropped), ' +
        'rule analysis results (probable cause, remediation), and aggregate stats. Heavy event payloads ' +
        '(data JSON) are NOT included — pull them via get_session_events for the same sessionId when needed. ' +
        'Use this as the first tool when investigating a session. ' +
        'For raw unfiltered events use get_session_events. For full metadata use get_session.',
      inputSchema: {
        sessionId: SessionIdSchema.describe('Session UUID'),
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated, 'Tenant ID. If omitted, auto-resolved from the session (Global Admin can access any tenant).', 'Tenant ID. If omitted, auto-resolved from the session.')),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_session_summary', async () => {
      try {
        const { sessionId, tenantId: rawTenantId } = args;
        const tenantId = enforceDelegatedTenant(rawTenantId);
        const q = buildQuery({ tenantId } as Record<string, string | undefined>);
        const fetchOpts = { signal: AbortSignal.timeout(90_000) };

        const [sessionData, eventsData, analysisData] = await Promise.all([
          apiFetch(`/api/sessions/${sessionId}${q}`, fetchOpts) as Promise<Record<string, unknown>>,
          apiFetch(`/api/sessions/${sessionId}/events${q}`, fetchOpts) as Promise<{ events?: Array<Record<string, unknown>>; count?: number }>,
          apiFetch(`/api/sessions/${sessionId}/analysis${q}`, fetchOpts).catch(() => null) as Promise<Record<string, unknown> | null>,
        ]);

        const s = (sessionData.session ?? sessionData) as Record<string, unknown>;

        const overview = {
          sessionId,
          tenantId: s.tenantId ?? tenantId,
          status: s.status,
          failureReason: s.failureReason ?? null,
          startedAt: s.startedAt,
          completedAt: s.completedAt ?? null,
          durationSeconds: s.durationSeconds ?? null,
          currentPhase: phaseName(s.currentPhase, s.enrollmentType),
          enrollmentType: s.enrollmentType,
          isPreProvisioned: s.isPreProvisioned ?? false,
          isHybridJoin: s.isHybridJoin ?? false,
          isUserDriven: s.isUserDriven ?? false,
          device: {
            name: s.deviceName,
            serialNumber: s.serialNumber,
            manufacturer: s.manufacturer,
            model: s.model,
            osBuild: s.osBuild,
            osEdition: s.osEdition,
          },
          agent: {
            version: s.agentVersion,
            imeVersion: s.imeAgentVersion,
          },
          location: (s.geoCountry || s.geoRegion || s.geoCity)
            ? { country: s.geoCountry, region: s.geoRegion, city: s.geoCity }
            : null,
        };

        const allEvents = (eventsData?.events ?? []) as Array<Record<string, unknown>>;

        let errorCount = 0;
        let warningCount = 0;
        let appTotal = 0;
        let appSucceeded = 0;
        let appFailed = 0;
        let appSkipped = 0;
        for (const e of allEvents) {
          const sev = String(e.severity ?? '');
          // A compliant health-script detection mis-stamped script_failed/Error is benign —
          // exclude it so a green session's errorCount isn't inflated by routine compliance reports.
          const benign = isBenignHealthDetectionReport(
            String(e.eventType ?? ''), e.data as Record<string, unknown> | undefined);
          if (!benign && (sev === 'Error' || sev === 'Critical')) errorCount++;
          if (sev === 'Warning') warningCount++;
          const et = String(e.eventType ?? '');
          if (et === 'app_install_started') appTotal++;
          if (et === 'app_install_completed') appSucceeded++;
          if (et === 'app_install_failed') appFailed++;
          if (et === 'app_install_skipped') appSkipped++;
        }

        // Triage timeline: keep noise-free events, sort by relevance (errors >
        // phase transitions > warnings > others, then chronological), cap at 50
        // entries. Heavy `details` payloads are dropped by default — they were the
        // root cause of the previous 80 KB+ responses; callers needing full payloads
        // pull them via get_session_events with the same sessionId.
        const KEY_EVENTS_CAP = 50;
        const allKey = allEvents.filter((e) => {
          const et = String(e.eventType ?? '');
          if (EXCLUDED_EVENT_TYPES.has(et)) return false;
          if (KEY_EVENT_TYPES.has(et)) return true;
          return (SEVERITY_RANK[String(e.severity ?? '')] ?? -1) >= 2;
        });

        const relevanceScore = (e: Record<string, unknown>): number => {
          // Benign compliant detection mis-stamped Error → rank as info-level, not top.
          if (isBenignHealthDetectionReport(
            String(e.eventType ?? ''), e.data as Record<string, unknown> | undefined)) return 10;
          const sev = SEVERITY_RANK[String(e.severity ?? '')] ?? -1;
          if (sev >= 3) return 100;                        // Error/Critical
          if (PHASE_EVENT_TYPES.has(String(e.eventType ?? ''))) return 60;
          if (sev === 2) return 30;                        // Warning
          return 10;                                       // info-level key event
        };

        const sortedKey = [...allKey].sort((a, b) => {
          const r = relevanceScore(b) - relevanceScore(a);
          if (r !== 0) return r;
          return String(a.timestamp ?? '').localeCompare(String(b.timestamp ?? ''));
        });

        const truncated = sortedKey.length > KEY_EVENTS_CAP;
        const cappedKey = truncated ? sortedKey.slice(0, KEY_EVENTS_CAP) : sortedKey;

        // Re-sort the displayed slice chronologically — easier to read as a timeline.
        cappedKey.sort((a, b) => String(a.timestamp ?? '').localeCompare(String(b.timestamp ?? '')));

        const mappedEvents = cappedKey.map((e) => ({
          timestamp: e.timestamp,
          eventType: e.eventType,
          severity: e.severity,
          phase: phaseName(e.phase, s.enrollmentType),
          message: e.message,
          source: e.source,
        }));

        let analysis = null;
        if (analysisData) {
          // Resolve {{token}} placeholders from each result's matchedConditions
          // before mapping, so issues carry readable text not raw {{...}} tokens.
          interpolateAnalysisResults(analysisData);
          const a = analysisData as Record<string, unknown>;
          const results = (a.results ?? []) as Array<Record<string, unknown>>;
          analysis = {
            totalIssues: a.totalIssues ?? results.length,
            criticalCount: a.criticalCount ?? 0,
            highCount: a.highCount ?? 0,
            warningCount: a.warningCount ?? 0,
            issues: results.map((r) => ({
              ruleTitle: r.ruleTitle ?? r.title,
              severity: r.severity,
              explanation: r.explanation,
              remediation: r.remediation,
            })),
          };
        }

        const result = {
          overview,
          keyEvents: mappedEvents,
          analysis,
          stats: {
            totalEvents: allEvents.length,
            keyEventsTotal: sortedKey.length,
            keyEventsShown: mappedEvents.length,
            keyEventsTruncated: truncated,
            errorCount,
            warningCount,
            // `started` events can under-count vs terminal ones (dependencies and
            // retries emit completed/failed without a matching "started"), which
            // previously made `total` smaller than `succeeded`. Derive `total` as
            // the max of the start signal and the sum of terminal outcomes so it is
            // never smaller than its own breakdown; expose `started` for context.
            appInstalls: {
              total: Math.max(appTotal, appSucceeded + appFailed + appSkipped),
              started: appTotal,
              succeeded: appSucceeded,
              failed: appFailed,
              skipped: appSkipped,
            },
          },
        };

        return toolResultText(result, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('get_session_summary', args, error);
      }
    })
  );

  // Tool 6: get_metrics
  server.registerTool(
    'get_metrics',
    {
      title: 'Enrollment Metrics',
      description:
        'Get aggregated enrollment metrics: failure rates, slowest/most-failing apps, session counts. ' +
        (ga ? 'Omit tenantId for cross-tenant platform overview (Global Admin). Specify tenantId for single-tenant metrics. ' : '') +
        'days accepts any value 1-365 (e.g. 5, 7, 12, 30, 90).',
      inputSchema: {
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated, 'Tenant ID. Omit for cross-tenant overview (Global Admin only).', 'Optional tenant ID. Defaults to your tenant.')),
        days: z.coerce.number().int().min(1).max(365).optional().default(30)
          .describe('Time window in days (1-365). Defaults to 30. Applied to both summary and app metrics.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_metrics', async () => {
      try {
        const { tenantId: rawTenantId, ...rest } = args;
        const tenantId = enforceDelegatedTenant(rawTenantId);
        const params: Record<string, string | number | undefined> = { ...rest };
        if (tenantId) params.tenantId = tenantId;
        const q = buildQuery(params);
        const prefix = pickGlobalOrTenantPath('/api/global/metrics', '/api/metrics');
        const [summaryRes, appsRes] = await Promise.allSettled([
          apiFetch(`${prefix}/summary${q}`),
          apiFetch(`${prefix}/app${q}`),
        ]);
        // Swallowing both failures as {summary:null, apps:null} reports a 403/500/
        // timeout as success. Only tolerate a partial failure (one endpoint down);
        // when BOTH fail, surface the error so the caller doesn't read it as "0".
        if (summaryRes.status === 'rejected' && appsRes.status === 'rejected') {
          throw summaryRes.reason;
        }
        const summary = summaryRes.status === 'fulfilled' ? summaryRes.value : null;
        const apps = appsRes.status === 'fulfilled' ? appsRes.value : null;
        const partialErrors: Record<string, string> = {};
        if (summaryRes.status === 'rejected') {
          partialErrors.summary = summaryRes.reason instanceof Error ? summaryRes.reason.message : String(summaryRes.reason);
        }
        if (appsRes.status === 'rejected') {
          partialErrors.apps = appsRes.reason instanceof Error ? appsRes.reason.message : String(appsRes.reason);
        }
        return toolResultText(
          Object.keys(partialErrors).length ? { summary, apps, partialErrors } : { summary, apps },
          MAX_RESULT_SIZE_CHARS.small,
        );
      } catch (error: unknown) {
        return toolError('get_metrics', args, error);
      }
    })
  );

  // Tool 7: search_sessions_by_cve
  server.registerTool(
    'search_sessions_by_cve',
    {
      title: 'Search Sessions by CVE',
      description:
        "Find enrollment sessions where a specific CVE was detected in the device's software inventory. " +
        (ga ? "Omit tenantId for cross-tenant search (Global Admin). " : "") +
        "Requires vulnerability scanning to be enabled. " +
        "Use this to answer: which devices are affected by CVE-2024-XXXX, show all critical vulnerability sessions. " +
        "This endpoint is fully paginated — there is no truncation. The default pageSize=200 is tuned for typical " +
        "interactive queries; raise it (up to 1000) for full exposure audits. For \"how many of my devices have CVE-X\" " +
        "use pageSize=1000 and follow nextLink repeatedly until absent. Pass the whole nextLink string as " +
        "\"continuation\" so all backend-echoed query params (cveId, minCvssScore, overallRisk) round-trip correctly.",
      inputSchema: {
        cveId: z.string()
          .regex(/^CVE-\d{4}-\d{4,}$/i, 'Must be a CVE identifier like "CVE-2024-21447" (CVE-YYYY-NNNN+).')
          .describe('CVE identifier (e.g. "CVE-2024-21447"). Validated — a non-CVE string is rejected, not silently empty.'),
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated, 'Tenant ID. Omit for cross-tenant search (Global Admin only).', 'Optional tenant ID. Defaults to your tenant.')),
        minCvssScore: z.coerce.number().min(0).max(10).optional().describe('Minimum CVSS score filter (e.g. 7.0 for high+critical)'),
        overallRisk: z.enum(['low', 'medium', 'high', 'critical']).optional(),
        pageSize: z.coerce.number().int().min(1).max(1000).optional().default(200)
          .describe('Page size (1-1000, default 200). Returns this many affected sessions per call; follow nextLink to fetch more.'),
        continuation: z.string().optional()
          .describe('Either the opaque "continuation" value from a prior response or the full nextLink path — both are accepted; the latter is preferred so backend-echoed query params round-trip correctly.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('search_sessions_by_cve', async () => {
      try {
        const { cveId, tenantId: rawTenantId, minCvssScore, overallRisk, pageSize, continuation } = args;
        const tenantId = enforceDelegatedTenantForPage(rawTenantId, continuation);
        // Normalize to canonical upper-case form (schema accepts case-insensitive).
        const normalizedCve = cveId.toUpperCase();
        const basePath = pickGlobalOrTenantPath('/api/global/search/sessions-by-cve', '/api/search/sessions-by-cve');
        const path = followNextLink(
          basePath,
          { cveId: normalizedCve, tenantId, minCvssScore, overallRisk, pageSize },
          continuation,
        );
        const data = await apiFetch(path);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.indexSessions);
      } catch (error: unknown) {
        return toolError('search_sessions_by_cve', args, error);
      }
    })
  );

  // Tool 8: list_blocked_devices — Global Admin only. Registered only for a GA,
  // so a normal tenant user never sees it in tools/list (no name, no hint).
  if (ga) {
  server.registerTool(
    'list_blocked_devices',
    {
      title: 'List Blocked Devices',
      description:
        'List devices currently blocked from enrolling. Blocked devices have their enrollment sessions rejected by the backend. ' +
        'Global Admin only — both the tenant-scoped (?tenantId=) and cross-tenant variants of this endpoint require Global Admin. ' +
        'Tenant Admins and Operators receive 403 (the backend manages the device block list as a platform-wide concern).',
      inputSchema: {
        tenantId: z.string().optional().describe('Tenant ID to scope results. Optional — both forms require Global Admin.'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('list_blocked_devices', async () => {
      try {
        const { tenantId } = args;
        // GA: /api/global/devices/blocked (tenantId is filter); non-GA: /api/devices/blocked
        // (backend will 403 — list is platform-wide and GA-only by policy).
        const basePath = pickGlobalOrTenantPath('/api/global/devices/blocked', '/api/devices/blocked');
        const endpoint = `${basePath}${buildQuery({ tenantId } as Record<string, string | undefined>)}`;
        const data = await apiFetch(endpoint);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.adminStream);
      } catch (error: unknown) {
        return toolError('list_blocked_devices', args, error);
      }
    })
  );
  } // end if (ga) — list_blocked_devices

  // Tool: get_ime_version_history — a global (non-tenant) archive available to all tenant members.
  // Hidden for a delegated (MSP) caller: their surface is the tenant-boundable managed-tenant subset,
  // and a platform-wide archive with no tenantId to bound is outside that contract (§2.2). A platform
  // GA/Reader (delegated=false here) and ordinary tenant users still see it.
  if (!delegated) server.registerTool(
    'get_ime_version_history',
    {
      title: 'IME Version History',
      description:
        'Get the history of all IME (Intune Management Extension) agent versions seen across enrollments. ' +
        'Shows when each version was first and last seen, and how many sessions reported it. ' +
        'This is a permanent archive that survives data retention — useful for tracking Microsoft IME release rollouts over time. ' +
        'Available to all tenant members (no tenantId needed, data is global).',
      inputSchema: {},
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_ime_version_history', async () => {
      try {
        const data = await apiFetch('/api/metrics/ime-versions');
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('get_ime_version_history', args, error);
      }
    })
  );
}
