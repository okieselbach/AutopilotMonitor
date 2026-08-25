import { describe, it, expect } from "vitest";
import {
  buildEventSearchMatcher,
  parseEventSearchQuery,
  type EventSearchFields,
} from "../eventSearchQuery";

const ev = (eventType: string, message = "", source = "Agent"): EventSearchFields => ({
  eventType,
  message,
  source,
});

const timeline: EventSearchFields[] = [
  ev("app_install_started", "Installing Contoso Reader"),
  ev("app_install_progress", "Downloading 42%"),
  ev("app_install_completed", "Contoso Reader installed"),
  ev("esp_provisioning_status", "DeviceSetup: Apps 3/7"),
  ev("enrollment_failed", "Apps failed with exit code -1", "DecisionEngine"),
];

const visible = (query: string) => {
  const matcher = buildEventSearchMatcher(query);
  const rows = matcher ? timeline.filter(matcher) : timeline;
  return rows.map(e => e.eventType);
};

describe("parseEventSearchQuery", () => {
  it("splits whitespace-separated terms and lowercases them", () => {
    expect(parseEventSearchQuery("ESP Provisioning")).toEqual({
      include: ["esp", "provisioning"],
      exclude: [],
    });
  });

  it("reads a leading minus as an exclusion", () => {
    expect(parseEventSearchQuery("error -app_install_progress -perf")).toEqual({
      include: ["error"],
      exclude: ["app_install_progress", "perf"],
    });
  });

  it("keeps a quoted minus literal — otherwise an exit code could not be searched", () => {
    expect(parseEventSearchQuery('"-1"')).toEqual({ include: ["-1"], exclude: [] });
  });

  it("excludes a quoted phrase as one term", () => {
    expect(parseEventSearchQuery('-"exit code 1"')).toEqual({
      include: [],
      exclude: ["exit code 1"],
    });
  });

  it("drops a lone minus so the timeline does not blank out mid-typing", () => {
    expect(parseEventSearchQuery("-")).toEqual({ include: [], exclude: [] });
  });

  it("de-duplicates repeated terms", () => {
    expect(parseEventSearchQuery("app app -perf -perf")).toEqual({
      include: ["app"],
      exclude: ["perf"],
    });
  });
});

describe("buildEventSearchMatcher", () => {
  it("returns null when there is nothing to filter on", () => {
    expect(buildEventSearchMatcher("")).toBeNull();
    expect(buildEventSearchMatcher("   ")).toBeNull();
    expect(buildEventSearchMatcher("-")).toBeNull();
  });

  it("matches event type, message and source", () => {
    const matcher = buildEventSearchMatcher("decisionengine")!;
    expect(timeline.filter(matcher).map(e => e.eventType)).toEqual(["enrollment_failed"]);
    expect(visible("contoso")).toEqual(["app_install_started", "app_install_completed"]);
  });

  it("hides every match of an excluded term", () => {
    expect(visible("-app_install_progress")).toEqual([
      "app_install_started",
      "app_install_completed",
      "esp_provisioning_status",
      "enrollment_failed",
    ]);
  });

  it("treats a partial type name as a prefix filter over the whole family", () => {
    expect(visible("-app_install")).toEqual(["esp_provisioning_status", "enrollment_failed"]);
  });

  it("combines a search term with exclusions", () => {
    expect(visible("apps -esp")).toEqual(["enrollment_failed"]);
  });

  it("ANDs multiple search terms across different fields", () => {
    expect(visible("app_install contoso")).toEqual([
      "app_install_started",
      "app_install_completed",
    ]);
  });

  it("never matches a term across a field boundary", () => {
    // "started" ends eventType, "Installing" opens message — a naive concatenation
    // would let the joined string match.
    expect(visible("startedinstalling")).toEqual([]);
  });

  it("is case-insensitive on both sides", () => {
    expect(visible("-APP_INSTALL_PROGRESS")).not.toContain("app_install_progress");
  });

  it("lets an exclusion win over a search term that also matches", () => {
    expect(visible("app_install -progress")).toEqual([
      "app_install_started",
      "app_install_completed",
    ]);
  });
});
