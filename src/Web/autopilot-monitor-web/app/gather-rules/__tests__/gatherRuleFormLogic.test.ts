import { describe, it, expect } from "vitest";
import {
  EMPTY_FORM,
  NewRuleForm,
  buildScopeFields,
  firesExactlyOnce,
  supportsEmitMode,
  supportsPhaseScope,
  validateScopeSelection,
  withDerivedScopeMode,
} from "../types";

const form = (over: Partial<NewRuleForm>): NewRuleForm => ({ ...EMPTY_FORM, ...over });

/**
 * The gather-rule form offers three independent "when" controls (trigger, phase scope, emit
 * mode) and not every combination is meaningful: a trigger naming a concrete phase already
 * pins the single firing, so a scope could only ever suppress it and emit-dedup has nothing
 * to compare against. These predicates decide which controls are shown, so they also decide
 * what the payload may contain.
 */
describe("which controls apply to a trigger", () => {
  it("treats startup and concrete-phase triggers as one-shot", () => {
    expect(firesExactlyOnce(form({ trigger: "startup" }))).toBe(true);
    expect(firesExactlyOnce(form({ trigger: "phase_change", triggerPhase: "AccountSetup" }))).toBe(true);
    expect(firesExactlyOnce(form({ trigger: "phase_exit", triggerPhase: "FinalizingSetup" }))).toBe(true);
  });

  it("treats repeating triggers as multi-shot", () => {
    expect(firesExactlyOnce(form({ trigger: "interval" }))).toBe(false);
    expect(firesExactlyOnce(form({ trigger: "on_event", triggerEventType: "app_install_failed" }))).toBe(false);
    // "Any phase" fires on every transition
    expect(firesExactlyOnce(form({ trigger: "phase_change", triggerPhase: "" }))).toBe(false);
    expect(firesExactlyOnce(form({ trigger: "phase_exit", triggerPhase: "" }))).toBe(false);
  });

  it("hides phase scope only where the trigger already names the phase", () => {
    expect(supportsPhaseScope(form({ trigger: "phase_change", triggerPhase: "AccountSetup" }))).toBe(false);
    expect(supportsPhaseScope(form({ trigger: "phase_exit", triggerPhase: "AccountSetup" }))).toBe(false);

    expect(supportsPhaseScope(form({ trigger: "phase_change", triggerPhase: "" }))).toBe(true);
    expect(supportsPhaseScope(form({ trigger: "interval" }))).toBe(true);
    expect(supportsPhaseScope(form({ trigger: "on_event" }))).toBe(true);
  });

  it("keeps phase scope for startup — it defers the one-shot instead of suppressing it", () => {
    expect(supportsPhaseScope(form({ trigger: "startup" }))).toBe(true);
    expect(supportsEmitMode(form({ trigger: "startup" }))).toBe(false);
  });

  it("hides emit mode for every one-shot trigger", () => {
    expect(supportsEmitMode(form({ trigger: "phase_change", triggerPhase: "AccountSetup" }))).toBe(false);
    expect(supportsEmitMode(form({ trigger: "interval" }))).toBe(true);
    expect(supportsEmitMode(form({ trigger: "phase_exit", triggerPhase: "" }))).toBe(true);
  });
});

describe("validateScopeSelection", () => {
  it("rejects a scope mode that was selected but left unfilled", () => {
    // The trap: the dropdown reads "From a phase onwards" while the payload would send null,
    // so the rule would silently run everywhere.
    expect(validateScopeSelection(form({ trigger: "interval", scopeMode: "from", activeFromPhase: "" })))
      .toMatch(/Active From Phase/);
    expect(validateScopeSelection(form({ trigger: "interval", scopeMode: "during", activePhases: [] })))
      .toMatch(/Active Phases/);
  });

  it("accepts a filled scope and the always mode", () => {
    expect(validateScopeSelection(form({ trigger: "interval", scopeMode: "from", activeFromPhase: "AccountSetup" }))).toBeNull();
    expect(validateScopeSelection(form({ trigger: "interval", scopeMode: "during", activePhases: ["AppsUser"] }))).toBeNull();
    expect(validateScopeSelection(form({ trigger: "interval", scopeMode: "always" }))).toBeNull();
  });

  it("does not block a trigger whose scope control is hidden", () => {
    // Stale scope state from an earlier trigger choice must not make the form unsavable.
    expect(validateScopeSelection(form({
      trigger: "phase_change", triggerPhase: "AccountSetup", scopeMode: "from", activeFromPhase: "",
    }))).toBeNull();
  });
});

describe("buildScopeFields", () => {
  it("sends the selected scope and emit mode for a repeating trigger", () => {
    expect(buildScopeFields(form({
      trigger: "interval", scopeMode: "from", activeFromPhase: "AccountSetup", emitMode: "on_change",
    }))).toEqual({ activePhases: null, activeFromPhase: "AccountSetup", emitMode: "on_change" });

    expect(buildScopeFields(form({
      trigger: "interval", scopeMode: "during", activePhases: ["DeviceSetup", "AppsDevice"], emitMode: "always",
    }))).toEqual({ activePhases: ["DeviceSetup", "AppsDevice"], activeFromPhase: null, emitMode: "always" });
  });

  it("drops hidden-control state instead of leaking it into the payload", () => {
    // User configures a scoped interval rule, then switches to a concrete-phase trigger:
    // the now-hidden scope and emit values must not be persisted.
    const switched = form({
      trigger: "phase_change",
      triggerPhase: "FinalizingSetup",
      scopeMode: "during",
      activePhases: ["AccountSetup"],
      emitMode: "on_change",
    });

    expect(buildScopeFields(switched)).toEqual({
      activePhases: null, activeFromPhase: null, emitMode: null,
    });
  });

  it("keeps a deferring scope on a startup rule but drops its emit mode", () => {
    expect(buildScopeFields(form({
      trigger: "startup", scopeMode: "from", activeFromPhase: "AccountSetup", emitMode: "on_change",
    }))).toEqual({ activePhases: null, activeFromPhase: "AccountSetup", emitMode: null });
  });

  it("treats an unfilled scope mode as unrestricted", () => {
    expect(buildScopeFields(form({ trigger: "interval", scopeMode: "during", activePhases: [] })).activePhases).toBeNull();
  });
});

describe("withDerivedScopeMode", () => {
  it("derives the UI-only scopeMode from rule-shaped JSON", () => {
    expect(withDerivedScopeMode(form({ activePhases: ["AccountSetup"], activeFromPhase: "" })).scopeMode).toBe("during");
    expect(withDerivedScopeMode(form({ activePhases: [], activeFromPhase: "DeviceSetup" })).scopeMode).toBe("from");
    expect(withDerivedScopeMode(form({ activePhases: [], activeFromPhase: "" })).scopeMode).toBe("always");
  });

  it("coerces unknown or missing emit modes to always", () => {
    expect(withDerivedScopeMode(form({ emitMode: "sometimes" })).emitMode).toBe("always");
    expect(withDerivedScopeMode(form({ emitMode: "on_change" })).emitMode).toBe("on_change");
  });

  it("survives non-array activePhases from hand-written JSON", () => {
    const parsed = { ...EMPTY_FORM, activePhases: undefined } as unknown as NewRuleForm;
    expect(withDerivedScopeMode(parsed).activePhases).toEqual([]);
  });
});
