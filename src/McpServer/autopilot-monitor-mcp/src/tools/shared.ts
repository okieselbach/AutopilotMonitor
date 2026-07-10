import type { ToolAnnotations } from '@modelcontextprotocol/sdk/types.js';
import { z } from 'zod';

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
      'home tenant if you are a member of it. There is no cross-tenant aggregate and no implicit default — ' +
      'every query must name a specific tenant. Call list_tenants to see your tenants with display names.';
  }
  return ga ? gaText : tenantText;
}

/** Read-only query tool — no side effects, idempotent, closed-world (our backend only). */
export const READ_ONLY: ToolAnnotations = {
  readOnlyHint: true,
  destructiveHint: false,
  idempotentHint: true,
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
 * Wraps a tool response payload with the Anthropic <c>maxResultSizeChars</c>
 * annotation and a single text content block. Use in preference to bare
 * <c>{ content: [...] }</c> so the cap travels with every call without
 * client-side configuration.
 */
export function toolResultText(
  data: unknown,
  maxResultSizeChars: number,
): { content: Array<{ type: 'text'; text: string }>; _meta: Record<string, unknown> } {
  return {
    // Pretty-printed: the output is frequently eyeballed during interactive MCP use, and
    // the indentation whitespace gzips away to almost nothing on the wire (compression
    // middleware), so readability is kept essentially for free.
    content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }],
    _meta: { 'anthropic/maxResultSizeChars': maxResultSizeChars },
  };
}
