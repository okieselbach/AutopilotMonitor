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
 */
const registeredClients = new Map<string, { name: string; redirectUris: string[] }>();

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
    const { client_name, redirect_uris, grant_types, response_types, token_endpoint_auth_method } = req.body ?? {};

    // Reject hostile redirect_uris at registration time so the registry can be
    // trusted by /oauth/authorize and /oauth/callback. The endpoint itself is
    // unauthenticated per RFC 7591, so this filter is the only lever stopping
    // an attacker from registering arbitrary post-callback destinations.
    const uris = Array.isArray(redirect_uris) ? redirect_uris : [];
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

    // Store Claude Code's redirect_uri in state so we can forward the code back.
    // The clientId is bound into the state so /oauth/callback can re-validate
    // against the registry — the inbound request to /oauth/callback only sees
    // the state, not the original query, and tampering with the state is
    // harmless because the same allowlist + registry checks run there too.
    const proxyState = Buffer.from(JSON.stringify({
      originalState: state,
      redirectUri: redirect_uri,
      clientId: client_id ?? null,
    })).toString('base64url');

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
      ...(code_challenge ? { code_challenge } : {}),
      ...(code_challenge_method ? { code_challenge_method } : {}),
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

    // Decode proxy state to get Claude Code's original redirect_uri
    let originalState = state;
    let redirectUri = '';
    let clientId: string | null = null;
    try {
      const decoded = JSON.parse(Buffer.from(state, 'base64url').toString('utf-8'));
      originalState = decoded.originalState;
      redirectUri = decoded.redirectUri;
      clientId = decoded.clientId ?? null;
    } catch {
      res.status(400).json({ error: 'invalid_state', error_description: 'Could not decode state' });
      return;
    }

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

    // Build form body for Entra ID token endpoint
    const body = new URLSearchParams();
    const params = req.body as Record<string, string>;

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
