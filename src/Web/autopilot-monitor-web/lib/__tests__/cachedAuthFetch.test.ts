import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import {
  CONFIG_PATH_PREFIX,
  cachedAuthFetchJson,
  clearCachedAuthFetch,
  invalidateCachedAuthFetch,
} from "../cachedAuthFetch";
import { __resetDedupedAuthFetchForTests } from "../dedupedAuthFetch";
import { ApiError } from "../apiClient";

const URL_FLAGS = "https://api.example/api/config/t1/feature-flags";
const URL_ALL = "https://api.example/api/config/all";
const URL_OTHER = "https://api.example/api/sessions";
const TTL = { ttlMs: 60_000 };

function mockResponse(status: number, body: unknown = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

/** The underlying collapser frees its in-flight slot on a microtask after settle; drain it before the next call. */
async function drain() {
  await Promise.resolve();
  await Promise.resolve();
  await Promise.resolve();
}

describe("cachedAuthFetchJson", () => {
  const getToken = vi.fn().mockResolvedValue("tok-1");
  let fetchMock: ReturnType<typeof vi.fn>;
  let now = 1_000_000;

  beforeEach(() => {
    clearCachedAuthFetch();
    __resetDedupedAuthFetchForTests();
    fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    now = 1_000_000;
    vi.spyOn(Date, "now").mockImplementation(() => now);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("serves a fresh hit without a second round-trip", async () => {
    fetchMock.mockResolvedValueOnce(mockResponse(200, { edition: "pro" }));

    const a = await cachedAuthFetchJson<{ edition: string }>(URL_FLAGS, getToken, TTL);
    await drain();
    now += 59_000;
    const b = await cachedAuthFetchJson<{ edition: string }>(URL_FLAGS, getToken, TTL);

    expect(a).toEqual({ edition: "pro" });
    expect(b).toBe(a);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("refetches once the TTL has elapsed", async () => {
    fetchMock
      .mockResolvedValueOnce(mockResponse(200, { n: 1 }))
      .mockResolvedValueOnce(mockResponse(200, { n: 2 }));

    expect(await cachedAuthFetchJson<{ n: number }>(URL_FLAGS, getToken, TTL)).toEqual({ n: 1 });
    await drain();
    now += 60_000;
    expect(await cachedAuthFetchJson<{ n: number }>(URL_FLAGS, getToken, TTL)).toEqual({ n: 2 });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("collapses concurrent callers of one URL into a single flight", async () => {
    const d = deferred<Response>();
    fetchMock.mockReturnValueOnce(d.promise);

    const a = cachedAuthFetchJson<{ v: number }>(URL_FLAGS, getToken, TTL);
    const b = cachedAuthFetchJson<{ v: number }>(URL_FLAGS, getToken, TTL);
    await drain();
    expect(fetchMock).toHaveBeenCalledTimes(1);

    d.resolve(mockResponse(200, { v: 42 }));
    const [ra, rb] = await Promise.all([a, b]);
    expect(ra).toEqual({ v: 42 });
    expect(rb).toBe(ra);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("never stores a backend refusal: the next call retries", async () => {
    fetchMock
      .mockResolvedValueOnce(mockResponse(403, { error: "nope", code: "Forbidden" }))
      .mockResolvedValueOnce(mockResponse(200, { ok: true }));

    await expect(cachedAuthFetchJson<unknown>(URL_FLAGS, getToken, TTL)).rejects.toBeInstanceOf(ApiError);
    await drain();
    expect(await cachedAuthFetchJson<{ ok: boolean }>(URL_FLAGS, getToken, TTL)).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("never stores a network failure either", async () => {
    fetchMock
      .mockRejectedValueOnce(new Error("offline"))
      .mockResolvedValueOnce(mockResponse(200, { ok: true }));

    await expect(cachedAuthFetchJson<unknown>(URL_FLAGS, getToken, TTL)).rejects.toThrow("offline");
    await drain();
    expect(await cachedAuthFetchJson<{ ok: boolean }>(URL_FLAGS, getToken, TTL)).toEqual({ ok: true });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("invalidates by path prefix and leaves other entries untouched", async () => {
    fetchMock
      .mockResolvedValueOnce(mockResponse(200, { flags: 1 }))
      .mockResolvedValueOnce(mockResponse(200, { all: 1 }))
      .mockResolvedValueOnce(mockResponse(200, { other: 1 }));
    await cachedAuthFetchJson<unknown>(URL_FLAGS, getToken, TTL);
    await cachedAuthFetchJson<unknown>(URL_ALL, getToken, TTL);
    await cachedAuthFetchJson<unknown>(URL_OTHER, getToken, TTL);
    await drain();

    invalidateCachedAuthFetch(CONFIG_PATH_PREFIX);

    fetchMock
      .mockResolvedValueOnce(mockResponse(200, { flags: 2 }))
      .mockResolvedValueOnce(mockResponse(200, { all: 2 }));
    expect(await cachedAuthFetchJson<unknown>(URL_FLAGS, getToken, TTL)).toEqual({ flags: 2 });
    await drain();
    expect(await cachedAuthFetchJson<unknown>(URL_ALL, getToken, TTL)).toEqual({ all: 2 });
    expect(await cachedAuthFetchJson<unknown>(URL_OTHER, getToken, TTL)).toEqual({ other: 1 });
    expect(fetchMock).toHaveBeenCalledTimes(5);
  });

  it("invalidates by predicate", async () => {
    fetchMock
      .mockResolvedValueOnce(mockResponse(200, { flags: 1 }))
      .mockResolvedValueOnce(mockResponse(200, { all: 1 }));
    await cachedAuthFetchJson<unknown>(URL_FLAGS, getToken, TTL);
    await cachedAuthFetchJson<unknown>(URL_ALL, getToken, TTL);
    await drain();

    invalidateCachedAuthFetch((url) => url.endsWith("/feature-flags"));

    fetchMock.mockResolvedValueOnce(mockResponse(200, { flags: 2 }));
    expect(await cachedAuthFetchJson<unknown>(URL_FLAGS, getToken, TTL)).toEqual({ flags: 2 });
    expect(await cachedAuthFetchJson<unknown>(URL_ALL, getToken, TTL)).toEqual({ all: 1 });
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it("clearCachedAuthFetch drops everything", async () => {
    fetchMock
      .mockResolvedValueOnce(mockResponse(200, { flags: 1 }))
      .mockResolvedValueOnce(mockResponse(200, { other: 1 }));
    await cachedAuthFetchJson<unknown>(URL_FLAGS, getToken, TTL);
    await cachedAuthFetchJson<unknown>(URL_OTHER, getToken, TTL);
    await drain();

    clearCachedAuthFetch();

    fetchMock
      .mockResolvedValueOnce(mockResponse(200, { flags: 2 }))
      .mockResolvedValueOnce(mockResponse(200, { other: 2 }));
    expect(await cachedAuthFetchJson<unknown>(URL_FLAGS, getToken, TTL)).toEqual({ flags: 2 });
    await drain();
    expect(await cachedAuthFetchJson<unknown>(URL_OTHER, getToken, TTL)).toEqual({ other: 2 });
    expect(fetchMock).toHaveBeenCalledTimes(4);
  });

  it("does not store a response that was already in flight when the invalidation happened", async () => {
    const d = deferred<Response>();
    fetchMock.mockReturnValueOnce(d.promise);
    const pending = cachedAuthFetchJson<{ v: number }>(URL_FLAGS, getToken, TTL);
    await drain();

    // A write landed while the read was out: the waiting caller still gets the response, but the
    // pre-write value must not be served to the next caller.
    invalidateCachedAuthFetch(CONFIG_PATH_PREFIX);
    d.resolve(mockResponse(200, { v: 1 }));
    expect(await pending).toEqual({ v: 1 });
    await drain();

    fetchMock.mockResolvedValueOnce(mockResponse(200, { v: 2 }));
    expect(await cachedAuthFetchJson<{ v: number }>(URL_FLAGS, getToken, TTL)).toEqual({ v: 2 });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
