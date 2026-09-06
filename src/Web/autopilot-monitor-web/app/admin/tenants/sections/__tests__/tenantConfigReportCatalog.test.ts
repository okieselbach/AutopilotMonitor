/**
 * Parity pins for the Tenant Config Report catalog.
 *
 * The catalog is compile-time exhaustive over the generated wire types; this file adds the
 * runtime half against the shared manifest (the C#-reflected field lists and initializer
 * defaults, independent of the TS types): every field is catalogued, every catalogued field
 * has a default to compare against, and the value accessors read the effective agent config
 * the backend returns. The derivation rules themselves are pinned in the backend
 * (AgentConfigResolverTests) — nothing is re-computed here.
 */
import { describe, expect, it } from "vitest";
import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";
import type { AgentConfigResponse } from "@/utils/wire-types.generated";
import {
  ANALYZER_FIELDS,
  COLLECTOR_FIELDS,
  isExcluded,
  isNonDefault,
  RUNTIME_FIELDS,
  RUNTIME_REPORT_SECTIONS,
  RUNTIME_SECTIONS_COLLAPSED,
  TENANT_FIELDS,
  TENANT_REPORT_SECTIONS,
} from "../tenantConfigReportCatalog";

const manifestFields = [...SHARED_MANIFEST.tenantConfiguration.fields] as string[];
const manifestDefaults = SHARED_MANIFEST.tenantConfiguration.defaults as Record<string, unknown>;

describe("TENANT_FIELDS parity with the C# field manifest", () => {
  it("catalogues every wire field of TenantConfiguration (rendered or excluded with a reason)", () => {
    const missing = manifestFields.filter((f) => !(f in TENANT_FIELDS));
    expect(missing).toEqual([]);
  });

  it("has no key outside the manifest (a removed C# property must leave the catalog)", () => {
    const stale = Object.keys(TENANT_FIELDS).filter((k) => !manifestFields.includes(k));
    expect(stale).toEqual([]);
  });

  it("the manifest carries an initializer default for every field", () => {
    const withoutDefault = manifestFields.filter((f) => !(f in manifestDefaults));
    expect(withoutDefault).toEqual([]);
  });

  it("gives every exclusion a reason and every row a non-empty label", () => {
    for (const [key, spec] of Object.entries(TENANT_FIELDS)) {
      if (isExcluded(spec)) expect(spec.excluded, key).not.toBe("");
      else expect(spec.label, key).not.toBe("");
    }
  });

  it("renders the fields the operator asked for by name", () => {
    const rendered = new Set(TENANT_REPORT_SECTIONS.flatMap((s) => s.rows.map((r) => r.key as string)));
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

  it("renders each field in exactly one section, with its manifest default attached", () => {
    const seen = new Map<string, number>();
    for (const { rows } of TENANT_REPORT_SECTIONS) {
      for (const { key, default: def } of rows) {
        seen.set(key, (seen.get(key) ?? 0) + 1);
        expect(def, key).toEqual(manifestDefaults[key]);
      }
    }
    expect([...seen.entries()].filter(([, n]) => n !== 1)).toEqual([]);
  });

  it("secrets, masked values and timestamps are informational (never marked custom)", () => {
    for (const [key, spec] of Object.entries(TENANT_FIELDS)) {
      if (isExcluded(spec)) continue;
      if (spec.kind === "secret" || spec.kind === "masked" || spec.kind === "date") {
        expect(spec.informational, key).toBe(true);
      }
    }
  });

  it("pins a few manifest defaults the report relies on", () => {
    expect(manifestDefaults.planTier).toBe("free");
    expect(manifestDefaults.dataRetentionDays).toBe(90);
    expect(manifestDefaults.diagnosticsUploadDestination).toBe("CustomerSas");
    expect(manifestDefaults.payingCustomer).toBe(false);
    // The one string default the table read also applies to a blank cell — a stored
    // "time.windows.com" must not read as custom.
    expect(manifestDefaults.ntpServer).toBe("time.windows.com");
  });
});

// ── Runtime catalog ──────────────────────────────────────────────────────────

const agentDefaults = SHARED_MANIFEST.agentConfig;

describe("runtime catalogs parity with the agent-config defaults manifest", () => {
  it.each([
    ["responseDefaults", RUNTIME_FIELDS],
    ["collectorDefaults", COLLECTOR_FIELDS],
    ["analyzerDefaults", ANALYZER_FIELDS],
  ] as const)("%s ↔ catalog: same key set", (section, record) => {
    const keys = Object.keys(agentDefaults[section]);
    expect(keys.filter((k) => !(k in record))).toEqual([]);
    expect(Object.keys(record).filter((k) => !keys.includes(k))).toEqual([]);
  });

  it("renders every runtime row in exactly one section", () => {
    const keys = RUNTIME_REPORT_SECTIONS.flatMap((s) => s.rows.map((r) => r.key));
    expect(new Set(keys).size).toBe(keys.length);
    expect(keys).toContain("uploadIntervalSeconds");
    expect(keys).toContain("collectors.collectorIdleTimeoutMinutes");
    expect(keys).toContain("analyzers.enableConsoleBypassDetection");
    expect(keys).toContain("configVersion");
    expect(keys).not.toContain("deviceKillSignal");
  });

  it("collapsed sections exist in the section list", () => {
    const sections = new Set(RUNTIME_REPORT_SECTIONS.map((s) => s.section));
    for (const s of RUNTIME_SECTIONS_COLLAPSED) expect(sections.has(s), s).toBe(true);
  });

  it("value accessors read the top level and the two sub-objects of the response", () => {
    const response = {
      uploadIntervalSeconds: 10,
      collectors: { collectorIdleTimeoutMinutes: 25 },
      analyzers: { enableConsoleBypassDetection: false },
      diagnosticsLogPaths: [{ path: "a" }, { path: "b" }],
    } as unknown as AgentConfigResponse;
    const byKey = new Map(RUNTIME_REPORT_SECTIONS.flatMap((s) => s.rows.map((r) => [r.key, r] as const)));

    expect(byKey.get("uploadIntervalSeconds")!.value(response)).toBe(10);
    expect(byKey.get("collectors.collectorIdleTimeoutMinutes")!.value(response)).toBe(25);
    expect(byKey.get("analyzers.enableConsoleBypassDetection")!.value(response)).toBe(false);
    expect(byKey.get("diagnosticsLogPaths")!.row.kind).toBe("count");
    expect(byKey.get("diagnosticsLogPaths")!.value(response)).toHaveLength(2);
  });

  it("pins the agent defaults the report highlights against", () => {
    expect(agentDefaults.responseDefaults.uploadIntervalSeconds).toBe(10);
    expect(agentDefaults.responseDefaults.maxBatchSize).toBe(100);
    expect(agentDefaults.collectorDefaults.collectorIdleTimeoutMinutes).toBe(15);
    expect(agentDefaults.analyzerDefaults.enableIntegrityBypassAnalyzer).toBe(true);
    expect(agentDefaults.analyzerDefaults.enableRealmJoinWatcher).toBe(false);
  });
});

describe("isNonDefault", () => {
  it("treats null, undefined and the empty string as one value and compares arrays by content", () => {
    expect(isNonDefault(undefined, null)).toBe(false);
    expect(isNonDefault("", null)).toBe(false);
    expect(isNonDefault("", "Off")).toBe(true);
    expect(isNonDefault(null, false)).toBe(true);
    expect(isNonDefault(false, false)).toBe(false);
    expect(isNonDefault([100, 1005, 1010], [100, 1005, 1010])).toBe(false);
    expect(isNonDefault([100, 42], [100, 1005, 1010])).toBe(true);
  });
});
