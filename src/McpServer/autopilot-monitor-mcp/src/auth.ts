/**
 * Auth module for the remote MCP server.
 *
 * The MCP client obtains a Bearer token through this server's OAuth proxy (oauth.ts) and sends it
 * with each MCP request. This module reads the JWT claims; the token itself is validated by the
 * backend.
 *
 * Token model — a DELIBERATE deviation from the MCP authorization spec, recorded as a decision in
 * the internal decision register: the token's audience is the backend API app (access_as_user),
 * not this server, and the same token is forwarded to the backend. The spec requires an MCP
 * server to accept only tokens issued for itself and never to transit them downstream. The
 * compensations: the backend validates signature, issuer, audience and lifetime on EVERY
 * forwarded call (this server never treats a token as valid on its own), /api/auth/mcp decides
 * MCP eligibility per principal, and every tool reaches only its own backend path (followNextLink
 * basePath pinning). Audience separation would need a second app registration with consent in
 * every customer tenant plus an on-behalf-of exchange, and would break the app-only
 * service-principal path — not built until a reviewer requires it.
 */

import { createDecoder } from './jwt-decode.js';

export interface TokenClaims {
  /** User Principal Name (email) — absent on an app-only token. */
  upn?: string;
  /** Azure AD Object ID */
  oid?: string;
  /** Tenant ID */
  tid?: string;
  /** Token expiry (unix timestamp) */
  exp?: number;
  /**
   * "app" on an app-only token (client credentials — a service principal, typically behind a federated
   * credential). Optional claim the API app registration emits; absent on every user token.
   */
  idtyp?: string;
  /** Calling application's client id on a v1.0 app-only token. */
  appid?: string;
  /** Calling application's client id on a v2.0 token. */
  azp?: string;
  /**
   * Audience. Parsed for observability/diagnostics but intentionally NOT validated here — the
   * backend enforces it cryptographically alongside the signature/issuer/lifetime checks
   * (`AuthenticationMiddleware.cs`: `ValidateAudience = true`, `ValidAudiences = { clientId,
   * api://clientId }`); duplicating that gate here would only add a second, drift-prone copy of
   * the accepted-audience list. The audience is the backend API, not this server — see the
   * module header for why that deviation from the MCP spec is deliberate.
   */
  aud?: string;
}

const decode = createDecoder();

/**
 * Extracts claims from a JWT access token without cryptographic validation.
 * Full validation (signature, issuer, audience) is deferred to the backend API
 * which receives the same token. This avoids duplicating JWKS/OIDC config here.
 */
export function extractTokenClaims(token: string): TokenClaims | null {
  try {
    return decode(token);
  } catch {
    return null;
  }
}

/** Prefix of the principal key a service principal is granted under (mirrors Constants.PrincipalKeys). */
export const APPLICATION_KEY_PREFIX = 'app:';

/**
 * The caller's principal key — the value the backend keys every role table on and reports back as
 * `upn` from `auth/mcp`: a person's UPN (lowercase), or `app:<client-id>` for an app-only token
 * (`idtyp === 'app'` plus `appid` / `azp`). Undefined when the token names no principal at all; a token
 * without `idtyp` is a person by definition (fail-closed classification, same rule as the backend).
 */
export function principalKeyOf(claims: TokenClaims): string | undefined {
  if (claims.upn) return claims.upn.toLowerCase();
  if (claims.idtyp?.toLowerCase() !== 'app') return undefined;
  const applicationId = (claims.appid ?? claims.azp)?.trim().toLowerCase();
  return applicationId ? `${APPLICATION_KEY_PREFIX}${applicationId}` : undefined;
}

export function isApplicationKey(principalKey: string | undefined): boolean {
  return principalKey?.startsWith(APPLICATION_KEY_PREFIX) === true;
}

/**
 * Checks if a token is expired (with 60s buffer).
 */
export function isTokenExpired(claims: TokenClaims): boolean {
  if (!claims.exp) return true;
  return Date.now() / 1000 > claims.exp - 60;
}
