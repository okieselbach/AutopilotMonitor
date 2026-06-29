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

/**
 * In-memory store for dynamically registered OAuth clients (RFC 7591).
 * Clients re-register after a server restart — this is fine because the MCP
 * server acts as an OAuth proxy and the real credentials live in Entra ID.
 *
 * TODO(oauth-map-ttl): TTL/LRU eviction is intentionally NOT enabled. With a
 * stateless scale-to-zero Container App, an evicted client_id forces the user
 * back through Claude Code's manual `/mcp` reconnect flow (no auto-recovery).
 * The hard size cap (MAX_REGISTERED_CLIENTS) bounds memory; revisit TTL once
 * the reconnect UX is debugged or the registry moves to persistent storage.
 */
const registeredClients = new Map<string, { name: string; redirectUris: string[] }>();

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

/**
 * Hard cap on the dynamic-client registry. At ~300 bytes per entry this caps
 * memory at ~3 MB worst case — well below the 0.5 GiB container budget. Real
 * usage adds one entry per Claude-Code-session × server-URL, lifetime; even a
 * busy deployment stays in the low thousands.
 */
export const MAX_REGISTERED_CLIENTS = 10_000;
export const MAX_REDIRECT_URIS_PER_CLIENT = 10;
export const MAX_REDIRECT_URI_LENGTH = 1024;
export const MAX_CLIENT_NAME_LENGTH = 256;

/**
 * Test-only handle on the registry. Lets unit tests verify cap behavior
 * without spinning up the full Express stack. Production code never imports
 * this — it only ever touches the closure inside createOAuthRouter().
 */
export const _registryForTest = {
  size: () => registeredClients.size,
  fill: (count: number) => {
    for (let i = 0; i < count; i++) {
      registeredClients.set(`__test_${i}_${crypto.randomUUID()}`, {
        name: '__test',
        redirectUris: ['http://localhost:1/cb'],
      });
    }
  },
  clear: () => registeredClients.clear(),
};

/**
 * Strict allowlist of host patterns acceptable for OAuth redirect_uri.
 * Defense-in-depth on top of the per-client redirect_uri match: even if an
 * attacker calls the unauthenticated /oauth/register endpoint to register a
 * client with a hostile redirect_uri, the URI must still pass this filter.
 *
 * Loopback (localhost / 127.0.0.1 / [::1]) is always allowed for native CLI
 * flows (RFC 8252 §7.3). Hosted-AI-client hosts are configurable via the
 * MCP_ALLOWED_REDIRECT_HOSTS env var (comma-separated). When unset, a
 * baseline of widely-deployed AI vendors is used.
 *
 * Entry syntax: bare host (`claude.ai`) for exact match, or `*.example.com`
 * for any subdomain. The leading-dot suffix check anchors the match to a
 * real subdomain boundary so `evilclaude.ai` does not slip past `claude.ai`.
 */
const DEFAULT_ALLOWED_HOSTS: readonly string[] = [
  // Anthropic
  'claude.ai',
  '*.claude.ai',
  '*.anthropic.com',
  // OpenAI / ChatGPT
  'chatgpt.com',
  '*.chatgpt.com',
  '*.openai.com',
  // Google / Gemini
  'gemini.google.com',
  // Microsoft / VS Code (GitHub Copilot, Continue, etc.)
  // Stable uses https://vscode.dev/redirect; Insiders uses
  // https://insiders.vscode.dev/redirect. The loopback URI VS Code also
  // registers (http://127.0.0.1:<port>) is already accepted by the
  // RFC 8252 loopback rule below.
  'vscode.dev',
  '*.vscode.dev',
];

export function parseAllowedHosts(envValue: string | undefined): { exact: Set<string>; suffixes: string[] } {
  const exact = new Set<string>();
  const suffixes: string[] = [];
  const entries = envValue
    ? envValue.split(',').map((s) => s.trim()).filter(Boolean)
    : DEFAULT_ALLOWED_HOSTS;
  for (const entry of entries) {
    const lower = entry.toLowerCase();
    if (lower.startsWith('*.')) {
      // Store with the leading dot so the suffix check is anchored to a real
      // subdomain boundary — `*.claude.ai` matches `api.claude.ai` but not
      // `evilclaude.ai`.
      suffixes.push(lower.slice(1));
    } else {
      exact.add(lower);
    }
  }
  return { exact, suffixes };
}

const ALLOWED_HOSTS = parseAllowedHosts(process.env.MCP_ALLOWED_REDIRECT_HOSTS);

if (process.env.MCP_ALLOWED_REDIRECT_HOSTS) {
  console.error(
    `[oauth] MCP_ALLOWED_REDIRECT_HOSTS set — using ${ALLOWED_HOSTS.exact.size} exact + ` +
    `${ALLOWED_HOSTS.suffixes.length} wildcard host(s) (loopback always allowed)`,
  );
}

/**
 * Internal worker that applies the host check against an arbitrary allowlist.
 * Exposed for tests so the ENV-override path can be exercised without
 * reloading the module (ENV is captured at import time).
 */
export function isAllowedRedirectUriWith(
  uri: string | undefined | null,
  allowed: { exact: Set<string>; suffixes: string[] },
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

  if (allowed.exact.has(host)) return true;
  for (const suffix of allowed.suffixes) {
    if (host.endsWith(suffix)) return true;
  }
  return false;
}

export function isAllowedRedirectUri(uri: string | undefined | null): boolean {
  return isAllowedRedirectUriWith(uri, ALLOWED_HOSTS);
}

/**
 * Derives the public base URL of this MCP server.
 * In production: from X-Forwarded-Host / X-Forwarded-Proto (set by Container Apps ingress).
 * Fallback: MCP_PUBLIC_URL env var or localhost.
 */
function getPublicBaseUrl(req: import('express').Request): string {
  if (process.env.MCP_PUBLIC_URL) return process.env.MCP_PUBLIC_URL;

  const proto = (req.headers['x-forwarded-proto'] as string) ?? req.protocol;
  const host = (req.headers['x-forwarded-host'] as string) ?? req.headers.host;
  return `${proto}://${host}`;
}

export function createOAuthRouter(): Router {
  const router = Router();

  // --- Protected Resource Metadata (RFC 9728) ---
  // This is the FIRST endpoint Claude Code fetches after receiving a 401.
  // It tells the client where the authorization server lives.
  router.get('/.well-known/oauth-protected-resource', (req, res) => {
    const baseUrl = getPublicBaseUrl(req);
    res.json({
      resource: baseUrl,
      authorization_servers: [baseUrl],
      bearer_methods_supported: ['header'],
      scopes_supported: ['openid', 'profile', 'offline_access', `api://${CLIENT_ID}/access_as_user`],
    });
  });

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
  router.post('/oauth/register', (req, res) => {
    // Hard memory ceiling — refuse new registrations once the registry is at
    // capacity. The endpoint is unauthenticated per RFC 7591, so without this
    // a flood would push the 0.5 GiB container into OOM.
    if (registeredClients.size >= MAX_REGISTERED_CLIENTS) {
      console.error(
        `[oauth/register] Registry at cap (${MAX_REGISTERED_CLIENTS}) — rejecting new registration`,
      );
      res.status(503).json({
        error: 'temporarily_unavailable',
        error_description: 'Registration capacity reached, please retry later',
      });
      return;
    }

    const { client_name, redirect_uris, grant_types, response_types, token_endpoint_auth_method } = req.body ?? {};

    // Field-level bounds — backstop the route-level body-size limit. The
    // parser caps total body bytes; these caps stop a single registration
    // from carrying e.g. a thousand redirect_uris that all individually
    // pass the URI allowlist.
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
        `[oauth/register] Rejected client ${client_name ?? 'unknown'}: ` +
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

    const clientId = crypto.randomUUID();
    registeredClients.set(clientId, {
      name: client_name ?? 'unknown',
      redirectUris: uris,
    });

    console.error(`[oauth/register] Registered dynamic client ${clientId} (${client_name ?? 'unknown'})`);

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
  router.get('/oauth/authorize', (req, res) => {
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
      console.error(`[oauth/authorize] Rejected redirect_uri (host not in allowlist): ${redirect_uri}`);
      res.status(400).json({
        error: 'invalid_request',
        error_description: 'redirect_uri host is not in the allowlist',
      });
      return;
    }
    if (client_id) {
      const registered = registeredClients.get(client_id);
      if (!registered) {
        res.status(400).json({
          error: 'invalid_client',
          error_description: 'Unknown client_id — register via /oauth/register first',
        });
        return;
      }
      if (!registered.redirectUris.includes(redirect_uri)) {
        console.error(
          `[oauth/authorize] redirect_uri not registered for client ${client_id}: ${redirect_uri}`,
        );
        res.status(400).json({
          error: 'invalid_request',
          error_description: 'redirect_uri does not match any URI registered for this client_id',
        });
        return;
      }
    }
    // If client_id is missing we still allow the flow (the host allowlist
    // already gates which destinations are reachable), but emit a warning so
    // misbehaving clients are noticed.
    if (!client_id) {
      console.warn('[oauth/authorize] No client_id supplied — proceeding on host-allowlist gate only');
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
    });

    res.redirect(`${AUTHORITY}/oauth2/v2.0/authorize?${entraParams}`);
  });

  // --- Callback: receive code from Entra ID, forward to Claude Code ---
  router.get('/oauth/callback', (req, res) => {
    const { code, state, error, error_description } = req.query as Record<string, string>;

    if (error) {
      res.status(400).json({ error, error_description });
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
    // /oauth/authorize already gated the URI, but the state value transits
    // the user-agent and could in principle be replayed against a server
    // whose registeredClients map was wiped (cold start) — re-running the
    // host allowlist + registry check here closes that gap.
    if (!isAllowedRedirectUri(redirectUri)) {
      console.error(`[oauth/callback] Rejected redirectUri from state (host not in allowlist): ${redirectUri}`);
      res.status(400).json({
        error: 'invalid_request',
        error_description: 'redirect_uri host is not in the allowlist',
      });
      return;
    }
    if (clientId) {
      const registered = registeredClients.get(clientId);
      // Cold-start may have wiped the registry — fall back to host allowlist
      // alone (already validated above) rather than fail the user's flow.
      if (registered && !registered.redirectUris.includes(redirectUri)) {
        console.error(
          `[oauth/callback] redirectUri not registered for client ${clientId}: ${redirectUri}`,
        );
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
  router.post('/oauth/token', async (req, res) => {
    const baseUrl = getPublicBaseUrl(req);

    const params = req.body as Record<string, string>;

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

    if (params.grant_type) body.set('grant_type', params.grant_type);
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
        console.error(`[oauth/token] Entra error: status=${tokenResponse.status}`, JSON.stringify(data));
      }
      res.status(tokenResponse.status).json(data);
    } catch (err) {
      console.error('[oauth] Token exchange failed:', err);
      res.status(502).json({ error: 'token_exchange_failed', error_description: 'Failed to exchange token with identity provider' });
    }
  });

  return router;
}
