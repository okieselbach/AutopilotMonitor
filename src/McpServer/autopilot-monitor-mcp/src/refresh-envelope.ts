/**
 * Sealed refresh tokens for tenant-bound clients (portal registrations, D-280 / D-283).
 *
 * A self-hosted client keeps its users' refresh tokens in its own database. An Entra refresh token is
 * bound to user and app but not to a tenant, and the proxy's public token endpoint adds the app's
 * client secret for every caller — so a raw token copied out of that database could be redeemed
 * without the client's `client_id`, past the registration check, and deleting the registration would
 * stop the client but not the copy. The proxy therefore hands such a client its refresh token only
 * sealed: a compact JWE (RFC 7516, `dir` + `A256GCM`) carrying the Entra token together with the
 * registration id. Only this server can open it, and at refresh the registration comes from the
 * envelope, never from a request parameter — a deleted registration ends every token issued through it.
 *
 * Encrypted, not merely signed: a signed envelope would expose the raw Entra token, which is
 * redeemable on its own. Stateless like the signed client_id: the key is derived from
 * OAuthStateSigningKey (oauth.ts), so any replica opens any envelope, and rotating that secret ends
 * every sealed token (the users sign in again). Access and ID tokens are untouched Entra JWTs.
 */
import { CompactEncrypt, compactDecrypt, decodeProtectedHeader } from 'jose';

/** Key id in the protected header; a later key change adds a second id instead of breaking the first. */
const ENVELOPE_KID = 'amc-rt-v1';
const REGISTRATION_ID = /^[0-9a-f]{32}$/;

export interface OpenedRefreshToken {
  /** The Entra refresh token. */
  refreshToken: string;
  /** The portal registration the token was issued through. */
  registrationId: string;
}

/**
 * True for a value this server sealed — a five-part compact JWE with our key id. Anything else
 * (an Entra refresh token, garbage) is not an envelope and takes the unsealed path.
 */
export function isSealedRefreshToken(value: string): boolean {
  if (value.split('.').length !== 5) return false;
  try {
    return decodeProtectedHeader(value).kid === ENVELOPE_KID;
  } catch {
    return false;
  }
}

export async function sealRefreshToken(key: Uint8Array, refreshToken: string, registrationId: string): Promise<string> {
  const payload = new TextEncoder().encode(JSON.stringify({ rt: refreshToken, reg: registrationId }));
  return new CompactEncrypt(payload)
    .setProtectedHeader({ alg: 'dir', enc: 'A256GCM', kid: ENVELOPE_KID })
    .encrypt(key);
}

/** The sealed token and its registration, or null for a tampered, foreign-key or malformed envelope. */
export async function openRefreshToken(key: Uint8Array, value: string): Promise<OpenedRefreshToken | null> {
  try {
    const { plaintext, protectedHeader } = await compactDecrypt(value, key, {
      keyManagementAlgorithms: ['dir'],
      contentEncryptionAlgorithms: ['A256GCM'],
    });
    if (protectedHeader.kid !== ENVELOPE_KID) return null;
    const parsed = JSON.parse(new TextDecoder().decode(plaintext)) as { rt?: unknown; reg?: unknown };
    if (typeof parsed.rt !== 'string' || !parsed.rt || typeof parsed.reg !== 'string' || !REGISTRATION_ID.test(parsed.reg)) {
      return null;
    }
    return { refreshToken: parsed.rt, registrationId: parsed.reg };
  } catch {
    return null;
  }
}
