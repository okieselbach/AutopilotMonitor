/**
 * The Settings form projections, what a save sends and the unsaved-changes check built on them. A
 * freshly loaded row must never count as edited (otherwise the sidebar marks pages nobody touched
 * and leaving asks for nothing), an edit must mark exactly its own section, the merge after a save
 * must clear it — and a save sends what the admin changed, nothing else: a form default the row
 * never stored must not travel, or a page loaded before somebody else's write reverts that write.
 * Rows are built here by hand; expectations are test-local lists, never derived from the map.
 */
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";
import { SAVE_BAR_SECTIONS, type SettingsSectionName } from "../../sectionFieldMap";
import type { NotificationChannel, TenantConfiguration } from "../../types";
import {
  DEFAULT_SETTINGS_FORM,
  configFromForm,
  formFromConfig,
  hardwareRejectionNotify,
  sectionSavePayload,
  sectionsWithUnsavedEdits,
  settingsFormSetters,
  type SettingsForm,
} from "../settingsForm";

function channel(id: string, overrides: Partial<NotificationChannel> = {}): NotificationChannel {
  return {
    id,
    name: `Channel ${id}`,
    providerType: 2,
    url: `https://hooks.example.com/${id}`,
    enabled: true,
    notifyOnSuccess: true,
    notifyOnFailure: true,
    notifyOnHardwareRejection: false,
    ...overrides,
  };
}

/** Only the fields the model always carries; everything optional is absent. */
function minimalRow(): TenantConfiguration {
  return {
    tenantId: "00000000-0000-4000-8000-000000000001",
    lastUpdated: "2026-11-01T08:00:00Z",
    updatedBy: "admin@example.com",
    manufacturerWhitelist: "Dell*,HP*",
    modelWhitelist: "*",
    validateAutopilotDevice: true,
    dataRetentionDays: 90,
    sessionTimeoutHours: 5,
    enablePerformanceCollector: true,
    performanceCollectorIntervalSeconds: 30,
  };
}

function fullRow(): TenantConfiguration {
  return {
    ...minimalRow(),
    webhookNotifyOnHardwareRejection: false,
    validateCorporateIdentifier: true,
    helloWaitTimeoutSeconds: 600,
    selfDestructOnComplete: false,
    keepLogFile: true,
    rebootOnComplete: true,
    rebootDelaySeconds: 30,
    contactEmail: "ops@example.com",
    companyName: "Example Corp",
    logLevel: "Verbose",
    showEnrollmentSummary: true,
    enrollmentSummaryBrandingImageUrl: "https://cdn.example.com/banner.png",
    notificationChannelsJson: JSON.stringify([channel("a"), channel("b", { enabled: false })]),
    slaTargetSuccessRate: 95,
    slaNotifyOnConsecutiveFailures: true,
    slaConsecutiveFailureThreshold: 3,
    diagnosticsBlobSasUrl: "https://storage.example.com/diag?sv=2024&sig=abc",
    diagnosticsUploadMode: "OnFailure",
    diagnosticsUploadDestination: "CustomerSas",
    diagnosticsLogPathsJson: JSON.stringify([{ path: "C:\\Logs\\setup.log", description: "Setup" }]),
    localAdminAllowedAccountsJson: JSON.stringify(["svc-deploy", "kiosk|user"]),
    enableSoftwareInventoryAnalyzer: true,
    unrestrictedMode: false,
  };
}

/** Rows whose stored shape differs from what the form writes back without anyone editing. */
const LOADED_ROWS: Record<string, () => TenantConfiguration> = {
  "minimal row (every default filled in)": minimalRow,
  "full row": fullRow,
  "empty strings where the form writes undefined": () => ({
    ...fullRow(),
    enrollmentSummaryBrandingImageUrl: "",
    diagnosticsBlobSasUrl: "",
    notificationChannelsJson: "",
    diagnosticsLogPathsJson: "",
    localAdminAllowedAccountsJson: "",
  }),
  "untrimmed contact values": () => ({ ...fullRow(), contactEmail: " ops@example.com ", companyName: "Example Corp  " }),
  "malformed JSON lists": () => ({
    ...fullRow(),
    notificationChannelsJson: "[{broken",
    diagnosticsLogPathsJson: "{not json",
    localAdminAllowedAccountsJson: "nope",
  }),
  "legacy single webhook without channels": () => ({
    ...minimalRow(),
    webhookProviderType: 10,
    webhookUrl: "https://hooks.example.com/legacy",
    webhookNotifyOnHardwareRejection: true,
  }),
  "channels stored with other formatting": () => ({
    ...fullRow(),
    notificationChannelsJson: JSON.stringify([channel("a")], null, 2),
  }),
};

/** The row after a section save: what the save sends, merged like the settings provider merges it. */
function saveAndMerge(row: TenantConfiguration, form: SettingsForm, section: SettingsSectionName): TenantConfiguration {
  return { ...row, ...sectionSavePayload(row, form, section) } as TenantConfiguration;
}

function unsaved(row: TenantConfiguration, form: SettingsForm): string[] {
  return [...sectionsWithUnsavedEdits(row, form)].sort();
}

/** The context's write-through of the hardware switch. */
function toggleHardwareNotify(form: SettingsForm, value: boolean): SettingsForm {
  return {
    ...form,
    webhookNotifyOnHardwareRejection: value,
    notificationChannels: form.notificationChannels.map((c) => ({ ...c, notifyOnHardwareRejection: value })),
  };
}

describe("sectionsWithUnsavedEdits", () => {
  it.each(Object.entries(LOADED_ROWS))("a freshly loaded row never counts as edited: %s", (_, build) => {
    const row = build();
    expect(unsaved(row, formFromConfig(row))).toEqual([]);
  });

  const EDITS: Array<[string, (form: SettingsForm) => SettingsForm, string]> = [
    ["manufacturer added", (f) => ({ ...f, manufacturerWhitelist: `${f.manufacturerWhitelist},Lenovo*` }), "hardwareWhitelist"],
    ["channel added", (f) => ({ ...f, notificationChannels: [...f.notificationChannels, channel("new")] }), "notifications"],
    ["SLA threshold", (f) => ({ ...f, slaConsecutiveFailureThreshold: 7 }), "slaTargets"],
    ["company name", (f) => ({ ...f, companyName: "Other Corp" }), "contact"],
    ["log level", (f) => ({ ...f, logLevel: "Debug" }), "agentParameters"],
    ["branding cleared", (f) => ({ ...f, enrollmentSummaryBrandingImageUrl: "" }), "agentParameters"],
    ["collector interval", (f) => ({ ...f, performanceCollectorInterval: 120 }), "agentCollectors"],
    ["allowed account", (f) => ({ ...f, localAdminAllowedAccounts: [...f.localAdminAllowedAccounts, "svc-build"] }), "agentAnalyzers"],
    ["log path removed", (f) => ({ ...f, tenantDiagPaths: [] }), "diagnostics"],
    ["retention", (f) => ({ ...f, dataRetentionDays: 30 }), "dataManagement"],
  ];

  it.each(EDITS)("an edit marks exactly its own section: %s", (_, edit, section) => {
    const row = fullRow();
    expect(unsaved(row, edit(formFromConfig(row)))).toEqual([section]);
  });

  it.each(EDITS)("the merge after saving clears that section: %s", (_, edit, section) => {
    const row = fullRow();
    const form = edit(formFromConfig(row));
    expect(unsaved(saveAndMerge(row, form, section as SettingsSectionName), form)).toEqual([]);
  });

  it("saving one card of a page leaves the other card's edit unsaved", () => {
    const row = fullRow();
    const form = { ...formFromConfig(row), logLevel: "Debug", performanceCollectorInterval: 120 };
    expect(unsaved(row, form)).toEqual(["agentCollectors", "agentParameters"]);
    expect(unsaved(saveAndMerge(row, form, "agentParameters"), form)).toEqual(["agentCollectors"]);
  });

  it("an edit taken back is no longer unsaved", () => {
    const row = fullRow();
    const loaded = formFromConfig(row);
    const edited = { ...loaded, logLevel: "Debug" };
    expect(unsaved(row, { ...edited, logLevel: loaded.logLevel })).toEqual([]);
  });

  it("whitespace around contact values is no edit, because the save would trim it away", () => {
    const row = fullRow();
    expect(unsaved(row, { ...formFromConfig(row), companyName: "Example Corp " })).toEqual([]);
  });

  it("validation gates and Unrestricted Mode never count: they save on change", () => {
    const row = fullRow();
    const loaded = formFromConfig(row);
    const form: SettingsForm = {
      ...loaded,
      validateAutopilotDevice: !loaded.validateAutopilotDevice,
      validateCorporateIdentifier: !loaded.validateCorporateIdentifier,
      validateDeviceAssociation: !loaded.validateDeviceAssociation,
      validateCloudPcDevice: !loaded.validateCloudPcDevice,
      validateIntuneDeviceBinding: !loaded.validateIntuneDeviceBinding,
      unrestrictedMode: !loaded.unrestrictedMode,
    };
    expect(unsaved(row, form)).toEqual([]);
  });

  it("saving the notifications of a legacy tenant materializes the channel and clears the legacy webhook", () => {
    const row = LOADED_ROWS["legacy single webhook without channels"]();
    const form = { ...formFromConfig(row), notificationChannels: formFromConfig(row).notificationChannels.map((c) => ({ ...c, name: "Ops" })) };
    expect(unsaved(row, form)).toEqual(["notifications"]);
    const merged = saveAndMerge(row, form, "notifications");
    expect(merged.webhookProviderType).toBe(0);
    expect(merged.webhookUrl).toBeNull();
    expect(JSON.parse(merged.notificationChannelsJson ?? "[]")).toHaveLength(1);
    expect(unsaved(merged, form)).toEqual([]);
  });

  describe("hardware rejection switch", () => {
    it("with channels, toggling marks the hardware page and the channels it wrote through", () => {
      const row = fullRow();
      expect(unsaved(row, toggleHardwareNotify(formFromConfig(row), true))).toEqual(["hardwareWhitelist", "notifications"]);
    });

    it("toggling back is clean again", () => {
      const row = fullRow();
      const form = toggleHardwareNotify(toggleHardwareNotify(formFromConfig(row), true), false);
      expect(unsaved(row, form)).toEqual([]);
    });

    it("the legacy flag behind the switch never counts while channels exist", () => {
      // Stored: legacy flag on, every channel off, so the switch shows off.
      const row = { ...fullRow(), webhookNotifyOnHardwareRejection: true };
      const form = toggleHardwareNotify(toggleHardwareNotify(formFromConfig(row), true), false);
      expect(form.webhookNotifyOnHardwareRejection).toBe(false);
      expect(unsaved(row, form)).toEqual([]);
    });

    it("turning the switch on while the legacy flag is already on still marks the hardware page", () => {
      // Stored: legacy flag on, every channel off. Only the channel view moves, not the legacy flag.
      const row = { ...fullRow(), webhookNotifyOnHardwareRejection: true };
      const form = toggleHardwareNotify(formFromConfig(row), true);
      expect(form.webhookNotifyOnHardwareRejection).toBe(true);
      expect(unsaved(row, form)).toEqual(["hardwareWhitelist", "notifications"]);
    });

    it("renaming a channel marks only the notifications page", () => {
      const row = fullRow();
      const loaded = formFromConfig(row);
      const form = { ...loaded, notificationChannels: loaded.notificationChannels.map((c) => ({ ...c, name: `${c.name}!` })) };
      expect(unsaved(row, form)).toEqual(["notifications"]);
    });

    it("without channels, the switch is the legacy flag and marks only the hardware page", () => {
      const row = { ...minimalRow(), webhookNotifyOnHardwareRejection: false };
      expect(unsaved(row, toggleHardwareNotify(formFromConfig(row), true))).toEqual(["hardwareWhitelist"]);
    });

    it("saving the hardware page clears both pages", () => {
      const row = fullRow();
      const form = toggleHardwareNotify(formFromConfig(row), true);
      expect(unsaved(saveAndMerge(row, form, "hardwareWhitelist"), form)).toEqual([]);
    });
  });
});

describe("hardwareRejectionNotify", () => {
  it("with channels: on when an enabled channel opted in", () => {
    const base = { webhookNotifyOnHardwareRejection: false };
    expect(hardwareRejectionNotify({ ...base, notificationChannels: [channel("a", { notifyOnHardwareRejection: true })] })).toBe(true);
    expect(hardwareRejectionNotify({ ...base, notificationChannels: [channel("a")] })).toBe(false);
  });

  it("with channels: a disabled channel's flag does not count", () => {
    const channels = [channel("a", { enabled: false, notifyOnHardwareRejection: true })];
    expect(hardwareRejectionNotify({ webhookNotifyOnHardwareRejection: true, notificationChannels: channels })).toBe(false);
  });

  it("without channels: the legacy tenant flag", () => {
    expect(hardwareRejectionNotify({ webhookNotifyOnHardwareRejection: true, notificationChannels: [] })).toBe(true);
    expect(hardwareRejectionNotify({ webhookNotifyOnHardwareRejection: false, notificationChannels: [] })).toBe(false);
  });
});

describe("formFromConfig / configFromForm", () => {
  it("fills the model defaults a row leaves out", () => {
    const form = formFromConfig(minimalRow());
    expect(form.logLevel).toBe("Info");
    expect(form.showScriptOutput).toBe(true);
    expect(form.enableIntegrityBypassAnalyzer).toBe(true);
    expect(form.diagnosticsUploadMode).toBe("Off");
    expect(form.diagnosticsUploadDestination).toBe("CustomerSas");
    expect(form.slaConsecutiveFailureThreshold).toBe(5);
    expect(form.slaTargetSuccessRate).toBeNull();
    expect(form.notificationChannels).toEqual([]);
  });

  it("synthesizes the legacy webhook as the one channel 'legacy'", () => {
    const channels = formFromConfig(LOADED_ROWS["legacy single webhook without channels"]()).notificationChannels;
    expect(channels.map((c) => [c.id, c.providerType, c.url, c.notifyOnHardwareRejection])).toEqual([
      ["legacy", 10, "https://hooks.example.com/legacy", true],
    ]);
  });

  it("writes the stored shape back: renamed interval field, trimmed contact, cleared legacy webhook", () => {
    const row = fullRow();
    const config = configFromForm(row, { ...formFromConfig(row), performanceCollectorInterval: 90, contactEmail: " a@example.com " });
    expect(config.performanceCollectorIntervalSeconds).toBe(90);
    expect(config.contactEmail).toBe("a@example.com");
    expect(config.webhookProviderType).toBe(0);
    expect(config.webhookUrl).toBeUndefined();
  });

});

describe("sectionSavePayload", () => {
  it("is the one place that decides what the settings provider sends", () => {
    // A second diff next to it (the raw row against the form) is how untouched defaults travelled.
    const provider = readFileSync(join(__dirname, "..", "..", "TenantConfigContext.tsx"), "utf8");
    expect(provider).toContain("sectionSavePayload(config, form, sectionName, overrides)");
    expect(provider).not.toContain("changedTenantConfigFields");
  });

  it.each(Object.entries(LOADED_ROWS))("a freshly loaded row sends nothing in any section: %s", (_, build) => {
    const row = build();
    for (const section of SAVE_BAR_SECTIONS) {
      expect(sectionSavePayload(row, formFromConfig(row), section)).toEqual({});
    }
  });

  it("a single edit sends only that field, also on a row that never stored the fields beside it", () => {
    // minimalRow stores none of the agent parameters: their form defaults must stay at home.
    const row = minimalRow();
    const form = { ...formFromConfig(row), logLevel: "Debug" };
    expect(sectionSavePayload(row, form, "agentParameters")).toEqual({ logLevel: "Debug" });
  });

  it("a field the row never stored travels once the admin changes it, and no longer after he takes it back", () => {
    const row = minimalRow();
    const loaded = formFromConfig(row);
    expect(sectionSavePayload(row, { ...loaded, enableTimezoneAutoSet: true }, "agentParameters")).toEqual({ enableTimezoneAutoSet: true });
    expect(sectionSavePayload(row, { ...loaded, enableTimezoneAutoSet: false }, "agentParameters")).toEqual({});
  });

  it("a page loaded before somebody else's write does not revert that write", () => {
    // Loaded: the timezone was never set. Since then the setup guide stored it as on (another
    // instance answered this page from its older copy). The admin edits the log level and saves.
    const loadedBefore = minimalRow();
    const storedNow: TenantConfiguration = { ...minimalRow(), enableTimezoneAutoSet: true, enableSoftwareInventoryAnalyzer: true };
    const form = { ...formFromConfig(loadedBefore), logLevel: "Debug" };

    const afterSave = { ...storedNow, ...sectionSavePayload(loadedBefore, form, "agentParameters") } as TenantConfiguration;

    expect(afterSave.logLevel).toBe("Debug");
    expect(afterSave.enableTimezoneAutoSet).toBe(true);
    expect(afterSave.enableSoftwareInventoryAnalyzer).toBe(true);
  });

  it.each(Object.entries(LOADED_ROWS))("unsaved means exactly: a save would send something: %s", (_, build) => {
    const row = build();
    const edits: SettingsForm[] = [
      formFromConfig(row),
      { ...formFromConfig(row), logLevel: "Trace" },
      { ...formFromConfig(row), manufacturerWhitelist: "Contoso*" },
      toggleHardwareNotify(formFromConfig(row), !hardwareRejectionNotify(formFromConfig(row))),
    ];
    for (const form of edits) {
      const sending = SAVE_BAR_SECTIONS.filter((section) => Object.keys(sectionSavePayload(row, form, section)).length > 0);
      expect([...sending].sort()).toEqual(unsaved(row, form));
    }
  });

  describe("sections that save on change", () => {
    it("send exactly the switch that was flipped", () => {
      const row = fullRow();
      expect(sectionSavePayload(row, formFromConfig(row), "autopilotValidation", { validateCloudPcDevice: true })).toEqual({ validateCloudPcDevice: true });
      expect(sectionSavePayload(row, formFromConfig(row), "unrestrictedMode", { unrestrictedMode: true })).toEqual({ unrestrictedMode: true });
    });

    it("send the flipped switch even when the loaded copy already holds that value", () => {
      // The copy may be outdated; an explicit click is never swallowed by it.
      const row = fullRow();
      expect(row.validateAutopilotDevice).toBe(true);
      expect(sectionSavePayload(row, formFromConfig(row), "autopilotValidation", { validateAutopilotDevice: true })).toEqual({ validateAutopilotDevice: true });
    });

    it("send nothing from the form: a gate that only differs there is not a click", () => {
      const row = fullRow();
      const form = { ...formFromConfig(row), validateDeviceAssociation: true, validateIntuneDeviceBinding: true };
      expect(sectionSavePayload(row, form, "autopilotValidation")).toEqual({});
      expect(sectionSavePayload(row, form, "autopilotValidation", { validateCloudPcDevice: false })).toEqual({ validateCloudPcDevice: false });
    });

    it("drop a field the section does not own", () => {
      const row = fullRow();
      expect(sectionSavePayload(row, formFromConfig(row), "unrestrictedMode", { validateAutopilotDevice: false })).toEqual({});
    });
  });

  it("a section with a Save bar ignores switch values", () => {
    const row = fullRow();
    expect(sectionSavePayload(row, formFromConfig(row), "agentParameters", { unrestrictedMode: true })).toEqual({});
  });

  describe("hardware card", () => {
    it("a whitelist edit sends the whitelist and leaves the channels alone, whatever their stored formatting", () => {
      for (const build of [fullRow, LOADED_ROWS["channels stored with other formatting"], LOADED_ROWS["malformed JSON lists"]]) {
        const row = build();
        const form = { ...formFromConfig(row), manufacturerWhitelist: "Dell*" };
        expect(sectionSavePayload(row, form, "hardwareWhitelist")).toEqual({ manufacturerWhitelist: "Dell*" });
      }
    });

    it("a whitelist edit does not carry a channel edit waiting on the notifications card", () => {
      const row = fullRow();
      const loaded = formFromConfig(row);
      const form = {
        ...loaded,
        manufacturerWhitelist: "Dell*",
        notificationChannels: loaded.notificationChannels.map((c) => ({ ...c, name: `${c.name}!` })),
      };
      expect(sectionSavePayload(row, form, "hardwareWhitelist")).toEqual({ manufacturerWhitelist: "Dell*" });
    });

    it("the moved switch sends the flag and its write-through into the channels", () => {
      const row = fullRow();
      const form = toggleHardwareNotify(formFromConfig(row), true);
      expect(sectionSavePayload(row, form, "hardwareWhitelist")).toEqual({
        webhookNotifyOnHardwareRejection: true,
        notificationChannelsJson: JSON.stringify(form.notificationChannels),
      });
    });

    it("the switch moved and moved back sends nothing, also when the legacy flag behind it differs", () => {
      const row = { ...fullRow(), webhookNotifyOnHardwareRejection: true };
      const form = toggleHardwareNotify(toggleHardwareNotify(formFromConfig(row), true), false);
      expect(form.webhookNotifyOnHardwareRejection).toBe(false);
      expect(sectionSavePayload(row, form, "hardwareWhitelist")).toEqual({});
      expect(sectionSavePayload(row, { ...form, modelWhitelist: "Latitude*" }, "hardwareWhitelist")).toEqual({ modelWhitelist: "Latitude*" });
    });

    it("without channels the switch is the legacy flag alone", () => {
      const row = { ...minimalRow(), webhookNotifyOnHardwareRejection: false };
      expect(sectionSavePayload(row, toggleHardwareNotify(formFromConfig(row), true), "hardwareWhitelist")).toEqual({ webhookNotifyOnHardwareRejection: true });
    });
  });

  describe("tenant with the legacy single webhook", () => {
    const legacy = LOADED_ROWS["legacy single webhook without channels"];

    it("a whitelist edit leaves the webhook as it is stored", () => {
      const row = legacy();
      const form = { ...formFromConfig(row), manufacturerWhitelist: "Dell*" };
      expect(sectionSavePayload(row, form, "hardwareWhitelist")).toEqual({ manufacturerWhitelist: "Dell*" });
    });

    it("deleting the synthesized channel clears the legacy webhook with it", () => {
      const row = legacy();
      const form = { ...formFromConfig(row), notificationChannels: [] };
      expect(sectionSavePayload(row, form, "notifications")).toEqual({
        notificationChannelsJson: "",
        webhookProviderType: 0,
        webhookUrl: null,
      });
    });

    it("the hardware switch materializes the channel and does not touch the legacy webhook fields", () => {
      const row = legacy();
      const form = toggleHardwareNotify(formFromConfig(row), false);
      const payload = sectionSavePayload(row, form, "hardwareWhitelist");
      expect(Object.keys(payload).sort()).toEqual(["notificationChannelsJson", "webhookNotifyOnHardwareRejection"]);
      expect(JSON.parse(payload.notificationChannelsJson as string)).toHaveLength(1);
    });
  });
});

describe("form defaults", () => {
  // A field the admin never saved stays empty in the row, and the form shows its default for good.
  // That default has to be what the backend does with an empty field; the generated manifest carries
  // the model's and the agent's defaults, so a change there without the form fails here.
  it("a default row shows exactly the form's defaults", () => {
    const defaults = SHARED_MANIFEST.tenantConfiguration.defaults as unknown as TenantConfiguration;
    expect(formFromConfig(defaults)).toEqual(DEFAULT_SETTINGS_FORM);
  });

  it("match what the agent does when a setting was never stored", () => {
    const { responseDefaults: agent, collectorDefaults: collectors, analyzerDefaults: analyzers } = SHARED_MANIFEST.agentConfig;
    const form = formFromConfig(minimalRow());
    expect({
      selfDestructOnComplete: form.selfDestructOnComplete,
      keepLogFile: form.keepLogFile,
      enableGeoLocation: form.enableGeoLocation,
      enableImeMatchLog: form.enableImeMatchLog,
      enableGatherRuleDebugLog: form.enableGatherRuleDebugLog,
      logLevel: form.logLevel,
      rebootOnComplete: form.rebootOnComplete,
      rebootDelaySeconds: form.rebootDelaySeconds,
      showEnrollmentSummary: form.showEnrollmentSummary,
      enrollmentSummaryTimeoutSeconds: form.enrollmentSummaryTimeoutSeconds,
      enrollmentSummaryLaunchRetrySeconds: form.enrollmentSummaryLaunchRetrySeconds,
      diagnosticsUploadMode: form.diagnosticsUploadMode,
      unrestrictedMode: form.unrestrictedMode,
      enableTimezoneAutoSet: form.enableTimezoneAutoSet,
      enableDoGroupIdAutoSet: form.enableDoGroupIdAutoSet,
    }).toEqual({
      selfDestructOnComplete: agent.selfDestructOnComplete,
      keepLogFile: agent.keepLogFile,
      enableGeoLocation: agent.enableGeoLocation,
      enableImeMatchLog: agent.enableImeMatchLog,
      enableGatherRuleDebugLog: agent.enableGatherRuleDebugLog,
      logLevel: agent.logLevel,
      rebootOnComplete: agent.rebootOnComplete,
      rebootDelaySeconds: agent.rebootDelaySeconds,
      showEnrollmentSummary: agent.showEnrollmentSummary,
      enrollmentSummaryTimeoutSeconds: agent.enrollmentSummaryTimeoutSeconds,
      enrollmentSummaryLaunchRetrySeconds: agent.enrollmentSummaryLaunchRetrySeconds,
      diagnosticsUploadMode: agent.diagnosticsUploadMode,
      unrestrictedMode: agent.unrestrictedMode,
      enableTimezoneAutoSet: agent.enableTimezoneAutoSet,
      enableDoGroupIdAutoSet: agent.enableDoGroupIdAutoSet,
    });
    expect({
      enableLocalAdminAnalyzer: form.enableLocalAdminAnalyzer,
      localAdminAllowedAccounts: form.localAdminAllowedAccounts,
      enableSoftwareInventoryAnalyzer: form.enableSoftwareInventoryAnalyzer,
      enableIntegrityBypassAnalyzer: form.enableIntegrityBypassAnalyzer,
      enableRealmJoinWatcher: form.enableRealmJoinWatcher,
      enableConfigMgrWatcher: form.enableConfigMgrWatcher,
      keepAwakeDuringUserEsp: form.keepAwakeDuringUserEsp,
      enableConsoleBypassDetection: form.enableConsoleBypassDetection,
    }).toEqual(analyzers);
    // minimalRow stores the collector values itself; the defaults are what a row without them shows.
    const bare = formFromConfig({ ...minimalRow(), enablePerformanceCollector: undefined, performanceCollectorIntervalSeconds: undefined } as unknown as TenantConfiguration);
    expect(bare.enablePerformanceCollector).toBe(collectors.enablePerformanceCollector);
    expect(bare.performanceCollectorInterval).toBe(collectors.performanceIntervalSeconds);
    expect(bare.helloWaitTimeoutSeconds).toBe(collectors.helloWaitTimeoutSeconds);
  });
});

describe("settingsFormSetters", () => {
  function harness() {
    let state: SettingsForm = DEFAULT_SETTINGS_FORM;
    const setters = settingsFormSetters((update) => {
      state = typeof update === "function" ? update(state) : update;
    }) as unknown as Record<string, (value: unknown) => void>;
    return { setters, read: () => state };
  }

  it("has one setter per field, each writing only its own field", () => {
    const { setters, read } = harness();
    const keys = Object.keys(DEFAULT_SETTINGS_FORM) as (keyof SettingsForm)[];
    expect(Object.keys(setters)).toHaveLength(keys.length);
    for (const key of keys) {
      const before = read();
      const marker = { field: key };
      setters[`set${key.charAt(0).toUpperCase()}${key.slice(1)}`](marker);
      const after = read();
      expect(after[key]).toBe(marker);
      for (const other of keys) if (other !== key) expect(after[other]).toBe(before[other]);
    }
  });

  it("keeps the names the sections use, including the fields named unlike their column", () => {
    const { setters } = harness();
    for (const name of ["setPerformanceCollectorInterval", "setTenantDiagPaths", "setLocalAdminAllowedAccounts", "setNotificationChannels", "setLogLevel"]) {
      expect(typeof setters[name]).toBe("function");
    }
  });

  it("accepts an updater like a React setter", () => {
    const { setters, read } = harness();
    setters.setDataRetentionDays((days: number) => days + 1);
    expect(read().dataRetentionDays).toBe(DEFAULT_SETTINGS_FORM.dataRetentionDays + 1);
  });
});
