/**
 * Unit tests for the security guards added in the MCP review:
 *   H1 — OAuth redirect_uri allowlist
 *   M1 — followNextLink basePath validation
 *   M2 — SessionIdSchema UUID enforcement
 *   L3 — error-handler 5xx body redaction (also covers post-2026-05 review:
 *        structured 5xx are now sanitized identically to unstructured)
 *
 * Plus 2026-05-08 security review fixes:
 *   F1 — access-guard cache key includes token hash
 *   F2 — /oauth/register field-level + registry-cap defenses
 *   F4 — PKCE S256 enforcement + HMAC-signed state + state expiry
 *
 * These tests are pure functions; no live backend or token needed.
 */
import { describe, it, expect } from 'vitest';
import express from 'express';
import type { AddressInfo } from 'node:net';
import { followNextLink } from '../client.js';
import { SessionIdSchema } from '../tools/shared.js';
import { ApiError } from '../client.js';
import { toolError } from '../tools/error-handler.js';
import { extractTenantList, TENANT_SAFE_FIELDS } from '../tools/admin.js';

// Importing the OAuth helper requires the env var that gates module load.
// Set a dummy value before the import resolves so the throw doesn't fire.
process.env.AUTOPILOT_ENTRA_CLIENT_ID ??= '00000000-0000-0000-0000-000000000000';
// access-guard reads ALL its env tunables at module init, and oauth.js now
// imports access-guard (the /oauth/* throttles live there) — so every pin must
// land BEFORE this first import, not next to the later access-guard import.
// Pin a small pre-auth budget so the throttle tests are cheap and
// limit-independent, and a tiny rate-bucket cap so the size-cap test can flood
// a handful of distinct keys instead of 10_000 (well above the per-UPN
// throttle tests' key count).
const PRE_AUTH_TEST_LIMIT = 5;
process.env.MCP_PRE_AUTH_RATE_LIMIT_PER_MINUTE = String(PRE_AUTH_TEST_LIMIT);
const RATE_BUCKET_TEST_CAP = 10;
process.env.MCP_RATE_BUCKET_MAX_ENTRIES = String(RATE_BUCKET_TEST_CAP);
const {
  isAllowedRedirectUri,
  isAllowedRedirectUriWith,
  parseAllowedHosts,
  sanitizeForLog,
  createOAuthRouter,
  signState,
  verifyState,
  MAX_REDIRECT_URIS_PER_CLIENT,
  MAX_REDIRECT_URI_LENGTH,
  MAX_CLIENT_NAME_LENGTH,
} = await import('../oauth.js');

describe('M1 — followNextLink basePath validation', () => {
  const basePath = '/api/global/audit/logs';

  it('accepts a nextLink that targets the same basePath', () => {
    const result = followNextLink(
      basePath,
      {},
      `${basePath}?pageSize=200&continuation=abc&dateFrom=2026-01-01`,
    );
    expect(result).toBe(`${basePath}?pageSize=200&continuation=abc&dateFrom=2026-01-01`);
  });

  it('accepts a nextLink with no query string', () => {
    const result = followNextLink(basePath, {}, basePath);
    expect(result).toBe(basePath);
  });

  it('rejects a nextLink that targets a different /api/ endpoint', () => {
    expect(() =>
      followNextLink(
        basePath,
        {},
        '/api/global/raw/logs?somequery=evil',
      ),
    ).toThrow(/does not match the tool's expected base path/);
  });

  it('rejects a fully different /api/ path even when the prefix overlaps', () => {
    // /api/global/audit/logs-extra would naively pass startsWith(basePath); ensure
    // exact-equal path matching, not prefix.
    expect(() =>
      followNextLink(
        basePath,
        {},
        '/api/global/audit/logs-extra?continuation=x',
      ),
    ).toThrow(/does not match the tool's expected base path/);
  });

  it('falls back to legacy buildQuery when continuation is opaque (no /api/ prefix)', () => {
    const result = followNextLink(
      basePath,
      { pageSize: 200 },
      'opaque-token-abc',
    );
    expect(result).toBe(`${basePath}?pageSize=200&continuation=opaque-token-abc`);
  });

  it('falls back to buildQuery when no continuation is supplied', () => {
    const result = followNextLink(basePath, { pageSize: 100 }, undefined);
    expect(result).toBe(`${basePath}?pageSize=100`);
  });
});

describe('M2 — SessionIdSchema UUID enforcement', () => {
  it('accepts a valid lowercase UUID', () => {
    expect(SessionIdSchema.safeParse('e259c121-1234-4abc-9def-0123456789ab').success).toBe(true);
  });

  it('accepts a valid uppercase UUID', () => {
    expect(SessionIdSchema.safeParse('E259C121-1234-4ABC-9DEF-0123456789AB').success).toBe(true);
  });

  it('rejects path-traversal payloads', () => {
    // The reason we added this guard: WHATWG-URL normalizes "../admin/foo" in
    // the path segment, which would route the request to a different backend
    // endpoint than the tool intended.
    expect(SessionIdSchema.safeParse('../admin/secrets').success).toBe(false);
    expect(SessionIdSchema.safeParse('..%2Fadmin%2Fsecrets').success).toBe(false);
    expect(SessionIdSchema.safeParse('foo/bar').success).toBe(false);
  });

  it('rejects values that are nearly-UUID but malformed', () => {
    expect(SessionIdSchema.safeParse('e259c121-1234-4abc-9def').success).toBe(false); // too short
    expect(SessionIdSchema.safeParse('e259c121-1234-4abc-9def-0123456789abcd').success).toBe(false); // too long
    expect(SessionIdSchema.safeParse('zzzzzzzz-1234-4abc-9def-0123456789ab').success).toBe(false); // non-hex
  });

  it('rejects empty strings and obvious garbage', () => {
    expect(SessionIdSchema.safeParse('').success).toBe(false);
    expect(SessionIdSchema.safeParse('abc').success).toBe(false);
  });
});

describe('H1 — OAuth redirect_uri allowlist (default = exact vendor callback URIs)', () => {
  it('accepts loopback over http with arbitrary port', () => {
    expect(isAllowedRedirectUri('http://localhost:1234/callback')).toBe(true);
    expect(isAllowedRedirectUri('http://127.0.0.1:54321/cb')).toBe(true);
  });

  it('accepts loopback even over https (some clients use TLS-loopback)', () => {
    expect(isAllowedRedirectUri('https://localhost:8443/cb')).toBe(true);
  });

  it('accepts the documented Claude web/desktop callback (claude.ai + claude.com)', () => {
    expect(isAllowedRedirectUri('https://claude.ai/api/mcp/auth_callback')).toBe(true);
    expect(isAllowedRedirectUri('https://claude.com/api/mcp/auth_callback')).toBe(true);
  });

  it('accepts the documented ChatGPT connector callback', () => {
    expect(isAllowedRedirectUri('https://chatgpt.com/connector_platform_oauth_redirect')).toBe(true);
  });

  it('accepts the documented VS Code stable/insiders callbacks', () => {
    expect(isAllowedRedirectUri('https://vscode.dev/redirect')).toBe(true);
    expect(isAllowedRedirectUri('https://insiders.vscode.dev/redirect')).toBe(true);
  });

  it('tolerates a trailing slash and a query string on a path-template match', () => {
    expect(isAllowedRedirectUri('https://vscode.dev/redirect/')).toBe(true);
    expect(isAllowedRedirectUri('https://vscode.dev/redirect?quality=stable')).toBe(true);
  });

  it('rejects other paths on allowlisted hosts (open-redirect containment)', () => {
    expect(isAllowedRedirectUri('https://claude.ai/api/oauth/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://claude.ai/')).toBe(false);
    expect(isAllowedRedirectUri('https://chatgpt.com/connector/oauth/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://vscode.dev/redirect/extra')).toBe(false);
  });

  it('rejects dot-segment traversal into a template path (URL normalizes it away)', () => {
    // new URL() collapses "/api/mcp/auth_callback/../evil" to "/api/mcp/evil",
    // which must then fail the exact-path compare.
    expect(isAllowedRedirectUri('https://claude.ai/api/mcp/auth_callback/../evil')).toBe(false);
  });

  it('rejects subdomains and unrelated vendor hosts no longer in the defaults', () => {
    expect(isAllowedRedirectUri('https://api.claude.ai/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://accounts.anthropic.com/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://app.chatgpt.com/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://platform.openai.com/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://chat.openai.com/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://gemini.google.com/oauth/cb')).toBe(false);
  });

  it('rejects allowed hosts over plain http (downgrade attempt)', () => {
    expect(isAllowedRedirectUri('http://claude.ai/api/mcp/auth_callback')).toBe(false);
    expect(isAllowedRedirectUri('http://chatgpt.com/connector_platform_oauth_redirect')).toBe(false);
  });

  it('rejects arbitrary hosts', () => {
    expect(isAllowedRedirectUri('https://attacker.tld/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://evil.example.com/cb')).toBe(false);
  });

  it('rejects look-alike hosts', () => {
    expect(isAllowedRedirectUri('https://evilclaude.ai/api/mcp/auth_callback')).toBe(false);
    expect(isAllowedRedirectUri('https://fakechatgpt.com/connector_platform_oauth_redirect')).toBe(false);
    expect(isAllowedRedirectUri('https://google.com/cb')).toBe(false);
  });

  it('rejects userinfo in the URI (host-confusion primitive), including loopback', () => {
    expect(isAllowedRedirectUri('https://user@claude.ai/api/mcp/auth_callback')).toBe(false);
    expect(isAllowedRedirectUri('https://claude.ai:pw@evil.tld/cb')).toBe(false);
    expect(isAllowedRedirectUri('http://user@localhost:1234/callback')).toBe(false);
  });

  it('rejects fragments (forbidden by RFC 6749 §3.1.2), including loopback', () => {
    expect(isAllowedRedirectUri('https://claude.ai/api/mcp/auth_callback#frag')).toBe(false);
    expect(isAllowedRedirectUri('http://localhost:1234/callback#frag')).toBe(false);
  });

  it('rejects javascript:, data:, file:, ftp: schemes', () => {
    expect(isAllowedRedirectUri('javascript:alert(1)')).toBe(false);
    expect(isAllowedRedirectUri('data:text/html,<script>alert(1)</script>')).toBe(false);
    expect(isAllowedRedirectUri('file:///etc/passwd')).toBe(false);
    expect(isAllowedRedirectUri('ftp://localhost/x')).toBe(false);
  });

  it('rejects malformed URIs', () => {
    expect(isAllowedRedirectUri('not a url')).toBe(false);
    expect(isAllowedRedirectUri('')).toBe(false);
    expect(isAllowedRedirectUri(null)).toBe(false);
    expect(isAllowedRedirectUri(undefined)).toBe(false);
  });
});

describe('H1 — OAuth allowlist via MCP_ALLOWED_REDIRECT_HOSTS env override', () => {
  const find = (r: { entries: { host: string; wildcard: boolean; path: string | null }[] }, host: string) =>
    r.entries.find((e) => e.host === host);

  it('parseAllowedHosts splits exact hosts and *.wildcards', () => {
    const r = parseAllowedHosts('foo.example.com,*.bar.example.com,baz.org');
    expect(find(r, 'foo.example.com')).toMatchObject({ wildcard: false, path: null });
    expect(find(r, 'baz.org')).toMatchObject({ wildcard: false, path: null });
    expect(find(r, 'bar.example.com')).toMatchObject({ wildcard: true, path: null });
  });

  it('parseAllowedHosts parses optional path templates', () => {
    const r = parseAllowedHosts('a.example.com/oauth/cb,*.b.example.com/redirect,c.example.com');
    expect(find(r, 'a.example.com')).toMatchObject({ wildcard: false, path: '/oauth/cb' });
    expect(find(r, 'b.example.com')).toMatchObject({ wildcard: true, path: '/redirect' });
    expect(find(r, 'c.example.com')).toMatchObject({ wildcard: false, path: null });
  });

  it('parseAllowedHosts normalizes case', () => {
    const r = parseAllowedHosts('Foo.EXAMPLE.com');
    expect(find(r, 'foo.example.com')).toBeDefined();
  });

  it('parseAllowedHosts trims whitespace and drops empty entries', () => {
    const r = parseAllowedHosts(' a.com ,  , b.com ');
    expect(r.entries).toHaveLength(2);
    expect(find(r, 'a.com')).toBeDefined();
    expect(find(r, 'b.com')).toBeDefined();
  });

  it('parseAllowedHosts falls back to path-pinned defaults when env is undefined', () => {
    const r = parseAllowedHosts(undefined);
    expect(find(r, 'claude.ai')).toMatchObject({ path: '/api/mcp/auth_callback' });
    expect(find(r, 'claude.com')).toMatchObject({ path: '/api/mcp/auth_callback' });
    expect(find(r, 'chatgpt.com')).toMatchObject({ path: '/connector_platform_oauth_redirect' });
    expect(find(r, 'vscode.dev')).toMatchObject({ path: '/redirect' });
    expect(find(r, 'insiders.vscode.dev')).toMatchObject({ path: '/redirect' });
    // No host-wide or wildcard entries remain in the defaults.
    expect(r.entries.every((e) => e.path !== null && !e.wildcard)).toBe(true);
  });

  it('custom allowlist accepts only the configured entries (defaults are NOT additive)', () => {
    const customOnly = parseAllowedHosts('mycompany.example.com,*.partner.example.org');
    expect(isAllowedRedirectUriWith('https://mycompany.example.com/cb', customOnly)).toBe(true);
    expect(isAllowedRedirectUriWith('https://api.partner.example.org/cb', customOnly)).toBe(true);
    // Defaults must NOT leak through — operator-supplied list fully replaces them.
    expect(isAllowedRedirectUriWith('https://claude.ai/api/mcp/auth_callback', customOnly)).toBe(false);
    expect(isAllowedRedirectUriWith('https://chatgpt.com/cb', customOnly)).toBe(false);
  });

  it('custom path-template entries pin the path; host-only entries allow any path', () => {
    const r = parseAllowedHosts('pinned.example.com/oauth/cb,open.example.com');
    expect(isAllowedRedirectUriWith('https://pinned.example.com/oauth/cb', r)).toBe(true);
    expect(isAllowedRedirectUriWith('https://pinned.example.com/other', r)).toBe(false);
    expect(isAllowedRedirectUriWith('https://open.example.com/anything/goes', r)).toBe(true);
  });

  it('wildcard entries match subdomains on the dot boundary only', () => {
    const r = parseAllowedHosts('*.partner.example.org/cb');
    expect(isAllowedRedirectUriWith('https://api.partner.example.org/cb', r)).toBe(true);
    // Bare apex does not match a `*.` entry, and look-alikes stay out.
    expect(isAllowedRedirectUriWith('https://partner.example.org/cb', r)).toBe(false);
    expect(isAllowedRedirectUriWith('https://evilpartner.example.org.attacker.tld/cb', r)).toBe(false);
  });

  it('custom allowlist still requires HTTPS for non-loopback', () => {
    const r = parseAllowedHosts('mycompany.example.com');
    expect(isAllowedRedirectUriWith('http://mycompany.example.com/cb', r)).toBe(false);
  });

  it('loopback is always allowed even with empty allowlist', () => {
    const empty = { entries: [] };
    expect(isAllowedRedirectUriWith('http://localhost:9000/cb', empty)).toBe(true);
    expect(isAllowedRedirectUriWith('http://127.0.0.1:9000/cb', empty)).toBe(true);
    // But arbitrary hosts are not.
    expect(isAllowedRedirectUriWith('https://anywhere.com/cb', empty)).toBe(false);
  });
});

describe('sanitizeForLog — user-controlled log-field hygiene', () => {
  it('strips CR/LF so a hostile value cannot forge log lines', () => {
    expect(sanitizeForLog('evil\r\n[oauth] fake line')).toBe('evil[oauth] fake line');
  });

  it('strips other control characters', () => {
    expect(sanitizeForLog('a\x00b\x1bc\x7fd')).toBe('abcd');
  });

  it('caps the length', () => {
    const out = sanitizeForLog('x'.repeat(500), 200);
    expect(out).toHaveLength(203); // 200 chars + '...'
    expect(out.endsWith('...')).toBe(true);
  });

  it('stringifies null/undefined without throwing', () => {
    expect(sanitizeForLog(undefined)).toBe('');
    expect(sanitizeForLog(null)).toBe('');
  });
});

describe('/oauth/token response hygiene', () => {
  it('sets Cache-Control: no-store and Pragma: no-cache on token responses', async () => {
    const app = express();
    app.use(express.urlencoded({ extended: false }));
    app.use(createOAuthRouter());
    const server = app.listen(0, '127.0.0.1');
    await new Promise<void>((resolve) => server.once('listening', resolve));
    const { port } = server.address() as AddressInfo;
    try {
      // Missing grant_type → the proxy's own 400, no outbound Entra call.
      // The anti-caching headers are set route-wide at handler entry, so this
      // cheap path proves they are present on every response shape.
      const res = await fetch(`http://127.0.0.1:${port}/oauth/token`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
        body: '',
      });
      expect(res.status).toBe(400);
      expect(res.headers.get('cache-control')).toBe('no-store');
      expect(res.headers.get('pragma')).toBe('no-cache');
    } finally {
      await new Promise<void>((resolve) => server.close(() => resolve()));
    }
  });
});

describe('L3 — error-handler 5xx body redaction', () => {
  it('does not leak the response body on 500', () => {
    const apiErr = new ApiError(500, 'Stack frame at /usr/src/app/secrets.js:42 with token sk-leak');
    const result = toolError('test_tool', { foo: 'bar' }, apiErr);
    const text = result.content[0].text;
    expect(text).not.toContain('sk-leak');
    expect(text).not.toContain('/usr/src/app');
    expect(text).toContain('HTTP 500');
  });

  it('does not leak the response body on 502/503/504', () => {
    for (const status of [502, 503, 504]) {
      const apiErr = new ApiError(status, 'sensitive backend internal');
      const text = toolError('test_tool', {}, apiErr).content[0].text;
      expect(text).not.toContain('sensitive backend internal');
      expect(text).toContain(`HTTP ${status}`);
    }
  });

  it('still surfaces 4xx body content (caller can act on it)', () => {
    const apiErr = new ApiError(400, 'invalid filter syntax: bad_field');
    const text = toolError('test_tool', {}, apiErr).content[0].text;
    expect(text).toContain('invalid filter syntax');
  });

  it('sanitizes structured 5xx errors too — `error` and `hint` must NOT leak', () => {
    // 2026-05-08 review: structured 5xx errors used to flow through the
    // `parsed`-detail branch with full `error`/`hint`/`exceptionType` fields.
    // The current handler treats all status >= 500 the same: generic message,
    // correlationId + errorCode kept (operational handles), everything else
    // dropped.
    const structured = new ApiError(
      503,
      JSON.stringify({
        error: 'service unavailable',
        hint: 'retry in 30s',
        exceptionType: 'System.Data.SqlClient.SqlException',
        correlationId: 'corr-abc-123',
        errorCode: 'SQL_TIMEOUT',
      }),
    );
    const text = toolError('test_tool', {}, structured).content[0].text;

    expect(text).toContain('HTTP 503');
    // Operational handles still surface so an operator can pivot.
    expect(text).toContain('corr-abc-123');
    expect(text).toContain('SQL_TIMEOUT');
    // Internal-detail fields must be dropped.
    expect(text).not.toContain('service unavailable');
    expect(text).not.toContain('retry in 30s');
    expect(text).not.toContain('System.Data.SqlClient');
  });
});

// ---------------------------------------------------------------------------
// 2026-05-08 security review fixes
// ---------------------------------------------------------------------------

// NOTE: the env pins for these tests (PRE_AUTH_TEST_LIMIT / RATE_BUCKET_TEST_CAP)
// live at the top of the file — access-guard is already loaded transitively by
// the oauth.js import there, so pinning here would be too late.
const { buildCacheKey, boundedSet, isPreAuthRateLimited, getRateBucketSizes } = await import('../access-guard.js');
const { parsePositiveInt, getPublicBaseUrl } = await import('../config.js');

describe('F1 — access-guard cache key includes token hash', () => {
  it('produces stable keys for the same (upn, token) pair', () => {
    const a = buildCacheKey('alice@contoso.com', 'header.payload.signature');
    const b = buildCacheKey('alice@contoso.com', 'header.payload.signature');
    expect(a).toBe(b);
  });

  it('distinguishes two tokens that share a UPN', () => {
    // The whole point of the fix: a forged token with a victim UPN cannot
    // piggyback on a legitimate user's cached `allowed: true`.
    const legit = buildCacheKey('alice@contoso.com', 'legit-token-from-entra');
    const forged = buildCacheKey('alice@contoso.com', 'forged-token-no-signature');
    expect(legit).not.toBe(forged);
  });

  it('distinguishes UPNs with the same token (defensive)', () => {
    const x = buildCacheKey('alice@contoso.com', 'shared-token');
    const y = buildCacheKey('bob@contoso.com', 'shared-token');
    expect(x).not.toBe(y);
  });

  it('keeps the UPN as the visible prefix (operability — log scanning)', () => {
    const k = buildCacheKey('alice@contoso.com', 'whatever');
    expect(k.startsWith('alice@contoso.com:')).toBe(true);
  });
});

describe('F2 — /oauth/register defense constants', () => {
  // The client registry is now stateless (signed client_id, no server-side
  // Map), so there is no MAX_REGISTERED_CLIENTS memory cap to assert — the
  // field-level bounds below are what backstop the signed-token size.
  it('caps redirect_uris per client (RFC 7591 typically lists 1-3)', () => {
    expect(MAX_REDIRECT_URIS_PER_CLIENT).toBeGreaterThanOrEqual(2);
    expect(MAX_REDIRECT_URIS_PER_CLIENT).toBeLessThanOrEqual(50);
  });

  it('caps individual redirect_uri length below practical browser URL limits', () => {
    expect(MAX_REDIRECT_URI_LENGTH).toBeGreaterThanOrEqual(256);
    expect(MAX_REDIRECT_URI_LENGTH).toBeLessThanOrEqual(8192);
  });

  it('caps client_name length below realistic display strings', () => {
    expect(MAX_CLIENT_NAME_LENGTH).toBeGreaterThanOrEqual(64);
    expect(MAX_CLIENT_NAME_LENGTH).toBeLessThanOrEqual(2048);
  });
});

describe('F4 — HMAC-signed OAuth state', () => {
  const validPayload = {
    originalState: 'client-supplied-state-abc',
    redirectUri: 'https://claude.ai/cb',
    clientId: 'client-uuid-123',
  };

  it('round-trips a freshly signed state', () => {
    const signed = signState(validPayload);
    const verified = verifyState(signed);
    expect(verified).not.toBeNull();
    expect(verified!.redirectUri).toBe('https://claude.ai/cb');
    expect(verified!.originalState).toBe('client-supplied-state-abc');
    expect(verified!.clientId).toBe('client-uuid-123');
    expect(typeof verified!.iat).toBe('number');
  });

  it('rejects state without an HMAC suffix', () => {
    // Old behavior was bare base64url-JSON — must be rejected now.
    const unsigned = Buffer.from(JSON.stringify(validPayload)).toString('base64url');
    expect(verifyState(unsigned)).toBeNull();
  });

  it('rejects state where the body was tampered after signing', () => {
    const signed = signState(validPayload);
    const [body, sig] = signed.split('.');
    // Flip a byte in the body but keep the original signature.
    const tamperedBody = Buffer.from(body, 'base64url').toString('utf-8').replace('claude.ai', 'evil.tld');
    const tampered = `${Buffer.from(tamperedBody).toString('base64url')}.${sig}`;
    expect(verifyState(tampered)).toBeNull();
  });

  it('rejects state with a corrupt HMAC suffix', () => {
    const signed = signState(validPayload);
    const [body] = signed.split('.');
    const corrupt = `${body}.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA`;
    expect(verifyState(corrupt)).toBeNull();
  });

  it('rejects state older than 10 minutes (replay window cap)', () => {
    const signed = signState(validPayload);
    // verifyState accepts an injectable clock for deterministic testing.
    const elevenMinutesLater = Math.floor(Date.now() / 1000) + 11 * 60;
    expect(verifyState(signed, elevenMinutesLater)).toBeNull();
  });

  it('accepts state at the 9-minute mark (still within window)', () => {
    const signed = signState(validPayload);
    const nineMinutesLater = Math.floor(Date.now() / 1000) + 9 * 60;
    expect(verifyState(signed, nineMinutesLater)).not.toBeNull();
  });

  it('rejects malformed state strings that lack the dot separator', () => {
    expect(verifyState('no-dot-no-signature')).toBeNull();
    expect(verifyState('')).toBeNull();
    expect(verifyState('a.b.c')).toBeNull(); // too many parts
  });

});

describe('list_tenants — extractTenantList keep-list projection', () => {
  // /api/config/all returns full tenant configs incl. secrets. The tool must
  // surface only non-sensitive fields; these tests pin that boundary.
  const fullConfig = {
    tenantId: 'contoso-tenant-id',
    domainName: 'contoso.example.com',
    planTier: 'pro',
    disabled: false,
    onboardedAt: '2026-01-15T00:00:00Z',
    onboardedBy: 'alice@contoso.example.com',
    lastUpdated: '2026-05-01T00:00:00Z',
    dataRetentionDays: 90,
    // ── sensitive / must NOT leak ──
    teamsWebhookUrl: 'https://contoso.webhook.office.com/secret-token',
    webhookUrl: 'https://example.com/hook?key=supersecret',
    diagnosticsBlobSasUrl: 'https://blob.core.windows.net/c?sig=SECRETSAS',
    enrollmentSummaryBrandingImageUrl: 'https://internal/branding.png',
    localAdminAllowedAccountsJson: '["admin"]',
    diagnosticsLogPathsJson: '[]',
    updatedBy: 'bob@contoso.example.com',
  };

  it('keeps only the safe identity/lifecycle/plan fields', () => {
    const [t] = extractTenantList([fullConfig]);
    // Every surviving key must be in the keep-list (no leak), and the fields
    // present in the input must survive. A safe field absent from the input
    // (e.g. disabledReason on a non-disabled tenant) is legitimately omitted.
    for (const key of Object.keys(t)) expect(TENANT_SAFE_FIELDS.has(key)).toBe(true);
    for (const present of [
      'tenantId', 'domainName', 'planTier', 'disabled',
      'onboardedAt', 'onboardedBy', 'lastUpdated', 'dataRetentionDays',
    ]) {
      expect(t).toHaveProperty(present);
    }
    expect(t.tenantId).toBe('contoso-tenant-id');
    expect(t.planTier).toBe('pro');
  });

  it('strips every secret-bearing field', () => {
    const [t] = extractTenantList([fullConfig]);
    for (const leaky of [
      'teamsWebhookUrl', 'webhookUrl', 'diagnosticsBlobSasUrl',
      'enrollmentSummaryBrandingImageUrl', 'localAdminAllowedAccountsJson',
      'diagnosticsLogPathsJson', 'updatedBy',
    ]) {
      expect(t).not.toHaveProperty(leaky);
    }
    // Belt-and-suspenders: no surviving value contains a SAS/webhook marker.
    const serialized = JSON.stringify(t);
    expect(serialized).not.toMatch(/sig=|webhook|SECRET/i);
  });

  it('normalizes a bare array, a {configurations} envelope, and a {tenants} envelope', () => {
    expect(extractTenantList([fullConfig])).toHaveLength(1);
    expect(extractTenantList({ configurations: [fullConfig] })).toHaveLength(1);
    expect(extractTenantList({ tenants: [fullConfig, fullConfig] })).toHaveLength(2);
  });

  it('returns an empty list for malformed / empty payloads', () => {
    expect(extractTenantList(null)).toEqual([]);
    expect(extractTenantList(undefined)).toEqual([]);
    expect(extractTenantList({})).toEqual([]);
    expect(extractTenantList('not-json')).toEqual([]);
  });

  it('tolerates non-object rows without throwing', () => {
    const result = extractTenantList([null, 42, 'x', fullConfig]);
    expect(result).toHaveLength(4);
    expect(result[0]).toEqual({});
    expect(result[3].tenantId).toBe('contoso-tenant-id');
  });
});

// ---------------------------------------------------------------------------
// 2026-06-30 security review fixes (M1 / M2 / M3)
// ---------------------------------------------------------------------------

describe('M1 — parsePositiveInt env guard (rate-limit NaN bug)', () => {
  it('returns the fallback for an undefined env var', () => {
    expect(parsePositiveInt(undefined, 60)).toBe(60);
  });

  it('parses a valid positive integer', () => {
    expect(parsePositiveInt('120', 60)).toBe(120);
  });

  it('returns the fallback for non-numeric input (would be NaN — disables limiter)', () => {
    // The actual bug: a bare parseInt('abc') is NaN, and `count >= NaN` is
    // always false, silently disabling the rate limiter.
    expect(parsePositiveInt('abc', 60)).toBe(60);
    expect(parsePositiveInt('', 60)).toBe(60);
  });

  it('returns the fallback for zero and negative values', () => {
    expect(parsePositiveInt('0', 60)).toBe(60);
    expect(parsePositiveInt('-5', 60)).toBe(60);
  });
});

describe('M1 — boundedSet size cap (accessCache memory bound)', () => {
  it('evicts the oldest entry once the cap is reached', () => {
    const m = new Map<string, number>();
    boundedSet(m, 'a', 1, 2);
    boundedSet(m, 'b', 2, 2);
    boundedSet(m, 'c', 3, 2); // over cap → evict 'a' (oldest)
    expect(m.size).toBe(2);
    expect(m.has('a')).toBe(false);
    expect(m.has('b')).toBe(true);
    expect(m.has('c')).toBe(true);
  });

  it('updates an existing key in place without evicting or growing', () => {
    const m = new Map<string, number>();
    boundedSet(m, 'a', 1, 2);
    boundedSet(m, 'b', 2, 2);
    boundedSet(m, 'a', 99, 2); // re-set existing key — must not evict 'b'
    expect(m.size).toBe(2);
    expect(m.get('a')).toBe(99);
    expect(m.has('b')).toBe(true);
  });

  it('stays bounded under a flood far exceeding the cap', () => {
    const m = new Map<string, number>();
    for (let i = 0; i < 1000; i++) boundedSet(m, `k${i}`, i, 10);
    expect(m.size).toBe(10);
    // Only the most recent 10 keys survive.
    expect(m.has('k999')).toBe(true);
    expect(m.has('k989')).toBe(false);
  });
});

describe('M1b — rate-bucket maps stay bounded under a distinct-key flood', () => {
  // The rate buckets only shrink via the 5-min reaper, so a distinct-key flood
  // (forged UPNs / NAT churn) must not grow them unbounded between passes.
  it('caps the pre-auth (per-IP) bucket map at MCP_RATE_BUCKET_MAX_ENTRIES', () => {
    for (let i = 0; i < RATE_BUCKET_TEST_CAP * 20; i++) {
      isPreAuthRateLimited(`10.0.${Math.floor(i / 256)}.${i % 256}`);
    }
    expect(getRateBucketSizes().preAuth).toBeLessThanOrEqual(RATE_BUCKET_TEST_CAP);
  });
});

describe('M1 — isPreAuthRateLimited (per-IP pre-auth throttle)', () => {
  // Budget pinned to PRE_AUTH_TEST_LIMIT above. Use a unique IP per test so the
  // module-level bucket map does not leak across cases.
  let ipSeed = 0;
  const uniqueIp = () => `203.0.113.${ipSeed++}`;

  it('allows requests up to the budget, then throttles', () => {
    const ip = uniqueIp();
    let throttledAt = -1;
    for (let i = 0; i < PRE_AUTH_TEST_LIMIT + 5; i++) {
      if (isPreAuthRateLimited(ip)) { throttledAt = i; break; }
    }
    // The (limit+1)th call is the first to exceed the budget.
    expect(throttledAt).toBe(PRE_AUTH_TEST_LIMIT);
  });

  it('tracks distinct IPs independently', () => {
    const a = uniqueIp();
    const b = uniqueIp();
    // Burn a's budget; b must be unaffected.
    for (let i = 0; i < PRE_AUTH_TEST_LIMIT; i++) isPreAuthRateLimited(a);
    expect(isPreAuthRateLimited(a)).toBe(true);
    expect(isPreAuthRateLimited(b)).toBe(false);
  });
});

describe('M3 — getPublicBaseUrl forwarded-header fallback (no MCP_PUBLIC_URL pin)', () => {
  // This file is imported with MCP_PUBLIC_URL unset and NODE_ENV != production,
  // so the config module captured no pin and getPublicBaseUrl derives from
  // forwarded headers. The pin-wins path is covered in config-public-url.test.ts.
  it('prefers X-Forwarded-Host / X-Forwarded-Proto', () => {
    const req = {
      headers: { 'x-forwarded-proto': 'https', 'x-forwarded-host': 'mcp.example.com', host: 'internal:8080' },
      protocol: 'http',
    } as unknown as Parameters<typeof getPublicBaseUrl>[0];
    expect(getPublicBaseUrl(req)).toBe('https://mcp.example.com');
  });

  it('falls back to req.protocol + Host when no forwarded headers', () => {
    const req = {
      headers: { host: 'localhost:8080' },
      protocol: 'http',
    } as unknown as Parameters<typeof getPublicBaseUrl>[0];
    expect(getPublicBaseUrl(req)).toBe('http://localhost:8080');
  });
});

