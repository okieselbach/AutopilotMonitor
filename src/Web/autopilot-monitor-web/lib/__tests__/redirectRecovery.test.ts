import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  authErrorCode,
  consumeLoginFailed,
  consumeRedirectRecovery,
  decideRedirectOutcome,
  markLoginFailed,
  purgeMsalRedirectState,
  readBootAuthResponse,
  redirectRetried,
  tryBeginRedirectRetry,
  type BootAuthResponse,
  type RedirectOutcome,
} from "../redirectRecovery";

// A failed sign-in return must never leave the tab on the public landing, and its recovery must never
// loop: one automatic retry per tab, then the sign-in card that waits for a click. A tab that holds an
// account (a stale /#code= history entry, a reload during auth/me) always takes the normal path.

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

function storage(): Storage {
  return (window as unknown as { sessionStorage: Storage }).sessionStorage;
}

beforeEach(() => {
  vi.unstubAllGlobals();
  vi.stubGlobal("window", { sessionStorage: makeStorage() });
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("readBootAuthResponse", () => {
  it.each<[string, BootAuthResponse]>([
    ["#code=abc&client_info=x&state=y", { code: true, error: false }],
    ["#state=y&code=abc", { code: true, error: false }],
    ["#error=access_denied&error_description=x&state=y", { code: false, error: true }],
    ["", { code: false, error: false }],
    ["#section-details", { code: false, error: false }],
    // Only whole parameters count.
    ["#barcode=1&noerror=1", { code: false, error: false }],
  ])("%s", (hash, expected) => {
    expect(readBootAuthResponse(hash)).toEqual(expected);
  });
});

describe("decideRedirectOutcome", () => {
  const code = { code: true, error: false };
  const error = { code: false, error: true };
  const none = { code: false, error: false };
  it.each<[string, BootAuthResponse, boolean, boolean, RedirectOutcome]>([
    // An account wins whatever the URL says: stale /#code= entry, reload during auth/me.
    ["account after a code return", code, true, false, "proceed"],
    ["account after a code return, retried before", code, true, true, "proceed"],
    ["account after an error return", error, true, false, "proceed"],
    // Not a sign-in return at all: an anonymous visit stays anonymous.
    ["no auth response", none, false, false, "proceed"],
    ["no auth response, retried before", none, false, true, "proceed"],
    // A code that did not yield an account (state mismatch, lost redirect state, token endpoint error).
    ["code without account", code, false, false, "retry"],
    ["code without account, retried before", code, false, true, "card"],
    // An Entra error keeps its first-attempt handling; after the retry it is just another failure.
    ["error return", error, false, false, "landing"],
    ["error return, retried before", error, false, true, "card"],
    ["code and error in one hash", { code: true, error: true }, false, false, "landing"],
  ])("%s → %s", (_name, boot, hasAccount, retried, outcome) => {
    expect(decideRedirectOutcome({ boot, hasAccount, retried })).toBe(outcome);
  });
});

describe("retry budget and sign-in card markers", () => {
  it("allows exactly one automatic retry per tab", () => {
    expect(redirectRetried()).toBe(false);
    expect(tryBeginRedirectRetry("state_mismatch")).toBe(true);
    expect(redirectRetried()).toBe(true);
    expect(tryBeginRedirectRetry("state_mismatch")).toBe(false);
  });

  it("hands the card its reason exactly once", () => {
    markLoginFailed("no_redirect_state");
    expect(consumeLoginFailed()).toBe("no_redirect_state");
    expect(consumeLoginFailed()).toBeNull();
  });

  it("reports an automatic recovery and clears the trail", () => {
    tryBeginRedirectRetry("state_mismatch");
    expect(consumeRedirectRecovery()).toEqual({ via: "auto", reason: "state_mismatch" });
    expect(redirectRetried()).toBe(false);
    expect(consumeRedirectRecovery()).toBeNull();
  });

  it("reports a recovery through the card and clears both markers", () => {
    tryBeginRedirectRetry("state_mismatch");
    markLoginFailed("invalid_grant");
    expect(consumeRedirectRecovery()).toEqual({ via: "card", reason: "invalid_grant" });
    expect(consumeLoginFailed()).toBeNull();
    expect(redirectRetried()).toBe(false);
  });

  it("fails closed without storage: no retry, no card marker, no throw", () => {
    vi.stubGlobal("window", {
      get sessionStorage(): Storage {
        throw new Error("blocked");
      },
    });
    expect(tryBeginRedirectRetry("x")).toBe(false);
    expect(redirectRetried()).toBe(false);
    expect(() => markLoginFailed("x")).not.toThrow();
    expect(consumeLoginFailed()).toBeNull();
    expect(consumeRedirectRecovery()).toBeNull();
    expect(() => purgeMsalRedirectState()).not.toThrow();
  });
});

describe("purgeMsalRedirectState", () => {
  const clientId = "00000000-0000-0000-0000-000000000001";
  const redirectState = [
    "msal.interaction.status",
    `msal.${clientId}.request.params`,
    `msal.${clientId}.code.verifier`,
    `msal.${clientId}.request.origin`,
    `msal.${clientId}.urlHash`,
    `msal.${clientId}.request.native`,
  ];
  const kept = [
    "msal.2.account.keys",
    `msal.token.keys.${clientId}`,
    `msal.2|uid.utid|login.microsoftonline.com|idtoken|${clientId}|utid|`,
    "apm:postLoginReturnUrl",
    "am_redirect_retry",
    "am_login_attempt",
  ];

  it("removes the in-flight redirect state of every app registration and keeps accounts, tokens and markers", () => {
    for (const key of [...redirectState, ...kept]) storage().setItem(key, "x");
    purgeMsalRedirectState();
    for (const key of redirectState) expect(storage().getItem(key)).toBeNull();
    for (const key of kept) expect(storage().getItem(key)).toBe("x");
  });
});

describe("authErrorCode", () => {
  it.each<[unknown, string | null]>([
    [{ errorCode: "state_mismatch", errorMessage: "State mismatch error" }, "state_mismatch"],
    // The message of an Entra error can carry the user's UPN — only the code ever leaves.
    [{ errorCode: "invalid_grant", errorMessage: "AADSTS54005: user@example.com" }, "invalid_grant"],
    [{ errorCode: "no code with spaces" }, null],
    [{ errorCode: 42 }, null],
    [new Error("plain"), null],
    [null, null],
    [undefined, null],
  ])("%j → %s", (error, expected) => {
    expect(authErrorCode(error)).toBe(expected);
  });
});
