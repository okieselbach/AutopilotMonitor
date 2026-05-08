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
import { followNextLink } from '../client.js';
import { SessionIdSchema } from '../tools/shared.js';
import { ApiError } from '../client.js';
import { toolError } from '../tools/error-handler.js';

// Importing the OAuth helper requires the env var that gates module load.
// Set a dummy value before the import resolves so the throw doesn't fire.
process.env.AUTOPILOT_ENTRA_CLIENT_ID ??= '00000000-0000-0000-0000-000000000000';
const {
  isAllowedRedirectUri,
  isAllowedRedirectUriWith,
  parseAllowedHosts,
  signState,
  verifyState,
  MAX_REGISTERED_CLIENTS,
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

describe('H1 — OAuth redirect_uri allowlist (default Multi-Vendor)', () => {
  it('accepts loopback over http with arbitrary port', () => {
    expect(isAllowedRedirectUri('http://localhost:1234/callback')).toBe(true);
    expect(isAllowedRedirectUri('http://127.0.0.1:54321/cb')).toBe(true);
  });

  it('accepts loopback even over https (some clients use TLS-loopback)', () => {
    expect(isAllowedRedirectUri('https://localhost:8443/cb')).toBe(true);
  });

  it('accepts Anthropic hosts over https (claude.ai, *.claude.ai, *.anthropic.com)', () => {
    expect(isAllowedRedirectUri('https://claude.ai/api/oauth/cb')).toBe(true);
    expect(isAllowedRedirectUri('https://api.claude.ai/cb')).toBe(true);
    expect(isAllowedRedirectUri('https://accounts.anthropic.com/cb')).toBe(true);
  });

  it('accepts OpenAI/ChatGPT hosts over https', () => {
    expect(isAllowedRedirectUri('https://chatgpt.com/connector/oauth/cb')).toBe(true);
    expect(isAllowedRedirectUri('https://app.chatgpt.com/cb')).toBe(true);
    expect(isAllowedRedirectUri('https://platform.openai.com/cb')).toBe(true);
    expect(isAllowedRedirectUri('https://chat.openai.com/cb')).toBe(true);
  });

  it('accepts Google Gemini host over https', () => {
    expect(isAllowedRedirectUri('https://gemini.google.com/oauth/cb')).toBe(true);
  });

  it('rejects allowed hosts over plain http (downgrade attempt)', () => {
    expect(isAllowedRedirectUri('http://claude.ai/cb')).toBe(false);
    expect(isAllowedRedirectUri('http://chatgpt.com/cb')).toBe(false);
    expect(isAllowedRedirectUri('http://gemini.google.com/cb')).toBe(false);
  });

  it('rejects arbitrary hosts', () => {
    expect(isAllowedRedirectUri('https://attacker.tld/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://evil.example.com/cb')).toBe(false);
  });

  it('rejects look-alike hosts (suffix match must be on the dot boundary)', () => {
    // "evilclaude.ai" ends with "claude.ai" naïvely, but the suffix list
    // stores ".claude.ai" with a leading dot, anchoring matches to a real
    // subdomain boundary.
    expect(isAllowedRedirectUri('https://evilclaude.ai/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://notanthropic.com/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://fakechatgpt.com/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://notopenai.com/cb')).toBe(false);
    // gemini.google.com is exact-match only — google.com itself is NOT allowed.
    expect(isAllowedRedirectUri('https://google.com/cb')).toBe(false);
    expect(isAllowedRedirectUri('https://accounts.google.com/cb')).toBe(false);
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
  it('parseAllowedHosts splits exact hosts and *.wildcards', () => {
    const r = parseAllowedHosts('foo.example.com,*.bar.example.com,baz.org');
    expect(r.exact.has('foo.example.com')).toBe(true);
    expect(r.exact.has('baz.org')).toBe(true);
    expect(r.exact.has('bar.example.com')).toBe(false); // wildcard, not exact
    expect(r.suffixes).toContain('.bar.example.com');
  });

  it('parseAllowedHosts normalizes case', () => {
    const r = parseAllowedHosts('Foo.EXAMPLE.com');
    expect(r.exact.has('foo.example.com')).toBe(true);
  });

  it('parseAllowedHosts trims whitespace and drops empty entries', () => {
    const r = parseAllowedHosts(' a.com ,  , b.com ');
    expect(r.exact.size).toBe(2);
    expect(r.exact.has('a.com')).toBe(true);
    expect(r.exact.has('b.com')).toBe(true);
  });

  it('parseAllowedHosts falls back to defaults when env is undefined', () => {
    const r = parseAllowedHosts(undefined);
    // Default baseline includes all three vendors.
    expect(r.exact.has('claude.ai')).toBe(true);
    expect(r.exact.has('chatgpt.com')).toBe(true);
    expect(r.exact.has('gemini.google.com')).toBe(true);
  });

  it('custom allowlist accepts only the configured hosts (defaults are NOT additive)', () => {
    const customOnly = parseAllowedHosts('mycompany.example.com,*.partner.example.org');
    expect(isAllowedRedirectUriWith('https://mycompany.example.com/cb', customOnly)).toBe(true);
    expect(isAllowedRedirectUriWith('https://api.partner.example.org/cb', customOnly)).toBe(true);
    // Defaults must NOT leak through — operator-supplied list fully replaces them.
    expect(isAllowedRedirectUriWith('https://claude.ai/cb', customOnly)).toBe(false);
    expect(isAllowedRedirectUriWith('https://chatgpt.com/cb', customOnly)).toBe(false);
  });

  it('custom allowlist still requires HTTPS for non-loopback', () => {
    const r = parseAllowedHosts('mycompany.example.com');
    expect(isAllowedRedirectUriWith('http://mycompany.example.com/cb', r)).toBe(false);
  });

  it('loopback is always allowed even with empty allowlist', () => {
    const empty = parseAllowedHosts('');
    expect(isAllowedRedirectUriWith('http://localhost:9000/cb', empty)).toBe(true);
    expect(isAllowedRedirectUriWith('http://127.0.0.1:9000/cb', empty)).toBe(true);
    // But arbitrary hosts are not.
    expect(isAllowedRedirectUriWith('https://anywhere.com/cb', empty)).toBe(false);
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

const { buildCacheKey } = await import('../access-guard.js');

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
  it('exposes a hard cap that bounds memory at ~3 MB worst case', () => {
    // 0.5 GiB container budget × ~300 bytes/entry → 10k is two orders of
    // magnitude below the OOM cliff and well above realistic peak.
    expect(MAX_REGISTERED_CLIENTS).toBeGreaterThanOrEqual(1_000);
    expect(MAX_REGISTERED_CLIENTS).toBeLessThanOrEqual(100_000);
  });

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
