import { describe, expect, it } from "vitest";
import {
  APP_VERSION,
  base64UrlToUint8Array,
  bufferToBase64Url,
  cleanLabel,
  defaultLabel,
  detectPlatform,
  DEVICE_TOKEN_HEADER,
  entryFragment,
  GENERIC_BODY,
  GENERIC_TITLE,
  HISTORY_MAX_AGE_MS,
  HISTORY_MAX_ENTRIES,
  HISTORY_RETENTION_DEFAULT_DAYS,
  HISTORY_RETENTION_MAX_DAYS,
  isIosDevice,
  isValidPairingCode,
  isWipeCommand,
  LABEL_MAX_CHARS,
  normalizePairingCode,
  normalizePayload,
  normalizeRetentionDays,
  PAIRING_CODE_ALPHABET,
  PAIRING_CODE_LENGTH,
  parseFragment,
  PLATFORMS,
  pruneHistory,
  pushApiRequest,
  toNotificationOptions,
  type HistoryEntry,
} from "../push/pushCore";

/**
 * The push receiver's pure core (public/push/sw-core.js, re-exported by lib/push/pushCore.ts).
 * It is shared by the service worker and the pages, so the contract is pinned here once:
 * payload normalisation with the generic fallback, both push event shapes, the history
 * retention rule, the pairing-code grammar (K8), platform detection (K13) and the URL
 * fragment grammar.
 */

const NOW = Date.parse("2026-10-07T12:00:00Z");

const FULL_PAYLOAD = {
  web_push: 8030,
  mutable: true,
  notification: {
    title: "Enrollment failed",
    body: "DESKTOP-1234 failed in Device Setup",
    navigate: "https://portal.example.invalid/push/#e/11111111-1111-1111-1111-111111111111",
    tag: "abc",
    data: {
      v: 1,
      id: "11111111-1111-1111-1111-111111111111",
      type: "enrollment_failed",
      ts: "2026-10-07T11:58:00Z",
      severity: "error",
      facts: [
        { name: "Device", value: "DESKTOP-1234" },
        { name: "Serial", value: "****5678" },
      ],
      portalUrl: "https://portal.example.invalid/sessions?id=22222222-2222-2222-2222-222222222222",
      scope: "tenant",
    },
  },
};

describe("normalizePayload", () => {
  it("reads the full payload (event.data.json())", () => {
    const entry = normalizePayload(FULL_PAYLOAD, NOW);
    expect(entry).toEqual({
      id: "11111111-1111-1111-1111-111111111111",
      ts: "2026-10-07T11:58:00.000Z",
      title: "Enrollment failed",
      body: "DESKTOP-1234 failed in Device Setup",
      type: "enrollment_failed",
      severity: "error",
      facts: [
        { name: "Device", value: "DESKTOP-1234" },
        { name: "Serial", value: "****5678" },
      ],
      portalUrl: "https://portal.example.invalid/sessions?id=22222222-2222-2222-2222-222222222222",
      navigate: "https://portal.example.invalid/push/#e/11111111-1111-1111-1111-111111111111",
      tag: "abc",
      scope: "tenant",
      generic: false,
    });
  });

  it("reads the bare notification shape Safari hands the worker (event.notification / proposedNotification)", () => {
    // A Notification instance is not a plain object: property access is all that may be relied on.
    class FakeNotification {
      title = FULL_PAYLOAD.notification.title;
      body = FULL_PAYLOAD.notification.body;
      tag = FULL_PAYLOAD.notification.tag;
      data = FULL_PAYLOAD.notification.data;
    }
    const fromSafari = normalizePayload(new FakeNotification(), NOW);
    const fromJson = normalizePayload(FULL_PAYLOAD, NOW);
    expect(fromSafari).toEqual({ ...fromJson, navigate: null });
  });

  it("accepts a JSON string (event.data.text())", () => {
    expect(normalizePayload(JSON.stringify(FULL_PAYLOAD), NOW).id).toBe(FULL_PAYLOAD.notification.data.id);
  });

  it.each([
    ["null", null],
    ["undefined", undefined],
    ["garbage string", "not json at all"],
    ["number", 42],
    ["empty object", {}],
    ["notification without text", { notification: { data: { id: "x" } } }],
  ])("falls back to the generic notification for %s — never a silent push", (_name, input) => {
    const entry = normalizePayload(input, NOW);
    expect(entry.generic).toBe(true);
    expect(entry.title).toBe(GENERIC_TITLE);
    expect(entry.body).toBe(GENERIC_BODY);
    expect(entry.severity).toBe("info");
    expect(entry.ts).toBe(new Date(NOW).toISOString());
    expect(entry.id.length).toBeGreaterThan(0);
  });

  it("keeps the payload id when only the text is missing, and mints a local one otherwise", () => {
    expect(normalizePayload({ notification: { data: { id: "x" } } }, NOW).id).toBe("x");
    expect(normalizePayload({ notification: { title: "t" } }, NOW).id).toBe(`local-${NOW}`);
  });

  it("strips control characters, caps lengths and drops malformed facts and links", () => {
    const entry = normalizePayload(
      {
        notification: {
          title: "A\u0007B\u001b[31m  C" + "x".repeat(200),
          body: "line1\nline2",
          navigate: "javascript:alert(1)",
          data: {
            id: "ok",
            severity: "critical",
            ts: "not a date",
            facts: [
              { name: "Fine", value: "v" },
              { name: "", value: "no name" },
              { name: "NoValue" },
              "string",
              { name: "Long", value: "y".repeat(100) },
            ],
            portalUrl: "ftp://portal.example.invalid/x",
            scope: 7,
          },
        },
      },
      NOW,
    );
    expect(entry.title.startsWith("A B [31m C")).toBe(true);
    expect(entry.title.length).toBe(120);
    expect(entry.body).toBe("line1 line2");
    expect(entry.navigate).toBeNull();
    expect(entry.portalUrl).toBeNull();
    expect(entry.severity).toBe("info");
    expect(entry.ts).toBe(new Date(NOW).toISOString());
    expect(entry.facts).toEqual([
      { name: "Fine", value: "v" },
      { name: "Long", value: "y".repeat(64) },
    ]);
    expect(entry.scope).toBe("tenant");
    expect(entry.generic).toBe(false);
  });

  it("recognises the wipe command by type only", () => {
    expect(isWipeCommand(normalizePayload({ notification: { title: "Unpaired", data: { type: "push_revoked" } } }, NOW))).toBe(true);
    expect(isWipeCommand(normalizePayload(FULL_PAYLOAD, NOW))).toBe(false);
  });

  it("carries the whole entry into the notification so a click can re-persist it", () => {
    const entry = normalizePayload(FULL_PAYLOAD, NOW);
    const options = toNotificationOptions(entry);
    expect(options.tag).toBe("abc");
    expect(options.body).toBe(entry.body);
    expect(options.data).toEqual({ id: entry.id, entry });
    expect(toNotificationOptions({ ...entry, tag: null }).tag).toBeUndefined();
  });
});

describe("pruneHistory", () => {
  const entry = (id: string, ts: string): HistoryEntry => ({
    ...normalizePayload(FULL_PAYLOAD, NOW),
    id,
    ts,
  });

  it("drops entries older than 30 days, keeps the newest 200, newest first, and drops unreadable timestamps", () => {
    const fresh = Array.from({ length: 250 }, (_, i) => entry(`f${i}`, new Date(NOW - i * 60_000).toISOString()));
    const old = entry("old", new Date(NOW - HISTORY_MAX_AGE_MS - 1).toISOString());
    const edge = entry("edge", new Date(NOW - HISTORY_MAX_AGE_MS).toISOString());
    const broken = entry("broken", "yesterday-ish");
    const result = pruneHistory([old, broken, ...fresh.slice().reverse(), edge], NOW);
    expect(result.length).toBe(HISTORY_MAX_ENTRIES);
    expect(result[0].id).toBe("f0");
    expect(result[199].id).toBe("f199");
    expect(result.find((e) => e.id === "old" || e.id === "broken" || e.id === "edge")).toBeUndefined();
    // The edge entry survives when there is room.
    expect(pruneHistory([edge, old], NOW).map((e) => e.id)).toEqual(["edge"]);
  });

  it("does not mutate its input", () => {
    const input = [entry("b", "2026-10-06T00:00:00Z"), entry("a", "2026-10-07T00:00:00Z")];
    const copy = [...input];
    pruneHistory(input, NOW);
    expect(input).toEqual(copy);
  });

  it("honours the user's retention: 7 days drops week-old entries, 365 keeps them, 0 keeps everything up to the cap", () => {
    const dayMs = 24 * 60 * 60 * 1000;
    const recent = entry("recent", new Date(NOW - 3 * dayMs).toISOString());
    const weekOld = entry("week", new Date(NOW - 8 * dayMs).toISOString());
    const yearOld = entry("year", new Date(NOW - 400 * dayMs).toISOString());
    expect(pruneHistory([yearOld, weekOld, recent], NOW, 7).map((e) => e.id)).toEqual(["recent"]);
    expect(pruneHistory([yearOld, weekOld, recent], NOW, 365).map((e) => e.id)).toEqual(["recent", "week"]);
    expect(pruneHistory([yearOld, weekOld, recent], NOW, 0).map((e) => e.id)).toEqual(["recent", "week", "year"]);
    const many = Array.from({ length: 250 }, (_, i) => entry(`m${i}`, new Date(NOW - i * 10 * dayMs).toISOString()));
    expect(pruneHistory(many, NOW, 0).length).toBe(HISTORY_MAX_ENTRIES);
  });

  it("falls back to the 30-day default for an invalid retention value", () => {
    const dayMs = 24 * 60 * 60 * 1000;
    const old = entry("old", new Date(NOW - 31 * dayMs).toISOString());
    expect(pruneHistory([old], NOW, Number.NaN)).toEqual([]);
    expect(pruneHistory([old], NOW, 400)).toEqual([]);
  });
});

describe("normalizeRetentionDays", () => {
  it("accepts integers 0–365 as numbers or stored strings", () => {
    expect(normalizeRetentionDays(0)).toBe(0);
    expect(normalizeRetentionDays("0")).toBe(0);
    expect(normalizeRetentionDays(7)).toBe(7);
    expect(normalizeRetentionDays("365")).toBe(HISTORY_RETENTION_MAX_DAYS);
  });

  it("maps absent, blank, fractional, negative and out-of-range values to the default, never to forever", () => {
    expect(HISTORY_RETENTION_DEFAULT_DAYS).toBe(30);
    for (const value of [undefined, null, "", " ", "abc", 1.5, -1, 366, "1e3", Number.POSITIVE_INFINITY]) {
      expect(normalizeRetentionDays(value), String(value)).toBe(HISTORY_RETENTION_DEFAULT_DAYS);
    }
  });
});

describe("pairing code grammar (K8)", () => {
  it("uses Crockford base32 without I, L, O, U", () => {
    expect(PAIRING_CODE_ALPHABET).toBe("0123456789ABCDEFGHJKMNPQRSTVWXYZ");
    expect(PAIRING_CODE_ALPHABET.length).toBe(32);
    for (const letter of "ILOU") expect(PAIRING_CODE_ALPHABET).not.toContain(letter);
    expect(PAIRING_CODE_LENGTH).toBe(11);
  });

  it("normalises typing: case, spaces, hyphens and the look-alikes O→0, I/L→1", () => {
    expect(normalizePairingCode("7q3m-k9t2-xh4")).toBe("7Q3MK9T2XH4");
    expect(normalizePairingCode(" 7Q3 MK9 T2X H4 ")).toBe("7Q3MK9T2XH4");
    expect(normalizePairingCode("OQ3MK9T2XHI")).toBe("0Q3MK9T2XH1");
    expect(normalizePairingCode("lQ3MK9T2XHo")).toBe("1Q3MK9T2XH0");
  });

  it("rejects wrong length, excluded letters and non-strings", () => {
    expect(normalizePairingCode("7Q3MK9T2XH")).toBeNull();
    expect(normalizePairingCode("7Q3MK9T2XH44")).toBeNull();
    expect(normalizePairingCode("7Q3MK9T2XHU")).toBeNull();
    expect(normalizePairingCode("")).toBeNull();
    expect(normalizePairingCode(null)).toBeNull();
    expect(normalizePairingCode(undefined)).toBeNull();
    expect(isValidPairingCode("7Q3MK9T2XH4")).toBe(true);
    expect(isValidPairingCode("7q3mk9t2xh4")).toBe(false);
  });

  it("can never spell the substrings MSAL, authHint and HostRoutingGuard react to", () => {
    // `code=` and `state=` need "=", `error` needs an O: none of them is expressible in the alphabet.
    const alphabet = new Set(PAIRING_CODE_ALPHABET);
    for (const needle of ["code=", "state=", "error"]) {
      const expressible = [...needle.toUpperCase()].every((ch) => alphabet.has(ch));
      expect(expressible, `${needle} must not be expressible in a pairing code`).toBe(false);
    }
    expect(/[#&](code|state|error)=/.test("#p=7Q3MK9T2XH4")).toBe(false);
  });
});

describe("parseFragment", () => {
  it("reads #p=<code> with normalisation and #e/<id>", () => {
    expect(parseFragment("#p=7q3m-k9t2-xh4")).toEqual({ kind: "pair", code: "7Q3MK9T2XH4" });
    expect(parseFragment("p=7Q3MK9T2XH4")).toEqual({ kind: "pair", code: "7Q3MK9T2XH4" });
    expect(parseFragment("#p=7Q3M%2DK9T2XH4")).toEqual({ kind: "pair", code: "7Q3MK9T2XH4" });
    expect(parseFragment("#e/11111111-1111-1111-1111-111111111111")).toEqual({
      kind: "entry",
      id: "11111111-1111-1111-1111-111111111111",
    });
    expect(entryFragment("abc")).toBe("#e/abc");
  });

  it("yields null for anything else", () => {
    expect(parseFragment("")).toBeNull();
    expect(parseFragment("#")).toBeNull();
    expect(parseFragment("#p=")).toBeNull();
    expect(parseFragment("#p=INVALID")).toBeNull();
    expect(parseFragment("#e/")).toBeNull();
    expect(parseFragment("#e/../x")).toBeNull();
    expect(parseFragment("#code=abc&state=x")).toBeNull();
    expect(parseFragment("#story")).toBeNull();
  });
});

describe("platform detection (K13)", () => {
  const IPHONE = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.4 Mobile/15E148 Safari/604.1";
  const IPAD_DESKTOP_UA = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.4 Safari/605.1.15";
  const MAC_SAFARI = IPAD_DESKTOP_UA;
  const ANDROID_CHROME = "Mozilla/5.0 (Linux; Android 15; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36";
  const WINDOWS_EDGE = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.0.0";
  const WINDOWS_FIREFOX = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:143.0) Gecko/20100101 Firefox/143.0";
  const MAC_CHROME = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

  it("maps browsers onto the closed list", () => {
    expect(detectPlatform({ userAgent: IPHONE })).toBe("ios-homescreen");
    expect(detectPlatform({ userAgent: IPAD_DESKTOP_UA, maxTouchPoints: 5 })).toBe("ios-homescreen");
    expect(detectPlatform({ userAgent: MAC_SAFARI, maxTouchPoints: 0 })).toBe("macos-safari");
    expect(detectPlatform({ userAgent: ANDROID_CHROME })).toBe("android-chrome");
    expect(detectPlatform({ userAgent: WINDOWS_EDGE })).toBe("windows-chromium");
    expect(detectPlatform({ userAgent: WINDOWS_FIREFOX })).toBe("firefox");
    expect(detectPlatform({ userAgent: MAC_CHROME })).toBe("other");
    expect(detectPlatform({ userAgent: "" })).toBe("other");
  });

  it("every result is a member of the backend's list", () => {
    for (const ua of [IPHONE, MAC_SAFARI, ANDROID_CHROME, WINDOWS_EDGE, WINDOWS_FIREFOX, MAC_CHROME, ""]) {
      expect(PLATFORMS).toContain(detectPlatform({ userAgent: ua }));
    }
    expect(PLATFORMS).toEqual(["ios-homescreen", "android-chrome", "windows-chromium", "macos-safari", "firefox", "other"]);
  });

  it("iPadOS is iOS even with the desktop UA", () => {
    expect(isIosDevice({ userAgent: IPAD_DESKTOP_UA, maxTouchPoints: 5 })).toBe(true);
    expect(isIosDevice({ userAgent: IPAD_DESKTOP_UA, maxTouchPoints: 0 })).toBe(false);
    expect(isIosDevice({ userAgent: IPAD_DESKTOP_UA })).toBe(false);
  });

  it("offers a label per platform within the backend cap", () => {
    for (const platform of PLATFORMS) {
      const label = defaultLabel(platform);
      expect(label.length).toBeGreaterThan(0);
      expect(label.length).toBeLessThanOrEqual(LABEL_MAX_CHARS);
    }
    expect(cleanLabel(" My\u0000 phone  " + "z".repeat(60)).length).toBe(LABEL_MAX_CHARS);
    expect(APP_VERSION.length).toBeLessThanOrEqual(32);
  });
});

describe("key encoding", () => {
  it("round-trips base64url without padding", () => {
    const bytes = new Uint8Array([0, 1, 2, 250, 251, 252, 253, 254, 255]);
    const encoded = bufferToBase64Url(bytes.buffer);
    expect(encoded).not.toMatch(/[+/=]/);
    expect([...base64UrlToUint8Array(encoded)]).toEqual([...bytes]);
    expect(bufferToBase64Url(null)).toBe("");
  });
});

describe("pushApiRequest", () => {
  it("puts the device token in its header only and never in the URL (K11)", () => {
    const token = "part.11111111-1111-1111-1111-111111111111.secret";
    const { url, init } = pushApiRequest("https://api.example.invalid/", "/api/push/device", {
      method: "PUT",
      token,
      body: { endpoint: "https://push.example.invalid/x" },
    });
    expect(url).toBe("https://api.example.invalid/api/push/device");
    expect(url).not.toContain("secret");
    const headers = init.headers as Record<string, string>;
    expect(headers[DEVICE_TOKEN_HEADER]).toBe(token);
    expect(DEVICE_TOKEN_HEADER).toBe("X-Push-Device-Token");
    expect(headers["Content-Type"]).toBe("application/json");
    expect(init.method).toBe("PUT");
    expect(JSON.parse(init.body as string)).toEqual({ endpoint: "https://push.example.invalid/x" });
  });

  it("sends no token header and no body for an anonymous GET", () => {
    const { init } = pushApiRequest("https://api.example.invalid", "/api/push/pair/begin");
    const headers = init.headers as Record<string, string>;
    expect(headers[DEVICE_TOKEN_HEADER]).toBeUndefined();
    expect(headers["Content-Type"]).toBeUndefined();
    expect(init.body).toBeUndefined();
    expect(init.method).toBe("GET");
  });
});
