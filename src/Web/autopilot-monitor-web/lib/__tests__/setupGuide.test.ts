import { describe, expect, it } from "vitest";
import type { TenantConfiguration } from "@/lib/generated/wire-types.generated";
import {
  deriveSetupChecklist,
  parseSetupStep,
  parseSetupTour,
  recommendedPatch,
  recommendedStatus,
  selectionUsesHostedStorage,
  type RecommendedItem,
  type SetupGuideFlags,
} from "../setupGuide";

const NEW_TENANT: SetupGuideFlags = {
  deviceValidationEnabled: false,
  setupGuideSettingsDone: false,
  setupGuideHardwareDone: false,
  setupGuideDismissed: false,
  hasEnrollmentSessions: false,
};

const flags = (overrides: Partial<SetupGuideFlags> = {}): SetupGuideFlags => ({ ...NEW_TENANT, ...overrides });

describe("deriveSetupChecklist", () => {
  it("a new tenant sees all four steps open, starting with the settings", () => {
    const checklist = deriveSetupChecklist(flags(), {})!;
    expect(checklist.doneCount).toBe(0);
    expect(checklist.next).toBe("settings");
    expect(checklist.visible).toBe(true);
  });

  it("done states come from the flags; the next step is the first open one", () => {
    const checklist = deriveSetupChecklist(flags({ setupGuideSettingsDone: true, deviceValidationEnabled: true }), {})!;
    expect(checklist.done).toEqual({ settings: true, hardware: false, validation: true, deploy: false });
    expect(checklist.doneCount).toBe(2);
    expect(checklist.next).toBe("hardware");
  });

  it("local marks finish a step before the cached flags catch up", () => {
    const checklist = deriveSetupChecklist(flags(), { settings: true, hardware: true, validation: true })!;
    expect(checklist.next).toBe("deploy");
    expect(checklist.visible).toBe(true);
  });

  it("the first enrollment completes the guide and hides it", () => {
    const fromFlags = deriveSetupChecklist(flags({ hasEnrollmentSessions: true }), {})!;
    expect(fromFlags.done.deploy).toBe(true);
    expect(fromFlags.visible).toBe(false);

    const fromLocal = deriveSetupChecklist(flags(), { enrolled: true })!;
    expect(fromLocal.done.deploy).toBe(true);
    expect(fromLocal.visible).toBe(false);
  });

  it("all done leaves no next step", () => {
    const checklist = deriveSetupChecklist(
      flags({ setupGuideSettingsDone: true, setupGuideHardwareDone: true, deviceValidationEnabled: true, hasEnrollmentSessions: true }), {})!;
    expect(checklist.doneCount).toBe(4);
    expect(checklist.next).toBeNull();
  });

  it("a hidden guide stays hidden; a local mark wins over the cached flag both ways", () => {
    expect(deriveSetupChecklist(flags({ setupGuideDismissed: true }), {})!.visible).toBe(false);
    expect(deriveSetupChecklist(flags(), { dismissed: true })!.visible).toBe(false);
    expect(deriveSetupChecklist(flags({ setupGuideDismissed: true }), { dismissed: false })!.visible).toBe(true);
  });

  it("an unknown enrollment state never shows the guide", () => {
    expect(deriveSetupChecklist(flags({ hasEnrollmentSessions: undefined }), {})!.visible).toBe(false);
  });

  it("no flags, no checklist", () => {
    expect(deriveSetupChecklist(null, { settings: true })).toBeNull();
  });
});

const config = (overrides: Partial<TenantConfiguration> = {}): TenantConfiguration =>
  ({
    enableGeoLocation: true,
    enableTimezoneAutoSet: false,
    enableSoftwareInventoryAnalyzer: false,
    diagnosticsUploadMode: "Off",
    diagnosticsUploadDestination: undefined,
    diagnosticsBlobSasUrl: undefined,
    ...overrides,
  }) as TenantConfiguration;

const ALL: ReadonlySet<RecommendedItem> = new Set(["timezone", "diagnostics", "inventory"]);

describe("recommended settings", () => {
  it("a default tenant gets every recommendation, diagnostics to hosted storage with the chosen mode", () => {
    const current = config();
    expect(recommendedStatus(current).alreadyOn).toEqual({ timezone: false, diagnostics: false, inventory: false });
    expect(recommendedPatch(current, ALL, "OnFailure")).toEqual({
      enableTimezoneAutoSet: true,
      diagnosticsUploadDestination: "Hosted",
      diagnosticsUploadMode: "OnFailure",
      enableSoftwareInventoryAnalyzer: true,
    });
    expect(selectionUsesHostedStorage(current, ALL)).toBe(true);
  });

  it("only the selected items are written", () => {
    expect(recommendedPatch(config(), new Set<RecommendedItem>(["inventory"]), "Always")).toEqual({ enableSoftwareInventoryAnalyzer: true });
    expect(selectionUsesHostedStorage(config(), new Set<RecommendedItem>(["inventory"]))).toBe(false);
  });

  it("items already on stay untouched", () => {
    const current = config({ enableTimezoneAutoSet: true, enableSoftwareInventoryAnalyzer: true, diagnosticsUploadMode: "Always", diagnosticsUploadDestination: "Hosted" });
    expect(recommendedStatus(current).alreadyOn).toEqual({ timezone: true, diagnostics: true, inventory: true });
    expect(recommendedPatch(current, ALL, "OnFailure")).toEqual({});
    expect(selectionUsesHostedStorage(current, ALL)).toBe(false);
  });

  it("turning the timezone on turns geo-location back on, which it depends on", () => {
    const current = config({ enableGeoLocation: false, enableTimezoneAutoSet: true });
    const status = recommendedStatus(current);
    expect(status.geoOff).toBe(true);
    expect(status.alreadyOn.timezone).toBe(false);
    expect(recommendedPatch(current, new Set<RecommendedItem>(["timezone"]), "OnFailure")).toEqual({
      enableTimezoneAutoSet: true,
      enableGeoLocation: true,
    });
  });

  it("an own storage account keeps its destination and never needs the hosted disclosure", () => {
    const current = config({ diagnosticsUploadDestination: "CustomerSas", diagnosticsBlobSasUrl: "https://storage.example.test/container?sv=x" });
    expect(recommendedStatus(current).ownStorage).toBe(true);
    expect(recommendedPatch(current, new Set<RecommendedItem>(["diagnostics"]), "OnFailure")).toEqual({ diagnosticsUploadMode: "OnFailure" });
    expect(selectionUsesHostedStorage(current, ALL)).toBe(false);
  });
});

describe("parseSetupTour", () => {
  it("accepts the known tours only", () => {
    expect(parseSetupTour("validation")).toBe("validation");
    expect(parseSetupTour("showme:diagnostics")).toBe("showme:diagnostics");
    expect(parseSetupTour("showme:elsewhere")).toBeNull();
    expect(parseSetupTour(null)).toBeNull();
  });
});

describe("parseSetupStep", () => {
  it("accepts the four steps a link may name", () => {
    expect(["settings", "hardware", "validation", "deploy"].map(parseSetupStep)).toEqual(["settings", "hardware", "validation", "deploy"]);
  });

  it("ignores anything else, so a link can never open a state the guide does not have", () => {
    for (const raw of ["complete", "Settings", "", "reset", "showme:timezone", null, undefined]) {
      expect(parseSetupStep(raw)).toBeNull();
    }
  });
});
