import { describe, it, expect } from "vitest";
import { EMPTY_FORM, formToEvaluateOn, jsonToForm } from "../types";

/**
 * The JSON editor serializes the FORM object (eval* checkbox fields), but what users
 * paste is usually a rule EXPORT (the "Export" button's shape, e.g. received from
 * another tenant) which carries `evaluateOn` instead. Before the mapper, a pasted
 * export silently lost evaluateOn — the imported rule quietly reverted to the
 * enrollment-end default.
 */
describe("jsonToForm", () => {
  const baseExport = {
    ruleId: "ANALYZE-CUSTOM-101",
    title: "VPN client install failed",
    severity: "high",
    category: "network",
    conditions: [
      {
        signal: "vpn_failed",
        source: "event_data",
        eventType: "app_install_failed",
        dataField: "appName",
        operator: "contains",
        value: "VPN",
        required: true,
      },
    ],
    baseConfidence: 70,
    confidenceThreshold: 50,
    tags: ["vpn", "network"],
  };

  it("maps a rule-shaped export's evaluateOn onto the form flags", () => {
    const form = jsonToForm({
      ...baseExport,
      evaluateOn: ["whiteglove_sealed", "on_event:vpn_error"],
    });

    expect(form.evalAtEnrollmentEnd).toBe(false);
    expect(form.evalAtWhitegloveSealed).toBe(true);
    expect(form.evalOnEventTypes).toBe("vpn_error");
    expect(form.conditions).toHaveLength(1);
    expect(form.tags).toEqual(["vpn", "network"]);
  });

  it("defaults a terminal-only export (no evaluateOn key) to enrollment end", () => {
    const form = jsonToForm(baseExport);

    expect(form.evalAtEnrollmentEnd).toBe(true);
    expect(form.evalAtWhitegloveSealed).toBe(false);
    expect(form.evalOnEventTypes).toBe("");
  });

  it("passes form-shaped JSON (eval* fields, no evaluateOn) through unchanged", () => {
    const form = jsonToForm({
      ...EMPTY_FORM,
      ruleId: "ANALYZE-CUSTOM-102",
      title: "Pasted form",
      evalAtEnrollmentEnd: false,
      evalAtWhitegloveSealed: true,
    });

    expect(form.evalAtEnrollmentEnd).toBe(false);
    expect(form.evalAtWhitegloveSealed).toBe(true);
  });

  it("prefers an explicit evaluateOn over stale form flags on an edit merge", () => {
    // Edit-mode paste merges into the current form, so both shapes are present:
    // the export's evaluateOn must win over the edited rule's stale checkboxes.
    const form = jsonToForm({
      ...EMPTY_FORM,
      ...baseExport,
      evalAtEnrollmentEnd: true,
      evaluateOn: ["whiteglove_sealed"],
    });

    expect(form.evalAtEnrollmentEnd).toBe(false);
    expect(form.evalAtWhitegloveSealed).toBe(true);
  });

  it("folds the tenant markSessionAsFailed override into the imported default", () => {
    // An exported rule that (effectively) marks sessions failed must keep doing so
    // when re-created in another tenant from its JSON.
    expect(jsonToForm({ ...baseExport, markSessionAsFailed: true }).markSessionAsFailedDefault).toBe(true);
    expect(jsonToForm({ ...baseExport, markSessionAsFailedDefault: true }).markSessionAsFailedDefault).toBe(true);
    expect(jsonToForm({ ...baseExport, markSessionAsFailedDefault: true, markSessionAsFailed: false }).markSessionAsFailedDefault).toBe(false);
    expect(jsonToForm(baseExport).markSessionAsFailedDefault).toBe(false);
  });

  it("round-trips evaluateOn through the form and back", () => {
    const evaluateOn = ["enrollment_end", "whiteglove_sealed", "on_event:vpn_error"];

    expect(formToEvaluateOn(jsonToForm({ ...baseExport, evaluateOn }))).toEqual(evaluateOn);
    // Terminal-only stays clean of the field (undefined = backend default).
    expect(formToEvaluateOn(jsonToForm(baseExport))).toBeUndefined();
  });
});
