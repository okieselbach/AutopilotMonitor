import { ApiError, getCurrentArgPolicy, isTimeoutError, type ParsedErrorBody } from '../client.js';
import type { McpQuotaExceededResponse } from '../generated/wire-types.generated.js';
import { summarizeArgs } from '../telemetry.js';

interface ToolErrorResult {
  [x: string]: unknown;
  isError: true;
  content: Array<{ type: 'text'; text: string }>;
}

/**
 * The correlation id line every backend failure carries — from the X-Correlation-ID response
 * header (present on every backend answer) or the error envelope's `correlationId`. It is the
 * one handle an operator needs to find the request in the backend logs, so no branch may drop it.
 */
function correlationLine(error: ApiError): string | null {
  return error.correlationId ? `**Correlation ID**: ${error.correlationId}` : null;
}

/** The server's Retry-After (429/503) — apiFetch already waited it out once when it was short enough. */
function retryAfterLine(error: ApiError): string | null {
  return error.retryAfterSeconds !== null ? `**Retry after**: ${error.retryAfterSeconds} s` : null;
}

/** Machine-readable code of the error envelope (`code`); `errorCode` is the pre-envelope spelling. */
function errorCodeOf(p: ParsedErrorBody | null): string | null {
  const code = p?.code ?? p?.errorCode;
  return typeof code === 'string' && code.length > 0 ? code : null;
}

/**
 * Format any error into an MCP-compliant `{ isError: true }` response with
 * structured, AI-consumable details. Never throws — always returns a result
 * the SDK can send back to the model.
 *
 * Handles:
 * - Structured backend errors (ApiError with the error envelope: error, code, correlationId, hint?)
 * - Authentication errors
 * - Timeouts (AbortError / timeout signals)
 * - Legacy unstructured errors (fallback formatting)
 */
export function toolError(
  toolName: string,
  args: Record<string, unknown>,
  error: unknown,
): ToolErrorResult {
  const parts: string[] = [];

  if (error instanceof ApiError && error.status >= 500) {
    // Sanitize ALL 5xx — structured or not. Even structured backend errors
    // can carry internal fingerprints (stack frames, hint strings that name
    // internal services), and the model has no legitimate reason to act on
    // them. correlationId + code stay — those are operational handles the
    // operator can pivot on, not internals.
    if (error.status === 503) {
      parts.push(`**Backend unavailable in ${toolName}** (HTTP 503): the server is temporarily unavailable.`);
    } else {
      parts.push(`**Backend error in ${toolName}** (HTTP ${error.status}): the server returned an error.`);
    }
    const cid = correlationLine(error);
    if (cid) parts.push(cid);
    const code = errorCodeOf(error.parsed);
    if (code) parts.push(`**Error code**: ${code}`);
    const retryAfter = retryAfterLine(error);
    if (retryAfter) parts.push(retryAfter);
    parts.push(
      error.retryAfterSeconds !== null
        ? `**Suggestion**: retry after ${error.retryAfterSeconds} s; if persistent, ask an operator to inspect backend logs.`
        : '**Suggestion**: retry in a few seconds; if persistent, ask an operator to inspect backend logs.',
    );
  } else if (error instanceof ApiError && error.status === 429 && error.parsed?.quotaExceeded === true) {
    // Backend MCP quota (McpQuotaExceededResponse): the daily/monthly budget of the caller's own plan
    // (level=user) or of the caller's own organization (level=tenant). Every request — a delegated (MSP)
    // read into a managed tenant included — is charged to the caller's HOME tenant ("the budget follows
    // the delegating tenant"), so a tenant-level block always names the caller's own organization, never
    // a managed one. Retrying is pointless until resetUtc — say so, and say WHOSE budget it is, so a
    // member blocked by the tenant window does not go and create more accounts or ask for a bigger
    // personal plan.
    const p = error.parsed as Partial<McpQuotaExceededResponse> & Record<string, unknown>;
    const tenantLevel = p.level === 'tenant';
    const whose = tenantLevel ? "your organization's" : 'your';
    // `error` is the envelope's message; `message` is the pre-envelope spelling.
    parts.push(`**Quota exceeded in ${toolName}**: ${p.error ?? p.message ?? `${whose} MCP ${p.scope ?? ''} request quota is exhausted.`}`);
    if (p.limit != null && p.used != null) parts.push(`**Budget**: ${p.used} of ${p.limit} requests used (${p.scope ?? 'window'}, ${p.level ?? 'user'} level).`);
    if (p.resetUtc) parts.push(`**Resets at**: ${p.resetUtc}`);
    parts.push(
      tenantLevel
        ? '**Suggestion**: do not retry before the reset — the window is shared by every member of the tenant; a tenant admin can review consumption under Configuration → Reporting → MCP Usage.'
        : '**Suggestion**: do not retry before the reset; narrow further queries, or ask an administrator about a larger usage plan.',
    );
    const cid = correlationLine(error);
    if (cid) parts.push(cid);
  } else if (error instanceof ApiError && error.parsed) {
    // Structured backend error (4xx) — the error envelope: error, code, correlationId, hint?
    const p = error.parsed;
    parts.push(`**Error in ${toolName}**: ${p.error ?? error.message}`);
    if (p.hint) parts.push(`**Suggestion**: ${p.hint}`);
    const cid = correlationLine(error);
    if (cid) parts.push(cid);
    const code = errorCodeOf(p);
    if (code) parts.push(`**Error code**: ${code}`);
    const retryAfter = retryAfterLine(error);
    if (retryAfter) parts.push(retryAfter);
    // The operator log proxy (query_backend_logs) forwards the telemetry store's own error code and
    // JSON: that is what `az monitor … query` prints, and the parity promise is that nothing of it is lost.
    if (typeof p.upstreamCode === 'string' && p.upstreamCode.length > 0) parts.push(`**Upstream code**: ${p.upstreamCode}`);
    if (typeof p.upstream === 'string' && p.upstream.length > 0) {
      parts.push(`**Upstream response**:\n\`\`\`json\n${p.upstream.length > 4000 ? p.upstream.slice(0, 4000) + '…' : p.upstream}\n\`\`\``);
    }
  } else if (error instanceof ApiError) {
    // API error but non-JSON body
    if (error.status === 401) {
      parts.push(`**Authentication required in ${toolName}**: your session is not authenticated or has expired.`);
      parts.push('**Suggestion**: Re-authenticate and retry.');
    } else if (error.status === 403) {
      parts.push(`**Access denied in ${toolName}**: you do not have permission to perform this operation.`);
    } else if (error.status === 404) {
      parts.push(`**Not found in ${toolName}**: The requested resource does not exist. Verify IDs, table names, or filters.`);
    } else if (error.status === 429) {
      parts.push(`**Rate limited in ${toolName}**: Too many requests. Wait a moment and retry.`);
      const retryAfter = retryAfterLine(error);
      if (retryAfter) parts.push(retryAfter);
    } else {
      const body = error.body || 'No response body';
      const truncated = body.length > 500 ? body.slice(0, 500) + '…' : body;
      parts.push(`**Error in ${toolName}** (HTTP ${error.status}): ${truncated}`);
    }
    const cid = correlationLine(error);
    if (cid) parts.push(cid);
  } else {
    const message = error instanceof Error ? error.message : String(error);
    if (message.includes('No authentication token')) {
      parts.push(`**Authentication error in ${toolName}**: ${message}`);
      parts.push('**Suggestion**: The MCP session may have expired. Re-authenticate.');
    } else if (isTimeoutError(error)) {
      parts.push(`**Timeout in ${toolName}**: The backend did not respond in time.`);
      if (typeof args.continuation === 'string' && args.continuation.startsWith('/api/')) {
        // A nextLink carries the page-1 pageSize; only an EXPLICIT pageSize on the follow-up
        // call overrides it (the cursor stays valid). "Narrow the query" alone would re-send
        // the identical request and fail identically.
        parts.push(
          '**Suggestion**: Re-send the SAME continuation together with an explicitly smaller pageSize ' +
          '(it overrides the value embedded in the nextLink; the cursor stays valid), or narrow the date window.',
        );
      } else {
        parts.push('**Suggestion**: Try narrowing the query (smaller date range, smaller pageSize, more specific filters).');
      }
    } else {
      parts.push(`**Error in ${toolName}**: ${message}`);
    }
  }

  // Parameter summary so the AI can see what it sent — rendered under the SAME per-argument
  // policy as the tool_call log line (withToolTelemetry): an argument declared 'keys' shows only
  // its property names here too, so a clear-text config value never travels in an error text
  // (the log quotes the first lines of this text).
  const argsSummary = summarizeArgs(args, getCurrentArgPolicy());
  if (argsSummary) {
    parts.push(`**Parameters used**:\n${Object.entries(argsSummary).map(([k, v]) => `  ${k}: ${v}`).join('\n')}`);
  }

  return {
    isError: true,
    content: [{ type: 'text' as const, text: parts.join('\n\n') }],
  };
}
