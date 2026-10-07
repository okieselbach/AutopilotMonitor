import { describe, it, expect } from "vitest";
import {
  describePortalDeviceStatus,
  formatCountdown,
  formatPairingCode,
  hasEnabledPushChannel,
  isPushProvider,
  PAIRING_GRANT_SECONDS,
  pairPageUrl,
  pairingSecondsLeft,
  platformLabel,
  PUSH_PROVIDER,
  statusTone,
} from "../pushPortal";

describe("formatPairingCode", () => {
  it("groups an 11-character code as 4-4-3", () => {
    expect(formatPairingCode("ABCDEFGHJKM")).toBe("ABCD-EFGH-JKM");
  });

  it("upper-cases and ignores existing dashes and whitespace", () => {
    expect(formatPairingCode("abcd-efgh jkm")).toBe("ABCD-EFGH-JKM");
    expect(formatPairingCode("ABCD-EFGH-JKM")).toBe("ABCD-EFGH-JKM");
  });

  it("handles short and empty input without a trailing dash", () => {
    expect(formatPairingCode("")).toBe("");
    expect(formatPairingCode("ABCD")).toBe("ABCD");
    expect(formatPairingCode("ABCDE")).toBe("ABCD-E");
  });
});

describe("platformLabel", () => {
  it("names every platform of the wire's closed list", () => {
    expect(platformLabel("ios-homescreen")).toBe("iPhone / iPad");
    expect(platformLabel("android-chrome")).toBe("Android");
    expect(platformLabel("windows-chromium")).toBe("Windows");
    expect(platformLabel("macos-safari")).toBe("Mac");
    expect(platformLabel("firefox")).toBe("Firefox");
  });

  it("falls back to Other for unknown or free-text values", () => {
    expect(platformLabel("other")).toBe("Other");
    expect(platformLabel("")).toBe("Other");
    expect(platformLabel("<script>")).toBe("Other");
  });
});

describe("statusTone", () => {
  it("maps the documented statuses onto the three colour families", () => {
    expect(statusTone("Active")).toBe("green");
    expect(statusTone("Pending")).toBe("orange");
    expect(statusTone("Stale")).toBe("orange");
    expect(statusTone("Paused")).toBe("gray");
  });

  it("treats an unknown status as gray", () => {
    expect(statusTone("Whatever")).toBe("gray");
  });

  it("has a sentence for every documented status and none for unknown ones", () => {
    for (const status of ["Active", "Pending", "Paused", "Stale"]) {
      expect(describePortalDeviceStatus(status)).not.toBe("");
    }
    expect(describePortalDeviceStatus("x")).toBe("");
  });
});

describe("hasEnabledPushChannel", () => {
  it("is true only for an ENABLED channel with providerType 50", () => {
    expect(isPushProvider(PUSH_PROVIDER)).toBe(true);
    expect(hasEnabledPushChannel([{ providerType: 50, enabled: true }])).toBe(true);
    expect(hasEnabledPushChannel([{ providerType: 50, enabled: false }])).toBe(false);
    expect(hasEnabledPushChannel([{ providerType: 40, enabled: true }, { providerType: 2, enabled: true }])).toBe(false);
    expect(hasEnabledPushChannel([])).toBe(false);
  });

  it("reads the stored JSON string, including a reader's redacted copy (structure kept)", () => {
    const json = JSON.stringify([
      { id: "a", name: "Teams", providerType: 2, url: "***REDACTED***", enabled: true },
      { id: "b", name: "Push", providerType: 50, enabled: true },
    ]);
    expect(hasEnabledPushChannel(json)).toBe(true);
    expect(hasEnabledPushChannel(JSON.stringify([{ id: "b", providerType: 50, enabled: false }]))).toBe(false);
  });

  it("treats blank, malformed, non-array and whole-string-redacted input as no channel", () => {
    expect(hasEnabledPushChannel(undefined)).toBe(false);
    expect(hasEnabledPushChannel(null)).toBe(false);
    expect(hasEnabledPushChannel("")).toBe(false);
    expect(hasEnabledPushChannel("   ")).toBe(false);
    expect(hasEnabledPushChannel("***REDACTED***")).toBe(false);
    expect(hasEnabledPushChannel("{not json")).toBe(false);
    expect(hasEnabledPushChannel('{"providerType":50,"enabled":true}')).toBe(false);
  });

  it("ignores null entries and entries whose enabled flag is not strictly true", () => {
    expect(hasEnabledPushChannel("[null,{\"providerType\":50,\"enabled\":\"true\"}]")).toBe(false);
  });
});

describe("pairing countdown helpers", () => {
  const received = Date.parse("2026-10-07T10:00:00Z");

  it("counts the ten-minute grant down from the moment the code arrived, never below zero", () => {
    expect(PAIRING_GRANT_SECONDS).toBe(600);
    expect(pairingSecondsLeft(received, received)).toBe(600);
    expect(pairingSecondsLeft(received, received + 2_000)).toBe(598);
    expect(pairingSecondsLeft(received, received + 2_900)).toBe(598);
    expect(pairingSecondsLeft(received, received + 600_000)).toBe(0);
    expect(pairingSecondsLeft(received, received + 3_600_000)).toBe(0);
    expect(pairingSecondsLeft(Number.NaN, received)).toBe(0);
  });

  it("ignores the server clock entirely and tolerates a local clock that steps backwards", () => {
    // A PC clock far ahead of the server used to show "expired" at once; the anchor is local.
    expect(pairingSecondsLeft(received, received - 5_000)).toBe(600);
  });

  it("formats m:ss", () => {
    expect(formatCountdown(598)).toBe("9:58");
    expect(formatCountdown(65)).toBe("1:05");
    expect(formatCountdown(0)).toBe("0:00");
    expect(formatCountdown(-3)).toBe("0:00");
  });

  it("strips the code fragment from the pairing link", () => {
    expect(pairPageUrl("https://portal.example/push/pair/#p=ABCDEFGHJKM")).toBe("https://portal.example/push/pair/");
    expect(pairPageUrl("https://portal.example/push/pair/")).toBe("https://portal.example/push/pair/");
  });
});
