/**
 * Auth module for the remote MCP server.
 *
 * In remote mode, the Claude Code client handles OAuth and sends a Bearer token
 * with each MCP request. This module provides helpers for token validation and
 * extracting user info from the JWT claims.
 *
 * The user's token is passed through to the backend API (access_as_user scope),
 * so no service principal or separate credentials are needed.
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
   * Audience. Parsed for observability/diagnostics but intentionally NOT
   * validated here — the same Bearer token is passed through to the backend
   * API, which enforces the audience cryptographically alongside the
   * signature/issuer/lifetime checks. See
   * `src/Backend/AutopilotMonitor.Functions/Middleware/AuthenticationMiddleware.cs:200`
   * (`ValidateAudience = true`, `ValidAudiences = { clientId, api://clientId }`).
   * Duplicating that gate here would only add a second, drift-prone copy of the
   * accepted-audience list (RFC 8707 resource indicators are honored by the
   * backend, not the proxy). This is spec-conformant token pass-through: the
   * user's token is forwarded to the resource it was issued for.
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
