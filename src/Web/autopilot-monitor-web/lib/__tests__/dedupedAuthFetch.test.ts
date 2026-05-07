import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { dedupedAuthFetch, __resetDedupedAuthFetchForTests } from "../dedupedAuthFetch";

function mockResponse(status: number, body: unknown = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

/**
 * Returns [promise, resolve, reject] so the test can decide when fetch
 * resolves. Lets us assert behavior of two concurrent callers awaiting a
 * single in-flight request.
 */
function deferred<T>() {
  let resolve!: (v: T) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

describe("dedupedAuthFetch", () => {
  beforeEach(() => {
    __resetDedupedAuthFetchForTests();
    vi.stubGlobal("fetch", vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("collapses two concurrent GETs to the same URL into one underlying fetch", async () => {
    const getToken = vi.fn().mockResolvedValue("tok-1");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    const d = deferred<Response>();
    fetchMock.mockReturnValueOnce(d.promise);

    const a = dedupedAuthFetch("https://api/x", getToken);
    const b = dedupedAuthFetch("https://api/x", getToken);

    // Drain a few microtasks so authenticatedFetch can await getAccessToken
    // and reach its fetch() call. Both callers must already be hooked onto
    // the in-flight Promise before the network resolves.
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    expect(fetchMock).toHaveBeenCalledTimes(1);

    d.resolve(mockResponse(200, { hello: "world" }));
    const [resA, resB] = await Promise.all([a, b]);

    expect(resA.status).toBe(200);
    expect(resB.status).toBe(200);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(getToken).toHaveBeenCalledTimes(1);
  });

  it("each concurrent caller receives an independently readable body", async () => {
    const getToken = vi.fn().mockResolvedValue("tok-1");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(mockResponse(200, { value: 42 }));

    const [resA, resB] = await Promise.all([
      dedupedAuthFetch("https://api/x", getToken),
      dedupedAuthFetch("https://api/x", getToken),
    ]);

    // Each clone must be independently consumable — reading one must not
    // lock the other's body stream.
    const bodyA = await resA.json();
    const bodyB = await resB.json();
    expect(bodyA).toEqual({ value: 42 });
    expect(bodyB).toEqual({ value: 42 });
  });

  it("does not collapse calls separated by a microtask boundary (no caching)", async () => {
    const getToken = vi.fn().mockResolvedValue("tok-1");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock
      .mockResolvedValueOnce(mockResponse(200, { n: 1 }))
      .mockResolvedValueOnce(mockResponse(200, { n: 2 }));

    const a = await dedupedAuthFetch("https://api/x", getToken);
    // Yield twice: once for the .finally microtask that clears in-flight,
    // once to make sure the runtime actually drained it before we re-call.
    await Promise.resolve();
    await Promise.resolve();
    const b = await dedupedAuthFetch("https://api/x", getToken);

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(await a.json()).toEqual({ n: 1 });
    expect(await b.json()).toEqual({ n: 2 });
  });

  it("does not collapse concurrent calls to different URLs", async () => {
    const getToken = vi.fn().mockResolvedValue("tok-1");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock
      .mockResolvedValueOnce(mockResponse(200, { url: "x" }))
      .mockResolvedValueOnce(mockResponse(200, { url: "y" }));

    await Promise.all([
      dedupedAuthFetch("https://api/x", getToken),
      dedupedAuthFetch("https://api/y", getToken),
    ]);

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("bypasses the collapser for non-GET methods (mutations always pass through)", async () => {
    const getToken = vi.fn().mockResolvedValue("tok-1");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock
      .mockResolvedValueOnce(mockResponse(200, { mutated: 1 }))
      .mockResolvedValueOnce(mockResponse(200, { mutated: 2 }));

    await Promise.all([
      dedupedAuthFetch("https://api/x", getToken, { method: "POST" }),
      dedupedAuthFetch("https://api/x", getToken, { method: "POST" }),
    ]);

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("propagates rejection to all concurrent callers and frees the slot", async () => {
    const getToken = vi.fn().mockResolvedValue("tok-1");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    const d = deferred<Response>();
    fetchMock.mockReturnValueOnce(d.promise);

    const a = dedupedAuthFetch("https://api/x", getToken);
    const b = dedupedAuthFetch("https://api/x", getToken);

    d.reject(new Error("network kaputt"));

    await expect(a).rejects.toThrow("network kaputt");
    await expect(b).rejects.toThrow("network kaputt");
    expect(fetchMock).toHaveBeenCalledTimes(1);

    // Slot must be freed so the next call retries fresh, not stuck on the
    // rejected Promise forever.
    await Promise.resolve();
    await Promise.resolve();
    fetchMock.mockResolvedValueOnce(mockResponse(200, { recovered: true }));
    const c = await dedupedAuthFetch("https://api/x", getToken);
    expect(await c.json()).toEqual({ recovered: true });
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it("HEAD requests collapse just like GETs", async () => {
    const getToken = vi.fn().mockResolvedValue("tok-1");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    const d = deferred<Response>();
    fetchMock.mockReturnValueOnce(d.promise);

    const a = dedupedAuthFetch("https://api/x", getToken, { method: "HEAD" });
    const b = dedupedAuthFetch("https://api/x", getToken, { method: "HEAD" });

    // Same microtask drain as the GET case
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    d.resolve(mockResponse(200));
    await Promise.all([a, b]);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("GET and HEAD to the same URL are NOT collapsed (different keys)", async () => {
    const getToken = vi.fn().mockResolvedValue("tok-1");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock
      .mockResolvedValueOnce(mockResponse(200, { method: "get" }))
      .mockResolvedValueOnce(mockResponse(200));

    await Promise.all([
      dedupedAuthFetch("https://api/x", getToken),
      dedupedAuthFetch("https://api/x", getToken, { method: "HEAD" }),
    ]);

    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});
