/**
 * Parity pins for the Tenant Config Report catalog.
 *
 * The catalog is compile-time exhaustive over the generated wire types; this file adds the
 * runtime half against SHARED_MANIFEST.tenantConfiguration.fields (the C#-reflected field
 * list, independent of the TS type) and pins the runtime resolution rules that mirror
 * GetAgentConfigFunction.cs with test-local fixtures.
 */
import { describe, expect, it } from "vitest";
import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";
import type { AdminConfiguration, TenantConfiguration, TenantFeatureFlagsResponse } from "@/utils/wire-types.generated";
import {
  ANALYZER_FIELDS,
  COLLECTOR_FIELDS,
  DEFAULT_UPLOAD_INTERVAL_SECONDS,
  isExcluded,
  isNonDefault,
  RUNTIME_FIELDS,
  RUNTIME_REPORT_SECTIONS,
  TENANT_FIELDS,
  TENANT_REPORT_SECTIONS,
  UNRESOLVED,
  type RuntimeContext,
} from "../tenantConfigReportCatalog";

const manifestFields = [...SHARED_MANIFEST.tenantConfiguration.fields] as string[];

describe("TENANT_FIELDS parity with the C# field manifest", () => {
  it("catalogues every wire field of TenantConfiguration (rendered or excluded with a reason)", () => {
    const missing = manifestFields.filter((f) => !(f in TENANT_FIELDS));
    expect(missing).toEqual([]);
  });

  it("has no key outside the manifest (a removed C# property must leave the catalog)", () => {
    const stale = Object.keys(TENANT_FIELDS).filter((k) => !manifestFields.includes(k));
    expect(stale).toEqual([]);
  });

  it("gives every exclusion a reason and every row a non-empty label", () => {
    for (const [key, spec] of Object.entries(TENANT_FIELDS)) {
      if (isExcluded(spec)) expect(spec.excluded, key).not.toBe("");
      else expect(spec.label, key).not.toBe("");
    }
  });

  it("renders the fields the operator asked for by name", () => {
    const rendered = new Set(TENANT_REPORT_SECTIONS.flatMap((s) => s.rows.map((r) => r.key)));
    for (const key of [
      "payingCustomer", "maxDelegatedTenantsOverride", "mcpUsagePlanOverride", "managedByProTenantId",
      "trialConsumed", "proDowngradedUtc", "validateDeviceAssociation", "validateCloudPcDevice",
      "validateIntuneDeviceBinding", "sessionGraceHours", "absoluteMaxSessionHours",
      "diagnosticsUploadDestination", "webhookNotifyOnHardwareRejection", "entraAppRolesEnabled",
      "slaTargetSuccessRate", "slaConsecutiveFailureThreshold", "keepAwakeDuringUserEsp",
    ]) {
      expect(rendered.has(key), key).toBe(true);
    }
  });

  it("renders each field in exactly one section", () => {
    const seen = new Map<string, number>();
    for (const { rows } of TENANT_REPORT_SECTIONS) {
      for (const { key } of rows) seen.set(key, (seen.get(key) ?? 0) + 1);
    }
    expect([...seen.entries()].filter(([, n]) => n !== 1)).toEqual([]);
  });
});

// ── Runtime rules ────────────────────────────────────────────────────────────

/** Minimal stored config: everything nullable unset, non-nullables at their C# initializers. */
function storedConfig(overrides: Partial<TenantConfiguration> = {}): TenantConfiguration {
  return {
    tenantId: "00000000-0000-0000-0000-000000000001",
    domainName: "contoso.example",
    lastUpdated: "2026-09-06T00:00:00Z",
    updatedBy: "ga@operator.example",
    disabled: false,
    mcpDisabled: false,
    planTier: "free",
    trialConsumed: false,
    payingCustomer: false,
    manufacturerWhitelist: "Dell*,HP*,Lenovo*,Microsoft Corporation",
    modelWhitelist: "*",
    validateAutopilotDevice: false,
    validateCorporateIdentifier: false,
    validateDeviceAssociation: false,
    validateCloudPcDevice: false,
    validateIntuneDeviceBinding: false,
    allowInsecureAgentRequests: false,
    dataRetentionDays: 90,
    sessionTimeoutHours: 5,
    sessionGraceHours: 0,
    maxNdjsonPayloadSizeMB: 5,
    enablePerformanceCollector: true,
    performanceCollectorIntervalSeconds: 30,
    helloWaitTimeoutSeconds: 30,
    ntpServer: "time.windows.com",
    logLevel: "",
    enrollmentSummaryBrandingImageUrl: "",
    localAdminAllowedAccountsJson: "",
    bootstrapTokenEnabled: false,
    unrestrictedModeEnabled: false,
    unrestrictedMode: false,
    entraAppRolesEnabled: false,
    diagnosticsLogPathsJson: "",
    diagnosticsBlobSasUrl: "",
    diagnosticsUploadMode: "Off",
    diagnosticsUploadDestination: "CustomerSas",
    sendTraceEvents: true,
    teamsWebhookUrl: "",
    teamsNotifyOnSuccess: true,
    teamsNotifyOnFailure: true,
    teamsNotifyOnStart: false,
    webhookProviderType: 0,
    webhookUrl: "",
    webhookNotifyOnSuccess: true,
    webhookNotifyOnFailure: true,
    webhookNotifyOnHardwareRejection: false,
    webhookNotifyOnStart: false,
    webhookCustomHeadersJson: "",
    notificationChannelsJson: "",
    slaNotifyOnSuccessRateBreach: false,
    slaNotifyOnDurationBreach: false,
    slaNotifyOnAppInstallBreach: false,
    slaNotifyOnConsecutiveFailures: false,
    slaConsecutiveFailureThreshold: 5,
    ...overrides,
  };
}

const adminConfig = { collectorIdleTimeoutMinutes: 25, desktopDetectorNoCandidateTimeoutMinutes: 7, allowAgentDowngrade: true, modernDeploymentHarmlessEventIdsJson: "[100, 42]" } as AdminConfiguration;
const flags = (unrestrictedMode: boolean) => ({ unrestrictedMode }) as TenantFeatureFlagsResponse;

const ctx = (overrides: Partial<TenantConfiguration> = {}, extra: Partial<RuntimeContext> = {}): RuntimeContext => ({
  config: storedConfig(overrides),
  adminConfig: null,
  flags: null,
  ...extra,
});

const runtimeValue = (record: typeof RUNTIME_FIELDS | typeof COLLECTOR_FIELDS | typeof ANALYZER_FIELDS, key: string, c: RuntimeContext) => {
  const spec = (record as Record<string, (typeof RUNTIME_FIELDS)[keyof typeof RUNTIME_FIELDS]>)[key];
  if (!spec || isExcluded(spec)) throw new Error(`${key} is not a rendered runtime row`);
  return spec.value(c);
};

describe("runtime rows mirror GetAgentConfigFunction", () => {
  it("upload interval is the backend constant (10), not the old 30", () => {
    expect(DEFAULT_UPLOAD_INTERVAL_SECONDS).toBe(10);
    expect(runtimeValue(RUNTIME_FIELDS, "uploadIntervalSeconds", ctx())).toBe(10);
  });

  it("diagnostics upload is enabled by a SAS URL or the hosted destination, regardless of mode", () => {
    expect(runtimeValue(RUNTIME_FIELDS, "diagnosticsUploadEnabled", ctx())).toBe(false);
    expect(runtimeValue(RUNTIME_FIELDS, "diagnosticsUploadEnabled", ctx({ diagnosticsUploadDestination: "Hosted" }))).toBe(true);
    expect(runtimeValue(RUNTIME_FIELDS, "diagnosticsUploadEnabled", ctx({ diagnosticsBlobSasUrl: "https://x.blob.core.windows.net/c?sig=1", diagnosticsUploadMode: "Off" }))).toBe(true);
  });

  it("unrestricted mode comes from the entitlement flags, never from the stored toggle alone", () => {
    expect(runtimeValue(RUNTIME_FIELDS, "unrestrictedMode", ctx({ unrestrictedModeEnabled: true, unrestrictedMode: true }))).toBe(UNRESOLVED);
    expect(runtimeValue(RUNTIME_FIELDS, "unrestrictedMode", ctx({ unrestrictedModeEnabled: true, unrestrictedMode: true }, { flags: flags(false) }))).toBe(false);
    expect(runtimeValue(RUNTIME_FIELDS, "unrestrictedMode", ctx({}, { flags: flags(true) }))).toBe(true);
  });

  it("operator knobs resolve from the global config and are unresolved without it", () => {
    expect(runtimeValue(COLLECTOR_FIELDS, "collectorIdleTimeoutMinutes", ctx())).toBe(UNRESOLVED);
    const c = ctx({}, { adminConfig });
    expect(runtimeValue(COLLECTOR_FIELDS, "collectorIdleTimeoutMinutes", c)).toBe(25);
    expect(runtimeValue(COLLECTOR_FIELDS, "desktopDetectorNoCandidateTimeoutMinutes", c)).toBe(7);
    expect(runtimeValue(COLLECTOR_FIELDS, "modernDeploymentHarmlessEventIds", c)).toEqual([100, 42]);
    expect(runtimeValue(RUNTIME_FIELDS, "allowAgentDowngrade", c)).toBe(true);
  });

  it("applies the analyzer defaults of the agent config builder", () => {
    const c = ctx();
    expect(runtimeValue(ANALYZER_FIELDS, "enableLocalAdminAnalyzer", c)).toBe(true);
    expect(runtimeValue(ANALYZER_FIELDS, "enableIntegrityBypassAnalyzer", c)).toBe(true);
    expect(runtimeValue(ANALYZER_FIELDS, "enableConsoleBypassDetection", c)).toBe(true);
    expect(runtimeValue(ANALYZER_FIELDS, "enableSoftwareInventoryAnalyzer", c)).toBe(false);
    expect(runtimeValue(ANALYZER_FIELDS, "enableRealmJoinWatcher", c)).toBe(false);
    expect(runtimeValue(ANALYZER_FIELDS, "keepAwakeDuringUserEsp", c)).toBe(false);
    expect(runtimeValue(ANALYZER_FIELDS, "localAdminAllowedAccounts", ctx({ localAdminAllowedAccountsJson: '["Admin","Helpdesk"]' }))).toEqual(["Admin", "Helpdesk"]);
    expect(runtimeValue(ANALYZER_FIELDS, "localAdminAllowedAccounts", ctx({ localAdminAllowedAccountsJson: "not json" }))).toEqual([]);
  });

  it("nullable agent knobs fall back to the agent defaults", () => {
    const c = ctx();
    expect(runtimeValue(RUNTIME_FIELDS, "maxAuthFailures", c)).toBe(5);
    expect(runtimeValue(RUNTIME_FIELDS, "authFailureTimeoutMinutes", c)).toBe(0);
    expect(runtimeValue(RUNTIME_FIELDS, "logLevel", ctx({ logLevel: null as unknown as string }))).toBe("Info");
    expect(runtimeValue(RUNTIME_FIELDS, "ntpServer", ctx({ ntpServer: "" }))).toBe("time.windows.com");
    expect(runtimeValue(COLLECTOR_FIELDS, "agentMaxLifetimeMinutes", c)).toBe(360);
    expect(runtimeValue(RUNTIME_FIELDS, "rebootDelaySeconds", ctx({ rebootDelaySeconds: 30 }))).toBe(30);
  });

  it("renders every runtime row in exactly one section", () => {
    const keys = RUNTIME_REPORT_SECTIONS.flatMap((s) => s.rows.map((r) => r.key));
    expect(new Set(keys).size).toBe(keys.length);
    expect(keys).toContain("uploadIntervalSeconds");
    expect(keys).toContain("collectorIdleTimeoutMinutes");
    expect(keys).toContain("enableConsoleBypassDetection");
  });
});

describe("isNonDefault", () => {
  it("treats null and undefined as one value and compares arrays by content", () => {
    expect(isNonDefault(undefined, null)).toBe(false);
    expect(isNonDefault("", null)).toBe(false);
    expect(isNonDefault("", "Off")).toBe(true);
    expect(isNonDefault(null, false)).toBe(true);
    expect(isNonDefault(false, false)).toBe(false);
    expect(isNonDefault([100, 1005, 1010], [100, 1005, 1010])).toBe(false);
    expect(isNonDefault([100, 42], [100, 1005, 1010])).toBe(true);
  });
});
