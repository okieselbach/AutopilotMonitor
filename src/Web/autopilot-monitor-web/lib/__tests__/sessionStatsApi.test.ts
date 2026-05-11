import { describe, it, expect, vi } from "vitest";

// API_BASE_URL is read at import time of lib/api — stub it before the import.
vi.mock("@/utils/config", () => ({ API_BASE_URL: "https://test.example" }));

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const apiPromise = import("../api") as Promise<{ api: any }>;

/**
 * Stats endpoint contract. The dashboard cards used to derive their numbers
 * from whatever happened to be paginated client-side, which produced the
 * "40 → 22 → 20" drift the user reported. Server-side stats are now driven
 * by these two endpoints; here we lock down the URL surface so a refactor
 * of api.ts doesn't silently break the wire shape.
 */
// Routes live under /api/stats/sessions (NOT /api/sessions/stats). A literal
// "sessions/stats" path gets routed to GetSessionFunction by Azure Functions
// (sessions/{sessionId} with sessionId="stats") which then 404s. These tests
// pin the wire path so a future refactor of api.ts can't silently regress.
describe("api.sessions.stats — per-tenant", () => {
  it("uses /api/stats/sessions with no params by default", async () => {
    const { api } = await apiPromise;
    expect(api.sessions.stats()).toBe("https://test.example/api/stats/sessions");
  });

  it("honours an explicit days override", async () => {
    const { api } = await apiPromise;
    expect(api.sessions.stats({ days: 30 })).toBe(
      "https://test.example/api/stats/sessions?days=30",
    );
  });

  it("omits days when undefined", async () => {
    const { api } = await apiPromise;
    expect(api.sessions.stats({ days: undefined })).toBe(
      "https://test.example/api/stats/sessions",
    );
  });
});

describe("api.globalSessions.stats — GA cross-tenant", () => {
  it("uses /api/global/stats/sessions with no params by default", async () => {
    const { api } = await apiPromise;
    expect(api.globalSessions.stats()).toBe(
      "https://test.example/api/global/stats/sessions",
    );
  });

  it("forwards an optional tenantId filter", async () => {
    const { api } = await apiPromise;
    expect(
      api.globalSessions.stats({ tenantId: "11111111-1111-1111-1111-111111111111" }),
    ).toBe(
      "https://test.example/api/global/stats/sessions?tenantId=11111111-1111-1111-1111-111111111111",
    );
  });

  it("forwards both tenantId and days together", async () => {
    const { api } = await apiPromise;
    expect(
      api.globalSessions.stats({
        tenantId: "22222222-2222-2222-2222-222222222222",
        days: 14,
      }),
    ).toBe(
      "https://test.example/api/global/stats/sessions?tenantId=22222222-2222-2222-2222-222222222222&days=14",
    );
  });

  it("omits an empty-string tenantId so the backend treats it as platform-wide", async () => {
    // Important: an empty filter means "all tenants" in the GA endpoint.
    // The api builder must drop the param entirely rather than send tenantId=.
    const { api } = await apiPromise;
    expect(api.globalSessions.stats({ tenantId: "", days: 7 })).toBe(
      "https://test.example/api/global/stats/sessions?days=7",
    );
  });
});
