import { describe, expect, it } from "vitest";
import {
  buildOwnOriginNonApiPatterns,
  isExcludedRequest,
  PAGE_VIEW_BASE_TYPES,
  shouldDropDependency,
  stripPageViewFragments,
  stripUrlFragment,
} from "../webTelemetryExclusions";

const PORTAL = "https://portal.example.test";
const WWW = "https://www.example.test";
const API = "https://api.example.test";

describe("web telemetry dependency exclusions", () => {
  const patterns = buildOwnOriginNonApiPatterns([PORTAL, WWW, "http://localhost:3000"]);

  it.each([
    ["relative static JSON", "/whats-new.json"],
    ["relative version probe", "/version.json?ts=1"],
    ["router HEAD self-request (page URL)", `${PORTAL}/dashboard/?ruleId=ANALYZE-ESP-005`],
    ["RSC segment payload", `${PORTAL}/fleet-health/__next.fleet-health.__PAGE__.txt?_rsc=abc`],
    ["RSC full payload", `${WWW}/get-started/index.txt?_rsc=abc`],
    ["landing manifest on www", `${WWW}/platform-stats.json`],
    ["mixed case is normalised like the SDK does", `${PORTAL.toUpperCase()}/Dashboard/`],
  ])("drops %s", (_name, url) => {
    expect(isExcludedRequest(url, patterns)).toBe(true);
  });

  it.each([
    ["API call on the API host", `${API}/api/stats/sessions?days=7`],
    ["SignalR negotiate", `${API}/api/realtime/negotiate?negotiateVersion=1`],
    ["same-origin API through the dev proxy", "http://localhost:3000/api/auth/me"],
    ["relative API call", "/api/sessions?pageSize=10"],
    ["Entra token endpoint", "https://login.microsoftonline.com/organizations/oauth2/v2.0/token"],
    ["Graph photo", "https://graph.microsoft.com/v1.0/me/photos/64x64/$value"],
    ["a foreign origin that merely starts like ours", `${PORTAL}.evil.test/x`],
  ])("keeps %s", (_name, url) => {
    expect(isExcludedRequest(url, patterns)).toBe(false);
  });

  it("dedupes origins and skips values that are not URLs", () => {
    const p = buildOwnOriginNonApiPatterns([PORTAL, `${PORTAL}/some/path`, "not a url", "", PORTAL.toUpperCase()]);
    // the relative rule plus exactly one origin rule
    expect(p).toHaveLength(2);
    expect(isExcludedRequest(`${PORTAL}/about/`, p)).toBe(true);
  });

  it("always carries the relative rule even without origins", () => {
    const p = buildOwnOriginNonApiPatterns([]);
    expect(p).toHaveLength(1);
    expect(isExcludedRequest("/whats-new.json", p)).toBe(true);
    expect(isExcludedRequest("/api/x", p)).toBe(false);
  });
  describe("dependency initializer decision (URL-object fetches bypass the SDK's own pattern check)", () => {
    it.each([
      ["router segment prefetch", { name: "GET /apps/__next._tree.txt?_rsc=x", target: `${PORTAL}/apps/__next._tree.txt?_rsc=x` }],
      ["router HEAD self-request", { name: "HEAD https://portal.example.test/apps/", target: `${PORTAL}/apps/` }],
      ["full route payload, only data carries the URL", { name: "GET /audit/index.txt?_rsc=y", data: `${WWW}/audit/index.txt?_rsc=y` }],
      ["static JSON resolved from a relative fetch", { name: "GET /whats-new.json", target: `${WWW}/whats-new.json` }],
    ])("drops %s", (_name, item) => {
      expect(shouldDropDependency(item, patterns)).toBe(true);
    });

    it.each([
      ["API call", { name: "GET /api/stats/sessions?days=7", target: `${API}/api/stats/sessions?days=7` }],
      ["same-origin API through the dev proxy", { name: "POST /api/realtime/groups/join", target: "http://localhost:3000/api/realtime/groups/join" }],
      ["token endpoint (relative name, foreign host)", { name: "POST /organizations/oauth2/v2.0/token", target: "https://login.microsoftonline.com/organizations/oauth2/v2.0/token" }],
      ["only a relative name, no absolute URL to judge", { name: "GET /whats-new.json" }],
      ["a bare host as target", { target: "portal.example.test" }],
      ["empty item", {}],
    ])("keeps %s", (_name, item) => {
      expect(shouldDropDependency(item, patterns)).toBe(false);
    });
  });
});

describe("page-view fragment stripping (the pairing code never reaches pageViews.url)", () => {
  const PAIR = `${PORTAL}/push/pair/#p=7Q3MK9T2XH4`;

  it("cuts the fragment and keeps the rest of the URL", () => {
    expect(stripUrlFragment(PAIR)).toBe(`${PORTAL}/push/pair/`);
    expect(stripUrlFragment(`${PORTAL}/push/#e/abc`)).toBe(`${PORTAL}/push/`);
    expect(stripUrlFragment(`${PORTAL}/sessions?id=1#x#y`)).toBe(`${PORTAL}/sessions?id=1`);
    expect(stripUrlFragment(`${PORTAL}/sessions?id=1`)).toBe(`${PORTAL}/sessions?id=1`);
    expect(stripUrlFragment("")).toBe("");
  });

  it.each(PAGE_VIEW_BASE_TYPES.map((t) => [t]))("strips uri and refUri of a %s item in place", (baseType) => {
    const envelope = { baseType, baseData: { name: "Pair", uri: PAIR, refUri: `${PORTAL}/push/#e/abc`, duration: 12 } };
    expect(stripPageViewFragments(envelope)).toBe(true);
    expect(envelope.baseData).toEqual({ name: "Pair", uri: `${PORTAL}/push/pair/`, refUri: `${PORTAL}/push/`, duration: 12 });
  });

  it("reports no change for a page view without a fragment and never touches other fields", () => {
    const envelope = { baseType: "PageviewData", baseData: { uri: `${PORTAL}/dashboard/`, name: "Dashboard#1" } };
    expect(stripPageViewFragments(envelope)).toBe(false);
    expect(envelope.baseData).toEqual({ uri: `${PORTAL}/dashboard/`, name: "Dashboard#1" });
  });

  it("leaves other item kinds and malformed items alone", () => {
    const dependency = { baseType: "RemoteDependencyData", baseData: { uri: PAIR, target: PAIR } };
    expect(stripPageViewFragments(dependency)).toBe(false);
    expect(dependency.baseData.uri).toBe(PAIR);
    expect(stripPageViewFragments({ baseType: "PageviewData" })).toBe(false);
    expect(stripPageViewFragments({ baseData: { uri: PAIR } })).toBe(false);
    expect(stripPageViewFragments({ baseType: "PageviewData", baseData: { uri: 42, refUri: null } })).toBe(false);
  });
});
