import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  ApiError,
  apiErrorFromResponse,
  apiErrorNotification,
  apiErrorText,
  describeApiError,
  fetchBlob,
  fetchJson,
  fetchOk,
  nullOn404,
} from "../apiClient";
import { TokenExpiredError } from "../authenticatedFetch";

vi.mock("../appInsights", () => ({ trackEvent: vi.fn() }));

function jsonResponse(status: number, body: unknown, statusText = "Error", headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), { status, statusText, headers: { "Content-Type": "application/json", ...headers } });
}

describe("apiErrorFromResponse", () => {
  it("reads the error envelope: error, code, correlationId, hint, retryAfterSeconds", async () => {
    const err = await apiErrorFromResponse(
      jsonResponse(429, {
        error: "Rate limit exceeded.",
        code: "RateLimitExceeded",
        correlationId: "3f2a9c1e-7b4d-4e0a-9d2c-1a2b3c4d5e6f",
        hint: "Slow down.",
        retryAfterSeconds: 12,
      }),
    );

    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(429);
    expect(err.message).toBe("Rate limit exceeded.");
    expect(err.code).toBe("RateLimitExceeded");
    expect(err.correlationId).toBe("3f2a9c1e-7b4d-4e0a-9d2c-1a2b3c4d5e6f");
    expect(err.hint).toBe("Slow down.");
    expect(err.retryAfterSeconds).toBe(12);
  });

  it("takes Retry-After from the header when the body has no retryAfterSeconds", async () => {
    const err = await apiErrorFromResponse(jsonResponse(503, { error: "Busy.", code: "ServiceUnavailable", correlationId: "c" }, "x", { "Retry-After": "5" }));
    expect(err.retryAfterSeconds).toBe(5);

    const none = await apiErrorFromResponse(jsonResponse(503, { error: "Busy.", code: "ServiceUnavailable", correlationId: "c" }));
    expect(none.retryAfterSeconds).toBeNull();
  });

  it("keeps the parsed body so specialised envelopes stay readable", async () => {
    const err = await apiErrorFromResponse(jsonResponse(409, { error: "Slot limit.", code: "DelegatedSlotLimitReached", correlationId: "c", homeTenantId: "t1", limit: 3 }));
    expect(err.body?.homeTenantId).toBe("t1");
    expect(err.body?.limit).toBe(3);
    expect((await apiErrorFromResponse(new Response("nope", { status: 502 }))).body).toBeNull();
  });

  it("falls back to the pre-envelope message, then to statusText; an empty body is not an exception", async () => {
    const legacy = await apiErrorFromResponse(jsonResponse(400, { success: false, message: "tenantId is required" }, "Bad Request"));
    expect(legacy.message).toBe("tenantId is required");
    expect(legacy.code).toBe("");
    expect(legacy.correlationId).toBe("");

    const bare = await apiErrorFromResponse(new Response("not json", { status: 502, statusText: "Bad Gateway" }));
    expect(bare.message).toBe("Bad Gateway");
    expect(bare.hint).toBeNull();

    const empty = await apiErrorFromResponse(new Response("", { status: 500, statusText: "Internal Server Error" }));
    expect(empty.message).toBe("Internal Server Error");
  });
});

describe("fetchJson / fetchOk / fetchBlob", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });
  const fetchMock = () => globalThis.fetch as ReturnType<typeof vi.fn>;
  const getToken = () => vi.fn().mockResolvedValue("tok");

  it("returns the parsed body on success and throws the envelope ApiError otherwise", async () => {
    fetchMock().mockResolvedValueOnce(jsonResponse(200, { items: [1] }));
    await expect(fetchJson<{ items: number[] }>("https://api/x", getToken())).resolves.toEqual({ items: [1] });

    fetchMock().mockResolvedValueOnce(jsonResponse(403, { error: "Access denied.", code: "Forbidden", correlationId: "cid-1" }));
    const err = await fetchJson<unknown>("https://api/x", getToken()).catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe("Forbidden");
    expect((err as ApiError).correlationId).toBe("cid-1");
  });

  it("refuses an empty or malformed 2xx body instead of handing back undefined", async () => {
    fetchMock().mockResolvedValueOnce(new Response("", { status: 200 }));
    const empty = await fetchJson<unknown>("https://api/x", getToken()).catch((e) => e);
    expect(empty).toBeInstanceOf(ApiError);
    expect((empty as ApiError).message).toBe("Empty response body");

    fetchMock().mockResolvedValueOnce(new Response("<html>", { status: 200 }));
    const html = await fetchJson<unknown>("https://api/x", getToken()).catch((e) => e);
    expect((html as ApiError).message).toBe("Malformed response body");
  });

  it("sets Content-Type: application/json for a string body unless the caller set one", async () => {
    fetchMock().mockResolvedValue(jsonResponse(200, {}));
    await fetchJson<unknown>("https://api/x", getToken(), { method: "POST", body: JSON.stringify({ a: 1 }) });
    const headers1 = fetchMock().mock.calls[0][1].headers as Headers;
    expect(headers1.get("Content-Type")).toBe("application/json");

    await fetchOk("https://api/x", getToken(), { method: "PUT", body: "raw", headers: { "Content-Type": "text/plain" } });
    const headers2 = fetchMock().mock.calls[1][1].headers as Headers;
    expect(headers2.get("Content-Type")).toBe("text/plain");

    await fetchOk("https://api/x", getToken(), { method: "DELETE" });
    const headers3 = fetchMock().mock.calls[2][1].headers as Headers;
    expect(headers3.has("Content-Type")).toBe(false);
  });

  it("fetchOk returns the response for status checks and throws on non-ok; fetchBlob returns the blob", async () => {
    fetchMock().mockResolvedValueOnce(new Response("", { status: 202 }));
    const res = await fetchOk("https://api/x", getToken(), { method: "POST" });
    expect(res.status).toBe(202);

    fetchMock().mockResolvedValueOnce(jsonResponse(409, { error: "Locked.", code: "Conflict", correlationId: "c" }));
    await expect(fetchOk("https://api/x", getToken())).rejects.toBeInstanceOf(ApiError);

    fetchMock().mockResolvedValueOnce(new Response(new Blob(["zip"]), { status: 200 }));
    const blob = await fetchBlob("https://api/x", getToken());
    expect(await blob.text()).toBe("zip");
  });

  it("lets TokenExpiredError pass through untouched", async () => {
    const noToken = vi.fn().mockResolvedValue(null);
    await expect(fetchJson<unknown>("https://api/x", noToken)).rejects.toBeInstanceOf(TokenExpiredError);
  });
});

describe("nullOn404", () => {
  it("turns a 404 into null and rethrows everything else", async () => {
    await expect(Promise.reject(new ApiError(404, "Not found.", "NotFound", "c")).catch(nullOn404)).resolves.toBeNull();
    await expect(Promise.reject(new ApiError(403, "No.", "Forbidden", "c")).catch(nullOn404)).rejects.toBeInstanceOf(ApiError);
    await expect(Promise.reject(new Error("boom")).catch(nullOn404)).rejects.toThrow("boom");
  });
});

describe("describeApiError / apiErrorText / apiErrorNotification", () => {
  it("renders an ApiError with the short correlation id as reference", () => {
    const err = new ApiError(500, "GetSessions failed.", "InternalError", "3f2a9c1e-7b4d-4e0a-9d2c-1a2b3c4d5e6f");
    expect(describeApiError(err)).toEqual({ message: "GetSessions failed.", reference: "3f2a9c1e" });
    expect(apiErrorText(err)).toBe("GetSessions failed. (Ref 3f2a9c1e)");
  });

  it("adds the retry advice on 429 and 503", () => {
    const limited = new ApiError(429, "Rate limit exceeded.", "RateLimitExceeded", "cid", null, 12);
    expect(describeApiError(limited).message).toBe("Rate limit exceeded. Try again in 12 s.");
    const busy = new ApiError(503, "", "ServiceUnavailable", "cid");
    expect(describeApiError(busy).message).toBe("Service temporarily unavailable. Try again shortly.");
  });

  it("has no reference for a token expiry or a plain Error, and uses the fallback for unknown values", () => {
    expect(describeApiError(new TokenExpiredError()).reference).toBeNull();
    expect(describeApiError(new Error("boom"))).toEqual({ message: "boom", reference: null });
    expect(describeApiError(undefined, "Could not load.")).toEqual({ message: "Could not load.", reference: null });
    expect(describeApiError(new ApiError(502, "", "", ""), "Upstream failed.").message).toBe("Upstream failed.");
    expect(apiErrorText(new Error("boom"))).toBe("boom");
  });

  it("builds the notification: caller title and key, reference from the envelope; token expiry overrides both", () => {
    const err = new ApiError(404, "Session not found.", "NotFound", "3f2a9c1e-7b4d-4e0a-9d2c-1a2b3c4d5e6f");
    expect(apiErrorNotification("Backend Error", err, "apps-list-error")).toEqual({
      type: "error",
      title: "Backend Error",
      message: "Session not found.",
      key: "apps-list-error",
      reference: "3f2a9c1e",
    });
    expect(apiErrorNotification("Backend Error", new TokenExpiredError(), "apps-list-error")).toEqual({
      type: "error",
      title: "Session Expired",
      message: new TokenExpiredError().message,
      key: "session-expired",
      reference: undefined,
    });
    expect(apiErrorNotification("Backend Error", "?", undefined, "Unable to load.").message).toBe("Unable to load.");
  });
});
