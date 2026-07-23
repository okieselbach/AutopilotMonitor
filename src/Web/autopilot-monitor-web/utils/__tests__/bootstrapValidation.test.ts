import { describe, it, expect, beforeEach, afterEach, vi } from "vitest";
import { validateBootstrapResponse } from "../bootstrapValidation";

const VALID_TENANT_ID = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
const VALID_TOKEN = "11111111-2222-3333-4444-555555555555";
// Legacy blob host — stays allowlisted for the customer-migration transition.
const VALID_URL =
  "https://autopilotmonitor.blob.core.windows.net/agent/AutopilotMonitor-Agent.zip";

// Download alias — what ValidateBootstrapCodeFunction serves going forward.
const VALID_URL_ALIAS =
  "https://download.autopilotmonitor.com/agent/AutopilotMonitor-Agent.zip";

// Fixed system time so expiresAt math is deterministic
const FIXED_NOW = new Date("2026-04-08T12:00:00.000Z").getTime();

function inFuture(ms: number): string {
  return new Date(FIXED_NOW + ms).toISOString();
}

function validResponse(overrides: Record<string, unknown> = {}) {
  return {
    success: true,
    tenantId: VALID_TENANT_ID,
    token: VALID_TOKEN,
    agentDownloadUrl: VALID_URL,
    expiresAt: inFuture(60 * 60 * 1000), // 1 hour
    ...overrides,
  };
}

describe("validateBootstrapResponse", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(FIXED_NOW);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  // ---------- Happy paths ----------

  describe("happy path", () => {
    it("accepts a realistic backend response", () => {
      const result = validateBootstrapResponse(validResponse());
      expect(result.ok).toBe(true);
      if (result.ok) {
        expect(result.value.tenantId).toBe(VALID_TENANT_ID);
        expect(result.value.token).toBe(VALID_TOKEN);
        expect(result.value.agentDownloadUrl).toBe(VALID_URL);
      }
    });

    it("accepts the download-alias host (what the backend serves going forward)", () => {
      const result = validateBootstrapResponse(
        validResponse({ agentDownloadUrl: VALID_URL_ALIAS }),
      );
      expect(result.ok).toBe(true);
      if (result.ok) {
        expect(result.value.agentDownloadUrl).toBe(VALID_URL_ALIAS);
      }
    });

    it("accepts STJ 'O' format with 7 fractional digits", () => {
      const result = validateBootstrapResponse(
        validResponse({ expiresAt: "2026-04-08T13:34:56.7891234Z" }),
      );
      expect(result.ok).toBe(true);
    });

    it("accepts whole-second timestamp without fractional digits", () => {
      const result = validateBootstrapResponse(
        validResponse({ expiresAt: "2026-04-08T13:34:56Z" }),
      );
      expect(result.ok).toBe(true);
    });

    it("accepts numeric timezone offset", () => {
      const result = validateBootstrapResponse(
        validResponse({ expiresAt: "2026-04-08T15:34:56+02:00" }),
      );
      expect(result.ok).toBe(true);
    });
  });

  // ---------- Injection payloads (the actual vuln) ----------

  describe("injection payloads", () => {
    it("rejects token with PS double-quote escape", () => {
      const result = validateBootstrapResponse(
        validResponse({ token: 'x"; Start-Process calc.exe #' }),
      );
      expect(result).toEqual({ ok: false, reason: "token" });
    });

    it("rejects tenantId with PS subshell expansion", () => {
      const result = validateBootstrapResponse(
        validResponse({ tenantId: "$(calc)" }),
      );
      expect(result).toEqual({ ok: false, reason: "tenantId" });
    });

    it("rejects agentDownloadUrl on a foreign host", () => {
      const result = validateBootstrapResponse(
        validResponse({ agentDownloadUrl: "https://evil.com/agent/x.zip" }),
      );
      expect(result).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });

    it("rejects agentDownloadUrl with $ in pathname (PS subshell)", () => {
      const result = validateBootstrapResponse(
        validResponse({
          agentDownloadUrl:
            "https://autopilotmonitor.blob.core.windows.net/agent/$(calc).zip",
        }),
      );
      expect(result).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });

    it("rejects agentDownloadUrl with backtick in pathname", () => {
      const result = validateBootstrapResponse(
        validResponse({
          agentDownloadUrl:
            "https://autopilotmonitor.blob.core.windows.net/agent/`whoami`.zip",
        }),
      );
      expect(result).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });

    it("rejects agentDownloadUrl with query string (smuggling)", () => {
      const result = validateBootstrapResponse(
        validResponse({
          agentDownloadUrl:
            "https://autopilotmonitor.blob.core.windows.net/agent/x.zip?sas=evil",
        }),
      );
      expect(result).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });

    it("rejects agentDownloadUrl with hash fragment", () => {
      const result = validateBootstrapResponse(
        validResponse({
          agentDownloadUrl:
            "https://autopilotmonitor.blob.core.windows.net/agent/x.zip#frag",
        }),
      );
      expect(result).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });

    it("rejects agentDownloadUrl with trailing-dot hostname", () => {
      const result = validateBootstrapResponse(
        validResponse({
          agentDownloadUrl:
            "https://autopilotmonitor.blob.core.windows.net./agent/x.zip",
        }),
      );
      expect(result).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });

    it("rejects agentDownloadUrl with embedded credentials", () => {
      const result = validateBootstrapResponse(
        validResponse({
          agentDownloadUrl:
            "https://user:pass@autopilotmonitor.blob.core.windows.net/agent/x.zip",
        }),
      );
      expect(result).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });

    it("rejects agentDownloadUrl with non-standard port", () => {
      // Note: :443 is normalized to "" by Node's URL parser (default port),
      // so it can't be blocked and represents no attack surface. Non-default
      // ports like :8443 do remain in url.port and are correctly rejected.
      const result = validateBootstrapResponse(
        validResponse({
          agentDownloadUrl:
            "https://autopilotmonitor.blob.core.windows.net:8443/agent/x.zip",
        }),
      );
      expect(result).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });

    it("rejects agentDownloadUrl over http (not https)", () => {
      const result = validateBootstrapResponse(
        validResponse({
          agentDownloadUrl:
            "http://autopilotmonitor.blob.core.windows.net/agent/x.zip",
        }),
      );
      expect(result).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });

    it("rejects agentDownloadUrl with leading-dot filename", () => {
      const result = validateBootstrapResponse(
        validResponse({
          agentDownloadUrl:
            "https://autopilotmonitor.blob.core.windows.net/agent/.hidden.zip",
        }),
      );
      expect(result).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });

    it("rejects agentDownloadUrl with non-zip extension", () => {
      const result = validateBootstrapResponse(
        validResponse({
          agentDownloadUrl:
            "https://autopilotmonitor.blob.core.windows.net/agent/x.exe",
        }),
      );
      expect(result).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });
  });

  // ---------- Shape rejection ----------

  describe("shape", () => {
    it("rejects null", () => {
      expect(validateBootstrapResponse(null)).toEqual({ ok: false, reason: "shape" });
    });

    it("rejects array", () => {
      expect(validateBootstrapResponse([])).toEqual({ ok: false, reason: "shape" });
    });

    it("rejects string primitive", () => {
      expect(validateBootstrapResponse("string")).toEqual({
        ok: false,
        reason: "shape",
      });
    });

    it("rejects number primitive", () => {
      expect(validateBootstrapResponse(42)).toEqual({ ok: false, reason: "shape" });
    });

    it("rejects empty object (success missing)", () => {
      expect(validateBootstrapResponse({})).toEqual({
        ok: false,
        reason: "success",
      });
    });

    it("rejects prototype-pollution probe (fields on __proto__ only)", () => {
      const probe = Object.create({
        success: true,
        tenantId: VALID_TENANT_ID,
        token: VALID_TOKEN,
        agentDownloadUrl: VALID_URL,
        expiresAt: inFuture(60 * 60 * 1000),
      });
      expect(validateBootstrapResponse(probe)).toEqual({
        ok: false,
        reason: "success",
      });
    });
  });

  // ---------- success coercion ----------

  describe("success field", () => {
    it("rejects success: false", () => {
      expect(
        validateBootstrapResponse(validResponse({ success: false })),
      ).toEqual({ ok: false, reason: "success" });
    });

    it("rejects success: 1 (truthy number)", () => {
      expect(validateBootstrapResponse(validResponse({ success: 1 }))).toEqual({
        ok: false,
        reason: "success",
      });
    });

    it('rejects success: "true" (string)', () => {
      expect(
        validateBootstrapResponse(validResponse({ success: "true" })),
      ).toEqual({ ok: false, reason: "success" });
    });

    it("rejects missing success", () => {
      const { success: _omit, ...rest } = validResponse();
      void _omit;
      expect(validateBootstrapResponse(rest)).toEqual({
        ok: false,
        reason: "success",
      });
    });
  });

  // ---------- Control chars / non-ASCII ----------

  describe("control chars and non-ASCII", () => {
    it("rejects tenantId containing null byte", () => {
      // Pad to length 36 so we hit ASCII filter, not length check
      const padded = "\u0000" + VALID_TENANT_ID.slice(1);
      expect(
        validateBootstrapResponse(validResponse({ tenantId: padded })),
      ).toEqual({ ok: false, reason: "tenantId" });
    });

    it("rejects token containing newline", () => {
      const padded = "\n" + VALID_TOKEN.slice(1);
      expect(validateBootstrapResponse(validResponse({ token: padded }))).toEqual({
        ok: false,
        reason: "token",
      });
    });

    it("rejects expiresAt with fullwidth digits (non-ASCII)", () => {
      expect(
        validateBootstrapResponse(
          validResponse({ expiresAt: "2026-04-08T13:34:56\uFF10" }),
        ),
      ).toEqual({ ok: false, reason: "expiresAt" });
    });
  });

  // ---------- Length and field-type bounds ----------

  describe("length and type bounds", () => {
    it("rejects 35-char tenantId", () => {
      expect(
        validateBootstrapResponse(
          validResponse({ tenantId: VALID_TENANT_ID.slice(0, 35) }),
        ),
      ).toEqual({ ok: false, reason: "tenantId" });
    });

    it("rejects empty token", () => {
      expect(validateBootstrapResponse(validResponse({ token: "" }))).toEqual({
        ok: false,
        reason: "token",
      });
    });

    it("rejects agentDownloadUrl over 256 chars", () => {
      const longPath = "x".repeat(260);
      expect(
        validateBootstrapResponse(
          validResponse({
            agentDownloadUrl: `https://autopilotmonitor.blob.core.windows.net/agent/${longPath}.zip`,
          }),
        ),
      ).toEqual({ ok: false, reason: "agentDownloadUrl" });
    });

    it("rejects tenantId of wrong type (number)", () => {
      expect(
        validateBootstrapResponse(validResponse({ tenantId: 12345 })),
      ).toEqual({ ok: false, reason: "tenantId" });
    });

    it("rejects token of wrong type (null)", () => {
      expect(validateBootstrapResponse(validResponse({ token: null }))).toEqual({
        ok: false,
        reason: "token",
      });
    });
  });

  // ---------- expiresAt time bounds ----------

  describe("expiresAt time bounds", () => {
    it("rejects expired (in the past)", () => {
      expect(
        validateBootstrapResponse(
          validResponse({ expiresAt: inFuture(-60 * 1000) }),
        ),
      ).toEqual({ ok: false, reason: "expiresAt" });
    });

    it("rejects exactly now", () => {
      expect(
        validateBootstrapResponse(validResponse({ expiresAt: inFuture(0) })),
      ).toEqual({ ok: false, reason: "expiresAt" });
    });

    it("rejects more than 14 days in the future", () => {
      expect(
        validateBootstrapResponse(
          validResponse({ expiresAt: inFuture(15 * 24 * 60 * 60 * 1000) }),
        ),
      ).toEqual({ ok: false, reason: "expiresAt" });
    });

    it("accepts 7 days in the future (within backend cap)", () => {
      const result = validateBootstrapResponse(
        validResponse({ expiresAt: inFuture(7 * 24 * 60 * 60 * 1000) }),
      );
      expect(result.ok).toBe(true);
    });

    it("rejects malformed string 'not-a-date'", () => {
      expect(
        validateBootstrapResponse(validResponse({ expiresAt: "not-a-date" })),
      ).toEqual({ ok: false, reason: "expiresAt" });
    });

    it("rejects date-only string (no time component)", () => {
      expect(
        validateBootstrapResponse(validResponse({ expiresAt: "2026-04-08" })),
      ).toEqual({ ok: false, reason: "expiresAt" });
    });

    it("rejects timestamp without timezone", () => {
      expect(
        validateBootstrapResponse(
          validResponse({ expiresAt: "2026-04-08T13:34:56" }),
        ),
      ).toEqual({ ok: false, reason: "expiresAt" });
    });
  });
});
