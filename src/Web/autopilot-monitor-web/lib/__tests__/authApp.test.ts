import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  classifyEntraAuthError,
  consumeLoginDeclined,
  getBootAuthApp,
  getSelectedAuthApp,
  legacyConfigured,
  markLoginDeclined,
  otherApp,
  setLoginAttemptApp,
  setSelectedAuthApp,
  tryBeginLoginFallback,
} from "../authApp";

const LEGACY_ID = "1a400946-62c1-4ab4-aa37-f730ac89704d";

/** Minimal browser shims — vitest runs in node (no jsdom in this suite). */
function makeStorage(): Storage {
  const map = new Map<string, string>();
  return {
    get length() {
      return map.size;
    },
    clear: () => map.clear(),
    getItem: (k: string) => (map.has(k) ? map.get(k)! : null),
    key: (i: number) => Array.from(map.keys())[i] ?? null,
    removeItem: (k: string) => void map.delete(k),
    setItem: (k: string, v: string) => void map.set(k, v),
  };
}

function stubWindow(search = ""): void {
  vi.stubGlobal("window", {
    localStorage: makeStorage(),
    sessionStorage: makeStorage(),
    location: { search, reload: vi.fn() },
  });
}

beforeEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

afterEach(() => {
  vi.unstubAllEnvs();
  vi.unstubAllGlobals();
});

describe("getSelectedAuthApp", () => {
  it("is primary during SSR (no window)", () => {
    vi.stubEnv("NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID", LEGACY_ID);
    vi.stubEnv("NEXT_PUBLIC_ENTRA_DEFAULT_APP", "legacy");
    expect(getSelectedAuthApp()).toBe("primary");
  });

  it("is primary whenever the legacy app is not configured — pre-migration deploys unchanged", () => {
    stubWindow();
    vi.stubEnv("NEXT_PUBLIC_ENTRA_DEFAULT_APP", "legacy");
    expect(legacyConfigured()).toBe(false);
    expect(getSelectedAuthApp()).toBe("primary");
  });

  it("falls back to the env default when no flag is stored (legacy during the window)", () => {
    stubWindow();
    vi.stubEnv("NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID", LEGACY_ID);
    vi.stubEnv("NEXT_PUBLIC_ENTRA_DEFAULT_APP", "legacy");
    expect(getSelectedAuthApp()).toBe("legacy");
  });

  it("defaults to primary when the default var is unset or garbage", () => {
    stubWindow();
    vi.stubEnv("NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID", LEGACY_ID);
    expect(getSelectedAuthApp()).toBe("primary");
    vi.stubEnv("NEXT_PUBLIC_ENTRA_DEFAULT_APP", "bogus");
    expect(getSelectedAuthApp()).toBe("primary");
  });

  it("prefers the stored flag over the default (the learn mechanism)", () => {
    stubWindow();
    vi.stubEnv("NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID", LEGACY_ID);
    vi.stubEnv("NEXT_PUBLIC_ENTRA_DEFAULT_APP", "legacy");
    setSelectedAuthApp("primary");
    expect(getSelectedAuthApp()).toBe("primary");
  });

  it("persists the ?authapp URL override (support lever + signup funnel handover)", () => {
    stubWindow("?authapp=primary&foo=1");
    vi.stubEnv("NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID", LEGACY_ID);
    vi.stubEnv("NEXT_PUBLIC_ENTRA_DEFAULT_APP", "legacy");
    expect(getSelectedAuthApp()).toBe("primary");
    expect(window.localStorage.getItem("am_auth_app")).toBe("primary");
  });

  it("ignores a garbage ?authapp value", () => {
    stubWindow("?authapp=evil");
    vi.stubEnv("NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID", LEGACY_ID);
    vi.stubEnv("NEXT_PUBLIC_ENTRA_DEFAULT_APP", "legacy");
    expect(getSelectedAuthApp()).toBe("legacy");
  });
});

describe("getBootAuthApp", () => {
  it("pins the boot app to the in-flight redirect's app, not the stored flag", () => {
    stubWindow();
    vi.stubEnv("NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID", LEGACY_ID);
    setSelectedAuthApp("legacy");
    setLoginAttemptApp("primary");
    expect(getBootAuthApp()).toBe("primary");
  });

  it("uses the selection when no redirect is in flight", () => {
    stubWindow();
    vi.stubEnv("NEXT_PUBLIC_ENTRA_LEGACY_CLIENT_ID", LEGACY_ID);
    setSelectedAuthApp("legacy");
    expect(getBootAuthApp()).toBe("legacy");
  });
});

describe("safety-net primitives", () => {
  it("tryBeginLoginFallback is one-shot per tab", () => {
    stubWindow();
    expect(tryBeginLoginFallback()).toBe(true);
    expect(tryBeginLoginFallback()).toBe(false);
  });

  it("consumeLoginDeclined reads and clears the marker", () => {
    stubWindow();
    expect(consumeLoginDeclined()).toBe(false);
    markLoginDeclined();
    expect(consumeLoginDeclined()).toBe(true);
    expect(consumeLoginDeclined()).toBe(false);
  });

  it("classifies Entra redirect errors driving the fallback decision", () => {
    // 90094/65001 = the app has no consent in the tenant → auto-retry the other app.
    expect(classifyEntraAuthError("AADSTS90094: The grant requires admin permission."))
      .toBe("admin-approval-required");
    expect(classifyEntraAuthError("AADSTS65001: The user or administrator has not consented"))
      .toBe("admin-approval-required");
    // 65004 = the user actively declined → NEVER auto-fallback (a new tenant would
    // mis-consent the legacy app); failed screen instead.
    expect(classifyEntraAuthError("AADSTS65004: User declined to consent."))
      .toBe("declined");
    expect(classifyEntraAuthError("AADSTS50011: redirect mismatch")).toBe("other");
    expect(classifyEntraAuthError("")).toBe("other");
  });

  it("otherApp flips the selection", () => {
    expect(otherApp("legacy")).toBe("primary");
    expect(otherApp("primary")).toBe("legacy");
  });
});
