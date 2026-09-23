import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { authenticatedFetch } from "../authenticatedFetch";
import { clearSeeds, registerSeed } from "../prefetchSeeds";
import { trackEvent } from "../appInsights";

vi.mock("../appInsights", () => ({ trackEvent: vi.fn() }));

const URL_A = "https://api/x?pageSize=10";

function mockResponse(status: number, body: unknown = {}): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

describe("authenticatedFetch with response seeds", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
    clearSeeds();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("returns the seeded response for the first identical GET without fetching again", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    const seeded = mockResponse(200, { seeded: true });
    registerSeed(URL_A, "corr-seed", Promise.resolve(seeded));

    const res = await authenticatedFetch(URL_A, getToken, { headers: { "Content-Type": "application/json" } });

    expect(res).toBe(seeded);
    expect(globalThis.fetch).not.toHaveBeenCalled();
    // The seed is single-use: the next call fetches normally.
    (globalThis.fetch as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockResponse(200, { fresh: true }));
    const second = await authenticatedFetch(URL_A, getToken);
    expect(await second.json()).toEqual({ fresh: true });
    expect(globalThis.fetch).toHaveBeenCalledTimes(1);
  });

  it("ignores a seed for a POST", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    registerSeed(URL_A, "corr-seed", Promise.resolve(mockResponse(200, { seeded: true })));
    (globalThis.fetch as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockResponse(200, { posted: true }));

    const res = await authenticatedFetch(URL_A, getToken, { method: "POST", body: "{}" });

    expect(await res.json()).toEqual({ posted: true });
    expect(globalThis.fetch).toHaveBeenCalledTimes(1);
  });

  it("falls through to a normal fetch when the seed answered 401", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    registerSeed(URL_A, "corr-seed", Promise.resolve(mockResponse(401)));
    (globalThis.fetch as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockResponse(200, { fresh: true }));

    const res = await authenticatedFetch(URL_A, getToken);

    expect(await res.json()).toEqual({ fresh: true });
    expect(globalThis.fetch).toHaveBeenCalledTimes(1);
  });

  it("falls through to a normal fetch when the seed rejected", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    const failing = Promise.reject(new Error("network"));
    registerSeed(URL_A, "corr-seed", failing);
    await failing.catch(() => undefined);
    (globalThis.fetch as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockResponse(200, { fresh: true }));

    const res = await authenticatedFetch(URL_A, getToken);

    expect(await res.json()).toEqual({ fresh: true });
    expect(globalThis.fetch).toHaveBeenCalledTimes(1);
  });

  it("reports a failed seeded response under the seed's correlation id", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    registerSeed(URL_A, "corr-seed", Promise.resolve(mockResponse(500)));

    const res = await authenticatedFetch(URL_A, getToken);

    expect(res.status).toBe(500);
    expect(trackEvent).toHaveBeenCalledWith("api_request_failed", {
      path: "/x",
      method: "GET",
      status: 500,
      correlationId: "corr-seed",
    });
  });

  it("still requires a token before using a seed", async () => {
    const getToken = vi.fn().mockResolvedValue(null);
    registerSeed(URL_A, "corr-seed", Promise.resolve(mockResponse(200)));

    await expect(authenticatedFetch(URL_A, getToken)).rejects.toThrow("session has expired");
  });
});
