import { describe, it, expect } from "vitest";
import {
  buildEventSearchMatcher,
  eventDataSearchText,
  formatEventSearchTerm,
  parseEventSearchQuery,
  type EventSearchFields,
} from "../eventSearchQuery";

const ev = (
  eventType: string,
  message = "",
  source = "Agent",
  data?: Record<string, unknown>,
): EventSearchFields => ({
  eventType,
  message,
  source,
  data,
});

// Payload shape of a gather-rule log-analysis event — the customer case: the text the
// user reads in the expanded event lives only here, not in the message.
const hpiaData = {
  timestamp: "09.07.2026 04:16:33",
  phase: "Installation",
  exitcode: "0",
  logLine: "09.07.2026 04:16:33 -- Installation completed. Exit code = 0.",
  logLineNumber: 21,
  logFile: "HP Image Assistant.log",
  ruleId: "hpia-log-collect",
  ruleTitle: "HP Updater Log Analyze",
  exitCodeInfo: {
    description: "The action completed successfully",
    confidence: "high",
    category: "msi",
    symbol: "ERROR_SUCCESS",
  },
};

const timeline: EventSearchFields[] = [
  ev("app_install_started", "Installing Contoso Reader"),
  ev("app_install_progress", "Downloading 42%"),
  ev("app_install_completed", "Contoso Reader installed", "Agent", { exitCode: 0, errorCode: null }),
  ev("esp_provisioning_status", "DeviceSetup: Apps 3/7"),
  ev("enrollment_failed", "Apps failed with exit code -1", "DecisionEngine"),
  ev("hpia_log_analyze", "HP Updater Log Analyze", "GatherRuleExecutor", hpiaData),
];

const visible = (query: string) => {
  const matcher = buildEventSearchMatcher(query);
  const rows = matcher ? timeline.filter(matcher) : timeline;
  return rows.map(e => e.eventType);
};

describe("parseEventSearchQuery", () => {
  it("splits whitespace-separated terms and lowercases them", () => {
    expect(parseEventSearchQuery("ESP Provisioning")).toEqual({
      include: [{ text: "esp" }, { text: "provisioning" }],
      exclude: [],
    });
  });

  it("reads a leading minus as an exclusion", () => {
    expect(parseEventSearchQuery("error -app_install_progress -perf")).toEqual({
      include: [{ text: "error" }],
      exclude: [{ text: "app_install_progress" }, { text: "perf" }],
    });
  });

  it("keeps a quoted minus literal — otherwise an exit code could not be searched", () => {
    expect(parseEventSearchQuery('"-1"')).toEqual({ include: [{ text: "-1" }], exclude: [] });
  });

  it("excludes a quoted phrase as one term", () => {
    expect(parseEventSearchQuery('-"exit code 1"')).toEqual({
      include: [],
      exclude: [{ text: "exit code 1" }],
    });
  });

  it("drops a lone minus so the timeline does not blank out mid-typing", () => {
    expect(parseEventSearchQuery("-")).toEqual({ include: [], exclude: [] });
  });

  it("de-duplicates repeated terms", () => {
    expect(parseEventSearchQuery("app app -perf -perf")).toEqual({
      include: [{ text: "app" }],
      exclude: [{ text: "perf" }],
    });
  });

  it("maps a known qualifier to its field, case-insensitively, with = or :", () => {
    expect(parseEventSearchQuery('TYPE=app_install source:"Decision Engine" -data=hpia content=x')).toEqual({
      include: [
        { text: "app_install", field: "eventType" },
        { text: "decision engine", field: "source" },
        { text: "x", field: "data" },
      ],
      exclude: [{ text: "hpia", field: "data" }],
    });
  });

  it("keeps an unknown qualifier as a literal term", () => {
    expect(parseEventSearchQuery("14:30 foo=bar")).toEqual({
      include: [{ text: "14:30" }, { text: "foo=bar" }],
      exclude: [],
    });
  });

  it("treats the same text with and without a qualifier as two different terms", () => {
    expect(parseEventSearchQuery("apps type=apps")).toEqual({
      include: [{ text: "apps" }, { text: "apps", field: "eventType" }],
      exclude: [],
    });
  });
});

describe("formatEventSearchTerm", () => {
  it("renders the canonical qualifier for the hiding chip", () => {
    expect(formatEventSearchTerm({ text: "perf" })).toBe("perf");
    expect(formatEventSearchTerm({ text: "ime", field: "source" })).toBe("source=ime");
    expect(formatEventSearchTerm({ text: "x", field: "eventType" })).toBe("type=x");
    expect(formatEventSearchTerm({ text: "x", field: "data" })).toBe("data=x");
  });
});

describe("eventDataSearchText", () => {
  it("joins every leaf value, lowercased, one per line — keys are not included", () => {
    const text = eventDataSearchText({ exitCode: 0, nested: { symbol: "ERROR_SUCCESS" }, list: ["a", 1, true], none: null });
    expect(text.split("\n")).toEqual(["0", "error_success", "a", "1", "true"]);
    expect(text).not.toContain("exitcode");
  });

  it("parses nested JSON strings the way the details view renders them", () => {
    const text = eventDataSearchText({ output: '{"status":"Installation completed"}' });
    expect(text).toContain("installation completed");
  });

  it("is empty without a payload", () => {
    expect(eventDataSearchText(undefined)).toBe("");
    expect(eventDataSearchText(null)).toBe("");
  });
});

describe("buildEventSearchMatcher", () => {
  it("returns null when there is nothing to filter on", () => {
    expect(buildEventSearchMatcher("")).toBeNull();
    expect(buildEventSearchMatcher("   ")).toBeNull();
    expect(buildEventSearchMatcher("-")).toBeNull();
    expect(buildEventSearchMatcher("type=")).toBeNull();
  });

  it("matches event type, message and source", () => {
    const matcher = buildEventSearchMatcher("decisionengine")!;
    expect(timeline.filter(matcher).map(e => e.eventType)).toEqual(["enrollment_failed"]);
    expect(visible("contoso")).toEqual(["app_install_started", "app_install_completed"]);
  });

  it("finds text that only exists in the details payload — the customer's 'Installation' case", () => {
    expect(visible("Installation")).toEqual(["hpia_log_analyze"]);
    expect(visible('"Installation completed"')).toEqual(["hpia_log_analyze"]);
    expect(visible("ERROR_SUCCESS")).toEqual(["hpia_log_analyze"]);
  });

  it("hides every match of an excluded term", () => {
    expect(visible("-app_install_progress")).toEqual([
      "app_install_started",
      "app_install_completed",
      "esp_provisioning_status",
      "enrollment_failed",
      "hpia_log_analyze",
    ]);
  });

  it("treats a partial type name as a prefix filter over the whole family", () => {
    expect(visible("-app_install")).toEqual(["esp_provisioning_status", "enrollment_failed", "hpia_log_analyze"]);
  });

  it("combines a search term with exclusions", () => {
    expect(visible("apps -esp")).toEqual(["enrollment_failed"]);
  });

  it("ANDs multiple search terms across different fields", () => {
    expect(visible("app_install contoso")).toEqual([
      "app_install_started",
      "app_install_completed",
    ]);
    expect(visible("gatherruleexecutor completed")).toEqual(["hpia_log_analyze"]);
  });

  it("never matches a term across a field boundary", () => {
    // "started" ends eventType, "Installing" opens message — a naive concatenation
    // would let the joined string match.
    expect(visible("startedinstalling")).toEqual([]);
    // Two adjacent payload values, likewise.
    expect(visible("installation0")).toEqual([]);
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

  it("restricts a qualified term to its field", () => {
    // "installation" appears in the message of app_install_started only via "Installing" — not a match;
    // in the payload it is a match. type= must not look at either.
    expect(visible("type=installation")).toEqual([]);
    expect(visible("data=installation")).toEqual(["hpia_log_analyze"]);
    expect(visible("content=installation")).toEqual(["hpia_log_analyze"]);
    expect(visible("message=installing")).toEqual(["app_install_started"]);
    expect(visible("source=decision")).toEqual(["enrollment_failed"]);
    // "apps" is in messages, not in any event type.
    expect(visible("type=apps")).toEqual([]);
    expect(visible("message=apps")).toEqual(["esp_provisioning_status", "enrollment_failed"]);
  });

  it("restricts a qualified exclusion to its field", () => {
    // Free-text -completed would also hide hpia_log_analyze (payload); type= keeps it.
    expect(visible("-type=completed")).toEqual([
      "app_install_started",
      "app_install_progress",
      "esp_provisioning_status",
      "enrollment_failed",
      "hpia_log_analyze",
    ]);
    expect(visible("-completed")).toEqual([
      "app_install_started",
      "app_install_progress",
      "esp_provisioning_status",
      "enrollment_failed",
    ]);
  });

  it("does not match payload keys — an errorCode: null field is invisible to -error", () => {
    expect(visible("errorcode")).toEqual([]);
    expect(visible("-error")).toContain("app_install_completed");
  });
});
