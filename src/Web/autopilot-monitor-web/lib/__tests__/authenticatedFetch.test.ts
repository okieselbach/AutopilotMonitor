import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { authenticatedFetch, TokenExpiredError } from "../authenticatedFetch";
import { trackEvent } from "../appInsights";

vi.mock("../appInsights", () => ({ trackEvent: vi.fn() }));

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

function mockResponse(status: number, body: unknown = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("authenticatedFetch", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("attaches Bearer token and returns the response on success", async () => {
    const getToken = vi.fn().mockResolvedValue("tok-1");
    (globalThis.fetch as ReturnType<typeof vi.fn>).mockResolvedValueOnce(mockResponse(200, { ok: true }));

    const res = await authenticatedFetch("https://api/x", getToken);

    expect(res.status).toBe(200);
    expect(getToken).toHaveBeenCalledTimes(1);
    const call = (globalThis.fetch as ReturnType<typeof vi.fn>).mock.calls[0];
    const headers = call[1].headers as Headers;
    expect(headers.get("Authorization")).toBe("Bearer tok-1");
  });

  it("throws TokenExpiredError immediately when no token is available", async () => {
    const getToken = vi.fn().mockResolvedValue(null);

    await expect(authenticatedFetch("https://api/x", getToken)).rejects.toBeInstanceOf(TokenExpiredError);
    expect(globalThis.fetch).not.toHaveBeenCalled();
  });

  it("retries with forced fresh token on 401 and returns the retry response", async () => {
    const getToken = vi.fn()
      .mockResolvedValueOnce("stale")
      .mockResolvedValueOnce("fresh");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock
      .mockResolvedValueOnce(mockResponse(401))
      .mockResolvedValueOnce(mockResponse(200, { ok: true }));

    const res = await authenticatedFetch("https://api/x", getToken);

    expect(res.status).toBe(200);
    expect(getToken).toHaveBeenCalledTimes(2);
    expect(getToken).toHaveBeenNthCalledWith(2, true); // force refresh
    expect(fetchMock).toHaveBeenCalledTimes(2);
    const retryHeaders = fetchMock.mock.calls[1][1].headers as Headers;
    expect(retryHeaders.get("Authorization")).toBe("Bearer fresh");
  });

  it("throws TokenExpiredError when retry after 401 also returns 401", async () => {
    const getToken = vi.fn()
      .mockResolvedValueOnce("stale")
      .mockResolvedValueOnce("fresh");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock
      .mockResolvedValueOnce(mockResponse(401))
      .mockResolvedValueOnce(mockResponse(401));

    await expect(authenticatedFetch("https://api/x", getToken)).rejects.toBeInstanceOf(TokenExpiredError);
  });

  it("throws TokenExpiredError when forced refresh returns no token", async () => {
    const getToken = vi.fn()
      .mockResolvedValueOnce("stale")
      .mockResolvedValueOnce(null);
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(mockResponse(401));

    await expect(authenticatedFetch("https://api/x", getToken)).rejects.toBeInstanceOf(TokenExpiredError);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("sends a fresh X-Correlation-ID on every call", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValue(mockResponse(200));

    await authenticatedFetch("https://api/a", getToken);
    await authenticatedFetch("https://api/b", getToken);

    const first = (fetchMock.mock.calls[0][1].headers as Headers).get("X-Correlation-ID");
    const second = (fetchMock.mock.calls[1][1].headers as Headers).get("X-Correlation-ID");
    expect(first).toMatch(UUID);
    expect(second).toMatch(UUID);
    expect(first).not.toBe(second);
  });

  it("keeps the same correlation id and the caller's headers on the 401 retry", async () => {
    const getToken = vi.fn().mockResolvedValueOnce("stale").mockResolvedValueOnce("fresh");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(mockResponse(401)).mockResolvedValueOnce(mockResponse(200));

    await authenticatedFetch("https://api/x", getToken, { headers: { "X-Client-Hint": "keep-me" } });

    const first = fetchMock.mock.calls[0][1].headers as Headers;
    const retry = fetchMock.mock.calls[1][1].headers as Headers;
    expect(retry.get("X-Correlation-ID")).toBe(first.get("X-Correlation-ID"));
    expect(retry.get("X-Client-Hint")).toBe("keep-me");
  });

  it("tracks api_request_failed with path, method, status and the correlation id on a final >= 400", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(mockResponse(404, { error: "nope" }));

    await authenticatedFetch("https://api/api/sessions/abc?tenantId=t1", getToken, { method: "delete" });

    const sent = (fetchMock.mock.calls[0][1].headers as Headers).get("X-Correlation-ID");
    expect(trackEvent).toHaveBeenCalledWith("api_request_failed", {
      path: "/api/sessions/abc",
      method: "DELETE",
      status: 404,
      correlationId: sent,
    });
  });

  it("does not track a 401 that the retry turned into a success", async () => {
    const getToken = vi.fn().mockResolvedValueOnce("stale").mockResolvedValueOnce("fresh");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(mockResponse(401)).mockResolvedValueOnce(mockResponse(200));
    vi.mocked(trackEvent).mockClear();

    await authenticatedFetch("https://api/x", getToken);

    expect(trackEvent).not.toHaveBeenCalled();
  });

  it("passes through non-401 error responses without retry", async () => {
    const getToken = vi.fn().mockResolvedValue("tok");
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(mockResponse(500, { error: "boom" }));

    const res = await authenticatedFetch("https://api/x", getToken);

    expect(res.status).toBe(500);
    expect(getToken).toHaveBeenCalledTimes(1);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});
