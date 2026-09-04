import { describe, expect, it } from "vitest";
import { CORRELATION_HEADER, newCorrelationId, shortCorrelationId } from "../correlationId";

// Mirrors CorrelationIdMiddleware's allow-list — an id outside it is silently replaced server-side
// and the client-side reference would then never match a backend row.
const BACKEND_ALLOW_LIST = /^[A-Za-z0-9_-]{1,128}$/;

describe("correlationId", () => {
  it("uses the backend's header spelling (capital ID)", () => {
    expect(CORRELATION_HEADER).toBe("X-Correlation-ID");
  });

  it("mints ids the backend accepts verbatim and never repeats", () => {
    const ids = new Set(Array.from({ length: 200 }, () => newCorrelationId()));
    expect(ids.size).toBe(200);
    for (const id of ids) expect(id).toMatch(BACKEND_ALLOW_LIST);
  });

  it("shortens a UUID to its first block and anything else to 8 chars", () => {
    expect(shortCorrelationId("3f2a9c1e-7b4d-4e0a-9d2c-1a2b3c4d5e6f")).toBe("3f2a9c1e");
    expect(shortCorrelationId("abcdefghijklmnop")).toBe("abcdefgh");
  });
});
