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
    content: [{ type: 'text' as const, text: JSON.stringify(data, null, 2) }],
    _meta: { 'anthropic/maxResultSizeChars': maxResultSizeChars },
  };
}
