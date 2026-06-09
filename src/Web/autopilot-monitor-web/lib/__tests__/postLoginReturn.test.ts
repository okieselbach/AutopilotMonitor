import { describe, it, expect, beforeEach, afterEach } from "vitest";
import { savePostLoginReturnUrl, consumePostLoginReturnUrl } from "../postLoginReturn";

// Guards the deep-link-after-reauth contract: ProtectedRoute stashes the page the
// user opened (e.g. a /sessions/[id] link in a new tab) before the MSAL login
// redirect, and AuthGate restores it afterwards. MSAL runs with
// navigateToLoginRequestUrl=false, so a regression here silently drops the user
// on /dashboard instead of where they were headed — and the open-redirect guard
// is the only thing stopping a crafted path from bouncing them off-origin.

function installSessionStorageStub() {
  const store = new Map<string, string>();
  const sessionStorage = {
    getItem: (k: string) => (store.has(k) ? store.get(k)! : null),
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
    clear: () => store.clear(),
  };
  (globalThis as unknown as { window: unknown }).window = { sessionStorage };
}

describe("postLoginReturn", () => {
  beforeEach(() => {
    installSessionStorageStub();
  });

  afterEach(() => {
    delete (globalThis as unknown as { window?: unknown }).window;
  });

  it("round-trips a safe in-app path and clears it on consume", () => {
    savePostLoginReturnUrl("/sessions/abc-123?tab=events");
    expect(consumePostLoginReturnUrl()).toBe("/sessions/abc-123?tab=events");
    // Second consume returns null — the value is single-use.
    expect(consumePostLoginReturnUrl()).toBeNull();
  });

  it("returns null when nothing was saved", () => {
    expect(consumePostLoginReturnUrl()).toBeNull();
  });

  it("ignores the bare root path", () => {
    savePostLoginReturnUrl("/");
    expect(consumePostLoginReturnUrl()).toBeNull();
  });

  it("rejects open-redirect shapes", () => {
    for (const evil of ["//evil.com", "/\\evil.com", "https://evil.com", "javascript:alert(1)", ""]) {
      savePostLoginReturnUrl(evil);
      expect(consumePostLoginReturnUrl()).toBeNull();
    }
  });

  // Belt-and-suspenders: percent-encoded slash/backslash do NOT act as authority
  // separators at the URL level (the leading single "/" already fixed this as a
  // path), so these stay same-origin and are intentionally accepted as-is. This
  // locks that contract against a future change that decodes before validating —
  // which would turn "/%2F%2Fevil.com" into the "//evil.com" open redirect above.
  it("accepts percent-encoded slash/backslash as literal same-origin paths", () => {
    for (const encoded of ["/%2F%2Fevil.com", "/%5Cevil.com", "/%2f%2fevil.com"]) {
      savePostLoginReturnUrl(encoded);
      expect(consumePostLoginReturnUrl()).toBe(encoded);
    }
  });

  it("does not throw when sessionStorage is unavailable", () => {
    delete (globalThis as unknown as { window?: unknown }).window;
    expect(() => savePostLoginReturnUrl("/sessions/x")).not.toThrow();
    expect(consumePostLoginReturnUrl()).toBeNull();
  });
});
