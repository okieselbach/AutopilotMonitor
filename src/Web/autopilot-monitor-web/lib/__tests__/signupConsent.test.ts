import { afterEach, describe, expect, it, vi } from "vitest";
import {
  captureSignupConsentFromUrl,
  hasSignupConsent,
  markSignupConsent,
  stripSignupConsentParam,
} from "../signupConsent";

/** Minimal browser shims — vitest runs in node (no jsdom in this suite). */
function stubWindow(search = ""): void {
  const map = new Map<string, string>();
  vi.stubGlobal("window", {
    sessionStorage: {
      getItem: (k: string) => (map.has(k) ? map.get(k)! : null),
      setItem: (k: string, v: string) => void map.set(k, v),
      removeItem: (k: string) => void map.delete(k),
    },
    location: { search },
  });
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("signup consent marker", () => {
  it("is captured from the get-started handover and expires after 30 minutes", () => {
    stubWindow("?authapp=primary&signupConsent=1");
    const t0 = Date.now();
    captureSignupConsentFromUrl();
    expect(hasSignupConsent(t0 + 1000)).toBe(true);
    expect(hasSignupConsent(t0 + 29 * 60 * 1000)).toBe(true);
    expect(hasSignupConsent(t0 + 31 * 60 * 1000)).toBe(false);
  });

  it("is absent for every other sign-in", () => {
    stubWindow("?authapp=primary");
    captureSignupConsentFromUrl();
    expect(hasSignupConsent()).toBe(false);
    stubWindow("?signupConsent=0");
    captureSignupConsentFromUrl();
    expect(hasSignupConsent()).toBe(false);
  });

  it("is set directly by a same-origin get-started click", () => {
    stubWindow();
    markSignupConsent(1_000);
    expect(hasSignupConsent(2_000)).toBe(true);
    // A stamp from the future (clock change) never counts.
    expect(hasSignupConsent(500)).toBe(false);
  });

  it("fails closed without storage", () => {
    vi.stubGlobal("window", { location: { search: "?signupConsent=1" } });
    captureSignupConsentFromUrl();
    expect(hasSignupConsent()).toBe(false);
  });
});

describe("stripSignupConsentParam", () => {
  it("removes only the marker and keeps the rest of the address", () => {
    expect(stripSignupConsentParam("/dashboard?authapp=primary&signupConsent=1#top")).toBe("/dashboard?authapp=primary#top");
    expect(stripSignupConsentParam("/dashboard?signupConsent=1")).toBe("/dashboard");
  });

  it("leaves an address without the marker alone", () => {
    expect(stripSignupConsentParam("/dashboard?authapp=primary")).toBeNull();
    expect(stripSignupConsentParam("/")).toBeNull();
  });
});
