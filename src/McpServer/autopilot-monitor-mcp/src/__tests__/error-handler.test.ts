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
});
