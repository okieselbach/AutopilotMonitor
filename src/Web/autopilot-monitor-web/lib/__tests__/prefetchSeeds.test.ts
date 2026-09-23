import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SEED_TTL_MS, clearSeeds, registerSeed, takeSeed } from "../prefetchSeeds";

const URL_A = "https://api.example/api/sessions?tenantId=t&pageSize=10";

describe("prefetchSeeds", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    clearSeeds();
  });
  afterEach(() => {
    vi.useRealTimers();
  });

  it("hands a registered seed to exactly one taker", () => {
    const response = Promise.resolve(new Response("{}"));
    registerSeed(URL_A, "corr-1", response);

    const first = takeSeed(URL_A);
    expect(first?.response).toBe(response);
    expect(first?.correlationId).toBe("corr-1");
    expect(takeSeed(URL_A)).toBeNull();
  });

  it("matches on the exact URL only", () => {
    registerSeed(URL_A, "corr-1", Promise.resolve(new Response("{}")));
    expect(takeSeed(URL_A + "&days=7")).toBeNull();
    expect(takeSeed(URL_A)).not.toBeNull();
  });

  it("expires after SEED_TTL_MS so a later navigation never reuses an old response", () => {
    registerSeed(URL_A, "corr-1", Promise.resolve(new Response("{}")));
    vi.advanceTimersByTime(SEED_TTL_MS + 1);
    expect(takeSeed(URL_A)).toBeNull();
  });

  it("drops a seed whose fetch rejected", async () => {
    const failing = Promise.reject(new Error("network"));
    registerSeed(URL_A, "corr-1", failing);
    await failing.catch(() => undefined);
    await Promise.resolve();
    expect(takeSeed(URL_A)).toBeNull();
  });

  it("clearSeeds removes everything", () => {
    registerSeed(URL_A, "corr-1", Promise.resolve(new Response("{}")));
    clearSeeds();
    expect(takeSeed(URL_A)).toBeNull();
  });
});
