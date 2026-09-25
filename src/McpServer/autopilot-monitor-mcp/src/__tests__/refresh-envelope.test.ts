/**
 * Sealed refresh tokens (refresh-envelope.ts): a compact JWE only this server opens, recognised by its
 * key id, carrying the Entra refresh token and the registration it was issued through.
 */
import { describe, it, expect } from 'vitest';
import crypto from 'node:crypto';
import { CompactEncrypt, decodeProtectedHeader } from 'jose';
import { isSealedRefreshToken, openRefreshToken, sealRefreshToken } from '../refresh-envelope.js';

const KEY = crypto.randomBytes(32);
const REG_ID = '0123456789abcdef0123456789abcdef';
const ENTRA_RT = '1.AXoAqGnBexample-entra-refresh-token_with.dots';

async function jwe(payload: unknown, kid: string, key: Uint8Array = KEY): Promise<string> {
  return new CompactEncrypt(new TextEncoder().encode(JSON.stringify(payload)))
    .setProtectedHeader({ alg: 'dir', enc: 'A256GCM', kid })
    .encrypt(key);
}

describe('sealRefreshToken / openRefreshToken', () => {
  it('round-trips the Entra token and the registration id', async () => {
    const sealed = await sealRefreshToken(KEY, ENTRA_RT, REG_ID);
    expect(await openRefreshToken(KEY, sealed)).toEqual({ refreshToken: ENTRA_RT, registrationId: REG_ID });
  });

  it('is a standard compact JWE (dir + A256GCM) that does not carry the token in the clear', async () => {
    const sealed = await sealRefreshToken(KEY, ENTRA_RT, REG_ID);
    expect(sealed.split('.')).toHaveLength(5);
    expect(decodeProtectedHeader(sealed)).toEqual({ alg: 'dir', enc: 'A256GCM', kid: 'amc-rt-v1' });
    expect(sealed).not.toContain(ENTRA_RT);
    expect(Buffer.from(sealed.split('.')[3], 'base64url').toString('latin1')).not.toContain('AXoAqGnB');
  });

  it('opens nothing under another key', async () => {
    const sealed = await sealRefreshToken(crypto.randomBytes(32), ENTRA_RT, REG_ID);
    expect(await openRefreshToken(KEY, sealed)).toBeNull();
  });

  it('opens nothing that was tampered with', async () => {
    const parts = (await sealRefreshToken(KEY, ENTRA_RT, REG_ID)).split('.');
    parts[3] = (parts[3][0] === 'A' ? 'B' : 'A') + parts[3].slice(1);
    expect(await openRefreshToken(KEY, parts.join('.'))).toBeNull();
  });

  it('opens nothing under a foreign key id or with an incomplete payload', async () => {
    expect(await openRefreshToken(KEY, await jwe({ rt: ENTRA_RT, reg: REG_ID }, 'other'))).toBeNull();
    expect(await openRefreshToken(KEY, await jwe({ rt: ENTRA_RT }, 'amc-rt-v1'))).toBeNull();
    expect(await openRefreshToken(KEY, await jwe({ rt: ENTRA_RT, reg: 'not-a-registration' }, 'amc-rt-v1'))).toBeNull();
    expect(await openRefreshToken(KEY, await jwe({ reg: REG_ID }, 'amc-rt-v1'))).toBeNull();
  });
});

describe('isSealedRefreshToken', () => {
  it('recognises only an envelope with our key id', async () => {
    expect(isSealedRefreshToken(await sealRefreshToken(KEY, ENTRA_RT, REG_ID))).toBe(true);
    expect(isSealedRefreshToken(await jwe({ rt: ENTRA_RT, reg: REG_ID }, 'other'))).toBe(false);
  });

  it('leaves Entra refresh tokens, JWTs and garbage on the unsealed path', () => {
    expect(isSealedRefreshToken(ENTRA_RT)).toBe(false);
    expect(isSealedRefreshToken('eyJhbGciOiJSUzI1NiJ9.eyJ0aWQiOiJ4In0.sig')).toBe(false);
    expect(isSealedRefreshToken('a.b.c.d.e')).toBe(false);
    expect(isSealedRefreshToken('')).toBe(false);
  });
});
