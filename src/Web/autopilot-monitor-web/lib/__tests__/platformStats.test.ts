import { afterEach, beforeEach, describe, expect, it, vi, type MockInstance } from "vitest";
import {
  fetchLivePlatformStats,
  loadPlatformStatsSnapshot,
  resolvePlatformStatsManifestUrl,
  toStatItems,
} from "../platformStats";

const CONTAINER = "https://stats.example.test/publicstats";
const MANIFEST_URL = `${CONTAINER}/platform-stats.json`;
const PAYLOAD_URL = `${CONTAINER}/platform-stats.2026-10-02T140005Z.json`;

/** What the backend publishes (MaintenanceService.TryPublishPlatformStatsJsonAsync). */
const MANIFEST = { latest: "platform-stats.2026-10-02T140005Z.json", generatedAtUtc: "2026-10-02T14:00:05.1234567Z" };
const PAYLOAD = {
  totalEnrollments: 12481,
  totalUsers: 310,
  totalTenants: 92,
  totalSignedUpTenants: 87,
  uniqueDeviceModels: 214,
  totalEventsProcessed: 8341206,
  successfulEnrollments: 11020,
  issuesDetected: 1847,
  lastFullCompute: "2026-10-02T14:00:03.1234567Z",
  lastUpdated: "2026-10-02T14:00:03.1234567Z",
};

const BAND = [
  { label: "enrollments monitored", value: "12,481" },
  { label: "issues detected", value: "1,847" },
  { label: "organisations", value: "87" },
  { label: "device models", value: "214" },
  { label: "events processed", value: "8.3M" },
];

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

/** Answers by URL; anything not listed is a 404. */
function serve(routes: Record<string, () => Response>) {
  const fetchMock = vi.fn<(input: string, init?: RequestInit) => Promise<Response>>(async input =>
    routes[input]?.() ?? new Response("not found", { status: 404 }));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

const BOTH_FILES = { [MANIFEST_URL]: () => json(MANIFEST), [PAYLOAD_URL]: () => json(PAYLOAD) };

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe("resolvePlatformStatsManifestUrl", () => {
  it("falls back to the same-origin manifest without a configured URL", () => {
    expect([undefined, "", "  "].map(resolvePlatformStatsManifestUrl)).toEqual(Array(3).fill("/platform-stats.json"));
  });

  it("appends the manifest name to a container URL, with or without a trailing slash", () => {
    expect(resolvePlatformStatsManifestUrl(CONTAINER)).toBe(MANIFEST_URL);
    expect(resolvePlatformStatsManifestUrl(`${CONTAINER}/`)).toBe(MANIFEST_URL);
  });

  it("keeps a URL that already names a JSON file", () => {
    expect(resolvePlatformStatsManifestUrl(MANIFEST_URL)).toBe(MANIFEST_URL);
    expect(resolvePlatformStatsManifestUrl(`${CONTAINER}/custom.JSON`)).toBe(`${CONTAINER}/custom.JSON`);
  });

  it("keeps query and hash behind the appended manifest name", () => {
    expect(resolvePlatformStatsManifestUrl(`${CONTAINER}?v=1#x`)).toBe(`${MANIFEST_URL}?v=1#x`);
  });
});

describe("toStatItems", () => {
  it("maps the band's figures in display order", () => {
    expect(toStatItems(PAYLOAD)).toEqual(BAND);
  });

  it("leaves out figures that are missing, zero, negative or not a finite number", () => {
    const payload = {
      totalEnrollments: 0,
      issuesDetected: -5,
      totalSignedUpTenants: Number.POSITIVE_INFINITY,
      uniqueDeviceModels: "214" as unknown as number,
      totalEventsProcessed: 999_999,
    };
    expect(toStatItems(payload)).toEqual([{ label: "events processed", value: "999,999" }]);
    expect(toStatItems({})).toEqual([]);
  });
});

describe("fetchLivePlatformStats", () => {
  it("revalidates the manifest and takes the immutable file it names from the browser cache", async () => {
    const fetchMock = serve(BOTH_FILES);

    expect(await fetchLivePlatformStats(MANIFEST_URL)).toEqual(PAYLOAD);
    expect(fetchMock.mock.calls).toEqual([
      [MANIFEST_URL, { cache: "no-cache" }],
      [PAYLOAD_URL, { cache: "default" }],
    ]);
  });

  it("resolves the versioned file against the manifest as served, after redirects", async () => {
    const redirected = json(MANIFEST);
    Object.defineProperty(redirected, "url", { value: "https://stats.example.test/v2/platform-stats.json" });
    const fetchMock = serve({
      [MANIFEST_URL]: () => redirected,
      "https://stats.example.test/v2/platform-stats.2026-10-02T140005Z.json": () => json(PAYLOAD),
    });

    expect(await fetchLivePlatformStats(MANIFEST_URL)).toEqual(PAYLOAD);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  const failures: [string, Record<string, () => Response>][] = [
    ["the manifest is missing", { [PAYLOAD_URL]: () => json(PAYLOAD) }],
    ["the manifest names no file", { [MANIFEST_URL]: () => json({ generatedAtUtc: MANIFEST.generatedAtUtc }) }],
    ["the manifest is not JSON", { [MANIFEST_URL]: () => new Response("<html></html>") }],
    ["the manifest names a file on another origin", { [MANIFEST_URL]: () => json({ latest: "https://elsewhere.example.test/stats.json" }) }],
    ["the versioned file is missing", { [MANIFEST_URL]: () => json(MANIFEST) }],
    ["the versioned file holds no object", { [MANIFEST_URL]: () => json(MANIFEST), [PAYLOAD_URL]: () => json(42) }],
  ];
  for (const [name, routes] of failures) {
    it(`returns null when ${name}`, async () => {
      serve(routes);
      expect(await fetchLivePlatformStats(MANIFEST_URL)).toBeNull();
    });
  }

  it("returns null when the network fails", async () => {
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(new TypeError("Failed to fetch")));
    expect(await fetchLivePlatformStats(MANIFEST_URL)).toBeNull();
  });
});

describe("loadPlatformStatsSnapshot", () => {
  let log: MockInstance<typeof console.log>;
  let warn: MockInstance<typeof console.warn>;
  beforeEach(() => {
    log = vi.spyOn(console, "log").mockImplementation(() => undefined);
    warn = vi.spyOn(console, "warn").mockImplementation(() => undefined);
  });

  it("bakes nothing and sends no request without an absolute URL (local and CI builds)", async () => {
    const fetchMock = serve(BOTH_FILES);

    expect(await loadPlatformStatsSnapshot("/platform-stats.json")).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
    expect(warn).not.toHaveBeenCalled();
  });

  it("returns the band's items and the stats timestamp, read past Next's fetch cache within a deadline", async () => {
    const fetchMock = serve(BOTH_FILES);

    expect(await loadPlatformStatsSnapshot(MANIFEST_URL)).toEqual({ items: BAND, asOf: PAYLOAD.lastUpdated });
    const inits = fetchMock.mock.calls.map(([, init]) => init);
    expect(inits).toHaveLength(2);
    for (const init of inits) {
      expect(init?.cache).toBe("no-store");
      expect(init?.signal).toBeInstanceOf(AbortSignal);
    }
    expect(inits[0]?.signal).toBe(inits[1]?.signal);
    expect(log).toHaveBeenCalledWith(expect.stringContaining(`from ${MANIFEST.latest} (as of ${PAYLOAD.lastUpdated})`));
  });

  it("fails soft with the reason in the build log when the blob does not answer", async () => {
    serve({ [MANIFEST_URL]: () => new Response("unavailable", { status: 503 }) });

    expect(await loadPlatformStatsSnapshot(MANIFEST_URL)).toBeNull();
    expect(warn).toHaveBeenCalledWith(expect.stringContaining("HTTP 503"));
  });

  it("names the network cause behind Node's generic fetch failure", async () => {
    const failure = new TypeError("fetch failed", { cause: new Error("getaddrinfo ENOTFOUND stats.example.test") });
    vi.stubGlobal("fetch", vi.fn().mockRejectedValue(failure));

    expect(await loadPlatformStatsSnapshot(MANIFEST_URL)).toBeNull();
    expect(warn).toHaveBeenCalledWith(expect.stringContaining("fetch failed (getaddrinfo ENOTFOUND stats.example.test)"));
  });

  it("bakes nothing when the file holds no positive figure", async () => {
    serve({ [MANIFEST_URL]: () => json(MANIFEST), [PAYLOAD_URL]: () => json({ ...PAYLOAD, totalEnrollments: 0, issuesDetected: 0, totalSignedUpTenants: 0, uniqueDeviceModels: 0, totalEventsProcessed: 0 }) });

    expect(await loadPlatformStatsSnapshot(MANIFEST_URL)).toBeNull();
    expect(warn).toHaveBeenCalledWith(expect.stringContaining("no positive figure"));
  });
});
