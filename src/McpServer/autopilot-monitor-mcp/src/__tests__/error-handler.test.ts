import { describe, it, expect } from 'vitest';
import { toolError } from '../tools/error-handler.js';
import { ApiError } from '../client.js';

describe('toolError', () => {
  it('detects a real AbortSignal.timeout DOMException (name TimeoutError, non-matching message)', () => {
    // This is exactly what fetch rejects with under AbortSignal.timeout(): the
    // message does NOT contain "TimeoutError", only the name does — so a
    // message-only match would miss it.
    const reason = new DOMException('The operation was aborted due to timeout', 'TimeoutError');
    const res = toolError('get_session', {}, reason);
    expect(res.isError).toBe(true);
    expect(res.content[0].text).toContain('Timeout in get_session');
    expect(res.content[0].text).toContain('narrowing the query');
  });

  it('detects an AbortError DOMException', () => {
    const reason = new DOMException('The operation was aborted', 'AbortError');
    const res = toolError('query_table', {}, reason);
    expect(res.content[0].text).toContain('Timeout in query_table');
  });

  it('does NOT leak exceptionType (CLR type) from a structured 4xx body', () => {
    const err = new ApiError(400, JSON.stringify({
      error: 'Bad request',
      exceptionType: 'System.ArgumentException',
      correlationId: 'abc-123',
      errorCode: 'E42',
    }));
    const res = toolError('search_sessions', {}, err);
    const text = res.content[0].text;
    expect(text).toContain('Bad request');
    expect(text).toContain('abc-123');
    expect(text).toContain('E42');
    expect(text).not.toContain('System.ArgumentException');
    expect(text).not.toContain('Exception type');
  });

  it('gives a friendly 401 message for an unstructured auth failure', () => {
    const err = new ApiError(401, 'Unauthorized');
    const res = toolError('get_metrics', {}, err);
    expect(res.content[0].text).toContain('Authentication required in get_metrics');
    expect(res.content[0].text).toContain('Re-authenticate');
  });

  it('sanitizes 5xx to a generic message but keeps operational handles', () => {
    const err = new ApiError(500, JSON.stringify({
      error: 'boom',
      exceptionType: 'System.NullReferenceException',
      correlationId: 'cid-9',
      errorCode: 'E500',
    }));
    const res = toolError('get_session', {}, err);
    const text = res.content[0].text;
    expect(text).not.toContain('System.NullReferenceException');
    expect(text).not.toContain('boom');
    expect(text).toContain('cid-9');
    expect(text).toContain('E500');
  });

  it('renders the backend MCP quota 429 body instead of "undefined"', () => {
    // McpQuotaExceededResponse carries no `error` key — before this branch the structured-4xx path
    // printed "Error in …: undefined" and the model retried into the same wall.
    const err = new ApiError(429, JSON.stringify({
      quotaExceeded: true,
      plan: 'community',
      scope: 'daily',
      level: 'user',
      limit: 100,
      used: 100,
      resetUtc: '2026-09-03T00:00:00Z',
      message: "MCP daily request quota exceeded for plan 'community'. Resets at 2026-09-03T00:00:00Z.",
    }));
    const res = toolError('search_sessions', {}, err);
    const text = res.content[0].text;
    expect(text).toContain('Quota exceeded in search_sessions');
    expect(text).toContain("quota exceeded for plan 'community'");
    expect(text).toContain('100 of 100 requests used');
    expect(text).toContain('2026-09-03T00:00:00Z');
    expect(text).toContain('larger usage plan');
    expect(text).not.toContain('undefined');
  });

  it('names the organization when the tenant-wide quota is exhausted', () => {
    const err = new ApiError(429, JSON.stringify({
      quotaExceeded: true,
      plan: 'power',
      scope: 'monthly',
      level: 'tenant',
      limit: 60000,
      used: 60000,
      resetUtc: '2026-10-01T00:00:00Z',
      message: "MCP monthly request quota of your organization exceeded (tenant plan 'pro', shared by all its members). Resets at 2026-10-01T00:00:00Z.",
    }));
    const res = toolError('get_session', {}, err);
    const text = res.content[0].text;
    expect(text).toContain('of your organization exceeded');
    expect(text).toContain('tenant level');
    expect(text).toContain('shared by every member of the tenant');
    expect(text).not.toContain('larger usage plan');
  });
});
