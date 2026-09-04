import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ApiError, apiErrorFromResponse, describeApiError, fetchJson } from "../scopedFetch";
import { TokenExpiredError } from "../authenticatedFetch";

vi.mock("../appInsights", () => ({ trackEvent: vi.fn() }));

function jsonResponse(status: number, body: unknown, statusText = "Error"): Response {
  return new Response(JSON.stringify(body), { status, statusText, headers: { "Content-Type": "application/json" } });
}

describe("apiErrorFromResponse", () => {
  it("reads the error envelope: error, code, correlationId, hint", async () => {
    const err = await apiErrorFromResponse(
      jsonResponse(404, { error: "Session not found", code: "NotFound", correlationId: "3f2a9c1e-7b4d-4e0a-9d2c-1a2b3c4d5e6f", hint: "Check the id." }),
    );

    expect(err).toBeInstanceOf(ApiError);
    expect(err.status).toBe(404);
    expect(err.message).toBe("Session not found");
    expect(err.code).toBe("NotFound");
    expect(err.correlationId).toBe("3f2a9c1e-7b4d-4e0a-9d2c-1a2b3c4d5e6f");
    expect(err.hint).toBe("Check the id.");
  });

  it("falls back to the pre-envelope message and then to statusText", async () => {
    const legacy = await apiErrorFromResponse(jsonResponse(400, { success: false, message: "tenantId is required" }, "Bad Request"));
    expect(legacy.message).toBe("tenantId is required");
    expect(legacy.code).toBe("");
    expect(legacy.correlationId).toBe("");

    const bare = await apiErrorFromResponse(new Response("not json", { status: 502, statusText: "Bad Gateway" }));
    expect(bare.message).toBe("Bad Gateway");
    expect(bare.hint).toBeNull();
  });
});

describe("fetchJson", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.restoreAllMocks();
  });

  it("returns the parsed body on success and throws the envelope ApiError otherwise", async () => {
    const fetchMock = globalThis.fetch as ReturnType<typeof vi.fn>;
    fetchMock.mockResolvedValueOnce(jsonResponse(200, { items: [1] }));
    const getToken = vi.fn().mockResolvedValue("tok");

    await expect(fetchJson<{ items: number[] }>("https://api/x", getToken)).resolves.toEqual({ items: [1] });

    fetchMock.mockResolvedValueOnce(jsonResponse(403, { error: "Access denied.", code: "Forbidden", correlationId: "cid-1" }));
    const err = await fetchJson("https://api/x", getToken).catch((e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).code).toBe("Forbidden");
    expect((err as ApiError).correlationId).toBe("cid-1");
  });
});

describe("describeApiError", () => {
  it("renders an ApiError with the short correlation id as reference", () => {
    const err = new ApiError(500, "GetSessions failed.", "InternalError", "3f2a9c1e-7b4d-4e0a-9d2c-1a2b3c4d5e6f");
    expect(describeApiError(err)).toEqual({ message: "GetSessions failed.", reference: "3f2a9c1e" });
  });

  it("has no reference for a token expiry or a plain Error, and uses the fallback for unknown values", () => {
    expect(describeApiError(new TokenExpiredError()).reference).toBeNull();
    expect(describeApiError(new Error("boom"))).toEqual({ message: "boom", reference: null });
    expect(describeApiError(undefined, "Could not load.")).toEqual({ message: "Could not load.", reference: null });
    expect(describeApiError(new ApiError(502, "", "", ""), "Upstream failed.").message).toBe("Upstream failed.");
  });
});
