import { describe, it, expect } from "vitest";
import { buildMsalConfig } from "../msalConfig";

// MSAL's in-flight redirect state (interaction status, request incl. `state`, PKCE verifier) must stay
// in the tab that started the sign-in. Cookie mirroring (storeAuthStateInCookie) or a localStorage temp
// cache shares it across tabs: a second tab signing in overwrites it, and the first tab's return fails
// with state_mismatch and lands on the public page.

describe("buildMsalConfig cache", () => {
  const cache = buildMsalConfig("00000000-0000-0000-0000-000000000000").cache;

  it("keeps the token cache per tab", () => {
    expect(cache?.cacheLocation).toBe("sessionStorage");
  });

  it("never mirrors the redirect state into cookies", () => {
    expect(cache?.storeAuthStateInCookie ?? false).toBe(false);
  });

  it("keeps the temporary redirect state per tab", () => {
    expect(cache?.temporaryCacheLocation ?? "sessionStorage").toBe("sessionStorage");
  });
});
