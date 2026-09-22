import { McpServer } from '@modelcontextprotocol/server';
import { z } from 'zod';
import { ApiError, apiFetch, buildQuery, jsonBody, DEFAULT_FIRST_PAGE_SIZE, effectivePageSize, enforceDelegatedTenant, enforceDelegatedTenantForPage, followNextLink, pageSizeForCall, pickGlobalOrTenantPath, scanUntilMatch, scanWithTimeoutFallback } from '../client.js';
import { withToolTelemetry } from '../telemetry.js';
import { READ_ONLY, MAX_RESULT_SIZE_CHARS, LEAN_EVENT_FIELDS, LEAN_EVENT_OMISSION, leanFieldSelection, SUMMARY_EVENT_FIELDS, toolResultText, SessionIdSchema, isBenignHealthDetectionReport, tenantIdDescription, pageSizeDescription, CONTINUATION_DESCRIPTION, daysDescription } from './shared.js';
import { toolError } from './error-handler.js';
import { lookupErrorCode } from '../error-code-catalog.js';
import { assertKnownEventType, assertKnownDevicePropertyKeys } from '../resource-catalog.js';
import { interpolateAnalysisResults } from '../interpolate-rule-template.js';
import { buildSessionCoverage } from '../session-coverage.js';
import { API_BASE_URL } from '../config.js';
import type {
  AppMetricsResponse,
  BlockedDeviceListResponse,
  DiagnosticsDownloadTicketResponse,
  DownloadTicketRequest,
  EnrollmentEvent,
  GetRuleResultsResponse,
  GetSessionAnnotationsResponse,
  GetSessionEventsResponse,
  GetSessionResponse,
  ImePatternHealthResponse,
  ImeVersionHistoryEntry,
  ImeVersionHistoryLeanEntry,
  MetricsSummaryResponse,
  SessionListResponse,
} from '../generated/wire-types.generated.js';
// Generated vocabularies (values, not just types) — see wire-vocabularies.generated.ts.
import { EVENT_SEVERITIES, SESSION_STATUSES } from '../generated/wire-vocabularies.generated.js';

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
  'script_started', 'script_completed', 'script_failed', 'historic_ime_replay_detected',
  'vulnerability_report', 'system_sleep_episode',
  // Info-level counterpart of entra_user_affinity_pending (Warning — admitted by severity):
  // one line per agent run that disproves the hybrid affinity diagnosis, so it must key-rank.
  'ime_user_token_acquired',
]);
// Phase-defining events promoted to the top of the triage timeline. Module-level
// (not handler-local) so the same drift test can validate it.
export const PHASE_EVENT_TYPES = new Set([
  'phase_transition', 'esp_phase_changed', 'enrollment_type_detected',
  'enrollment_complete', 'enrollment_failed', 'desktop_arrived',
]);

// Mirrors the agent's 24 h source-timestamp staleness clamp (session eaf3d8c4).
const HISTORIC_REPLAY_THRESHOLD_MS = 24 * 60 * 60 * 1000;

/**
 * True when an event is replayed history from a previous enrollment: legacy agents replay
 * IME log content surviving a re-enrollment, stamping the rejected source-line time as
 * `data.rejectedSourceTimestamp` — more than 24 h older than the event stamp means the
 * activity happened during a previous enrollment. Newer agents suppress these at the
 * source; this filter covers sessions recorded by older agents. A rejected timestamp in
 * the FUTURE (clock jump) is genuine current activity and passes through. Exported so the
 * unit tests can pin the predicate.
 */
export function isHistoricImeReplay(e: Record<string, unknown>): boolean {
  const data = e.data as Record<string, unknown> | undefined;
  const rejected = data?.rejectedSourceTimestamp ?? data?.rejected_source_timestamp;
  if (typeof rejected !== 'string' || rejected.length === 0) return false;
  const rej = Date.parse(rejected);
  const ts = Date.parse(String(e.timestamp ?? ''));
  return Number.isFinite(rej) && Number.isFinite(ts) && ts - rej > HISTORIC_REPLAY_THRESHOLD_MS;
}
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
  // Session-bound reads (get_session, events, summary, diagnostics) resolve the tenant from the
  // session itself; the standard scope / default-tenant wording would be wrong for them.
  const SESSION_TENANT_TEXT = 'Optional; resolved from the session when omitted.';
  const sessionTenantIdDescription = tenantIdDescription(ga, delegated, SESSION_TENANT_TEXT, SESSION_TENANT_TEXT);

  // Tool 1: search_sessions
  server.registerTool(
    'search_sessions',
    {
      title: 'Search Sessions',
      description:
        'Search enrollment sessions. Basic properties (status, serial number, manufacturer, model, versions, dates, ...) ' +
        'filter on the session index; deviceProperties filters on device hardware/config with "eventType.propertyName" keys ' +
        '(syntax and catalog on that argument). ' +
        'Counting / aggregation: a full SessionSummary is ~1.5 KB, so pass a lean fields= projection ' +
        '(e.g. "sessionId,status,agentVersion,startedAt"). Version sweeps: agentVersionPrefix / imeAgentVersionPrefix ' +
        'match a whole build line in one call. ' +
        'deviceProperties / serial / geo / time filters are applied after the index read, so the tool scans forward past ' +
        'empty pages: "count": 0 without nextLink means no matches; "moreToScan": true means the per-call scan budget ' +
        'was hit — pass the nextLink as continuation to keep scanning.',
      inputSchema: {
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated)),
        status: z.enum(SESSION_STATUSES).optional()
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
        isSelfDeployingProfile: z.boolean().optional().describe(
          'Filter by self-deploying/kiosk Autopilot profile (CloudAssignedOobeConfig bits 0x20|0x40, agent-detected at registration)'),
        isCloudPc: z.boolean().optional().describe(
          'Windows 365 Cloud PC as detected by the agent (Windows365 registry key AND CloudManagedDesktopExtension service; sticky-true). ' +
          'Independent of validatedBy="CloudPc" (server-side Graph check). False on sessions from agents predating the field.'),
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
        connectionType: z.enum(['WiFi', 'Ethernet']).optional()
          .describe('Active network media during enrollment, indexed for exact match. Last emission wins (a device that ' +
                    'switches media reports the most recent state). Sessions predating the column are excluded.'),
        fields: z.string().optional()
          .describe('Comma-separated projection keys: sessionId, tenantId, status, serialNumber, manufacturer, model, ' +
                    'deviceName, osBuild, osName, startedAt, completedAt, durationSeconds, currentPhase, failureReason, ' +
                    'eventCount, enrollmentType, isPreProvisioned, isUserDriven, isHybridJoin, isSelfDeployingProfile, ' +
                    'isCloudPc, agentVersion, imeAgentVersion, geoCountry, rebootCount, connectionType, avgApiLatencyMs, ' +
                    'apiRequestCount (weight avgApiLatencyMs by it). A key newer than the recording agent is null.'),
        deviceProperties: z.record(z.string(), z.string()).optional().describe(
          'Keys "eventType.propertyName" from get_resource(name="device_properties"); unknown prefix rejected. ' +
          'Exact match; numeric ">=8" (>=, <=, >, <); "ARM*" = prefix; "True"/"False"; arrays: substring of any element. ' +
          'E.g. {"tpm_status.specVersion": "2.0"}.'
        ),
        pageSize: z.coerce.number().int().min(1).max(1000).optional()
          .describe(pageSizeDescription(DEFAULT_FIRST_PAGE_SIZE, 1000, 'Raise it for full sweeps.')),
        continuation: z.string().optional().describe(CONTINUATION_DESCRIPTION),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('search_sessions', args, async () => {
      try {
        const { deviceProperties, tenantId: rawTenantId, pageSize: explicitPageSize, continuation, ...rest } = args;
        const pageSize = pageSizeForCall(explicitPageSize, continuation, DEFAULT_FIRST_PAGE_SIZE);
        // Delegated (MSP): require a managed tenantId (no aggregate); a page-2 call carries it inside the
        // continuation nextLink. No-op for GA/Reader/tenant users.
        const tenantId = enforceDelegatedTenantForPage(rawTenantId, continuation);
        // GA → /api/global/search/sessions (tenantId is filter); Tenant-Admin → /api/search/sessions (JWT-bound).
        const basePath = pickGlobalOrTenantPath('/api/global/search/sessions', '/api/search/sessions', tenantId);
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
        const path = followNextLink(basePath, queryParams, continuation, { pageSize });
        // deviceProperties + scan-path filters (serial, geo, time, …) are post-filtered
        // in-memory by the backend, so a page can be empty yet still carry a nextLink.
        // Auto-exhaust forward so the model never sees a misleading empty-but-continuable page;
        // a timeout is retried once with a halved pageSize on the same cursor.
        const data = await scanWithTimeoutFallback(path, basePath, effectivePageSize(pageSize, continuation));
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
        'Find sessions that contain a given event type, read from the event-type index (e.g. every session with ' +
        'app_install_failed or enrollment_failed). eventType is validated against the event_types catalog; an unknown ' +
        'type is rejected, not an empty result.',
      inputSchema: {
        eventType: z.string().describe('Event type string from get_resource(name="event_types") (e.g. "app_install_failed", "enrollment_failed")'),
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated)),
        pageSize: z.coerce.number().int().min(1).max(1000).optional()
          .describe(pageSizeDescription(DEFAULT_FIRST_PAGE_SIZE, 1000, 'Raise it for full sweeps.')),
        continuation: z.string().optional().describe(CONTINUATION_DESCRIPTION),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('search_sessions_by_event', args, async () => {
      try {
        const { eventType, tenantId: rawTenantId, continuation } = args;
        const pageSize = pageSizeForCall(args.pageSize, continuation, DEFAULT_FIRST_PAGE_SIZE);
        const tenantId = enforceDelegatedTenantForPage(rawTenantId, continuation);
        // eventType is the sole filter and is applied server-side (EventTypeIndex OData),
        // so an empty page never carries a nextLink — no auto-exhaust needed. But validate
        // the type so a typo is a clear error rather than a silent empty result.
        assertKnownEventType(eventType);
        const basePath = pickGlobalOrTenantPath('/api/global/search/sessions-by-event', '/api/search/sessions-by-event', tenantId);
        const path = followNextLink(
          basePath,
          { eventType, tenantId, pageSize },
          continuation,
          { pageSize },
        );
        const data = await apiFetch<SessionListResponse>(path);
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
      description: 'Full record of one enrollment session including all device metadata. includeAnalysis=true adds the rule analysis (why the session failed, remediation).',
      inputSchema: {
        sessionId: SessionIdSchema.describe('Session UUID'),
        tenantId: z.string().optional().describe(sessionTenantIdDescription),
        includeAnalysis: z.boolean().optional().default(false).describe('Include rule analysis results (failure explanations and remediation steps)'),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_session', args, async () => {
      try {
        const { sessionId, tenantId: rawTenantId, includeAnalysis } = args;
        // Delegated (MSP): the session lives in a managed tenant — require + validate it (the backend
        // rescue authorizes /api/sessions/{id}?tenantId=<managed>). No-op for GA/Reader/tenant users.
        const tenantId = enforceDelegatedTenant(rawTenantId);
        const q = buildQuery({ tenantId } as Record<string, string | undefined>);
        const sessionPromise = apiFetch<GetSessionResponse>(`/api/sessions/${sessionId}${q}`);
        if (!includeAnalysis) {
          return toolResultText({ session: await sessionPromise, analysis: null }, MAX_RESULT_SIZE_CHARS.small);
        }
        // Fetch session + analysis in parallel (was sequential). Substitute
        // {{token}} placeholders so the raw explanation/remediation text never
        // leaks literal {{reason}}/{{appName}}/…. Swallow ONLY 404 (analysis may
        // not exist yet) — a 403/500 is a real failure that must surface.
        const analysisPromise = apiFetch<GetRuleResultsResponse>(`/api/sessions/${sessionId}/analysis${q}`)
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
        'Returns a short-lived signed download URL (~10 min, no auth header) for the agent DIAGNOSTICS ZIP of a ' +
        'session: agent logs, DecisionCore journal/signals, IME logs, final-status.json. The archive layout and file ' +
        'priority order are the static "diag_zip_layout" resource — read it once via get_resource(name="diag_zip_layout"); ' +
        'the response does not repeat it. Correlating the on-device agent log against the backend Events table is the ' +
        'highest-value way to root-cause an enrollment.\n\n' +
        'CLIENT REQUIREMENT: the client must download and unzip the archive locally (e.g. Claude Code); the backend ' +
        'never unzips or parses it. A pure chat client without a filesystem only gets a link a human can open.\n\n' +
        'Read files in the diag_zip_layout priority order; AppWorkload*.log can be hundreds of MB — grep, never read ' +
        'whole. Enrich with get_session_events / query_raw_events / search_knowledge. ' +
        '"available": false means no diagnostics package was uploaded (upload mode Off, or OnFailure on a successful ' +
        'session) — proceed with backend telemetry only.',
      inputSchema: {
        sessionId: SessionIdSchema.describe('Session UUID'),
        tenantId: z.string().optional().describe(sessionTenantIdDescription),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_session_diagnostics', args, async () => {
      try {
        const { sessionId, tenantId: rawTenantId } = args;
        const tenantId = enforceDelegatedTenant(rawTenantId);
        const q = buildQuery({ tenantId } as Record<string, string | undefined>);
        // The { success, session } envelope is the pinned wire contract (GetSessionResponse).
        const sessionResp = await apiFetch<GetSessionResponse>(`/api/sessions/${sessionId}${q}`);
        const session = sessionResp.session;

        const blobName = session.diagnosticsBlobName || '';
        // tenantId for the ticket: explicit arg → session's tenantId → (tenant user) JWT default.
        const resolvedTenantId = tenantId ?? session.tenantId;

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
        const ticket = await apiFetch<DiagnosticsDownloadTicketResponse>(ticketPath, {
          method: 'POST',
          body: jsonBody<DownloadTicketRequest>({ blobName }),
        });

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
            'Read files in the priority order of the diag_zip_layout resource (zipLayoutResource); ' +
            'AppWorkload*.log can be huge → grep only. Then correlate the agent log timeline against ' +
            'backend events (get_session_events / query_raw_events) and look up rules/patterns via ' +
            'search_knowledge. The download URL expires at expiresAt — re-call this tool for a fresh ' +
            'one if needed.',
          // The layout is static per deployment: one resource read per conversation instead of
          // ~5k characters repeated on every ticket.
          zipLayoutResource: 'get_resource(name="diag_zip_layout")',
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
        'RAW EVENT RETRIEVAL (fallback when ranked search_events misses, or when every event of one session is needed ' +
        'in chronological sequence). Filter by eventType (validated against the event_types catalog; an unknown type is ' +
        'rejected, not an empty result), severity or source (app name). An unfiltered read omits the per-event "data" ' +
        'payload by default and says so in omittedFields; a filtered read includes it (see fields). ' +
        'Filters are applied after the partition read, so the tool scans forward past empty pages: "count": 0 without ' +
        'nextLink means no matching events; "moreToScan": true means the per-call scan budget was hit — pass the ' +
        'nextLink as continuation to keep scanning.',
      inputSchema: {
        sessionId: SessionIdSchema.describe('Session UUID'),
        tenantId: z.string().optional().describe(sessionTenantIdDescription),
        eventType: z.string().optional().describe('Only events of this type; values from get_resource(name="event_types")'),
        severity: z.enum(EVENT_SEVERITIES).optional(),
        source: z.string().optional().describe('Filter by event source/app name (e.g. "MicrosoftTeams")'),
        fields: z.string().optional()
          .describe('Projection. Omitted on an UNFILTERED read = lean default "' + LEAN_EVENT_FIELDS + '" without the multi-KB "data" payload (omittedFields says so); omitted on a FILTERED read = full events incl. "data". List "data" for the whole payload or "data.<key>" (e.g. "data.errorCode") for single keys. Further keys: eventId, sessionId, tenantId, receivedAt, sentAt, rowKey, originalTimestamp, timestampClamped, causedByTransitionStepIndex, causedBySignalOrdinal.'),
        pageSize: z.coerce.number().int().min(1).max(1000).optional()
          .describe(pageSizeDescription(DEFAULT_FIRST_PAGE_SIZE)),
        continuation: z.string().optional().describe(CONTINUATION_DESCRIPTION),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_session_events', args, async () => {
      try {
        const { sessionId, tenantId: rawTenantId, continuation, eventType, severity, source, fields: explicitFields } = args;
        const pageSize = pageSizeForCall(args.pageSize, continuation, DEFAULT_FIRST_PAGE_SIZE);
        // Default projection follows intent — see leanFieldSelection.
        const { fields, leanDefaultApplied } = leanFieldSelection(explicitFields, continuation, Boolean(eventType || severity || source), LEAN_EVENT_FIELDS);
        const tenantId = enforceDelegatedTenantForPage(rawTenantId, continuation);
        if (eventType) assertKnownEventType(eventType);
        const basePath = `/api/sessions/${sessionId}/events`;
        const path = followNextLink(
          basePath,
          { tenantId, pageSize, eventType, severity, source, fields },
          continuation,
          { pageSize, fields },
        );
        // eventType/severity/source are post-filtered in-memory over the session's
        // event partition, so a page can be empty while matches sit on a later page.
        // Auto-exhaust forward so the model isn't misled by an empty-but-continuable page;
        // a timeout is retried once with a halved pageSize on the same cursor.
        const data = await scanWithTimeoutFallback(path, basePath, effectivePageSize(pageSize, continuation));
        // Announce the omission in-band so a reader of the result knows the payload exists and how to get it.
        return toolResultText(leanDefaultApplied ? { ...data, ...LEAN_EVENT_OMISSION } : data, MAX_RESULT_SIZE_CHARS.events);
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
        'First tool when investigating a session. Returns: overview (status, duration, device, enrollment config); ' +
        'observation coverage (from when the agent actually watched, IME log tracker / collector / upload / ' +
        'diagnostics-package health, and coverage.gaps — one line per known blind spot; read it before treating a ' +
        'missing event as proof that something did not happen); a key-event timeline (errors, warnings, phase ' +
        'transitions, app installs; noise filtered; capped at the 50 most relevant, stats.keyEventsTruncated says if ' +
        'more were dropped); rule analysis (probable cause, remediation); aggregate stats. A key event with an error ' +
        'code shows errorCode plus errorText (symbol and catalog meaning); event payloads (data) are otherwise not ' +
        'included — get_session_events returns them. get_session returns the full metadata.',
      inputSchema: {
        sessionId: SessionIdSchema.describe('Session UUID'),
        tenantId: z.string().optional().describe(sessionTenantIdDescription),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_session_summary', args, async () => {
      try {
        const { sessionId, tenantId: rawTenantId } = args;
        const tenantId = enforceDelegatedTenant(rawTenantId);
        const q = buildQuery({ tenantId } as Record<string, string | undefined>);
        // Unpaginated (the summary ranks the whole timeline) but projected: only the triage
        // fields plus the payload keys the two guards read (SUMMARY_EVENT_FIELDS) travel.
        const eventsQuery = buildQuery({ tenantId, fields: SUMMARY_EVENT_FIELDS } as Record<string, string | undefined>);
        const fetchOpts = { signal: AbortSignal.timeout(90_000) };

        const [sessionData, eventsData, analysisData, annotationsData] = await Promise.all([
          apiFetch<GetSessionResponse>(`/api/sessions/${sessionId}${q}`, fetchOpts),
          apiFetch<GetSessionEventsResponse>(`/api/sessions/${sessionId}/events${eventsQuery}`, fetchOpts),
          apiFetch<GetRuleResultsResponse>(`/api/sessions/${sessionId}/analysis${q}`, fetchOpts).catch(() => null),
          // Human annotations (verdict + note per lane). Backend filters the platform-internal
          // globaladmin lane for non-global callers — pass-through, no re-shaping needed.
          apiFetch<GetSessionAnnotationsResponse>(`/api/sessions/${sessionId}/annotations${q}`, fetchOpts).catch(() => null),
        ]);

        const s = sessionData.session;

        const overview = {
          sessionId,
          tenantId: s.tenantId ?? tenantId,
          status: s.status,
          failureReason: s.failureReason ?? null,
          // Non-null when the BACKEND declared the success (timeout-sweep reconcile or
          // late-completion upgrade) rather than the agent reporting enrollment_complete.
          reconcileReason: s.reconcileReason || null,
          startedAt: s.startedAt,
          completedAt: s.completedAt ?? null,
          durationSeconds: s.durationSeconds ?? null,
          currentPhase: phaseName(s.currentPhase, s.enrollmentType),
          enrollmentType: s.enrollmentType,
          isPreProvisioned: s.isPreProvisioned ?? false,
          isHybridJoin: s.isHybridJoin ?? false,
          isUserDriven: s.isUserDriven ?? false,
          isSelfDeployingProfile: s.isSelfDeployingProfile ?? false,
          // Agent-detected Windows 365 Cloud PC marker (Windows365 registry key +
          // CloudManagedDesktopExtension service). Independent of validatedBy="CloudPc".
          isCloudPc: s.isCloudPc ?? false,
          // Backend device-validation path that admitted the device at registration:
          // "AutopilotV1" | "CorporateIdentifier" | "DeviceAssociation" | "Bootstrap" | "CloudPc".
          // Null for sessions predating the field or tenants with device validation off.
          validatedBy: s.validatedBy || null,
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

        // Historic-replay filter: replayed app_install_failed events are Error-severity and
        // would inflate errorCount; replayed app_install_* would inflate the appInstalls
        // stats; replayed events would pollute the keyEvents triage timeline. One filter
        // cleans all three. stats.totalEvents therefore counts non-replayed events.
        const allEvents = (eventsData.events ?? []).filter((e) => !isHistoricImeReplay(e));

        // Observation coverage: what the agent could and could not see, folded from the
        // health/lifecycle events that the triage timeline below ranks low or drops.
        const coverage = buildSessionCoverage(s, allEvents);

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
          const benign = isBenignHealthDetectionReport(String(e.eventType ?? ''), e.data);
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

        const relevanceScore = (e: Partial<EnrollmentEvent>): number => {
          // Benign compliant detection mis-stamped Error → rank as info-level, not top.
          if (isBenignHealthDetectionReport(String(e.eventType ?? ''), e.data)) return 10;
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
          ...keyEventErrorCode(e.data, e.source),
        }));

        let analysis = null;
        if (analysisData) {
          // Resolve {{token}} placeholders from each result's matchedConditions
          // before mapping, so issues carry readable text not raw {{...}} tokens.
          interpolateAnalysisResults(analysisData);
          const allResults = analysisData.results ?? [];
          // Resolved findings (session healed after an interim fire) stay in the raw
          // /analysis response for audit but are not open issues; interim findings are
          // preliminary until the enrollment-end pass finalizes them.
          const results = allResults.filter((r) => !r.resolvedAt);
          const resolvedCount = allResults.length - results.length;
          analysis = {
            totalIssues: analysisData.totalIssues ?? results.length,
            criticalCount: analysisData.criticalCount ?? 0,
            highCount: analysisData.highCount ?? 0,
            warningCount: analysisData.warningCount ?? 0,
            ...(resolvedCount > 0 ? { resolvedCount } : {}),
            issues: results.map((r) => ({
              ruleTitle: r.ruleTitle,
              severity: r.severity,
              ...(r.isInterim ? { isInterim: true } : {}),
              explanation: r.explanation,
              remediation: r.remediation,
            })),
          };
        }

        // Null when no lanes are annotated (or the read failed) — omitting keeps old output stable.
        const annotations =
          annotationsData?.annotations && annotationsData.annotations.length > 0
            ? annotationsData.annotations
            : null;

        const result = {
          overview,
          coverage,
          keyEvents: mappedEvents,
          analysis,
          annotations,
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
        'Aggregated enrollment metrics over a trailing window: failure rates, slowest and most-failing apps, session counts ' +
        '(a summary block plus an app-metrics block; one failing block is reported in partialErrors, not as an error).',
      inputSchema: {
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated)),
        days: z.coerce.number().int().min(1).max(365).optional().default(30)
          .describe(daysDescription(30, 365, 'Applies to both the summary and the app metrics.')),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_metrics', args, async () => {
      try {
        const { tenantId: rawTenantId, ...rest } = args;
        const tenantId = enforceDelegatedTenant(rawTenantId);
        const params: Record<string, string | number | undefined> = { ...rest };
        if (tenantId) params.tenantId = tenantId;
        const q = buildQuery(params);
        const prefix = pickGlobalOrTenantPath('/api/global/metrics', '/api/metrics', tenantId);
        const [summaryRes, appsRes] = await Promise.allSettled([
          apiFetch<MetricsSummaryResponse>(`${prefix}/summary${q}`),
          apiFetch<AppMetricsResponse>(`${prefix}/app${q}`),
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
        "Find enrollment sessions whose software inventory reported a given CVE (requires vulnerability scanning to be " +
        "enabled): which devices are affected by CVE-YYYY-NNNN, narrowed by minCvssScore / overallRisk. " +
        "The per-session vulnerability report (get_session_summary / vulnerability_report event) lists each CVE with " +
        "cvssScore, cvssVector, isKev, epssScore (FIRST EPSS, 0-1), epssPercentile and priority (act/attend/track).",
      inputSchema: {
        cveId: z.string()
          .regex(/^CVE-\d{4}-\d{4,}$/i, 'Must be a CVE identifier like "CVE-2024-21447" (CVE-YYYY-NNNN+).')
          .describe('CVE identifier (e.g. "CVE-2024-21447"). Validated — a non-CVE string is rejected, not silently empty.'),
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated)),
        minCvssScore: z.coerce.number().min(0).max(10).optional().describe('Minimum CVSS score filter (e.g. 7.0 for high+critical)'),
        overallRisk: z.enum(['low', 'medium', 'high', 'critical']).optional(),
        pageSize: z.coerce.number().int().min(1).max(1000).optional()
          .describe(pageSizeDescription(DEFAULT_FIRST_PAGE_SIZE, 1000, 'Raise it for a full exposure audit.')),
        continuation: z.string().optional().describe(CONTINUATION_DESCRIPTION),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('search_sessions_by_cve', args, async () => {
      try {
        const { cveId, tenantId: rawTenantId, minCvssScore, overallRisk, continuation } = args;
        const pageSize = pageSizeForCall(args.pageSize, continuation, DEFAULT_FIRST_PAGE_SIZE);
        const tenantId = enforceDelegatedTenantForPage(rawTenantId, continuation);
        // Normalize to canonical upper-case form (schema accepts case-insensitive).
        const normalizedCve = cveId.toUpperCase();
        const basePath = pickGlobalOrTenantPath('/api/global/search/sessions-by-cve', '/api/search/sessions-by-cve', tenantId);
        const path = followNextLink(
          basePath,
          { cveId: normalizedCve, tenantId, minCvssScore, overallRisk, pageSize },
          continuation,
          { pageSize },
        );
        const data = await apiFetch<SessionListResponse>(path);
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
        'List devices currently blocked from enrolling; the backend rejects their enrollment sessions. The block list is ' +
        'platform-wide.',
      inputSchema: {
        tenantId: z.string().optional().describe(tenantIdDescription(ga, delegated)),
      },
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('list_blocked_devices', args, async () => {
      try {
        const { tenantId } = args;
        // GA: /api/global/devices/blocked (tenantId is filter); non-GA: /api/devices/blocked
        // (backend will 403 — list is platform-wide and GA-only by policy).
        const basePath = pickGlobalOrTenantPath('/api/global/devices/blocked', '/api/devices/blocked');
        const endpoint = `${basePath}${buildQuery({ tenantId } as Record<string, string | undefined>)}`;
        const data = await apiFetch<BlockedDeviceListResponse>(endpoint);
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.adminStream);
      } catch (error: unknown) {
        return toolError('list_blocked_devices', args, error);
      }
    })
  );
  } // end if (ga) — list_blocked_devices

  // Tool: get_ime_pattern_health — operator-only (GA + Global Reader; GlobalReadOrAdmin endpoint).
  // The pattern-drift loop's read side: which shipped IME log patterns still match on which IME
  // version, the fleet baseline, and the open ImePatternDriftSuspected alerts.
  if (ga) server.registerTool(
    'get_ime_pattern_health',
    {
      title: 'IME Pattern Health',
      description:
        'IME log-pattern drift: per IME agent version, how many sessions reported the session-end pattern histogram ' +
        'and in what share of them each shipped pattern matched (cells); the fleet baseline version; which patterns ' +
        'are EXPECTED (>= expectedHitRate on the baseline); and the open drift alerts (an expected pattern that ' +
        'matched in none of >= minCandidateSessions sessions on a newer version — Microsoft probably changed the log ' +
        'wording). On an alert: search_sessions with imeAgentVersion=<version> -> get_session_diagnostics on a session ' +
        'with a package -> validate the pattern against the real IME log -> compare with the IME decompile -> fix it ' +
        'in rules/ime-log-patterns. Only sessions that reached a terminal run report a histogram (crashes/kills are ' +
        'excluded from the denominator). catalog says where the shipped-pattern list comes from (the last GitHub ' +
        'reseed or the deployed backend build) and when it was written.',
      inputSchema: {},
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_ime_pattern_health', args, async () => {
      try {
        const data = await apiFetch<ImePatternHealthResponse>('/api/metrics/ime-pattern-health');
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.adminStream);
      } catch (error: unknown) {
        return toolError('get_ime_pattern_health', args, error);
      }
    })
  );

  // Tool: get_ime_version_history — a global (non-tenant) archive available to all tenant members.
  // Hidden for a delegated (MSP) caller: their surface is the tenant-boundable managed-tenant subset,
  // and a platform-wide archive with no tenantId to bound is outside that contract (§2.2). A platform
  // GA/Reader (delegated=false here) and ordinary tenant users still see it.
  if (!delegated) server.registerTool(
    'get_ime_version_history',
    {
      title: 'IME Version History',
      description:
        'History of every IME (Intune Management Extension) agent version seen across enrollments: first and last seen, ' +
        'session count. A permanent platform-wide archive that survives data retention (no tenantId; data is global) — ' +
        'tracks Microsoft IME rollouts over time.',
      inputSchema: {},
      annotations: READ_ONLY,
    },
    async (args) => withToolTelemetry('get_ime_version_history', args, async () => {
      try {
        const data = await apiFetch<Array<ImeVersionHistoryEntry | ImeVersionHistoryLeanEntry>>('/api/metrics/ime-versions');
        return toolResultText(data, MAX_RESULT_SIZE_CHARS.small);
      } catch (error: unknown) {
        return toolError('get_ime_version_history', args, error);
      }
    })
  );
}

/** Payload keys that carry an error code, in the order a reader wants them explained. */
const ERROR_CODE_KEYS: ReadonlyArray<[code: string, info: string]> = [
  ['errorCode', 'errorCodeInfo'],
  ['hresult', 'hresultInfo'],
  ['exitCode', 'exitCodeInfo'],
  ['hresultFromWin32', 'hresultFromWin32Info'],
];

/**
 * `source` the agent stamps on gather-rule output. Those payload keys are author-chosen and the
 * codes come from third-party logs/commands (HP Image Assistant, Dell Command Update, custom
 * scripts) with their own numbering, so neither the backend enricher nor the local catalog
 * fallback may explain them unless the rule opted in via `enrichErrorCodes` (the agent then
 * stamps `enrichErrorCodes: true` into the data). The raw code always travels.
 */
const GATHER_RULE_SOURCE = 'GatherRuleExecutor';
const ENRICH_OPT_IN_KEY = 'enrichErrorCodes';

function isOptedOutGatherEvent(data: Record<string, unknown>, source: string | null | undefined): boolean {
  if (typeof source !== 'string' || source.toLowerCase() !== GATHER_RULE_SOURCE.toLowerCase()) return false;
  const marker = data[ENRICH_OPT_IN_KEY];
  return !(marker === true || (typeof marker === 'string' && marker.toLowerCase() === 'true'));
}

/**
 * `errorCode` + `errorText` for a key event, only when the payload carries a code. The text
 * comes from the backend-enriched `*Info` sibling (symbol + catalog meaning); an older
 * response without the sibling falls back to the server's own catalog. A code the catalog
 * does not know is still surfaced as errorCode, without errorText. Gather-rule events
 * (`source === GatherRuleExecutor`) get catalog text only when their rule opted in.
 */
export function keyEventErrorCode(
  data: Record<string, unknown> | undefined,
  source?: string | null,
): { errorCode?: string; errorText?: string } {
  if (!data) return {};
  const suppressCatalogText = isOptedOutGatherEvent(data, source);
  for (const [codeKey, infoKey] of ERROR_CODE_KEYS) {
    const raw = data[codeKey];
    if (raw === undefined || raw === null || raw === '' || raw === 0 || raw === '0') continue;
    const errorCode = String(raw);
    if (suppressCatalogText) return { errorCode };
    const info = data[infoKey] as { description?: unknown; symbol?: unknown } | undefined;
    let symbol: string | undefined;
    let description: string | undefined;
    if (info && typeof info === 'object' && typeof info.description === 'string') {
      description = info.description;
      symbol = typeof info.symbol === 'string' ? info.symbol : undefined;
    } else {
      try {
        const hit = lookupErrorCode(errorCode);
        if (hit.found && 'description' in hit) { description = hit.description; symbol = hit.symbol; }
      } catch {
        // Catalog unavailable: the raw code still travels.
      }
    }
    if (!symbol && codeKey === 'hresult' && typeof data.hresultSymbol === 'string' && data.hresultSymbol !== 'WU_E_UNKNOWN') {
      symbol = data.hresultSymbol;
    }
    const errorText = [symbol, description].filter(Boolean).join(' — ');
    return errorText ? { errorCode, errorText } : { errorCode };
  }
  return {};
}
