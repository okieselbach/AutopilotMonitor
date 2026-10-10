import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { clearTenantScope, readTenantScope, writeTenantScope } from "../tenantScopeStorage";

const GUID = "11111111-1111-1111-1111-111111111111";

/**
 * The helper persists the cross-tenant switcher selection in sessionStorage for the tab lifetime.
 * It must be SSR-safe (no `window`) and never throw when storage is disabled/full — failures degrade
 * to "no preference". Vitest runs in the node env, so there is no real `window`; we stub a minimal one.
 */

/** Build a Map-backed Storage stand-in; `throwing` simulates private-mode / quota failures. */
function makeStorage(throwing = false) {
  const map = new Map<string, string>();
  return {
    getItem: (k: string) => {
      if (throwing) throw new Error("denied");
      return map.has(k) ? map.get(k)! : null;
    },
    setItem: (k: string, v: string) => {
      if (throwing) throw new Error("quota");
      map.set(k, v);
    },
    removeItem: (k: string) => {
      if (throwing) throw new Error("denied");
      map.delete(k);
    },
  };
}

function installWindow(storage: ReturnType<typeof makeStorage>) {
  (globalThis as { window?: unknown }).window = { sessionStorage: storage };
}

afterEach(() => {
  delete (globalThis as { window?: unknown }).window;
});

describe("tenantScopeStorage (SSR / no window)", () => {
  it("reads null and writes/clears as no-ops without a window", () => {
    expect(typeof (globalThis as { window?: unknown }).window).toBe("undefined");
    expect(readTenantScope()).toBeNull();
    expect(() => writeTenantScope(GUID)).not.toThrow();
    expect(() => clearTenantScope()).not.toThrow();
  });
});

describe("tenantScopeStorage (browser)", () => {
  beforeEach(() => installWindow(makeStorage()));

  it("returns null when nothing is stored", () => {
    expect(readTenantScope()).toBeNull();
  });

  it("round-trips a concrete tenant guid", () => {
    writeTenantScope(GUID);
    expect(readTenantScope()).toBe(GUID);
  });

  it("distinguishes a persisted aggregated '' from an unset null", () => {
    writeTenantScope("");
    expect(readTenantScope()).toBe(""); // aggregated intent — NOT the same as "never set"
  });

  it("clear removes the persisted selection", () => {
    writeTenantScope(GUID);
    clearTenantScope();
    expect(readTenantScope()).toBeNull();
  });
});

describe("tenantScopeStorage (storage disabled / quota)", () => {
  beforeEach(() => installWindow(makeStorage(true)));

  it("swallows read/write/clear failures and reads degrade to null", () => {
    expect(() => writeTenantScope(GUID)).not.toThrow();
    expect(() => clearTenantScope()).not.toThrow();
    expect(readTenantScope()).toBeNull();
  });
});
