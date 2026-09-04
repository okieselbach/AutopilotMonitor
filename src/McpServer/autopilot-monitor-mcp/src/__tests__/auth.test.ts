/**
 * Unit tests for the token-claim helpers (review finding tests-3 / tests-4).
 *
 * extractTokenClaims + isTokenExpired are the first gate every authenticated
 * request passes through in accessGuard. The code is correct; these tests pin
 * the contract so a regression (e.g. inverting the expiry comparison or losing
 * the skew buffer) fails loudly rather than silently weakening auth.
 *
 * No signature validation happens here by design — the backend re-validates the
 * same token. These helpers only decode + check the unix `exp`.
 */
import { describe, it, expect } from 'vitest';
import { extractTokenClaims, isApplicationKey, isTokenExpired, principalKeyOf } from '../auth.js';

/** Build an unsigned JWT-shaped string with the given payload claims. */
function makeToken(claims: Record<string, unknown>): string {
  const header = Buffer.from(JSON.stringify({ alg: 'none', typ: 'JWT' })).toString('base64url');
  const payload = Buffer.from(JSON.stringify(claims)).toString('base64url');
  return `${header}.${payload}.sig`;
}

const nowSec = () => Math.floor(Date.now() / 1000);

describe('extractTokenClaims', () => {
  it('decodes upn / oid / tid / exp / aud from a well-formed token', () => {
    const claims = extractTokenClaims(
      makeToken({
        upn: 'alice@contoso.com',
        oid: 'object-id-123',
        tid: 'tenant-id-456',
        exp: nowSec() + 3600,
        aud: 'api://00000000-0000-0000-0000-000000000000',
      }),
    );
    expect(claims).not.toBeNull();
    expect(claims!.upn).toBe('alice@contoso.com');
    expect(claims!.oid).toBe('object-id-123');
    expect(claims!.tid).toBe('tenant-id-456');
    expect(typeof claims!.exp).toBe('number');
  });

  it('surfaces the aud claim so it is observable (tests-4: parsed, not locally validated)', () => {
    // The proxy does not validate audience — the backend does. But it must
    // still decode it, e.g. for diagnostics. This pins that aud is parsed.
    const claims = extractTokenClaims(makeToken({ upn: 'a@b.c', aud: 'api://some-resource' }));
    expect(claims!.aud).toBe('api://some-resource');
  });

  it('returns null for a token that is not three dot-separated parts', () => {
    expect(extractTokenClaims('only.two')).toBeNull();
    expect(extractTokenClaims('no-dots-at-all')).toBeNull();
    expect(extractTokenClaims('a.b.c.d')).toBeNull();
    expect(extractTokenClaims('')).toBeNull();
  });

  it('returns null when the payload segment is not valid JSON', () => {
    const header = Buffer.from('{}').toString('base64url');
    const garbagePayload = Buffer.from('this-is-not-json').toString('base64url');
    expect(extractTokenClaims(`${header}.${garbagePayload}.sig`)).toBeNull();
  });

  it('returns null for completely malformed input', () => {
    expect(extractTokenClaims('!@#$%^&*()')).toBeNull();
  });
});

describe('isTokenExpired', () => {
  it('treats a token with no exp claim as expired (fail-closed)', () => {
    expect(isTokenExpired({})).toBe(true);
    expect(isTokenExpired({ upn: 'a@b.c' })).toBe(true);
  });

  it('reports a token whose exp is in the past as expired', () => {
    expect(isTokenExpired({ exp: nowSec() - 100 })).toBe(true);
  });

  it('reports a token expiring within the 60s skew buffer as expired', () => {
    // exp is technically still in the future, but inside the 60s buffer the
    // helper conservatively reports expired so a borderline token is not used.
    expect(isTokenExpired({ exp: nowSec() + 30 })).toBe(true);
  });

  it('reports a token comfortably in the future as not expired', () => {
    expect(isTokenExpired({ exp: nowSec() + 3600 })).toBe(false);
  });

  it('treats a token exactly at the skew boundary as still valid', () => {
    // now > exp - 60  ⇒  expired. At exp = now + 61, (exp - 60) = now + 1 > now,
    // so it is NOT expired. Pins the boundary direction.
    expect(isTokenExpired({ exp: nowSec() + 61 })).toBe(false);
  });
});

describe('principalKeyOf', () => {
  it('is the lowercased upn for a person', () => {
    expect(principalKeyOf({ upn: 'Alice@Contoso.Example' })).toBe('alice@contoso.example');
  });

  it('is app:<client-id> for an app-only token, from appid (v1) or azp (v2)', () => {
    expect(principalKeyOf({ idtyp: 'app', appid: 'AAAA-1' })).toBe('app:aaaa-1');
    expect(principalKeyOf({ idtyp: 'app', azp: 'BBBB-2' })).toBe('app:bbbb-2');
    expect(isApplicationKey('app:aaaa-1')).toBe(true);
    expect(isApplicationKey('alice@contoso.example')).toBe(false);
  });

  it('yields nothing for a token without idtyp or without a client id (fail-closed classification)', () => {
    expect(principalKeyOf({ appid: 'AAAA-1', oid: 'x' })).toBeUndefined();
    expect(principalKeyOf({ idtyp: 'app' })).toBeUndefined();
  });

  it('prefers the upn when a user token also names a client', () => {
    expect(principalKeyOf({ upn: 'alice@contoso.example', appid: 'portal-app' })).toBe('alice@contoso.example');
  });
});
