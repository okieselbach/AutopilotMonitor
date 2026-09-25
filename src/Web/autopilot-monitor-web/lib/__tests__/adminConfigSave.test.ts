import { describe, expect, it, vi } from "vitest";
import { saveAdminConfigChanges } from "@/lib/adminConfigSave";
import { changedAdminConfigFields, type AdminConfiguration } from "@/types/adminConfig";

// D-285: a save sends only the fields the page changed. The page's copy of everything else — the
// release pipeline's agent hashes above all — must never travel, or a page loaded before a release
// reverts it (2026-09-25).
const loaded = {
  partitionKey: "GlobalConfig",
  rowKey: "config",
  enforceClientAppBinding: false,
  mcpClientRegistrationEnabled: true,
  globalRateLimitRequestsPerMinute: 100,
  latestAgentV2Version: "2.0.1464",
  latestAgentV2ExeSha256: "3cd3a3f1",
  platformStatsBlobSasUrl: undefined,
  excessiveEventAutoActionMode: undefined,
} as unknown as AdminConfiguration;

describe("changedAdminConfigFields", () => {
  it("returns only the toggled field, never the stale agent hashes", () => {
    const edited = { ...loaded, enforceClientAppBinding: true };
    expect(changedAdminConfigFields(loaded, edited)).toEqual({ enforceClientAppBinding: true });
  });

  it("treats undefined and null as the same unset value", () => {
    const edited = { ...loaded, platformStatsBlobSasUrl: null } as unknown as AdminConfiguration;
    expect(changedAdminConfigFields(loaded, edited)).toEqual({});
  });

  it("compares the wire form, so an unset mode equal to the server default is no change", () => {
    const edited = { ...loaded, excessiveEventAutoActionMode: "Off" } as AdminConfiguration;
    expect(changedAdminConfigFields(loaded, edited)).toEqual({});
  });

  it("sends a cleared string as its new value", () => {
    const withUrl = { ...loaded, platformStatsBlobSasUrl: "https://example.test/sas" } as AdminConfiguration;
    const edited = { ...withUrl, platformStatsBlobSasUrl: "" } as AdminConfiguration;
    expect(changedAdminConfigFields(withUrl, edited)).toEqual({ platformStatsBlobSasUrl: "" });
  });
});

describe("saveAdminConfigChanges", () => {
  it("sends no request when nothing changed", async () => {
    const getAccessToken = vi.fn(async () => "token");
    const fetchSpy = vi.spyOn(globalThis, "fetch");
    await expect(saveAdminConfigChanges(loaded, { ...loaded }, getAccessToken)).resolves.toBeNull();
    expect(getAccessToken).not.toHaveBeenCalled();
    expect(fetchSpy).not.toHaveBeenCalled();
    fetchSpy.mockRestore();
  });
});
