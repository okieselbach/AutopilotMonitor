/**
 * /oauth/token relays Entra's error body through an allowlist: the OAuth error code, the human
 * message (cut before the trace-id tail, capped) and the numeric AADSTS codes. Trace, correlation,
 * timestamp, error_uri and claims never reach an unauthenticated caller (review R-5).
 */
import { describe, it, expect } from 'vitest';

// oauth.ts throws at import unless the Entra client id is present.
process.env.AUTOPILOT_ENTRA_CLIENT_ID ??= '00000000-0000-0000-0000-000000000000';
const { sanitizeTokenErrorBody } = await import('../oauth.js');

const ENTRA_INVALID_GRANT = {
  error: 'invalid_grant',
  error_description:
    'AADSTS70008: The provided authorization code or refresh token has expired due to inactivity. ' +
    'Send a new interactive authorization request for this user and resource. Trace ID: 5d1a2b3c-0000-4000-8000-000000000000\r\n' +
    'Correlation ID: 7e9f0a1b-0000-4000-8000-000000000000\r\nTimestamp: 2026-09-10 00:00:00Z',
  error_codes: [70008],
  timestamp: '2026-09-10 00:00:00Z',
  trace_id: '5d1a2b3c-0000-4000-8000-000000000000',
  correlation_id: '7e9f0a1b-0000-4000-8000-000000000000',
  error_uri: 'https://login.microsoftonline.com/error?code=70008',
  claims: '{"access_token":{"capolids":{"essential":true,"values":["x"]}}}',
};

describe('sanitizeTokenErrorBody', () => {
  it('keeps the OAuth error, the message before the trace tail, and the numeric codes only', () => {
    expect(sanitizeTokenErrorBody(ENTRA_INVALID_GRANT)).toEqual({
      error: 'invalid_grant',
      error_description:
        'AADSTS70008: The provided authorization code or refresh token has expired due to inactivity. ' +
        'Send a new interactive authorization request for this user and resource.',
      error_codes: [70008],
    });
  });

  it('never carries trace, correlation, timestamp, error_uri or claims', () => {
    const out = sanitizeTokenErrorBody(ENTRA_INVALID_GRANT);
    for (const key of ['trace_id', 'correlation_id', 'timestamp', 'error_uri', 'claims']) expect(out).not.toHaveProperty(key);
    expect(JSON.stringify(out)).not.toContain('5d1a2b3c');
    expect(JSON.stringify(out)).not.toContain('7e9f0a1b');
  });

  it('maps an unknown error code to invalid_request', () => {
    expect(sanitizeTokenErrorBody({ error: 'something_entra_specific', error_description: 'x' }).error).toBe('invalid_request');
  });

  it('caps a long description', () => {
    const out = sanitizeTokenErrorBody({ error: 'invalid_request', error_description: 'y'.repeat(1000) });
    expect((out.error_description as string).length).toBe(303);
    expect(out.error_description as string).toMatch(/\.\.\.$/);
  });

  it('tolerates a non-object body and non-numeric codes', () => {
    expect(sanitizeTokenErrorBody('not json')).toEqual({ error: 'invalid_request' });
    expect(sanitizeTokenErrorBody({ error: 'invalid_client', error_codes: ['7000218', 7000218] })).toEqual({
      error: 'invalid_client',
      error_codes: [7000218],
    });
  });
});
