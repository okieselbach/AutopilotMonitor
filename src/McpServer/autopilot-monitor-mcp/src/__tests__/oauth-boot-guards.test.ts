/**
 * Boot guards for operator-supplied OAuth origins (review findings 08-15).
 *
 * validateAuthority / validatePublicUrl run at module scope against env values,
 * so a bad AUTOPILOT_ENTRA_AUTHORITY or MCP_PUBLIC_URL fails the boot instead
 * of shipping a proxy that sends the client secret / advertises an issuer over
 * plain http. These are misconfiguration guards (env is operator-controlled),
 * matching the fail-fast posture of AUTOPILOT_API_URL and the MCP_PUBLIC_URL
 * production pin in config.ts.
 */
import { describe, it, expect } from 'vitest';

// oauth.ts throws at import unless the Entra client id is present.
process.env.AUTOPILOT_ENTRA_CLIENT_ID ??= '00000000-0000-0000-0000-000000000000';
const { validateAuthority } = await import('../oauth.js');
const { validatePublicUrl } = await import('../config.js');

describe('validateAuthority — Entra authority must be https', () => {
  it('accepts the default public-cloud authority and strips trailing slashes', () => {
    expect(validateAuthority('https://login.microsoftonline.com/organizations/'))
      .toBe('https://login.microsoftonline.com/organizations');
  });

  it('accepts sovereign-cloud authorities (deliberately not host-pinned)', () => {
    expect(validateAuthority('https://login.microsoftonline.us/organizations'))
      .toBe('https://login.microsoftonline.us/organizations');
  });

  it('rejects a plain-http authority (secret + auth codes would transit unencrypted)', () => {
    expect(() => validateAuthority('http://login.microsoftonline.com/organizations'))
      .toThrow(/must be an https:\/\/ URL/);
  });

  it('rejects an unparseable value', () => {
    expect(() => validateAuthority('not a url')).toThrow(/not a valid URL/);
  });
});

describe('validatePublicUrl — MCP_PUBLIC_URL pin must be https (loopback-http tolerated)', () => {
  it('accepts an https pin and strips trailing slashes (consumers do `${base}/mcp` joins)', () => {
    expect(validatePublicUrl('https://mcp.example.net/')).toBe('https://mcp.example.net');
  });

  it.each(['http://localhost:3000', 'http://127.0.0.1:8080'])(
    'accepts %s (local dev loopback)',
    (uri) => {
      expect(validatePublicUrl(uri)).toBe(uri);
    },
  );

  it('rejects a plain-http non-loopback pin (would advertise a downgradeable issuer)', () => {
    expect(() => validatePublicUrl('http://mcp.example.net')).toThrow(/must be an https:\/\/ URL/);
  });

  it('rejects an unparseable value', () => {
    expect(() => validatePublicUrl('not a url')).toThrow(/not a valid URL/);
  });
});
