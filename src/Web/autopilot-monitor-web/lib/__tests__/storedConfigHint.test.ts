import { afterEach, describe, expect, it, vi } from "vitest";
import { writesTenantConfig } from "../storedConfigHint";

const HEADER = { "X-Tenant-Config": "stored" };
const T0 = 1_800_000_000_000;

/** vitest runs in node: one Map is the browser's localStorage, shared by every tab that gets it. */
function stubBrowser(stored = new Map<string, string>(), denied = false) {
  vi.stubGlobal("window", {
    localStorage: {
      getItem: (key: string) => {
        if (denied) throw new Error("storage denied");
        return stored.get(key) ?? null;
      },
      setItem: (key: string, value: string) => {
        if (denied) throw new Error("storage denied");
        stored.set(key, value);
      },
    },
  });
  return stored;
}

/** A fresh module per tab or page load: the in-memory fallback lives as long as the page. */
async function pageLoad() {
  vi.resetModules();
  return import("../storedConfigHint");
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("writesTenantConfig", () => {
  it.each([
    ["PATCH", "https://api.example.com/api/config/t1/fields", true],
    ["POST", "/api/config/t1/setup-guide", true],
    ["post", "/api/config/t1/trial?x=1", true],
    ["PUT", "/api/config/t1/plan", true],
    ["POST", "/api/config/t1/autopilot-device-validation/consent-success", true],
    ["POST", "/api/auth/dpa-acceptance", true],
    ["GET", "/api/config/t1", false],
    ["GET", "/api/config/t1/feature-flags", false],
    ["HEAD", "/api/config/t1", false],
    ["POST", "/api/realtime/groups/join", false],
    ["DELETE", "/api/sessions/abc", false],
    ["PATCH", "/api/global/config", false],
    ["POST", "/api/auth/dpa-acceptance/more", false],
    ["PUT", "/api/auth/me/whats-new-seen", false],
  ])("%s %s → %s", (method, url, expected) => {
    expect(writesTenantConfig(method, url)).toBe(expected);
  });
});

describe("the mark after a configuration write", () => {
  it("sends nothing before any write", async () => {
    stubBrowser();
    const hint = await pageLoad();
    expect(hint.storedConfigHeaders(T0)).toEqual({});
  });

  it("asks for the stored row from the write on and stops after the hint window", async () => {
    stubBrowser();
    const hint = await pageLoad();

    hint.noteTenantConfigWritten(T0);

    expect(hint.storedConfigHeaders(T0)).toEqual(HEADER);
    expect(hint.storedConfigHeaders(T0 + hint.STORED_CONFIG_HINT_MS - 1)).toEqual(HEADER);
    expect(hint.storedConfigHeaders(T0 + hint.STORED_CONFIG_HINT_MS)).toEqual({});
  });

  it("outlasts the backend's five-minute configuration cache", async () => {
    const hint = await pageLoad();
    expect(hint.STORED_CONFIG_HINT_MS).toBeGreaterThan(5 * 60_000);
  });

  it("survives a reload and reaches another tab: the consent redirect comes back to a fresh page", async () => {
    const browser = stubBrowser();
    (await pageLoad()).noteTenantConfigWritten(T0);

    stubBrowser(browser);
    const reloaded = await pageLoad();

    expect(reloaded.storedConfigHeaders(T0 + 10_000)).toEqual(HEADER);
  });

  it("a later write extends the window", async () => {
    stubBrowser();
    const hint = await pageLoad();
    hint.noteTenantConfigWritten(T0);
    hint.noteTenantConfigWritten(T0 + 5 * 60_000);
    expect(hint.storedConfigHeaders(T0 + hint.STORED_CONFIG_HINT_MS + 1_000)).toEqual(HEADER);
  });

  it("still covers this page when the browser refuses storage", async () => {
    stubBrowser(new Map(), true);
    const hint = await pageLoad();

    hint.noteTenantConfigWritten(T0);

    expect(hint.storedConfigHeaders(T0 + 1_000)).toEqual(HEADER);
  });

  it("ignores a broken value and a mark far in the future", async () => {
    stubBrowser(new Map([["am_config_written", "not-a-number"]]));
    expect((await pageLoad()).storedConfigHeaders(T0)).toEqual({});

    stubBrowser(new Map([["am_config_written", String(T0 + 24 * 3_600_000)]]));
    expect((await pageLoad()).storedConfigHeaders(T0)).toEqual({});
  });

  it("sends nothing where there is no browser", async () => {
    const hint = await pageLoad();
    hint.noteTenantConfigWritten(T0);
    expect(hint.storedConfigHeaders(T0)).toEqual({});
  });
});
