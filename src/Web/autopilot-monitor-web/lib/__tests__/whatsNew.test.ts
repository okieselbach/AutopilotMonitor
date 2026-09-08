import { describe, expect, it } from "vitest";
import {
  countUnseen,
  FIRST_VISIT_WINDOW_DAYS,
  formatBadgeCount,
  laterMark,
  nextSeenMark,
  parseWhatsNewPayload,
  unseenEntries,
  type WhatsNewEntry,
} from "../whatsNew";

const NOW = new Date("2026-09-08T12:00:00.000Z");

function entry(id: string, addedUtc: string): WhatsNewEntry {
  return { id, addedUtc, period: "September 2026", title: null, body: id, link: null };
}

const ENTRIES = [
  entry("a", "2026-09-07T10:00:00.000Z"),
  entry("b", "2026-09-01T10:00:00.000Z"),
  entry("c", "2026-08-01T10:00:00.000Z"), // older than the first-visit window
];

describe("unseen logic", () => {
  it("counts entries newer than the seen mark", () => {
    expect(unseenEntries(ENTRIES, "2026-09-01T10:00:00.000Z", NOW).map(e => e.id)).toEqual(["a"]);
    expect(countUnseen(ENTRIES, "2026-09-07T10:00:00.000Z", NOW)).toBe(0);
  });

  it("falls back to the first-visit window without a mark", () => {
    expect(unseenEntries(ENTRIES, null, NOW).map(e => e.id)).toEqual(["a", "b"]);
    const edge = new Date(NOW.getTime() - FIRST_VISIT_WINDOW_DAYS * 86_400_000).toISOString();
    expect(countUnseen([entry("x", edge)], null, NOW)).toBe(0);
  });

  it("treats an unparsable mark like no mark", () => {
    expect(countUnseen(ENTRIES, "garbage", NOW)).toBe(2);
  });

  it("uses the newest loaded entry as the next mark, never now", () => {
    expect(nextSeenMark(ENTRIES)).toBe("2026-09-07T10:00:00.000Z");
    expect(nextSeenMark([])).toBeNull();
  });

  it("keeps marks monotonic", () => {
    expect(laterMark(null, "2026-09-01T00:00:00Z")).toBe("2026-09-01T00:00:00Z");
    expect(laterMark("2026-09-02T00:00:00Z", "2026-09-01T00:00:00Z")).toBe("2026-09-02T00:00:00Z");
    expect(laterMark("2026-09-01T00:00:00Z", "2026-09-02T00:00:00Z")).toBe("2026-09-02T00:00:00Z");
  });

  it("caps the badge like the notification bell", () => {
    expect(formatBadgeCount(3)).toBe("3");
    expect(formatBadgeCount(9)).toBe("9");
    expect(formatBadgeCount(10)).toBe("9+");
  });
});

describe("parseWhatsNewPayload", () => {
  const valid = {
    schemaVersion: 1,
    generatedUtc: "2026-09-08T09:00:00.000Z",
    docsCommit: "abc1234",
    channels: {
      platform: { docsUrl: "https://d/p", entries: [ENTRIES[0]] },
      agent: { docsUrl: "https://d/a", entries: [] },
    },
  };

  it("accepts the generated shape", () => {
    expect(parseWhatsNewPayload(valid)).not.toBeNull();
    expect(parseWhatsNewPayload({ ...valid, docsCommit: null })).not.toBeNull();
  });

  it("rejects wrong schema versions, missing channels and bad entries", () => {
    expect(parseWhatsNewPayload({ ...valid, schemaVersion: 2 })).toBeNull();
    expect(parseWhatsNewPayload({ ...valid, channels: { platform: valid.channels.platform } })).toBeNull();
    expect(
      parseWhatsNewPayload({
        ...valid,
        channels: { ...valid.channels, platform: { docsUrl: "x", entries: [{ id: "a" }] } },
      }),
    ).toBeNull();
    expect(
      parseWhatsNewPayload({
        ...valid,
        channels: { ...valid.channels, platform: { docsUrl: "x", entries: [entry("a", "not-a-date")] } },
      }),
    ).toBeNull();
    expect(parseWhatsNewPayload(null)).toBeNull();
    expect(parseWhatsNewPayload("string")).toBeNull();
  });
});
