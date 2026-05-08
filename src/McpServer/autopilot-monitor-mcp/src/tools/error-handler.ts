import { ApiError } from '../client.js';

interface ToolErrorResult {
  [x: string]: unknown;
  isError: true;
  content: Array<{ type: 'text'; text: string }>;
}

/**
 * Format any error into an MCP-compliant `{ isError: true }` response with
 * structured, AI-consumable details. Never throws — always returns a result
 * the SDK can send back to the model.
 *
 * Handles:
 * - Structured backend errors (ApiError with parsed JSON body)
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
    // can carry internal fingerprints (CLR exception types, stack frames,
    // hint strings that name internal services), and the model has no
    // legitimate reason to act on them. correlationId + errorCode stay —
    // those are operational handles the operator can pivot on, not
    // internals.
    parts.push(`**Backend error in ${toolName}** (HTTP ${error.status}): the server returned an error.`);
    if (error.parsed?.correlationId) parts.push(`**Correlation ID**: ${error.parsed.correlationId}`);
    if (error.parsed?.errorCode) parts.push(`**Error code**: ${error.parsed.errorCode}`);
    parts.push('**Suggestion**: retry in a few seconds; if persistent, ask an operator to inspect backend logs.');
  } else if (error instanceof ApiError && error.parsed) {
    // Structured backend error (4xx) — extract rich details
    const p = error.parsed;
    parts.push(`**Error in ${toolName}**: ${p.error ?? error.message}`);
    if (p.hint) parts.push(`**Suggestion**: ${p.hint}`);
    if (p.correlationId) parts.push(`**Correlation ID**: ${p.correlationId}`);
    if (p.exceptionType) parts.push(`**Exception type**: ${p.exceptionType}`);
    if (p.errorCode) parts.push(`**Error code**: ${p.errorCode}`);
  } else if (error instanceof ApiError) {
    // API error but non-JSON body
    if (error.status === 403) {
      parts.push(`**Access denied in ${toolName}**: This operation requires higher permissions (Global Admin or Tenant Admin).`);
    } else if (error.status === 404) {
      parts.push(`**Not found in ${toolName}**: The requested resource does not exist. Verify IDs, table names, or filters.`);
    } else if (error.status === 429) {
      parts.push(`**Rate limited in ${toolName}**: Too many requests. Wait a moment and retry.`);
    } else {
      const body = error.body || 'No response body';
      const truncated = body.length > 500 ? body.slice(0, 500) + '…' : body;
      parts.push(`**Error in ${toolName}** (HTTP ${error.status}): ${truncated}`);
    }
  } else {
    const message = error instanceof Error ? error.message : String(error);
    if (message.includes('No authentication token')) {
      parts.push(`**Authentication error in ${toolName}**: ${message}`);
      parts.push('**Suggestion**: The MCP session may have expired. Re-authenticate.');
    } else if (message.includes('TimeoutError') || message.includes('AbortError') || message.includes('timed out')) {
      parts.push(`**Timeout in ${toolName}**: The backend did not respond in time.`);
      parts.push('**Suggestion**: Try narrowing the query (smaller date range, fewer results, more specific filters).');
    } else {
      parts.push(`**Error in ${toolName}**: ${message}`);
    }
  }

  // Include parameter summary so the AI can see what it sent
  const argsSummary = Object.entries(args)
    .filter(([, v]) => v != null && v !== undefined)
    .map(([k, v]) => `  ${k}: ${JSON.stringify(v)}`)
    .join('\n');
  if (argsSummary) {
    parts.push(`**Parameters used**:\n${argsSummary}`);
  }

  return {
    isError: true,
    content: [{ type: 'text' as const, text: parts.join('\n\n') }],
  };
}
