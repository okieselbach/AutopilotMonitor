import { runWithToolName, hasGlobalScope, isDelegated } from './client.js';

export const toolLoggingEnabled = process.env.MCP_TOOL_LOGGING === 'true';

// Caps keep a single log line small no matter what the model sends (validate_rule
// takes whole rule JSON objects; queries are free text).
const ARG_VALUE_CAP = 200;
const ARGS_TOTAL_CAP = 1500;
const QUERY_CAP = 300;
const ERROR_MESSAGE_CAP = 300;

function cap(s: string, max: number): string {
  return s.length > max ? s.slice(0, max) + `…(+${s.length - max})` : s;
}

/**
 * Per-argument logging policy, declared at the tool's withToolTelemetry call
 * site so schema, handler and log policy are reviewed together.
 *  - 'keys': log only the property NAMES of an object-valued argument. For
 *    update_tenant_config.fields this keeps the useful signal (which fields a
 *    Global Admin changes) while the clear-text values (webhook URLs, SAS URLs,
 *    custom headers) never reach the container log stream — the backend keeps
 *    them in the backup snapshot and the audit log, both GA-gated.
 *  - 'drop': omit the argument entirely (for arguments whose key alone reveals
 *    something, e.g. a plain-string secret).
 * Arguments without a policy are rendered verbatim (capped).
 */
export type ArgPolicy = Record<string, 'keys' | 'drop'>;

function renderKeysOnly(value: unknown): string {
  if (Array.isArray(value)) return `[array:${value.length}]`;
  if (typeof value === 'object' && value !== null) return Object.keys(value).join(',');
  return `[${typeof value}]`;
}

/**
 * Compact, size-bounded view of the tool arguments for the log line. Values are
 * individually capped and the whole summary is bounded, so a pathological arg
 * object can never bloat a log line. Null/undefined entries are dropped.
 */
export function summarizeArgs(
  args: Record<string, unknown>,
  policy?: ArgPolicy,
): Record<string, string> | undefined {
  const out: Record<string, string> = {};
  let total = 0;
  for (const [key, value] of Object.entries(args)) {
    if (value === null || value === undefined) continue;
    const mode = policy?.[key];
    if (mode === 'drop') continue;
    const rendered =
      mode === 'keys' ? cap(renderKeysOnly(value), ARG_VALUE_CAP)
      : typeof value === 'string' ? cap(value, ARG_VALUE_CAP)
      : cap(JSON.stringify(value) ?? '', ARG_VALUE_CAP);
    total += key.length + rendered.length;
    if (total > ARGS_TOTAL_CAP) {
      out['…'] = 'args summary truncated';
      break;
    }
    out[key] = rendered;
  }
  return Object.keys(out).length > 0 ? out : undefined;
}

/** Caller scope for usage analysis — which audience a tool struggle belongs to. */
function callerScope(): 'ga' | 'delegated' | 'tenant' {
  if (hasGlobalScope()) return 'ga';
  if (isDelegated()) return 'delegated';
  return 'tenant';
}

interface ToolResultShape {
  isError?: boolean;
  content?: Array<{ type?: string; text?: string }>;
  _meta?: Record<string, unknown>;
}

/**
 * Wraps an MCP tool handler to:
 * 1. Always: propagate the tool name via AsyncLocalStorage so apiFetch sends
 *    the X-MCP-Tool-Name header to the backend (tracked in App Insights).
 * 2. Optionally (MCP_TOOL_LOGGING=true): emit one structured JSON line per call
 *    to stderr, queryable via Container App Logs in Azure Monitor. Beyond
 *    duration, the line carries the quality signals backend telemetry cannot
 *    see: soft errors (handlers never throw — toolError RETURNS `isError:
 *    true`), result size vs. the tool's inline-size cap (overCap = the host
 *    will truncate), a size-bounded argument summary, and the caller scope.
 *    This is also the ONLY telemetry for tools that never call the backend
 *    (search_docs, search_knowledge ranking, validate_rule, get_resource).
 */
export async function withToolTelemetry<T>(
  toolName: string,
  args: Record<string, unknown>,
  fn: () => T | Promise<T>,
  argPolicy?: ArgPolicy,
): Promise<T> {
  if (!toolLoggingEnabled) {
    return runWithToolName(toolName, fn) as Promise<T>;
  }

  const start = Date.now();
  let threw = false;
  let thrownMessage: string | undefined;
  let result: T | undefined;
  try {
    result = await runWithToolName(toolName, fn);
    return result;
  } catch (err) {
    threw = true;
    thrownMessage = err instanceof Error ? err.message : String(err);
    throw err;
  } finally {
    try {
      const r = result as ToolResultShape | undefined;
      const resultChars = r?.content?.reduce((sum, c) => sum + (c.text?.length ?? 0), 0) ?? 0;
      const capValue = Number(r?._meta?.['anthropic/maxResultSizeChars']);
      const isError = threw || r?.isError === true;
      // What actually failed — without this every error drilldown ends at
      // guessing from args. Soft errors carry the toolError text (its first
      // lines name the error class: HTTP status / timeout / auth / not found).
      const errorMessage = !isError
        ? undefined
        : cap(thrownMessage ?? r?.content?.find((c) => c.type === 'text')?.text ?? '', ERROR_MESSAGE_CAP);
      console.error(JSON.stringify({
        type: 'tool_call',
        tool: toolName,
        durationMs: Date.now() - start,
        isError,
        errorMessage,
        resultChars,
        // Result exceeds the inline-size hint → the host truncates it. A tool
        // that is frequently overCap needs tighter defaults or projections.
        overCap: Number.isFinite(capValue) && capValue > 0 ? resultChars > capValue : false,
        scope: callerScope(),
        args: summarizeArgs(args, argPolicy),
        timestamp: new Date().toISOString(),
      }));
    } catch {
      // Telemetry must never break a tool response.
    }
  }
}

/**
 * Zero-hit search log — the "does content/a tool for this exist at all?" signal.
 * Reviewing these queries periodically is the most direct way to find unmet
 * demand (missing docs, missing knowledge-base rules, missing tools).
 */
export function logSearchZeroHit(tool: string, query: string, detail?: Record<string, unknown>): void {
  if (!toolLoggingEnabled) return;
  try {
    console.error(JSON.stringify({
      type: 'search_zero_hit',
      tool,
      query: cap(query, QUERY_CAP),
      ...detail,
      timestamp: new Date().toISOString(),
    }));
  } catch {
    // Telemetry must never break a tool response.
  }
}

/**
 * JSON-RPC-level tool-call rejection (Zod arg validation, unknown tool). These
 * never reach a tool handler — the SDK rejects them before dispatch — so
 * without this log the strongest "the schema/description confuses the model"
 * signal is invisible everywhere.
 */
export function logToolCallRejection(toolName: string, errorCode: number, message: string): void {
  if (!toolLoggingEnabled) return;
  try {
    console.error(JSON.stringify({
      type: 'tool_call_rejected',
      tool: toolName,
      errorCode,
      message: cap(message, 500),
      timestamp: new Date().toISOString(),
    }));
  } catch {
    // Telemetry must never break a tool response.
  }
}

// Error envelopes are tiny; anything larger is a successful tool result and is
// skipped instead of buffered.
const SNIFF_BUFFER_CAP = 10_000;

interface SniffableResponse {
  write: (...args: never[]) => boolean;
  end: (...args: never[]) => unknown;
}

/**
 * Observe the JSON-RPC response of a tools/call POST for a rejection and log
 * it via logToolCallRejection. Two shapes count as a rejection: a JSON-RPC
 * error envelope, and — the shape SDK 1.30 actually produces for Zod/unknown-
 * tool failures — a CallToolResult with isError:true whose text starts with
 * `MCP error <code>: `. The SDK bridges the web-standard Response to the Node
 * res through Hono's getRequestListener, which depending on the body type
 * either calls res.end(string | Uint8Array) directly or pipes a ReadableStream
 * through res.write chunks followed by a bare res.end() — so BOTH must be
 * captured. Buffering stops at SNIFF_BUFFER_CAP (real rejections are a few
 * hundred bytes). Never throws into the response path.
 */
export function attachToolCallRejectionSniffer(toolName: string, res: SniffableResponse): void {
  if (!toolLoggingEnabled) return;

  let captured = '';
  let overflowed = false;
  const capture = (chunk: unknown): void => {
    if (overflowed || chunk == null) return;
    let text: string | undefined;
    if (typeof chunk === 'string') text = chunk;
    // Covers Node Buffers too (Buffer extends Uint8Array).
    else if (chunk instanceof Uint8Array) text = Buffer.from(chunk.buffer, chunk.byteOffset, chunk.byteLength).toString('utf8');
    if (text === undefined) return;
    captured += text;
    if (captured.length > SNIFF_BUFFER_CAP) {
      overflowed = true;
      captured = '';
    }
  };

  const originalWrite = res.write.bind(res);
  const originalEnd = res.end.bind(res);
  res.write = ((chunk: unknown, ...rest: unknown[]) => {
    try {
      capture(chunk);
    } catch {
      // Sniffing must never break the response.
    }
    return originalWrite(chunk as never, ...(rest as never[]));
  }) as typeof res.write;
  res.end = ((chunk?: unknown, ...rest: unknown[]) => {
    try {
      capture(chunk);
      if (!overflowed && (captured.includes('"error"') || captured.includes('"isError"'))) {
        const parsed = JSON.parse(captured) as {
          error?: { code?: number; message?: unknown };
          result?: { isError?: boolean; content?: Array<{ type?: string; text?: string }> };
        };
        if (parsed?.error?.code !== undefined) {
          // Protocol-level JSON-RPC error envelope (e.g. malformed request).
          logToolCallRejection(toolName, parsed.error.code, String(parsed.error.message ?? ''));
        } else if (parsed?.result?.isError === true) {
          // The SDK catches every McpError thrown before/around the handler
          // (Zod input validation, unknown/disabled tool) and wraps it as a
          // CallToolResult whose text is `MCP error <code>: <message>` — NOT
          // as a JSON-RPC error envelope. Handler-produced soft errors
          // (toolError) never start with that prefix, so they are not
          // double-logged here (withToolTelemetry already records them).
          const text = parsed.result.content?.find((c) => c.type === 'text')?.text ?? '';
          const match = /^MCP error (-?\d+): /.exec(text);
          if (match) {
            logToolCallRejection(toolName, Number(match[1]), text.slice(match[0].length));
          }
        }
      }
    } catch {
      // Sniffing must never break the response.
    }
    return originalEnd(chunk as never, ...(rest as never[]));
  }) as typeof res.end;
}
