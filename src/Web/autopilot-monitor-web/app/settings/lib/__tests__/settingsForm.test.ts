/**
 * The Settings form projections and the unsaved-changes check built on them. A freshly loaded row
 * must never count as edited (otherwise the sidebar marks pages nobody touched and leaving asks for
 * nothing), an edit must mark exactly its own section, and the merge after a save must clear it.
 * Rows are built here by hand; expectations are test-local lists, never derived from the map.
 */
import { describe, expect, it } from "vitest";
import { changedTenantConfigFields } from "@/lib/tenantConfigSave";
import { SECTION_FIELD_MAP, type SettingsSectionName } from "../../sectionFieldMap";
import type { NotificationChannel, TenantConfiguration } from "../../types";
import {
  DEFAULT_SETTINGS_FORM,
  configFromForm,
  formFromConfig,
  hardwareRejectionNotify,
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

/** What runSectionSave sends for a section, and the row after its merge. */
function saveAndMerge(row: TenantConfiguration, form: SettingsForm, section: SettingsSectionName): TenantConfiguration {
  const spec = SECTION_FIELD_MAP[section];
  const alsoWrites = "alsoWrites" in spec ? spec.alsoWrites : [];
  const patch = changedTenantConfigFields(row, configFromForm(row, form), [...spec.fields, ...alsoWrites]);
  return { ...row, ...patch } as TenantConfiguration;
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

  it("a single log-level edit on a row that stores every agent parameter sends only the log level", () => {
    // A row missing a field would also send that field's default on the first save, as before.
    const row: TenantConfiguration = {
      ...fullRow(),
      enableGeoLocation: true,
      enableTimezoneAutoSet: false,
      enableDoGroupIdAutoSet: false,
      keepAwakeDuringUserEsp: false,
      enableImeMatchLog: false,
      enableGatherRuleDebugLog: false,
      showScriptOutput: true,
      enrollmentSummaryTimeoutSeconds: 60,
      enrollmentSummaryLaunchRetrySeconds: 120,
    };
    const form = { ...formFromConfig(row), logLevel: "Debug" };
    const spec = SECTION_FIELD_MAP.agentParameters;
    expect(changedTenantConfigFields(row, configFromForm(row, form), spec.fields)).toEqual({ logLevel: "Debug" });
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
