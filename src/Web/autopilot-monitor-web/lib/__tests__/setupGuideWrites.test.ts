import { beforeEach, describe, expect, it, vi } from "vitest";
import { fetchOk } from "@/lib/apiClient";
import { invalidateCachedAuthFetch } from "@/lib/cachedAuthFetch";
import { recordSetupGuideStep, writeGuideConfigFields } from "../setupGuideWrites";
import { subscribeTenantConfigPatches } from "../tenantConfigPatches";

vi.mock("@/lib/apiClient", () => ({
  fetchOk: vi.fn(),
  jsonBody: (value: unknown) => JSON.stringify(value),
}));
vi.mock("@/lib/cachedAuthFetch", () => ({
  CONFIG_PATH_PREFIX: "/api/config/",
  invalidateCachedAuthFetch: vi.fn(),
}));

const TENANT = "00000000-0000-4000-8000-000000000001";
const token = vi.fn().mockResolvedValue("tok");

/**
 * The guide's two writes. Both change what the feature flags say, so both drop the cached flags:
 * a checklist read right after a step must not come from the browser's own older answer.
 */
describe("setup guide writes", () => {
  beforeEach(() => {
    vi.mocked(fetchOk).mockReset();
    vi.mocked(invalidateCachedAuthFetch).mockReset();
  });

  it("recording a step drops the cached flags once the server has it", async () => {
    vi.mocked(fetchOk).mockResolvedValueOnce(new Response("{}"));

    await recordSetupGuideStep(TENANT, "hardware", token);

    expect(vi.mocked(fetchOk).mock.calls[0][2]).toMatchObject({ method: "POST", body: JSON.stringify({ step: "hardware" }) });
    expect(invalidateCachedAuthFetch).toHaveBeenCalledWith("/api/config/");
  });

  it("a step the server refused drops nothing", async () => {
    vi.mocked(fetchOk).mockRejectedValueOnce(new Error("503"));

    await expect(recordSetupGuideStep(TENANT, "settings", token)).rejects.toThrow("503");

    expect(invalidateCachedAuthFetch).not.toHaveBeenCalled();
  });

  it("a field write drops the cached flags and hands exactly its fields to whoever shows the configuration", async () => {
    vi.mocked(fetchOk).mockResolvedValueOnce(new Response("{}"));
    const seen: Array<[string, Readonly<Record<string, unknown>>]> = [];
    const unsubscribe = subscribeTenantConfigPatches((tenantId, fields) => seen.push([tenantId, fields]));

    await writeGuideConfigFields(TENANT, { manufacturerWhitelist: "Dell*" }, "setup-guide:hardware", token);
    unsubscribe();

    expect(seen).toEqual([[TENANT, { manufacturerWhitelist: "Dell*" }]]);
    expect(invalidateCachedAuthFetch).toHaveBeenCalledWith("/api/config/");
  });

  it("a field write without fields sends and publishes nothing", async () => {
    const listener = vi.fn();
    const unsubscribe = subscribeTenantConfigPatches(listener);

    await writeGuideConfigFields(TENANT, {}, "setup-guide:recommended", token);
    unsubscribe();

    expect(fetchOk).not.toHaveBeenCalled();
    expect(listener).not.toHaveBeenCalled();
    expect(invalidateCachedAuthFetch).not.toHaveBeenCalled();
  });
});
