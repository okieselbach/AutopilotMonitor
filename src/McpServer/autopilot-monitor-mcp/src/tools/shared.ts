import type { ToolAnnotations } from '@modelcontextprotocol/server';
import { z } from 'zod';
import { PRETTY_JSON } from '../config.js';
import { isPrettyJsonRequested } from '../client.js';
import { COVERAGE_EVENT_FIELDS } from '../session-coverage.js';

/**
 * Projection sent by get_session_events when the caller omits `fields`: everything an event
 * carries EXCEPT the `data` payload (a single app_install_failed event can be tens of KB).
 * Callers that need the payload list `data` (whole) or `data.<key>` entries (a slice).
 */
export const LEAN_EVENT_FIELDS = 'eventType,severity,source,phase,phaseName,timestamp,message,sequence';

/**
 * Raw-events counterpart (query_raw_events): every stored column except DataJson. The raw
 * projection is a keep-list over literal column names, so "all but one" has to be spelled out;
 * a column added to the Events row later must be added here to appear by default.
 */
export const LEAN_RAW_EVENT_FIELDS =
  'PartitionKey,RowKey,Timestamp,OccurredUtc,EventId,SessionId,TenantId,EventType,Severity,Source,Phase,Message,Sequence,' +
  'ReceivedAt,SentAt,OriginalTimestamp,TimestampClamped,CausedByTransitionStepIndex,CausedBySignalOrdinal';

/**
 * In-band marker merged into a lean-default first page: a caller reading the RESULT (not the
 * schema) learns what was left out and the exact argument that brings it back. The model
 * must never have to guess whether a payload exists — omission is announced, not silent.
 *
 * The lean default applies to UNFILTERED reads only (a timeline skim). Usage telemetry
 * (30 days, 2026-09-02) shows callers project explicitly in >90 % of calls and, when they
 * filter by eventType, keep the payload about half the time — so a filtered read stays
 * complete and this marker never appears on it.
 */
export const LEAN_EVENT_OMISSION = {
  omittedFields: ['data'],
  omittedNote:
    'The per-event payload ("data", up to tens of KB per event) is omitted on an unfiltered timeline read. ' +
    `Re-run with fields="${LEAN_EVENT_FIELDS},data" for the whole payload, or add "data.<key>" entries ` +
    '(e.g. "data.errorCode") for just those keys; a read filtered by eventType/severity/source includes ' +
    'the payload by default, and a nextLink keeps whatever projection you chose.',
} as const;

/** Raw-events counterpart of LEAN_EVENT_OMISSION. */
export const LEAN_RAW_EVENT_OMISSION = {
  omittedFields: ['DataJson'],
  omittedNote:
    'DataJson (the raw per-event payload string) is omitted on an unfiltered read. ' +
    `Re-run with fields="${LEAN_RAW_EVENT_FIELDS},DataJson" to include it; a read filtered by ` +
    'eventType/severity/source includes it by default, and a nextLink keeps whatever projection you chose.',
} as const;

/**
 * The default projection of an event read follows intent (30-day usage telemetry, 2026-09-02):
 * an UNFILTERED first page is a timeline skim and leaves the payload out (the lean fields); a
 * read filtered by eventType/severity/source targets specific events and stays complete; an
 * explicit `fields` wins either way; a follow-up call keeps whatever projection the nextLink
 * carries (same rule as pageSize). `leanDefaultApplied` tells the caller to announce the omission.
 */
export function leanFieldSelection(
  explicitFields: string | undefined,
  continuation: string | undefined,
  targeted: boolean,
  leanFields: string,
): { fields: string | undefined; leanDefaultApplied: boolean } {
  const leanDefaultApplied = explicitFields === undefined && !continuation && !targeted;
  return { fields: explicitFields ?? (leanDefaultApplied ? leanFields : undefined), leanDefaultApplied };
}

/**
 * What get_session_summary reads per event, and nothing more: the triage fields plus the
 * handful of payload keys its two guards inspect (isBenignHealthDetectionReport,
 * isHistoricImeReplay) and the error-code keys with their backend-enriched `*Info`
 * siblings (errorCode/errorText on the key events), requested as `data.<key>` slices so the
 * backend never ships the full payload for a summary that drops it anyway.
 */
export const SUMMARY_EVENT_FIELDS =
  'eventType,severity,source,phase,timestamp,message,sequence,' +
  'data.scriptType,data.script_type,data.scriptPart,data.script_part,data.result,' +
  'data.rejectedSourceTimestamp,data.rejected_source_timestamp,' +
  'data.errorCode,data.exitCode,data.hresult,data.hresultFromWin32,data.hresultSymbol,' +
  'data.errorCodeInfo,data.exitCodeInfo,data.hresultInfo,data.hresultFromWin32Info,' +
  // Observation-coverage slices (session-coverage.ts): the health/lifecycle payload keys the
  // `coverage` block folds — still per-key, never the whole payload.
  COVERAGE_EVENT_FIELDS;

/**
 * Zod validator for session IDs. Sessions are UUIDs and the value is
 * interpolated unencoded into backend URL paths (`/api/sessions/{id}/...`).
 * WHATWG-URL normalization collapses `..` segments before fetch sends the
 * request, so an unvalidated value like `../admin/foo` would silently route
 * to a different endpoint. The strict GUID pattern blocks both that path-
 * traversal vector and accidental garbage inputs.
 */
export const SessionIdSchema = z
  .string()
  .regex(
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i,
    'sessionId must be a UUID (e.g. "e259c121-1234-4abc-9def-0123456789ab")',
  );

/**
 * Read-side guard for the historical / in-flight `script_failed` rows that the agent
 * mis-stamped `Severity=Error`. A health-script DETECTION / post-detection event is a
 * *compliance report*, not a crash: its authoritative outcome is the compliance verdict,
 * and detection PowerShell routinely leaks benign probe errors to stderr while still
 * reporting compliant. The agent historically routed those to `script_failed` / Error,
 * which inflated `errorCount` and ranked them #1 in `search_events` on green sessions.
 *
 * Returns true when an event is one of those benign detection/post-detection reports and
 * should therefore NOT be treated as an error for ranking or counting. Mirrors the agent's
 * post-fix routing (detection/post-detection are non-failures unless IME reported
 * `result === "Failed"`). The agent emit was corrected at the source; this guard de-pollutes
 * the rows already stored before that build rolls out per-enrollment — without mutating them.
 */
export function isBenignHealthDetectionReport(
  eventType: string | undefined,
  data: Record<string, unknown> | undefined,
): boolean {
  if (eventType !== 'script_failed' || !data) return false;
  const scriptType = String(data.scriptType ?? data.script_type ?? '').toLowerCase();
  if (scriptType !== 'remediation') return false;
  const scriptPart = String(data.scriptPart ?? data.script_part ?? '').toLowerCase();
  if (scriptPart !== 'detection' && scriptPart !== 'post-detection') return false;
  // Explicit IME failure verdict is authoritative — keep it as an error.
  if (String(data.result ?? '').toLowerCase() === 'failed') return false;
  return true;
}

/**
 * Builds the role-aware description for a tenant-boundable tool's `tenantId` argument. MCP clients weigh
 * the per-arg schema heavily, so a delegated (MSP) caller — for whom omitting tenantId is REJECTED, not
 * defaulted — must see "required, name a managed tenant" here, not the optional/home-tenant wording that
 * applies to GA (optional cross-tenant filter) or a plain tenant user (defaults to own tenant). Pass the
 * existing GA / tenant-user texts; the delegated text is shared so the contract reads identically
 * everywhere.
 */
export function tenantIdDescription(ga: boolean, delegated: boolean, gaText: string, tenantText: string): string {
  if (delegated) {
    // Deliberately says nothing about pagination: this string is shared across tools whose follow-up
    // pages behave differently — backend-nextLink pagers re-send tenantId inside the continuation, but
    // offset-based client-side pagers (geo-offset:/inv-offset:) still need it re-passed every page. The
    // per-tool `continuation` arg description owns those mechanics; here we only state the invariant.
    return 'REQUIRED: name the tenant to query — one of YOUR managed tenants (delegated/MSP), or your own ' +
      'home tenant if you are a member of it. There is no cross-tenant aggregate here and no implicit default — ' +
      'every query must name a specific tenant. Call list_tenants to see your tenants with display names; ' +
      'for a bounded overview across ALL your managed tenants call get_fleet_overview instead.';
  }
  return ga ? gaText : tenantText;
}

/**
 * Zod validator for tenant IDs interpolated into backend URL paths
 * (`/api/config/{tenantId}/...`). Same path-traversal rationale as
 * SessionIdSchema: WHATWG-URL normalization collapses `..` segments, so an
 * unvalidated value could silently route to a different endpoint.
 */
export const TenantGuidSchema = z
  .string()
  .regex(
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i,
    'tenantId must be a GUID (e.g. "e259c121-1234-4abc-9def-0123456789ab")',
  );

/** Read-only query tool — no side effects, idempotent, closed-world (our backend only). */
export const READ_ONLY: ToolAnnotations = {
  readOnlyHint: true,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: false,
};

/**
 * Config-mutating tool — writes tenant configuration via the backend's
 * transactional endpoints (PATCH fields / POST revert). destructiveHint is
 * true because the write overwrites prior values (clients may ask the user
 * to confirm); NOT idempotent — repeating a revert after further changes
 * restores a different state. Every write is preceded by a fail-closed
 * snapshot server-side, so the operation is always revertible.
 */
export const MUTATING: ToolAnnotations = {
  readOnlyHint: false,
  destructiveHint: true,
  idempotentHint: false,
  openWorldHint: false,
};

/** KQL / raw log query — read-only but open-world (arbitrary KQL against App Insights). */
export const READ_ONLY_OPEN: ToolAnnotations = {
  readOnlyHint: true,
  destructiveHint: false,
  idempotentHint: true,
  openWorldHint: true,
};

/**
 * Calibrated maximum response size (in characters) per tool. Communicated to
 * the MCP client via the per-response <c>_meta.anthropic/maxResultSizeChars</c>
 * annotation; the Anthropic client respects it and does not truncate below the
 * declared cap.
 *
 * Without this annotation, the client falls back to its default cap (~25–30k
 * chars) and silently truncates large responses mid-page — the LLM then
 * follows nextLink without realizing it lost rows in the middle of the prior
 * page. Sizing rule: each tool's cap must comfortably hold a full
 * default-pageSize response of typical record density, plus headroom for the
 * occasional fat row.
 */
export const MAX_RESULT_SIZE_CHARS = {
  /** ~200 SessionSummaries (~1.5KB each) + headroom for wide DeviceProperties. */
  sessions: 300_000,
  /** ~200 EnrollmentEvents — moderate density, few wide payloads. */
  events: 250_000,
  /** Audit / ops / report streams — compact rows. */
  adminStream: 150_000,
  /** Cross-session session lookups via index (event/CVE) — SessionSummary density. */
  indexSessions: 200_000,
  /** Generic raw table — TableEntity columns are unbounded; paranoid headroom. */
  rawTable: 500_000,
  /** Compact metadata responses (single objects, summaries, lists < 50 items). */
  small: 50_000,
} as const;

/**
 * Serializes a tool result or resource body. Compact by default: the text lands verbatim in
 * a language model's context, which — unlike the gzipped wire — pays for every indentation
 * character (measured with an OpenAI-family tokenizer: +14–46 % tokens, 2–2.7 tokens per line).
 * Indented output is available per client via the `X-MCP-Pretty: 1` request header (humans
 * reading results in an interactive client) or globally via MCP_PRETTY_JSON=true.
 */
export function stringifyResult(data: unknown): string {
  return PRETTY_JSON || isPrettyJsonRequested() ? JSON.stringify(data, null, 2) : JSON.stringify(data);
}

/**
 * `_meta` key of an overflow answer (see toolResultText). The telemetry reads it to log the
 * page at the size it WOULD have had, so the size distribution keeps naming the offending calls.
 */
export const OVERFLOW_META_KEY = 'autopilotmonitor.com/overflow';

/** Share of the cap an overflow advice aims at, so the re-sent page lands under it with headroom. */
const OVERFLOW_TARGET_RATIO = 0.9;

/** Fewest rows a page needs before its per-row size is worth projecting into a pageSize advice. */
const OVERFLOW_MIN_ROWS_FOR_ADVICE = 2;

export interface ToolResult {
  // Index signature: the SDK's CallToolResult is indexable, and an interface without one is not
  // assignable to it (an inline object type would be, implicitly).
  [x: string]: unknown;
  isError?: true;
  content: Array<{ type: 'text'; text: string }>;
  _meta: Record<string, unknown>;
}

/**
 * Wraps a tool response payload with the Anthropic <c>maxResultSizeChars</c>
 * annotation and a single text content block. Use in preference to bare
 * <c>{ content: [...] }</c> so the cap travels with every call without
 * client-side configuration.
 *
 * The cap is ENFORCED here, not merely advertised: the hint only tells an Anthropic host how
 * much to accept — other hosts cut the JSON somewhere in the middle, and even that host has a
 * ceiling. Either way the model would read a page with rows missing and no way to tell which
 * (the nextLink at the end is usually the first casualty). So a page above the cap is not sent
 * at all: the answer is an error carrying what the page held and, when the payload is a list,
 * the exact pageSize that fits — computed from THIS page's measured bytes per row, not guessed.
 * Nothing is lost because backend pages are re-requestable: the same continuation with the
 * smaller pageSize returns the first rows plus a nextLink to the rest. The oversized page's
 * own nextLink is deliberately withheld — following it would skip the rows that were dropped.
 */
export function toolResultText(data: unknown, maxResultSizeChars: number): ToolResult {
  const text = stringifyResult(data);
  if (text.length <= maxResultSizeChars) {
    return {
      content: [{ type: 'text' as const, text }],
      _meta: { 'anthropic/maxResultSizeChars': maxResultSizeChars },
    };
  }
  return overflowResult(data, text.length, maxResultSizeChars);
}

/** The top-level list a page carries (its largest array-valued property), if any. */
function largestTopLevelList(data: unknown): { field: string; rows: number } | undefined {
  if (typeof data !== 'object' || data === null || Array.isArray(data)) return undefined;
  let best: { field: string; rows: number } | undefined;
  for (const [field, value] of Object.entries(data as Record<string, unknown>)) {
    if (Array.isArray(value) && (best === undefined || value.length > best.rows)) best = { field, rows: value.length };
  }
  return best;
}

function overflowResult(data: unknown, responseChars: number, maxResultSizeChars: number): ToolResult {
  const list = largestTopLevelList(data);
  const rowAdvice = list !== undefined && list.rows >= OVERFLOW_MIN_ROWS_FOR_ADVICE;
  const rowsThatFit = rowAdvice
    ? Math.max(1, Math.floor((maxResultSizeChars * OVERFLOW_TARGET_RATIO) / (responseChars / list.rows)))
    : undefined;
  const body = {
    overflow: true,
    error:
      `Result too large: ${responseChars} characters exceed this tool's ${maxResultSizeChars}-character response cap. ` +
      'The page was NOT sent — a cut page would have lost rows without telling you which.',
    responseChars,
    maxResultSizeChars,
    ...(rowAdvice ? { rowsField: list.field, rowsFetched: list.rows, rowsThatFit } : {}),
    advice: rowAdvice
      ? `This page held ${list.rows} rows in "${list.field}" at about ${Math.round(responseChars / list.rows)} characters each; ` +
        `${rowsThatFit} rows fit the cap. If this tool takes pageSize, re-send the SAME call (same filters, same ` +
        `continuation if any) with pageSize=${rowsThatFit} and follow its nextLink for the rest — nothing is skipped. ` +
        'Otherwise narrow the query: tighter filters, a fields= projection without the payload column, a smaller date window.'
      : 'Narrow the query: tighter filters, a fields= projection without the payload column, a smaller date window, ' +
        'or a smaller pageSize if this tool takes one.',
  };
  return {
    isError: true,
    content: [{ type: 'text' as const, text: stringifyResult(body) }],
    _meta: {
      'anthropic/maxResultSizeChars': maxResultSizeChars,
      [OVERFLOW_META_KEY]: { responseChars },
    },
  };
}
