import { describe, expect, it, beforeEach, afterEach } from "vitest";
import { ADD_ON_GRANT_SCRIPT_URL, appHomingErrorMessage, buildAddOnGrantCommand } from "../appHoming";
import { classifyClientId } from "../authApp";

const PRIMARY = "aaaaaaaa-0000-0000-0000-000000000001";
const LEGACY = "bbbbbbbb-0000-0000-0000-000000000002";

describe("classifyClientId", () => {
  const originalPrimary = process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID;
  const originalLegacy = process.env.NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID;

  beforeEach(() => {
    process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID = PRIMARY;
    process.env.NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID = LEGACY;
  });

  afterEach(() => {
    process.env.NEXT_PUBLIC_ENTRA_CLIENT_ID = originalPrimary;
    process.env.NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID = originalLegacy;
  });

  it("treats null/undefined/empty as legacy (the backend's null-homing invariant)", () => {
    expect(classifyClientId(null)).toBe("legacy");
    expect(classifyClientId(undefined)).toBe("legacy");
    expect(classifyClientId("")).toBe("legacy");
    expect(classifyClientId("   ")).toBe("legacy");
  });

  it("matches the primary client id case-insensitively", () => {
    expect(classifyClientId(PRIMARY)).toBe("primary");
    expect(classifyClientId(PRIMARY.toUpperCase())).toBe("primary");
    expect(classifyClientId(`  ${PRIMARY}  `)).toBe("primary");
  });

  it("matches the legacy client id", () => {
    expect(classifyClientId(LEGACY)).toBe("legacy");
  });

  it("classifies a GUID matching neither app as unknown", () => {
    expect(classifyClientId("cccccccc-0000-0000-0000-000000000003")).toBe("unknown");
  });
});

describe("appHomingErrorMessage", () => {
  it("maps every known backend reason code to a specific message", () => {
    for (const reason of [
      "parallel-window-inactive",
      "self-service-disabled",
      "probe-failed",
      "probe-transient",
      "revert-is-ga-only",
      "force-is-ga-only",
      "tenant-not-found",
      "invalid-target",
    ]) {
      const message = appHomingErrorMessage(reason, "Conflict");
      expect(message).not.toContain("Conflict");
      expect(message.length).toBeGreaterThan(10);
    }
  });

  it("falls back to the HTTP status text for unknown reasons", () => {
    expect(appHomingErrorMessage(undefined, "Bad Gateway")).toContain("Bad Gateway");
    expect(appHomingErrorMessage("something-new", "Conflict")).toContain("Conflict");
  });

  it("names the blocking add-on roles on a refused probe", () => {
    const message = appHomingErrorMessage("probe-failed", "Conflict", ["DeviceManagementScripts.Read.All"]);
    expect(message).toContain("DeviceManagementScripts.Read.All");
    expect(message).toContain("Grant them on the new app");
    // No roles ⇒ the generic wording (nothing to name).
    expect(appHomingErrorMessage("probe-failed", "Conflict", [])).not.toContain("add-on");
  });
});

describe("buildAddOnGrantCommand", () => {
  const TENANT = "11111111-1111-1111-1111-111111111111";

  it("targets the given client id with raw permissions for the funnel", () => {
    const cmd = buildAddOnGrantCommand(PRIMARY, TENANT, {
      permissions: ["CloudPC.Read.All", "DeviceManagementScripts.Read.All"],
    });
    expect(cmd).toContain(`irm '${ADD_ON_GRANT_SCRIPT_URL}' -OutFile .\\Grant-AutopilotMonitorAddOn.ps1`);
    expect(cmd).toContain(`-ClientId "${PRIMARY}"`);
    expect(cmd).toContain('-Permissions "CloudPC.Read.All","DeviceManagementScripts.Read.All"');
    expect(cmd).toContain(`-TenantId "${TENANT}"`);
    expect(cmd).not.toContain("-Features");
  });

  it("uses the feature form for the Optional Graph capabilities page", () => {
    const cmd = buildAddOnGrantCommand(LEGACY, TENANT, { features: "ScriptDisplayNames" });
    expect(cmd).toContain(`-ClientId "${LEGACY}"`);
    expect(cmd).toContain("-Features ScriptDisplayNames");
    expect(cmd).not.toContain("-Permissions");
  });

  it("leaves a placeholder when the tenant id is unknown", () => {
    expect(buildAddOnGrantCommand(PRIMARY, undefined, { features: "All" })).toContain('-TenantId "<your-tenant-id>"');
  });
});
