import { describe, it, expect } from "vitest";
import { evidenceChips, humanizeKey } from "../evidenceChips";

describe("evidenceChips", () => {
  it("makes one chip per distinct event, labelled by event type or signal", () => {
    const chips = evidenceChips({
      enrollment_failed_reason: { eventId: "e1", eventType: "enrollment_failed", timestamp: "2026-10-13T14:44:24Z", field: "reason", value: "x" },
      esp_failure_subcategory: { eventId: "e1", field: "failedSubcategory", value: "Apps" },
      failed_app: { eventId: "e2", field: "appName", value: "Contoso Notes" },
      factor_failed_app: true,
      scalar: "text",
    });
    expect(chips).toEqual([
      { signal: "enrollment_failed_reason", eventId: "e1", label: "Enrollment failed", timestamp: "2026-10-13T14:44:24Z" },
      { signal: "failed_app", eventId: "e2", label: "Failed app", timestamp: undefined },
    ]);
  });

  it("links both events of a correlation", () => {
    const chips = evidenceChips({
      app_regression: { joinField: "appId", eventA_eventId: "a", eventA_eventType: "app_install_completed", eventB_eventId: "b", eventB_eventType: "app_install_failed" },
    });
    expect(chips.map((c) => [c.eventId, c.label])).toEqual([
      ["a", "App install completed"],
      ["b", "App install failed"],
    ]);
  });

  it("is empty without evidence", () => {
    expect(evidenceChips(null)).toEqual([]);
    expect(evidenceChips({ list: [{ eventId: "x" }] })).toEqual([]);
  });
});

describe("humanizeKey", () => {
  it.each([
    ["app_install_failed", "App install failed"],
    ["failedSubcategory", "Failed subcategory"],
    ["eventA_eventId", "Event a event id"],
    ["", ""],
  ])("%s → %s", (key, expected) => expect(humanizeKey(key)).toBe(expected));
});
