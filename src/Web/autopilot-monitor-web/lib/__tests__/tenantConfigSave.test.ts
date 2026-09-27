import { describe, expect, it } from "vitest";
import { changedTenantConfigFields } from "@/lib/tenantConfigSave";

// D-290: every tenant-configuration save sends only the listed fields that changed. A field the
// caller did not change — or does not own — must never travel, or a page loaded before someone
// else's write reverts it.
const loaded = {
  tenantId: "t1",
  dataRetentionDays: 90,
  disabled: false,
  disabledReason: undefined,
  homedAppClientId: "stale-view-of-the-homing",
  diagnosticsUploadMode: "Off",
};

describe("changedTenantConfigFields", () => {
  it("returns only the listed fields whose value changed", () => {
    const edited = { ...loaded, dataRetentionDays: 30, disabled: false };
    expect(changedTenantConfigFields(loaded, edited, ["dataRetentionDays", "disabled"])).toEqual({ dataRetentionDays: 30 });
  });

  it("never sends a field that is not listed, even when it differs", () => {
    const edited = { ...loaded, homedAppClientId: "something-else", dataRetentionDays: 30 };
    expect(changedTenantConfigFields(loaded, edited, ["dataRetentionDays"])).toEqual({ dataRetentionDays: 30 });
  });

  it("treats undefined and null as the same unset value", () => {
    const edited = { ...loaded, disabledReason: null };
    expect(changedTenantConfigFields(loaded, edited, ["disabledReason"])).toEqual({});
  });

  it("sends a clear as an explicit null", () => {
    const withReason = { ...loaded, disabledReason: "Maintenance" };
    const edited = { ...withReason, disabledReason: undefined };
    expect(changedTenantConfigFields(withReason, edited, ["disabledReason"])).toEqual({ disabledReason: null });
  });

  it("compares structured values by content", () => {
    const withJson = { ...loaded, notificationChannelsJson: '[{"id":"a"}]' };
    expect(changedTenantConfigFields(withJson, { ...withJson }, ["notificationChannelsJson"])).toEqual({});
  });
});
