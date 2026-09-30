import { describe, it, expect } from "vitest";
import fs from "node:fs";
import path from "node:path";
import { interpolateRuleTemplate, RULE_TEMPLATE_AUTO_FIELDS } from "../interpolateRuleTemplate";

interface SharedCase {
  name: string;
  template: string | null;
  matchedConditions: Record<string, unknown> | null;
  expected: string;
}

/** tests/fixtures/rule-template-interpolation at the repo root — the cases every renderer runs. */
function loadSharedCases(): { autoFields: string[]; cases: SharedCase[] } {
  let dir = __dirname;
  while (!fs.existsSync(path.join(dir, "AutopilotMonitor.sln"))) {
    const parent = path.dirname(dir);
    if (parent === dir) throw new Error("repository root (AutopilotMonitor.sln) not found");
    dir = parent;
  }
  return JSON.parse(
    fs.readFileSync(path.join(dir, "tests", "fixtures", "rule-template-interpolation", "cases.json"), "utf8")
  );
}

describe("interpolateRuleTemplate — shared cases (web = MCP = backend)", () => {
  const shared = loadSharedCases();

  it("has cases and the same auto-field list as the rule engine", () => {
    expect(shared.cases.length).toBeGreaterThanOrEqual(20);
    expect([...RULE_TEMPLATE_AUTO_FIELDS]).toEqual(shared.autoFields);
  });

  it.each(shared.cases.map((c) => [c.name, c] as const))("%s", (_name, c) => {
    expect(interpolateRuleTemplate(c.template, c.matchedConditions)).toBe(c.expected);
  });
});

describe("interpolateRuleTemplate", () => {
  const matched = {
    ca2023_not_updated: {
      eventId: "abc",
      sequence: 23,
      timestamp: "2026-04-28T18:24:16Z",
      field: "uefiCA2023Status",
      value: "unknown",
    },
    secureboot_enabled: {
      eventId: "abc",
      field: "uefiSecureBootEnabled",
      value: "True",
    },
  };

  it("replaces {{field}} tokens from matchedConditions", () => {
    const out = interpolateRuleTemplate(
      "Status: `{{uefiCA2023Status}}`",
      matched
    );
    expect(out).toBe("Status: `unknown`");
  });

  it("replaces multiple distinct tokens in one string", () => {
    const out = interpolateRuleTemplate(
      "SB={{uefiSecureBootEnabled}} CA={{uefiCA2023Status}}",
      matched
    );
    expect(out).toBe("SB=True CA=unknown");
  });

  it("falls back to signal-name lookup when field is unknown", () => {
    const out = interpolateRuleTemplate("{{ca2023_not_updated}}", matched);
    expect(out).toBe("unknown");
  });

  it("tolerates whitespace inside braces", () => {
    const out = interpolateRuleTemplate("[{{  uefiCA2023Status  }}]", matched);
    expect(out).toBe("[unknown]");
  });

  it("leaves unknown tokens untouched so authors notice the typo", () => {
    const out = interpolateRuleTemplate("{{notARealField}}", matched);
    expect(out).toBe("{{notARealField}}");
  });

  it("returns input unchanged when matchedConditions is null/undefined", () => {
    expect(interpolateRuleTemplate("hello {{x}}", null)).toBe("hello {{x}}");
    expect(interpolateRuleTemplate("hello {{x}}", undefined)).toBe(
      "hello {{x}}"
    );
  });

  it("handles non-string scalar values (numbers, booleans)", () => {
    const out = interpolateRuleTemplate("seq={{n}} ok={{b}}", {
      n: { field: "n", value: 42 },
      b: { field: "b", value: false },
    });
    expect(out).toBe("seq=42 ok=false");
  });

  it("returns empty string for null/undefined input", () => {
    expect(interpolateRuleTemplate(null, matched)).toBe("");
    expect(interpolateRuleTemplate(undefined, matched)).toBe("");
  });

  it("does not blow up on text with no placeholders", () => {
    expect(interpolateRuleTemplate("plain text", matched)).toBe("plain text");
  });

  // Codex review follow-up (P2): when a rule's required condition matches an event
  // that carries auto-injected whitelist fields (appName, errorCode, etc.), the
  // template MUST resolve those tokens from the SAME event — even if other
  // optional conditions matched different events. The interpolator pins to the
  // first matched-conditions entry so the rule's required (= first) condition
  // wins.
  it("ANALYZE-APP-013 multi-failure: interpolation uses the required-condition event, not the first failed app", () => {
    const matchedConditions = {
      // Required condition: matched the Office app with HRESULT 0x87D1041C.
      esp_apps_detection_failure: {
        eventId: "evt-office",
        sequence: 168,
        timestamp: "2026-05-28T10:41:08Z",
        eventType: "app_install_failed",
        field: "failureType",
        value: "esp_apps_detection_failure",
        // AddDataFieldsToEvidence auto-injects these from the SAME event:
        appId: "f9026516-dbec-47cd-ae4e-ec2312a1303c",
        appName: "Microsoft Apps for Enterprise - 64Bit",
        errorPatternId: "esp_apps_detection_failure",
        errorCode: "0x87d1041c",
      },
    };

    const explanation =
      "App **{{appName}}** failed during the Enrollment Status Page with HRESULT **{{errorCode}}**.";
    const out = interpolateRuleTemplate(explanation, matchedConditions);
    expect(out).toBe(
      "App **Microsoft Apps for Enterprise - 64Bit** failed during the Enrollment Status Page with HRESULT **0x87d1041c**."
    );
  });

  it("auto-field resolution pins to the first matched condition (no cross-event drift)", () => {
    // Order matters: the FIRST matched-conditions entry's auto-fields win even
    // when a later entry has different auto-fields (e.g. a second condition
    // that matched a different `app_install_failed` event in the same
    // session). Insertion order of matchedConditions mirrors rule.conditions
    // order on the backend. The decoy below uses a different explicit `field`
    // so the byField path doesn't cross-shadow — this asserts the auto-field
    // shadowing semantics only.
    const matchedConditions = {
      esp_apps_detection_failure: {
        eventId: "evt-office",
        field: "failureType",
        value: "esp_apps_detection_failure",
        appName: "Microsoft Apps for Enterprise - 64Bit",
        errorCode: "0x87d1041c",
      },
      another_app_failure_decoy: {
        eventId: "evt-other",
        field: "isError",
        value: "true",
        appName: "Some Other App",
        errorCode: "0x80070005",
      },
    };

    const out = interpolateRuleTemplate(
      "App={{appName}} HR={{errorCode}}",
      matchedConditions
    );
    expect(out).toBe(
      "App=Microsoft Apps for Enterprise - 64Bit HR=0x87d1041c"
    );
  });

  it("explicit field/value still wins over auto-fields from a later condition", () => {
    // Defense-in-depth: if a downstream condition explicitly declared a
    // dataField that happens to share a name with a whitelist auto-field, the
    // explicit-field path (byField) still wins over the auto-field fallback,
    // because byField is checked first.
    const matchedConditions = {
      explicit_appname: {
        field: "appName",
        value: "Explicit value",
      },
      esp_apps_detection_failure: {
        field: "failureType",
        value: "esp_apps_detection_failure",
        appName: "Auto-field value",
      },
    };
    const out = interpolateRuleTemplate("{{appName}}", matchedConditions);
    expect(out).toBe("Explicit value");
  });

  it("ignores non-whitelist evidence keys (no leakage of internal fields)", () => {
    // Evidence dicts may contain internal keys like `eventId`, `sequence`,
    // `timestamp`, `field`, `value`, `eventType` — none of these may be resolved
    // as template tokens, otherwise authors could accidentally leak Table
    // Storage row keys into customer-visible rule explanations.
    const matchedConditions = {
      sig: {
        eventId: "evt-secret",
        sequence: 42,
        timestamp: "2026-05-28T10:00:00Z",
        eventType: "app_install_failed",
        field: "failureType",
        value: "esp_apps_detection_failure",
      },
    };
    expect(interpolateRuleTemplate("{{eventId}}", matchedConditions)).toBe("{{eventId}}");
    expect(interpolateRuleTemplate("{{sequence}}", matchedConditions)).toBe("{{sequence}}");
    expect(interpolateRuleTemplate("{{timestamp}}", matchedConditions)).toBe("{{timestamp}}");
    expect(interpolateRuleTemplate("{{eventType}}", matchedConditions)).toBe("{{eventType}}");
  });
});
