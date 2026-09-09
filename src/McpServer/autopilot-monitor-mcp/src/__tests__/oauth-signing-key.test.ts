/**
 * OAuthStateSigningKey boot guard: production refuses to boot without the key (the signed
 * client_ids and the OAuth state derive from it and must survive restarts and replicas); the
 * per-process random fallback exists for development and tests only; a short key fails everywhere.
 */
import { describe, it, expect } from 'vitest';

// oauth.ts throws at import unless the Entra client id is present.
process.env.AUTOPILOT_ENTRA_CLIENT_ID ??= '00000000-0000-0000-0000-000000000000';
const { loadSigningKey } = await import('../oauth.js');

const KEY = Buffer.alloc(32, 7).toString('base64');

describe('loadSigningKey', () => {
  it('decodes a valid base64 key of 32+ bytes in production', () => {
    expect(loadSigningKey(KEY, 'production').length).toBe(32);
  });

  it('refuses to boot in production without a key', () => {
    expect(() => loadSigningKey(undefined, 'production')).toThrow('OAuthStateSigningKey is not set');
    expect(() => loadSigningKey('', 'production')).toThrow('OAuthStateSigningKey is not set');
  });

  it('falls back to a fresh random key outside production', () => {
    const a = loadSigningKey(undefined, 'test');
    const b = loadSigningKey(undefined, undefined);
    expect(a.length).toBe(32);
    expect(b.length).toBe(32);
    expect(a.equals(b)).toBe(false);
  });

  it('rejects a key shorter than 32 bytes in every environment', () => {
    expect(() => loadSigningKey(Buffer.alloc(16).toString('base64'), 'test')).toThrow('32 bytes');
    expect(() => loadSigningKey('not base64 at all', 'production')).toThrow('32 bytes');
  });
});
