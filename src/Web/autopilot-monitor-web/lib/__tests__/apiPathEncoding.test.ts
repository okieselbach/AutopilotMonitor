import { describe, it, expect, vi } from "vitest";

// API_BASE_URL is read at import time of lib/api — stub it before the import.
vi.mock("@/utils/config", () => ({ API_BASE_URL: "https://test.example" }));

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const apiPromise = import("../api") as Promise<{ api: any }>;

/**
 * Path-position ids must be percent-encoded so an attacker-controlled id
 * (deep link `/sessions?id=...`, decoded by useSearchParams) cannot rewrite the
 * route: '/' and '..' would otherwise be resolved by the URL parser into a
 * different API path, '#' would truncate the builder's suffix and query string.
 */
const HOSTILE = "x/../../global/y#";
const ENCODED = "x%2F..%2F..%2Fglobal%2Fy%23";

describe("api path ids are percent-encoded", () => {
  it("sessions.* keeps a hostile id inside the sessions/{id} segment", async () => {
    const { api } = await apiPromise;
    const builders: Array<(id: string) => string> = [
      (id) => api.sessions.get(id),
      (id) => api.sessions.events(id),
      (id) => api.sessions.delete(id, "t"),
      (id) => api.sessions.analysis(id),
      (id) => api.sessions.vulnerabilityReport(id),
      (id) => api.sessions.timeAttribution(id),
      (id) => api.sessions.markFailed(id),
      (id) => api.sessions.markSucceeded(id),
      (id) => api.sessions.queueAction(id),
      (id) => api.sessions.report(id),
      (id) => api.sessions.annotations(id),
      (id) => api.sessions.annotation(id, "lane"),
    ];
    for (const build of builders) {
      const url = build(HOSTILE);
      expect(url.startsWith(`https://test.example/api/sessions/${ENCODED}`)).toBe(true);
      expect(new URL(url).pathname.startsWith(`/api/sessions/${ENCODED}`)).toBe(true);
    }
  });

  it("markFailed keeps its suffix after a '#'-bearing id", async () => {
    const { api } = await apiPromise;
    expect(api.sessions.markFailed(HOSTILE, "t")).toBe(
      `https://test.example/api/sessions/${ENCODED}/mark-failed?tenantId=t`,
    );
  });

  it("annotation lane and gather rule id are encoded too", async () => {
    const { api } = await apiPromise;
    expect(api.sessions.annotation("s", "a/b")).toBe("https://test.example/api/sessions/s/annotations/a%2Fb");
    expect(api.rules.gatherRule(HOSTILE)).toBe(`https://test.example/api/rules/gather/${ENCODED}`);
  });

  it("no path builder lets an id escape its segment", async () => {
    const { api } = await apiPromise;
    type Builder = (...args: string[]) => string;
    const walk = (node: unknown): Builder[] =>
      typeof node === "function"
        ? [node as Builder]
        : Object.values(node as Record<string, unknown>).flatMap(walk);
    for (const fn of walk(api)) {
      if (fn.length === 0) continue;
      const url = fn(HOSTILE, HOSTILE);
      // The WHATWG parser resolves a raw "x/../../global/y" into "/api/global/y";
      // an encoded id survives verbatim and the fragment marker never appears.
      expect(new URL(url).pathname, url).not.toMatch(/\/global\/y/);
      expect(url).not.toContain("#");
    }
  });
});
