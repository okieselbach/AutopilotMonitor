/**
 * OAuth 2.0 proxy for the remote MCP server.
 *
 * Instead of requiring localhost redirect URIs, the MCP server acts as an
 * OAuth authorization server proxy to Entra ID. Claude Code talks to our
 * endpoints, which redirect to/from Entra ID.
 *
 * Flow:
 *   1. Claude Code → POST /mcp → 401 (with WWW-Authenticate header)
 *   2. Claude Code → GET /.well-known/oauth-protected-resource → discovers auth server (RFC 9728)
 *   3. Claude Code → GET /.well-known/oauth-authorization-server → discovers endpoints (RFC 8414)
 *   4. Claude Code → POST /oauth/register → dynamic client registration (RFC 7591)
 *   5. Claude Code → GET /oauth/authorize → 302 to Entra ID /authorize
 *   6. User authenticates at Entra ID
 *   7. Entra ID → GET /oauth/callback → 302 back to Claude Code with code
 *   8. Claude Code → POST /oauth/token → proxied to Entra ID /token
 *   9. Claude Code → POST /mcp (with Bearer token) → MCP session established
 */
import { Router } from 'express';
import crypto from 'node:crypto';
import { oauthRateLimit, oauthTokenRateLimit } from './access-guard.js';
import { getPublicBaseUrl } from './config.js';

const CLIENT_ID: string = (() => {
  const v = process.env.AUTOPILOT_ENTRA_CLIENT_ID;
  if (!v) {
    throw new Error(
      'AUTOPILOT_ENTRA_CLIENT_ID environment variable is required. ' +
      'Set it to the Entra ID app registration client_id.',
    );
  }
  return v;
})();
const CLIENT_SECRET = process.env.AUTOPILOT_ENTRA_CLIENT_SECRET ?? '';
const AUTHORITY = process.env.AUTOPILOT_ENTRA_AUTHORITY ?? 'https://login.microsoftonline.com/organizations';
const SCOPES = `api://${CLIENT_ID}/access_as_user openid profile offline_access`;

// Dynamic client registration (RFC 7591) is STATELESS: instead of a
// server-side Map, the issued client_id is itself an HMAC-signed token that
// encodes the client's registered redirect_uris (see signClientId below).
//
// Why: the Container App is stateless with scale-to-zero and may run multiple
// replicas. An in-memory registry was wiped on every cold start and never
// shared across replicas, so a client_id minted by /oauth/register read as
// "unknown" at /oauth/authorize once the container slept or a sibling replica
// served the request — surfacing as "invalid_client". A self-describing signed
// client_id needs no shared storage: any replica holding the same signing key
// can verify it and recover the registered redirect_uris. This also removes
// the unbounded-memory concern that the old hard size cap guarded against.

// ---- State signing (HMAC) -------------------------------------------------
//
// /oauth/authorize stores its proxy state in the OAuth `state` parameter that
// transits the user-agent. Without integrity protection a tampered state can
// inject a chosen redirectUri / clientId pair into /oauth/callback. The
// downstream redirect_uri allowlist + per-client registry already block
// hostile destinations, but signing the state is a cheap belt-and-suspenders
// that also catches replay (via iat/exp) and detects accidental client bugs.
//
// HMAC key: prefer OAuthStateSigningKey from the environment so all replicas
// agree on the signature; fall back to a per-instance random key when unset
// (state has a 10 min lifetime, so per-instance is acceptable for single-
// replica scale=0..1). Format and naming match the backend's
// PaginationTokenSigningKey for consistency: PascalCase env var, base64-
// encoded random bytes, ≥32 bytes after decode. Generate on PowerShell:
//   [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
const OAUTH_STATE_SIGNING_KEY: Buffer = (() => {
  const raw = process.env.OAuthStateSigningKey;
  if (!raw) return crypto.randomBytes(32);
  // Buffer.from(s, 'base64') silently drops invalid chars — the only useful
  // signal is the resulting byte length. Anything below 32 bytes is either a
  // malformed input or a too-weak key; either way we want to fail loud at
  // boot rather than ship a degraded-security configuration.
  const decoded = Buffer.from(raw, 'base64');
  if (decoded.length < 32) {
    throw new Error(
      `OAuthStateSigningKey must be a base64-encoded value yielding ≥32 bytes after decode (got ${decoded.length}). ` +
      'Generate one with PowerShell: ' +
      '[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)) ' +
      "or Node: node -e \"console.log(require('crypto').randomBytes(32).toString('base64'))\"",
    );
  }
  return decoded;
})();
const STATE_MAX_AGE_SECONDS = 600;

interface ProxyStatePayload {
  originalState?: string;
  redirectUri: string;
  clientId: string | null;
  iat: number; // unix seconds
}

export function signState(payload: Omit<ProxyStatePayload, 'iat'>): string {
  const full: ProxyStatePayload = { ...payload, iat: Math.floor(Date.now() / 1000) };
  const json = JSON.stringify(full);
  const body = Buffer.from(json, 'utf-8').toString('base64url');
  const sig = crypto.createHmac('sha256', OAUTH_STATE_SIGNING_KEY).update(body).digest('base64url');
  return `${body}.${sig}`;
}

export function verifyState(state: string, nowSeconds: number = Math.floor(Date.now() / 1000)): ProxyStatePayload | null {
  const parts = state.split('.');
  if (parts.length !== 2) return null;
  const [body, sig] = parts;

  const expected = crypto.createHmac('sha256', OAUTH_STATE_SIGNING_KEY).update(body).digest('base64url');
  // Constant-time compare; lengths must match before timingSafeEqual.
  const sigBuf = Buffer.from(sig, 'utf-8');
  const expBuf = Buffer.from(expected, 'utf-8');
  if (sigBuf.length !== expBuf.length) return null;
  if (!crypto.timingSafeEqual(sigBuf, expBuf)) return null;

  let parsed: ProxyStatePayload;
  try {
    parsed = JSON.parse(Buffer.from(body, 'base64url').toString('utf-8')) as ProxyStatePayload;
  } catch {
    return null;
  }

  if (typeof parsed.iat !== 'number' || nowSeconds - parsed.iat > STATE_MAX_AGE_SECONDS) return null;
  if (typeof parsed.redirectUri !== 'string') return null;

  return parsed;
}

export const MAX_REDIRECT_URIS_PER_CLIENT = 10;
export const MAX_REDIRECT_URI_LENGTH = 1024;
export const MAX_CLIENT_NAME_LENGTH = 256;

/**
 * Makes a caller-controlled value safe for single-line text logs: strips
 * control characters (CR/LF would let a hostile client_name or redirect_uri
 * forge additional log lines) and caps the length. Everything user-supplied
 * that reaches console.error in this module must pass through here.
 */
export function sanitizeForLog(value: unknown, maxLength = 200): string {
  const s = String(value ?? '');
  // eslint-disable-next-line no-control-regex
  const cleaned = s.replace(/[\x00-\x1f\x7f]/g, '');
  return cleaned.length > maxLength ? `${cleaned.slice(0, maxLength)}...` : cleaned;
}

// ---- Client-id signing (HMAC) ---------------------------------------------
//
// The dynamic-registration client_id is a self-describing signed token rather
// than a lookup key into server-side storage. It carries the client's
// registered redirect_uris so /oauth/authorize and /oauth/callback can enforce
// the exact-match check without any shared registry — making the whole OAuth
// proxy stateless and multi-replica safe.
//
// The signing key is DERIVED from OAuthStateSigningKey via a labelled HMAC so
// it is domain-separated from the proxy-state key: a `state` token can never be
// replayed as a `client_id` (or vice versa) even though both use the same root
// secret. Deriving means one secret to manage; the `typ` discriminator inside
// each payload is a second, independent guard against cross-use.
const OAUTH_CLIENT_ID_KEY: Buffer = crypto
  .createHmac('sha256', OAUTH_STATE_SIGNING_KEY)
  .update('autopilot-mcp/oauth-client-id/v1')
  .digest();

interface ClientIdPayload {
  typ: 'client';
  redirectUris: string[];
  name: string;
  iat: number; // unix seconds — issued-at, for observability; not expired
}

/**
 * Mints a stateless client_id: `base64url(json).hmac`. Survives restarts and is
 * verifiable on any replica holding the same root secret. A public-client
 * client_id is not a credential (token_endpoint_auth_method=none) — it only
 * asserts which already-host-allowlisted redirect_uris were registered — so it
 * carries no expiry, matching RFC 7591's client_secret_expires_at=0 semantics.
 */
export function signClientId(redirectUris: string[], name: string): string {
  const payload: ClientIdPayload = {
    typ: 'client',
    redirectUris,
    name,
    iat: Math.floor(Date.now() / 1000),
  };
  const body = Buffer.from(JSON.stringify(payload), 'utf-8').toString('base64url');
  const sig = crypto.createHmac('sha256', OAUTH_CLIENT_ID_KEY).update(body).digest('base64url');
  return `${body}.${sig}`;
}

/** Verifies a signed client_id and returns its payload, or null if forged/malformed. */
export function verifyClientId(clientId: string | undefined | null): ClientIdPayload | null {
  if (!clientId) return null;
  const parts = clientId.split('.');
  if (parts.length !== 2) return null;
  const [body, sig] = parts;

  const expected = crypto.createHmac('sha256', OAUTH_CLIENT_ID_KEY).update(body).digest('base64url');
  const sigBuf = Buffer.from(sig, 'utf-8');
  const expBuf = Buffer.from(expected, 'utf-8');
  if (sigBuf.length !== expBuf.length) return null;
  if (!crypto.timingSafeEqual(sigBuf, expBuf)) return null;

  let parsed: ClientIdPayload;
  try {
    parsed = JSON.parse(Buffer.from(body, 'base64url').toString('utf-8')) as ClientIdPayload;
  } catch {
    return null;
  }
  if (parsed.typ !== 'client' || !Array.isArray(parsed.redirectUris)) return null;
  return parsed;
}

/**
 * Strict allowlist of redirect_uri templates. Defense-in-depth on top of the
 * per-client redirect_uri match: even if an attacker calls the unauthenticated
 * /oauth/register endpoint to register a client with a hostile redirect_uri,
 * the URI must still pass this filter.
 *
 * Loopback (localhost / 127.0.0.1 / [::1]) is always allowed for native CLI
 * flows (RFC 8252 §7.3). `localhost` is deliberately kept alongside the IP
 * literals — Claude Code and Gemini CLI both register `http://localhost:...`
 * callbacks, so an IP-literal-only rule would break real clients.
 *
 * Hosted-AI-client entries are configurable via the MCP_ALLOWED_REDIRECT_HOSTS
 * env var (comma-separated). When unset, the baseline below allows exactly the
 * documented callback URIs of widely-deployed AI vendors — full host+path, not
 * whole domains. Scoping to the vendor's dedicated OAuth callback path keeps an
 * open redirect elsewhere on the same domain (or a compromised sibling
 * subdomain) from being usable to exfiltrate authorization codes; PKCE does not
 * help in that scenario because the attacker runs the whole flow with their own
 * verifier.
 *
 * Entry syntax: `host` (any path — operator escape hatch), `host/path`
 * (exact path, trailing-slash tolerant), and `*.host` / `*.host/path` for
 * subdomain wildcards. The dot-anchored suffix check keeps `evilclaude.ai`
 * from slipping past `*.claude.ai`.
 */
const DEFAULT_ALLOWED_HOSTS: readonly string[] = [
  // Anthropic — Claude web / Desktop / mobile connector callback (both the
  // claude.ai and claude.com front doors). Claude Code uses RFC 8252 loopback,
  // which the loopback rule below already covers.
  'claude.ai/api/mcp/auth_callback',
  'claude.com/api/mcp/auth_callback',
  // OpenAI — ChatGPT connector-platform callback.
  'chatgpt.com/connector_platform_oauth_redirect',
  // Microsoft / VS Code (GitHub Copilot, Continue, etc.) — Stable and Insiders
  // web redirect endpoints. The loopback URI VS Code also registers
  // (http://127.0.0.1:<port>) is accepted by the loopback rule below.
  'vscode.dev/redirect',
  'insiders.vscode.dev/redirect',
  // Google Gemini has no documented hosted-web MCP callback; Gemini CLI uses
  // loopback redirects, which are always allowed. No entry needed.
];

export interface AllowedRedirectEntry {
  host: string; // lowercase, no leading `*.`
  wildcard: boolean; // true → match any subdomain of host (dot-anchored)
  path: string | null; // null → any path; else exact pathname (trailing-slash tolerant)
}

export function parseAllowedHosts(envValue: string | undefined): { entries: AllowedRedirectEntry[] } {
  const entries: AllowedRedirectEntry[] = [];
  const raw = envValue
    ? envValue.split(',').map((s) => s.trim()).filter(Boolean)
    : DEFAULT_ALLOWED_HOSTS;
  for (const item of raw) {
    let rest = item.toLowerCase();
    const wildcard = rest.startsWith('*.');
    if (wildcard) rest = rest.slice(2);
    const slash = rest.indexOf('/');
    const host = slash >= 0 ? rest.slice(0, slash) : rest;
    const path = slash >= 0 ? rest.slice(slash) : null;
    if (!host) continue;
    entries.push({ host, wildcard, path });
  }
  return { entries };
}

const ALLOWED_HOSTS = parseAllowedHosts(process.env.MCP_ALLOWED_REDIRECT_HOSTS);

if (process.env.MCP_ALLOWED_REDIRECT_HOSTS) {
  console.error(
    `[oauth] MCP_ALLOWED_REDIRECT_HOSTS set — using ${ALLOWED_HOSTS.entries.length} ` +
    'allowlist entrie(s) (loopback always allowed)',
  );
}

/** Exact pathname compare, tolerant of a single trailing slash on either side. */
function pathMatchesTemplate(templatePath: string, pathname: string): boolean {
  const norm = (p: string) => (p.length > 1 && p.endsWith('/') ? p.slice(0, -1) : p);
  return norm(templatePath) === norm(pathname);
}

/**
 * Internal worker that applies the host check against an arbitrary allowlist.
 * Exposed for tests so the ENV-override path can be exercised without
 * reloading the module (ENV is captured at import time).
 */
export function isAllowedRedirectUriWith(
  uri: string | undefined | null,
  allowed: { entries: AllowedRedirectEntry[] },
): boolean {
  if (!uri) return false;
  let parsed: URL;
  try {
    parsed = new URL(uri);
  } catch {
    return false;
  }
  // Reject anything other than http/https. Blocks file:, javascript:, data:, etc.
  if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return false;

  // Userinfo and fragments have no legitimate place in a redirect_uri
  // (RFC 6749 §3.1.2 forbids fragments outright) and both are classic
  // confusion primitives — `https://claude.ai@evil.tld/` reads as claude.ai
  // to a human, and a fragment can smuggle state past server-side parsers.
  // Applies to loopback URIs too.
  if (parsed.username !== '' || parsed.password !== '') return false;
  if (parsed.hash !== '') return false;

  const host = parsed.hostname.toLowerCase();

  // Loopback (any port, http or https) — RFC 8252 §7.3 native-app pattern.
  // Cannot be disabled via env; CLI/desktop AI clients depend on it.
  if (host === 'localhost' || host === '127.0.0.1' || host === '[::1]' || host === '::1') {
    return true;
  }

  // Configured hosted AI clients must use HTTPS — no point allow-listing a
  // production AI vendor over plain HTTP, and refusing this blocks downgrade
  // tricks where an attacker gets the user to follow `http://claude.ai/...`.
  if (parsed.protocol !== 'https:') return false;

  // Host must match (exact, or dot-anchored subdomain for wildcards) AND the
  // pathname must match the entry's path template when one is set. `new URL()`
  // has already collapsed dot-segments, so `/cb/../evil` cannot masquerade as
  // a template path. Query strings are permitted — the host+path pair is what
  // pins the vendor's callback handler.
  for (const entry of allowed.entries) {
    const hostMatches = entry.wildcard
      ? host.endsWith(`.${entry.host}`)
      : host === entry.host;
    if (!hostMatches) continue;
    if (entry.path === null || pathMatchesTemplate(entry.path, parsed.pathname)) return true;
  }
  return false;
}

export function isAllowedRedirectUri(uri: string | undefined | null): boolean {
  return isAllowedRedirectUriWith(uri, ALLOWED_HOSTS);
}

/**
 * Canonical form of a redirect_uri for the per-client binding check. A naive
 * exact-string compare rejects two legitimate native-client behaviours:
 *
 *   1. Trailing-slash / serialization differences. A client registers
 *      `http://127.0.0.1:33418` but sends `http://127.0.0.1:33418/` at
 *      /authorize (URL parsing normalizes the empty path to "/"). Same endpoint.
 *      This is exactly what stalls VS Code / GitHub Copilot: it registers the
 *      loopback URI without a slash, then authorizes with the normalized form,
 *      so a string `includes()` returns false and the flow dies at /authorize.
 *   2. Ephemeral loopback ports. A native app binds a fresh loopback port per
 *      run, so the registered port rarely equals the port used later. RFC 8252
 *      §7.3 requires the AS to allow ANY port for loopback redirect URIs.
 *
 * So: parse + re-serialize (fixes #1), and drop the port for loopback hosts
 * (fixes #2). The host allowlist already permits any loopback port, so dropping
 * it here grants no reach beyond that existing gate. Returns null for an
 * unparseable URI so it can never accidentally compare-equal.
 */
function canonicalizeRedirectUri(uri: string): string | null {
  let u: URL;
  try {
    u = new URL(uri);
  } catch {
    return null;
  }
  const host = u.hostname.toLowerCase();
  const isLoopback = host === 'localhost' || host === '127.0.0.1' || host === '[::1]' || host === '::1';
  const portPart = isLoopback || !u.port ? '' : `:${u.port}`;
  return `${u.protocol}//${host}${portPart}${u.pathname}${u.search}`;
}

/**
 * True when `requested` matches one of the client's `registered` redirect_uris
 * under canonicalization (trailing-slash tolerant, loopback-port agnostic).
 */
export function redirectUriMatches(registered: string[], requested: string): boolean {
  const target = canonicalizeRedirectUri(requested);
  if (target === null) return false;
  return registered.some((r) => canonicalizeRedirectUri(r) === target);
}

export function createOAuthRouter(): Router {
  const router = Router();

  // --- Protected Resource Metadata (RFC 9728) ---
  // The protected resource is the MCP endpoint itself (<base>/mcp), so the
  // metadata `resource` MUST equal "<base>/mcp" — NOT the bare base URL — and
  // the document MUST be reachable at the resource-path-specific well-known
  // location (/.well-known/oauth-protected-resource/mcp). Strict clients
  // (VS Code / GitHub Copilot) fetch the path-specific URL first and, per
  // RFC 9728 §3.3, reject the document unless `resource` matches the URL they
  // are connecting to. Returning the bare base URL made VS Code reject the
  // metadata and never start the auth flow — it just looped `initialize`
  // forever (confirmed via its own log: "'resource' ... does not match
  // expected value .../mcp ... these MUST match").
  const protectedResource = (req: import('express').Request, res: import('express').Response) => {
    const baseUrl = getPublicBaseUrl(req);
    res.json({
      resource: `${baseUrl}/mcp`,
      authorization_servers: [baseUrl],
      bearer_methods_supported: ['header'],
      scopes_supported: ['openid', 'profile', 'offline_access', `api://${CLIENT_ID}/access_as_user`],
    });
  };
  // Resource-path-specific location (RFC 9728 §3.1) — what strict clients try
  // first; the bare location is kept for lenient clients and serves the same
  // `resource` value so a strict client that falls back to it still validates.
  router.get('/.well-known/oauth-protected-resource/mcp', protectedResource);
  router.get('/.well-known/oauth-protected-resource', protectedResource);

  // --- OAuth Authorization Server Metadata (RFC 8414) ---
  router.get('/.well-known/oauth-authorization-server', (req, res) => {
    const baseUrl = getPublicBaseUrl(req);
    res.json({
      issuer: baseUrl,
      authorization_endpoint: `${baseUrl}/oauth/authorize`,
      token_endpoint: `${baseUrl}/oauth/token`,
      registration_endpoint: `${baseUrl}/oauth/register`,
      response_types_supported: ['code'],
      grant_types_supported: ['authorization_code', 'refresh_token'],
      code_challenge_methods_supported: ['S256'],
      scopes_supported: ['openid', 'profile', 'offline_access', `api://${CLIENT_ID}/access_as_user`],
    });
  });

  // --- Dynamic Client Registration (RFC 7591) ---
  // All /oauth/* routes below carry a per-source-IP throttle: they are the only
  // unauthenticated surface (accessGuard covers /mcp only) and /oauth/token in
  // particular triggers an outbound Entra call with the client secret per
  // request — unthrottled, a flood would make Entra throttle the app
  // registration itself, an org-wide auth DoS. The .well-known discovery docs
  // stay unthrottled (static JSON, no outbound work, no logging).
  router.post('/oauth/register', oauthRateLimit, (req, res) => {
    const { client_name, redirect_uris, grant_types, response_types, token_endpoint_auth_method } = req.body ?? {};

    // Field-level bounds — backstop the route-level body-size limit. The
    // parser caps total body bytes; these caps stop a single registration
    // from carrying e.g. a thousand redirect_uris that all individually
    // pass the URI allowlist. They also bound the size of the signed client_id
    // token (which embeds redirect_uris) so a crafted registration cannot mint
    // an arbitrarily long client_id.
    if (typeof client_name === 'string' && client_name.length > MAX_CLIENT_NAME_LENGTH) {
      res.status(400).json({
        error: 'invalid_client_metadata',
        error_description: `client_name exceeds ${MAX_CLIENT_NAME_LENGTH} characters`,
      });
      return;
    }

    const uris = Array.isArray(redirect_uris) ? redirect_uris : [];
    if (uris.length > MAX_REDIRECT_URIS_PER_CLIENT) {
      res.status(400).json({
        error: 'invalid_redirect_uri',
        error_description: `Too many redirect_uris (max ${MAX_REDIRECT_URIS_PER_CLIENT})`,
      });
      return;
    }
    const oversized = uris.find((u: unknown) => typeof u === 'string' && u.length > MAX_REDIRECT_URI_LENGTH);
    if (oversized !== undefined) {
      res.status(400).json({
        error: 'invalid_redirect_uri',
        error_description: `redirect_uri exceeds ${MAX_REDIRECT_URI_LENGTH} characters`,
      });
      return;
    }

    // Reject hostile redirect_uris at registration time so the registry can be
    // trusted by /oauth/authorize and /oauth/callback. The endpoint itself is
    // unauthenticated per RFC 7591, so this filter is the only lever stopping
    // an attacker from registering arbitrary post-callback destinations.
    const invalidUris = uris.filter((u: unknown) => typeof u !== 'string' || !isAllowedRedirectUri(u));
    if (invalidUris.length > 0) {
      console.error(
        `[oauth/register] Rejected client ${sanitizeForLog(client_name ?? 'unknown')}: ` +
        `${invalidUris.length} redirect_uri(s) outside allowlist`,
      );
      res.status(400).json({
        error: 'invalid_redirect_uri',
        error_description:
          'One or more redirect_uris are not in the allowlist. Loopback (localhost / 127.0.0.1) ' +
          'is always accepted; hosted AI client hosts are governed by the ' +
          'MCP_ALLOWED_REDIRECT_HOSTS env var (default: Anthropic, OpenAI, Google).',
      });
      return;
    }

    // Stateless: the client_id is a signed token embedding the redirect_uris,
    // not a key into a server-side map. No storage, no memory cap, verifiable
    // on any replica (see signClientId).
    const clientId = signClientId(uris, client_name ?? 'unknown');

    console.error(`[oauth/register] Registered dynamic client (${sanitizeForLog(client_name ?? 'unknown')}), ${uris.length} redirect_uri(s)`);

    res.status(201).json({
      client_id: clientId,
      client_name: client_name ?? 'unknown',
      redirect_uris: uris,
      grant_types: grant_types ?? ['authorization_code', 'refresh_token'],
      response_types: response_types ?? ['code'],
      token_endpoint_auth_method: token_endpoint_auth_method ?? 'none',
    });
  });

  // --- Authorize: redirect to Entra ID ---
  router.get('/oauth/authorize', oauthRateLimit, (req, res) => {
    const baseUrl = getPublicBaseUrl(req);
    const {
      client_id,
      redirect_uri,
      state,
      code_challenge,
      code_challenge_method,
    } = req.query as Record<string, string>;

    // PKCE is mandatory. The discovery doc advertises S256 only, so any client
    // that omits the challenge or downgrades to `plain` is either misbehaving
    // or attempting to weaken the flow. Reject before redirecting to Entra so
    // the failure surfaces cleanly at the proxy boundary.
    if (!code_challenge) {
      res.status(400).json({
        error: 'invalid_request',
        error_description: 'code_challenge is required (PKCE S256 mandatory)',
      });
      return;
    }
    const method = code_challenge_method ?? 'S256';
    if (method !== 'S256') {
      res.status(400).json({
        error: 'invalid_request',
        error_description: `code_challenge_method must be S256 (got: ${method})`,
      });
      return;
    }

    // Defense in depth: validate redirect_uri before we build the proxy state.
    // Without this, /oauth/callback would happily 302 to any URL the caller
    // supplied — leaking the auth code to an attacker-controlled endpoint
    // (the attacker generates their own PKCE pair, so PKCE does not protect).
    //
    // Two checks:
    //   1. URI must be in the per-client redirect list (RFC 6749 §3.1.2.4).
    //   2. URI host must be in the static allowlist (defense against the
    //      unauthenticated /oauth/register endpoint being abused to whitelist
    //      a hostile URI on the fly).
    if (!redirect_uri) {
      res.status(400).json({ error: 'invalid_request', error_description: 'redirect_uri is required' });
      return;
    }
    if (!isAllowedRedirectUri(redirect_uri)) {
      console.error(`[oauth/authorize] Rejected redirect_uri (not in allowlist): ${sanitizeForLog(redirect_uri)}`);
      res.status(400).json({
        error: 'invalid_request',
        error_description: 'redirect_uri host is not in the allowlist',
      });
      return;
    }
    // client_id is REQUIRED. Now that it is a stateless, replica-portable
    // signed token, there is no cold-start reason to tolerate its absence —
    // and tolerating it would let a caller bypass the per-client redirect_uri
    // binding entirely, leaving only the (port-agnostic, loopback-permissive)
    // host allowlist as the gate. Every conforming MCP client registers via
    // /oauth/register first, so demanding client_id costs nothing.
    if (!client_id) {
      res.status(400).json({
        error: 'invalid_request',
        error_description: 'client_id is required — register via /oauth/register first',
      });
      return;
    }
    // The client_id is a self-describing signed token (see signClientId), so
    // this verifies statelessly — no registry lookup, correct across cold
    // starts and replicas. A forged/malformed token fails closed with
    // invalid_client; a valid one must still carry the exact redirect_uri.
    const registered = verifyClientId(client_id);
    if (!registered) {
      console.error('[oauth/authorize] client_id failed signature verification');
      res.status(400).json({
        error: 'invalid_client',
        error_description: 'client_id is invalid — register via /oauth/register first',
      });
      return;
    }
    if (!redirectUriMatches(registered.redirectUris, redirect_uri)) {
      console.error(
        `[oauth/authorize] redirect_uri not registered for this client_id: ${sanitizeForLog(redirect_uri)}`,
      );
      res.status(400).json({
        error: 'invalid_request',
        error_description: 'redirect_uri does not match any URI registered for this client_id',
      });
      return;
    }

    // HMAC-signed state: tampering or replay (>10 min) fails verification at
    // /oauth/callback. The original client state is preserved inside the
    // signed payload and re-emitted to the client unchanged.
    const proxyState = signState({
      originalState: state,
      redirectUri: redirect_uri,
      clientId: client_id ?? null,
    });

    // Always use server-defined SCOPES — Claude Code may send scope values
    // (e.g. after dynamic registration) that trigger AADSTS90009 when forwarded
    // to Entra ID, because the app would be requesting a token for itself with
    // an unsupported identifier format.
    const entraParams = new URLSearchParams({
      client_id: CLIENT_ID,
      response_type: 'code',
      redirect_uri: `${baseUrl}/oauth/callback`,
      scope: SCOPES,
      state: proxyState,
      code_challenge,
      code_challenge_method: 'S256',
      // Force the Entra account picker instead of silently reusing whatever
      // account the system/default browser already has an SSO session for.
      // Without this, a user whose default browser is signed into the WRONG
      // tenant/account gets authenticated as that account with no chance to
      // switch — and working around it by pasting the URL into another browser
      // profile fails, because the native client's loopback listener has
      // already closed by then (ERR_CONNECTION_REFUSED). select_account lets
      // the user choose the correct account in the same browser the client
      // opened, keeping the loopback callback alive.
      prompt: 'select_account',
    });

    res.redirect(`${AUTHORITY}/oauth2/v2.0/authorize?${entraParams}`);
  });

  // --- Callback: receive code from Entra ID, forward to Claude Code ---
  router.get('/oauth/callback', oauthRateLimit, (req, res) => {
    const { code, state, error, error_description } = req.query as Record<string, string>;

    if (error) {
      res.status(400).json({ error, error_description });
      return;
    }

    // No error and no code is a malformed callback — refuse rather than
    // redirect the client a `code=undefined` query (clean OAuth boundary).
    if (!code) {
      res.status(400).json({
        error: 'invalid_request',
        error_description: 'Missing authorization code',
      });
      return;
    }

    // verifyState rejects tampered, replayed-after-10-min, or malformed state.
    const decoded = state ? verifyState(state) : null;
    if (!decoded) {
      res.status(400).json({
        error: 'invalid_state',
        error_description: 'state failed HMAC verification or expired',
      });
      return;
    }
    const originalState = decoded.originalState;
    const redirectUri = decoded.redirectUri;
    const clientId = decoded.clientId;

    // Re-validate the redirectUri at callback time too. Belt-and-suspenders:
    // /oauth/authorize already gated the URI, but the state value transits the
    // user-agent — re-running the host allowlist here closes any replay gap.
    if (!isAllowedRedirectUri(redirectUri)) {
      console.error(`[oauth/callback] Rejected redirectUri from state (not in allowlist): ${sanitizeForLog(redirectUri)}`);
      res.status(400).json({
        error: 'invalid_request',
        error_description: 'redirect_uri host is not in the allowlist',
      });
      return;
    }
    if (clientId) {
      // Stateless verify (no registry) — correct across cold starts/replicas.
      // The redirectUri is already pinned inside the HMAC-verified state, so
      // this only adds defense-in-depth: a valid client_id must still list the
      // redirectUri. A forged client_id fails closed.
      const registered = verifyClientId(clientId);
      if (!registered || !redirectUriMatches(registered.redirectUris, redirectUri)) {
        console.error(`[oauth/callback] client_id invalid or redirectUri not registered: ${sanitizeForLog(redirectUri)}`);
        res.status(400).json({
          error: 'invalid_request',
          error_description: 'redirect_uri does not match any URI registered for this client_id',
        });
        return;
      }
    }

    // Redirect back to Claude Code with the authorization code
    const callbackParams = new URLSearchParams({
      code,
      ...(originalState ? { state: originalState } : {}),
    });

    res.redirect(`${redirectUri}?${callbackParams}`);
  });

  // --- Token: proxy token exchange to Entra ID ---
  router.post('/oauth/token', oauthTokenRateLimit, async (req, res) => {
    const baseUrl = getPublicBaseUrl(req);

    // RFC 6749 §5.1: responses carrying tokens MUST NOT be cached. Set on
    // every response from this route (success, Entra error passthrough, our
    // own 400s/502) — Entra sends these headers but the proxy's res.json()
    // does not copy upstream headers.
    res.set('Cache-Control', 'no-store');
    res.set('Pragma', 'no-cache');

    const params = req.body as Record<string, string>;

    // Allowlist the grant before anything is forwarded. Every request to this
    // endpoint goes out with the app's client_id + client_secret attached, so
    // an unlisted grant_type would make the proxy an unauthenticated
    // confidential-client oracle for whatever grants Entra happens to accept
    // (client_credentials/ROPC/device-code currently fail only by accident of
    // the pinned scope and which fields we copy — policy must live here, not
    // in Entra-side coincidences). The discovery doc advertises exactly these
    // two grants, so no conforming client loses anything.
    if (!params.grant_type) {
      res.status(400).json({
        error: 'invalid_request',
        error_description: 'grant_type is required',
      });
      return;
    }
    if (params.grant_type !== 'authorization_code' && params.grant_type !== 'refresh_token') {
      res.status(400).json({
        error: 'unsupported_grant_type',
        error_description: 'grant_type must be authorization_code or refresh_token',
      });
      return;
    }

    // Enforce PKCE finishing move on the authorization_code grant. /oauth/
    // authorize already required code_challenge — accepting a token request
    // without code_verifier here would defeat that gate.
    if (params.grant_type === 'authorization_code' && !params.code_verifier) {
      res.status(400).json({
        error: 'invalid_request',
        error_description: 'code_verifier is required for grant_type=authorization_code (PKCE)',
      });
      return;
    }

    // Build form body for Entra ID token endpoint
    const body = new URLSearchParams();

    body.set('client_id', CLIENT_ID);
    if (CLIENT_SECRET) body.set('client_secret', CLIENT_SECRET);
    body.set('redirect_uri', `${baseUrl}/oauth/callback`);

    body.set('grant_type', params.grant_type);
    if (params.code) body.set('code', params.code);
    if (params.refresh_token) body.set('refresh_token', params.refresh_token);
    if (params.code_verifier) body.set('code_verifier', params.code_verifier);
    // Always use server-defined SCOPES (see authorize endpoint comment)
    body.set('scope', SCOPES);

    try {
      const tokenResponse = await fetch(`${AUTHORITY}/oauth2/v2.0/token`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: body.toString(),
      });

      const data = await tokenResponse.json();
      if (tokenResponse.status !== 200) {
        // Log only stable identifiers — never the full body. error_description
        // can carry user/tenant details, and a full dump would also capture any
        // token material Entra chooses to include in edge-case errors. The
        // correlation_id is what Microsoft support needs to trace the request.
        const err = (typeof data === 'object' && data !== null ? data : {}) as Record<string, unknown>;
        const codes = Array.isArray(err.error_codes) ? err.error_codes.join(',') : err.error_codes;
        console.error(
          `[oauth/token] Entra error: status=${tokenResponse.status} ` +
          `error=${sanitizeForLog(err.error)} error_codes=${sanitizeForLog(codes)} ` +
          `correlation_id=${sanitizeForLog(err.correlation_id)}`,
        );
      }
      res.status(tokenResponse.status).json(data);
    } catch (err) {
      console.error('[oauth] Token exchange failed:', err);
      res.status(502).json({ error: 'token_exchange_failed', error_description: 'Failed to exchange token with identity provider' });
    }
  });

  return router;
}
