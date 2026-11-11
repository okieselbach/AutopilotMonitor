import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("../appInsights", () => ({ trackEvent: vi.fn() }));
vi.mock("../viewAs", () => ({ viewAsHeaders: vi.fn(() => ({})) }));

function response(status: number): Response {
  return new Response(status === 204 ? null : "{}", { status, headers: { "Content-Type": "application/json" } });
}

const fetchMock = () => globalThis.fetch as ReturnType<typeof vi.fn>;
const token = () => vi.fn().mockResolvedValue("tok");
const hintOf = (call: number) => (fetchMock().mock.calls[call][1].headers as Headers).get("X-Tenant-Config");

/**
 * A browser with localStorage and a freshly loaded fetch layer. The mark has an in-memory part
 * that lives as long as the page, so every page load gets its own module instance.
 */
async function pageLoad(browser: Map<string, string>) {
  vi.stubGlobal("window", {
    localStorage: {
      getItem: (key: string) => browser.get(key) ?? null,
      setItem: (key: string, value: string) => void browser.set(key, value),
    },
  });
  vi.resetModules();
  return (await import("../authenticatedFetch")).authenticatedFetch;
}

describe("authenticatedFetch after a configuration write", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("asks for the stored row on every later call", async () => {
    const authenticatedFetch = await pageLoad(new Map());
    fetchMock().mockImplementation(() => Promise.resolve(response(200)));

    await authenticatedFetch("/api/config/t1", token());
    await authenticatedFetch("/api/config/t1/fields", token(), { method: "PATCH", body: "{}" });
    await authenticatedFetch("/api/config/t1", token());
    await authenticatedFetch("/api/sessions", token());

    expect([hintOf(0), hintOf(1), hintOf(2), hintOf(3)]).toEqual([null, null, "stored", "stored"]);
  });

  it("leaves the cache mode alone: with no-store the browser would preflight every call again", async () => {
    const authenticatedFetch = await pageLoad(new Map());
    fetchMock().mockImplementation(() => Promise.resolve(response(200)));

    await authenticatedFetch("/api/config/t1/fields", token(), { method: "PATCH", body: "{}" });
    await authenticatedFetch("/api/config/t1", token());
    await authenticatedFetch("/api/sessions", token(), { cache: "no-cache" });

    expect(hintOf(1)).toBe("stored");
    expect(fetchMock().mock.calls[1][1].cache).toBeUndefined();
    expect(fetchMock().mock.calls[2][1].cache).toBe("no-cache");
  });

  it("still asks after a reload: the consent redirect returns to a fresh page within seconds", async () => {
    const browser = new Map<string, string>();
    fetchMock().mockImplementation(() => Promise.resolve(response(200)));
    await (await pageLoad(browser))("/api/config/t1/setup-guide", token(), { method: "POST", body: "{}" });

    await (await pageLoad(browser))("/api/config/t1", token());

    expect(hintOf(1)).toBe("stored");
  });

  it("counts the terms confirmation: auth/me must not answer from an older copy", async () => {
    const authenticatedFetch = await pageLoad(new Map());
    fetchMock().mockResolvedValueOnce(response(204)).mockResolvedValueOnce(response(200));

    await authenticatedFetch("/api/auth/dpa-acceptance", token(), { method: "POST" });
    await authenticatedFetch("/api/auth/me", token());

    expect(hintOf(1)).toBe("stored");
  });

  it("counts a write that needed the 401 retry", async () => {
    const authenticatedFetch = await pageLoad(new Map());
    fetchMock()
      .mockResolvedValueOnce(response(401))
      .mockResolvedValueOnce(response(200))
      .mockResolvedValueOnce(response(200));
    const getToken = vi.fn().mockResolvedValueOnce("stale").mockResolvedValue("fresh");

    await authenticatedFetch("/api/config/t1/fields", getToken, { method: "PATCH", body: "{}" });
    await authenticatedFetch("/api/config/t1", getToken);

    expect(hintOf(2)).toBe("stored");
  });

  it("leaves no mark after a failed write", async () => {
    const authenticatedFetch = await pageLoad(new Map());
    fetchMock().mockResolvedValueOnce(response(400)).mockResolvedValueOnce(response(200));

    await authenticatedFetch("/api/config/t1/fields", token(), { method: "PATCH", body: "{}" });
    await authenticatedFetch("/api/config/t1", token());

    expect(hintOf(1)).toBeNull();
  });

  it("leaves no mark after a write that is not a configuration write", async () => {
    const authenticatedFetch = await pageLoad(new Map());
    fetchMock().mockImplementation(() => Promise.resolve(response(200)));

    await authenticatedFetch("/api/realtime/groups/join", token(), { method: "POST", body: "{}" });
    await authenticatedFetch("/api/sessions/abc", token(), { method: "DELETE" });
    await authenticatedFetch("/api/config/t1", token());

    expect(hintOf(2)).toBeNull();
    expect(fetchMock().mock.calls[2][1].cache).toBeUndefined();
  });
});
