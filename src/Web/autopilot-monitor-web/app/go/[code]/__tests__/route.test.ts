/**
 * Tests for the OOBE bootstrap script route handler: app/go/[code]/route.ts.
 *
 * This is the web tier's single security-critical server endpoint: it proxies to the
 * backend and inlines validated values into a PowerShell script that runs as SYSTEM during
 * Windows OOBE. The VALIDATOR (utils/bootstrapValidation) is already well-tested; the HANDLER
 * itself (code-format gate, backend-error passthrough, 200-on-error `irm|iex` contract,
 * errorScript quote-escaping + length cap, and the "never leak the offending value" rule) was
 * not. These tests cover the first line of defense with fetch stubbed.
 */
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { GET } from "../route";

function makeCtx(code: string) {
  return { params: Promise.resolve({ code }) };
}

function invoke(code: string) {
  return GET(new Request(`https://portal.test/go/${code}`), makeCtx(code));
}

function okBackend(body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status: 200,
    headers: { "Content-Type": "application/json" },
  });
}

function errBackend(status: number, body: unknown = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

/** A bootstrap response that passes validateBootstrapResponse. */
function validPayload() {
  const expiresAt = new Date(Date.now() + 24 * 60 * 60 * 1000).toISOString();
  return {
    success: true,
    tenantId: "11111111-1111-1111-1111-111111111111",
    token: "22222222-2222-2222-2222-222222222222",
    agentDownloadUrl:
      "https://autopilotmonitor.blob.core.windows.net/agent/agent-v2.zip",
    expiresAt,
  };
}

const fetchMock = vi.fn();

beforeEach(() => {
  vi.stubGlobal("fetch", fetchMock);
  fetchMock.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

// ── Code-format gate: rejected BEFORE any backend call ──────────────────────
describe("code-format validation", () => {
  const badCodes: Array<[label: string, code: string]> = [
    ["empty", ""],
    ["too short (3)", "abc"],
    ["too long (11)", "abcdefghijk"],
    ["space", "ab cd"],
    ["slash / path traversal", "../etc"],
    ["encoded slash", "ab%2Fcd"],
    ["ps metachar $", "abc$"],
    ["quote", "ab'cd"],
    ["dot", "ab.cd"],
  ];

  it.each(badCodes)("rejects %s without hitting the backend", async (_label, code) => {
    const res = await invoke(code);
    expect(fetchMock).not.toHaveBeenCalled();
    // 200 so `irm | iex` still runs and surfaces the Write-Host error.
    expect(res.status).toBe(200);
    const body = await res.text();
    expect(body).toContain("ERROR: Invalid bootstrap code format.");
  });

  const goodCodes: Array<[string]> = [["abcd"], ["ABC123"], ["a1b2c3d4e5"]];
  it.each(goodCodes)("accepts well-formed code %s (proceeds to backend)", async (code) => {
    fetchMock.mockResolvedValueOnce(errBackend(404, {}));
    await invoke(code);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(String(fetchMock.mock.calls[0][0])).toContain(
      `/api/bootstrap/validate/${code}`,
    );
  });
});

// ── Backend error passthrough ───────────────────────────────────────────────
describe("backend validation errors", () => {
  it("passes through the backend message", async () => {
    fetchMock.mockResolvedValueOnce(errBackend(410, { message: "Code was revoked by admin." }));
    const res = await invoke("abcd");
    expect(res.status).toBe(200);
    expect(await res.text()).toContain("ERROR: Code was revoked by admin.");
  });

  it("falls back to a default message when the backend body has none", async () => {
    fetchMock.mockResolvedValueOnce(errBackend(404, {}));
    const res = await invoke("abcd");
    expect(await res.text()).toContain(
      "ERROR: Bootstrap code not found, expired, or revoked.",
    );
  });

  it("handles a non-JSON backend error body gracefully", async () => {
    fetchMock.mockResolvedValueOnce(
      new Response("<html>502</html>", { status: 502 }),
    );
    const res = await invoke("abcd");
    expect(res.status).toBe(200);
    expect(await res.text()).toContain("ERROR:");
  });

  it("returns a network-error script when fetch throws", async () => {
    fetchMock.mockRejectedValueOnce(new Error("ECONNREFUSED"));
    const res = await invoke("abcd");
    expect(res.status).toBe(200);
    const body = await res.text();
    expect(body).toContain("Failed to validate bootstrap code");
    // The raw exception text must never be inlined into the script.
    expect(body).not.toContain("ECONNREFUSED");
  });
});

// ── errorScript hardening: quote-escaping + length cap ───────────────────────
describe("errorScript hardening", () => {
  it("escapes single quotes for the PowerShell string literal", async () => {
    fetchMock.mockResolvedValueOnce(errBackend(400, { message: "it's a 'bad' code" }));
    const body = await (await invoke("abcd")).text();
    // Single quotes doubled; the surrounding literal stays intact.
    expect(body).toContain("it''s a ''bad'' code");
  });

  it("caps an oversized backend message (raw length, before escaping)", async () => {
    const huge = "X".repeat(300);
    fetchMock.mockResolvedValueOnce(errBackend(400, { message: huge }));
    const body = await (await invoke("abcd")).text();
    expect(body).toContain("X".repeat(200) + "...");
    expect(body).not.toContain("X".repeat(201));
  });
});

// ── Success path ────────────────────────────────────────────────────────────
describe("valid bootstrap → generated OOBE script", () => {
  it("emits the PS script with the validated values and hardened headers", async () => {
    const payload = validPayload();
    fetchMock.mockResolvedValueOnce(okBackend(payload));

    const res = await invoke("abcd");

    expect(res.status).toBe(200);
    expect(res.headers.get("Content-Type")).toBe("text/plain; charset=utf-8");
    expect(res.headers.get("X-Content-Type-Options")).toBe("nosniff");
    expect(res.headers.get("Cache-Control")).toContain("no-store");

    const body = await res.text();
    expect(body).toContain("#Requires -RunAsAdministrator");
    expect(body).toContain(`--tenant-id '${payload.tenantId}'`);
    expect(body).toContain(`--bootstrap-token '${payload.token}'`);
    expect(body).toContain(`$AgentDownloadUrl = '${payload.agentDownloadUrl}'`);
  });

  it("rejects a backend payload that fails validation WITHOUT leaking the offending value", async () => {
    // Valid shape/success, but hostile agentDownloadUrl (foreign host + PS injection).
    const hostile = {
      success: true,
      tenantId: "11111111-1111-1111-1111-111111111111",
      token: "22222222-2222-2222-2222-222222222222",
      agentDownloadUrl: "https://evil.example.com/x.zip'; $(calc); '",
      expiresAt: new Date(Date.now() + 3600_000).toISOString(),
    };
    const warn = vi.spyOn(console, "warn").mockImplementation(() => {});
    fetchMock.mockResolvedValueOnce(okBackend(hostile));

    const res = await invoke("abcd");
    const body = await res.text();

    expect(res.status).toBe(200);
    expect(body).toContain("ERROR: Bootstrap response failed validation. Contact support.");
    // The generated script must not be produced, and the payload must not appear anywhere.
    expect(body).not.toContain("#Requires -RunAsAdministrator");
    expect(body).not.toContain("evil.example.com");
    expect(body).not.toContain("calc");
    warn.mockRestore();
  });
});
