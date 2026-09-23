import { describe, expect, it } from "vitest";
import {
  AUTH_HINT_INLINE_SCRIPT,
  AUTH_HINT_INLINE_SCRIPT_BODY,
  AUTH_PENDING_CLASS,
  PORTAL_HOSTNAME,
  evaluateAuthHint,
} from "../authHint";

interface Case {
  name: string;
  /** Defaults to the site root, the only path that carries a hint. */
  pathname?: string;
  hash: string;
  hostname: string;
  storage: Record<string, string>;
  expected: boolean;
}

const cases: Case[] = [
  { name: "anonymous visitor on www", hash: "", hostname: "www.autopilotmonitor.com", storage: { "msal.version": "4.30.0" }, expected: false },
  { name: "landing anchor hash is not an auth response", hash: "#story", hostname: "www.autopilotmonitor.com", storage: {}, expected: false },
  { name: "MSAL auth code in the hash", hash: "#code=abc&client_info=x&state=y", hostname: "www.autopilotmonitor.com", storage: {}, expected: true },
  { name: "MSAL error in the hash", hash: "#error=access_denied&error_description=x", hostname: "www.autopilotmonitor.com", storage: {}, expected: true },
  { name: "portal host root", hash: "", hostname: PORTAL_HOSTNAME, storage: {}, expected: true },
  { name: "portal host, mixed case", hash: "", hostname: PORTAL_HOSTNAME.toUpperCase(), storage: {}, expected: true },
  { name: "signed-in browser (account keys present)", hash: "", hostname: "www.autopilotmonitor.com", storage: { "msal.version": "4.30.0", "msal.2.account.keys": '["abc-def"]' }, expected: true },
  { name: "account keys emptied after sign-out", hash: "", hostname: "www.autopilotmonitor.com", storage: { "msal.2.account.keys": "[]" }, expected: false },
  { name: "token keys alone are no hint", hash: "", hostname: "www.autopilotmonitor.com", storage: { "msal.2.token.keys.client": '{"idToken":[]}' }, expected: false },
  { name: "dev host without session", hash: "", hostname: "localhost", storage: {}, expected: false },
  { name: "root served as /index.html, signed-in browser", pathname: "/index.html", hash: "", hostname: "www.autopilotmonitor.com", storage: { "msal.2.account.keys": '["abc-def"]' }, expected: true },
  { name: "signed-in browser on a public page: AuthGate is not there to lift the overlay", pathname: "/about/", hash: "", hostname: "www.autopilotmonitor.com", storage: { "msal.2.account.keys": '["abc-def"]' }, expected: false },
  { name: "auth response hash on a non-root path", pathname: "/plans/", hash: "#code=abc&state=y", hostname: "www.autopilotmonitor.com", storage: {}, expected: false },
  { name: "portal host, non-root path", pathname: "/dashboard/", hash: "", hostname: PORTAL_HOSTNAME, storage: {}, expected: false },
];

function runInlineScript(c: Case): boolean {
  const classes = new Set<string>();
  const storageKeys = Object.keys(c.storage);
  const sessionStorage = {
    length: storageKeys.length,
    key: (i: number) => storageKeys[i] ?? null,
    getItem: (k: string) => c.storage[k] ?? null,
  };
  const document = { documentElement: { classList: { add: (x: string) => classes.add(x) } } };
  const fn = new Function("location", "sessionStorage", "document", AUTH_HINT_INLINE_SCRIPT_BODY);
  fn({ pathname: c.pathname ?? "/", hash: c.hash, hostname: c.hostname }, sessionStorage, document);
  return classes.has(AUTH_PENDING_CLASS);
}

describe("auth hint", () => {
  for (const c of cases) {
    it(`evaluateAuthHint: ${c.name}`, () => {
      const result = evaluateAuthHint({
        pathname: c.pathname ?? "/",
        hash: c.hash,
        hostname: c.hostname,
        storageKeys: Object.keys(c.storage),
        getItem: (k) => c.storage[k] ?? null,
      });
      expect(result).toBe(c.expected);
    });

    it(`inline script parity: ${c.name}`, () => {
      expect(runInlineScript(c)).toBe(c.expected);
    });
  }

  it("inline script is self-invoking with the real browser globals and stays ES5", () => {
    expect(AUTH_HINT_INLINE_SCRIPT.startsWith("(function(location,sessionStorage,document){")).toBe(true);
    expect(AUTH_HINT_INLINE_SCRIPT.endsWith("})(window.location,window.sessionStorage,window.document);")).toBe(true);
    // No template literals, arrow functions or `const`: the script runs before any polyfill.
    expect(AUTH_HINT_INLINE_SCRIPT).not.toMatch(/=>|`|\bconst\b|\blet\b/);
    // Never breaks out of the <script> tag it is inlined in.
    expect(AUTH_HINT_INLINE_SCRIPT).not.toContain("</script");
  });

  it("a storage failure means no hint (the landing must render for anonymous visitors)", () => {
    const classes = new Set<string>();
    const throwingStorage = {
      get length(): number {
        throw new Error("blocked");
      },
      key: () => null,
      getItem: () => null,
    };
    const document = { documentElement: { classList: { add: (x: string) => classes.add(x) } } };
    const fn = new Function("location", "sessionStorage", "document", AUTH_HINT_INLINE_SCRIPT_BODY);
    expect(() => fn({ pathname: "/", hash: "", hostname: "www.autopilotmonitor.com" }, throwingStorage, document)).not.toThrow();
    expect(classes.size).toBe(0);
  });
});
