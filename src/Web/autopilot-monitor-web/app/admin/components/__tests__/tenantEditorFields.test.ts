/**
 * The Global Admin tenant editor saves through the field PATCH (D-290): its field list IS the write
 * surface. Pinned against an independent copy of the backend's deny-list — a denied field in the
 * list would 400 the save — and against the manifest.
 */
import { describe, expect, it } from "vitest";
import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";
import type { TenantConfiguration } from "@/lib/generated/wire-types.generated";
import { TENANT_EDITOR_FIELDS, tenantEditorPatch } from "../tenantEditorFields";

/** BaseDeniedFields in TenantConfigPatchService, camelCased — independent copy. */
const SERVER_DENIED_FIELDS = new Set([
  "tenantId", "domainName", "partitionKey", "rowKey", "timestamp", "eTag",
  "lastUpdated", "updatedBy", "onboardedAt", "onboardedBy", "dpaVersion",
  "dpaAcceptancePending", "dpaAcceptedBy", "dpaAcceptedAt",
  "homedAppClientId", "lastAuthClientId", "lastAuthClientIdSince",
  "planTier", "trialExpiresUtc", "trialStartedUtc", "trialConsumed", "trialGrantedBy",
  "proDowngradedUtc", "maxDelegatedTenantsOverride", "mcpUsagePlanOverride", "payingCustomer",
  "managedByProTenantId",
]);

const loaded = {
  tenantId: "t1",
  disabled: false,
  dataRetentionDays: 90,
  unrestrictedModeEnabled: true,
  unrestrictedMode: true,
  planTier: "community",
  payingCustomer: false,
} as unknown as TenantConfiguration;

describe("TENANT_EDITOR_FIELDS", () => {
  it("names only fields the backend model has", () => {
    const manifest = new Set<string>(SHARED_MANIFEST.tenantConfiguration.fields);
    for (const field of TENANT_EDITOR_FIELDS) expect(manifest, field).toContain(field);
  });

  it("never names a field the patch endpoint denies", () => {
    for (const field of TENANT_EDITOR_FIELDS) {
      expect(SERVER_DENIED_FIELDS.has(field), `'${field}' is patch-denied server-side`).toBe(false);
    }
  });
});

describe("tenantEditorPatch", () => {
  it("sends only the changed editor fields", () => {
    const edited = { ...loaded, dataRetentionDays: 30 } as TenantConfiguration;
    expect(tenantEditorPatch(loaded, edited)).toEqual({ dataRetentionDays: 30 });
  });

  it("never sends plan fields — they have their own endpoint", () => {
    const edited = { ...loaded, planTier: "pro", payingCustomer: true } as TenantConfiguration;
    expect(tenantEditorPatch(loaded, edited)).toEqual({});
  });

  it("switches the mode off together with its gate", () => {
    const edited = { ...loaded, unrestrictedModeEnabled: false } as TenantConfiguration;
    expect(tenantEditorPatch(loaded, edited)).toEqual({ unrestrictedModeEnabled: false, unrestrictedMode: false });
  });

  it("leaves an already disabled mode alone when the gate closes", () => {
    const modeOff = { ...loaded, unrestrictedMode: false } as TenantConfiguration;
    const edited = { ...modeOff, unrestrictedModeEnabled: false } as TenantConfiguration;
    expect(tenantEditorPatch(modeOff, edited)).toEqual({ unrestrictedModeEnabled: false });
  });
});
