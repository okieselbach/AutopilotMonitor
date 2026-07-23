import { describe, it, expect } from "vitest";
import {
  HISTORIC_REPLAY_THRESHOLD_MS,
  partitionHistoricReplayEvents,
  type ReplayInputEvent,
} from "../historicReplay";

const BASE = 1700000000000;
const DAY_MS = 24 * 60 * 60 * 1000;

const evt = (overrides: Partial<ReplayInputEvent> = {}): ReplayInputEvent => ({
  timestamp: new Date(BASE).toISOString(),
  eventType: "app_install_completed",
  data: {},
  ...overrides,
});

const APP_FINALS = new Set(["app_install_completed", "app_install_failed"]);

describe("partitionHistoricReplayEvents", () => {
  it("drops events whose rejectedSourceTimestamp is > 24h older than the event stamp", () => {
    const historic = evt({ data: { rejectedSourceTimestamp: new Date(BASE - 7 * DAY_MS).toISOString() } });
    const fresh = evt({ data: {} });
    const { current, historicCount } = partitionHistoricReplayEvents([historic, fresh], APP_FINALS);
    expect(current).toEqual([fresh]);
    expect(historicCount).toBe(1);
  });

  it("counts only finals — dropped non-final events are removed uncounted", () => {
    const staleTs = new Date(BASE - 7 * DAY_MS).toISOString();
    const events = [
      evt({ eventType: "app_download_started", data: { rejectedSourceTimestamp: staleTs } }),
      evt({ eventType: "download_progress", data: { rejectedSourceTimestamp: staleTs } }),
      evt({ eventType: "app_install_started", data: { rejectedSourceTimestamp: staleTs } }),
      evt({ eventType: "app_install_completed", data: { rejectedSourceTimestamp: staleTs } }),
      evt({ eventType: "app_install_failed", data: { rejectedSourceTimestamp: staleTs } }),
    ];
    const { current, historicCount } = partitionHistoricReplayEvents(events, APP_FINALS);
    expect(current).toEqual([]);
    expect(historicCount).toBe(2);
  });

  it("keeps future-skew rejections (source timestamp ahead of the event stamp)", () => {
    const skewed = evt({ data: { rejectedSourceTimestamp: new Date(BASE + 2 * 60 * 60 * 1000).toISOString() } });
    const { current, historicCount } = partitionHistoricReplayEvents([skewed], APP_FINALS);
    expect(current).toEqual([skewed]);
    expect(historicCount).toBe(0);
  });

  it("keeps within-24h rejections, missing and malformed rejectedSourceTimestamp", () => {
    const within = evt({ data: { rejectedSourceTimestamp: new Date(BASE - 23 * 60 * 60 * 1000).toISOString() } });
    const none = evt({ data: {} });
    const noData = evt({ data: undefined });
    const malformed = evt({ data: { rejectedSourceTimestamp: "not-a-date" } });
    const { current, historicCount } = partitionHistoricReplayEvents([within, none, noData, malformed], APP_FINALS);
    expect(current).toHaveLength(4);
    expect(historicCount).toBe(0);
  });

  it("reads the snake_case wire variant", () => {
    const historic = evt({ data: { rejected_source_timestamp: new Date(BASE - 7 * DAY_MS).toISOString() } });
    const { current, historicCount } = partitionHistoricReplayEvents([historic], APP_FINALS);
    expect(current).toEqual([]);
    expect(historicCount).toBe(1);
  });

  it("empty finals set filters silently (DownloadProgress mode)", () => {
    const historic = evt({ data: { rejectedSourceTimestamp: new Date(BASE - 7 * DAY_MS).toISOString() } });
    const { current, historicCount } = partitionHistoricReplayEvents([historic], new Set<string>());
    expect(current).toEqual([]);
    expect(historicCount).toBe(0);
  });

  it("exposes the 24h threshold constant", () => {
    expect(HISTORIC_REPLAY_THRESHOLD_MS).toBe(DAY_MS);
  });
});
