import { describe, it, expect } from "vitest";
import { makeEvent } from "@/test/factories";
import { keyEvents, problemEvents } from "../eventLists";

const culprits = [{ kind: "app", evidence: "install_failed", key: "00000000-0000-4000-8000-0000000000a1", name: "Contoso Notes" }];

describe("keyEvents", () => {
  const events = [
    makeEvent({ eventId: "1", sequence: 1, eventType: "esp_phase_changed", message: "ESP phase: DeviceSetup" }),
    makeEvent({ eventId: "2", sequence: 2, eventType: "app_install_started", message: "Contoso Portal install started", data: { appName: "Contoso Portal" } }),
    makeEvent({ eventId: "3", sequence: 3, eventType: "app_install_started", message: "Contoso Notes install started", data: { appId: "00000000-0000-4000-8000-0000000000A1" } }),
    makeEvent({ eventId: "4", sequence: 4, eventType: "performance_snapshot", severity: "Info" }),
    makeEvent({ eventId: "5", sequence: 5, eventType: "app_install_failed", severity: "Error", message: "Contoso Notes failed", data: { appName: "Contoso Notes", errorCode: "0x80d03805" } }),
    makeEvent({ eventId: "6", sequence: 6, eventType: "app_install_failed", severity: "Error", message: "Contoso Notes failed", data: { appName: "Contoso Notes", errorCode: "0x80d03805" } }),
    makeEvent({ eventId: "7", sequence: 7, eventType: "enrollment_failed", severity: "Error", message: "enrollment_failed: esp_terminal_failure" }),
  ];

  it("keeps milestones, the culprit's attempts and errors, once each", () => {
    const { items, total } = keyEvents(events, culprits);
    expect(items.map((e) => e.eventId)).toEqual(["1", "3", "5", "7"]);
    expect(total).toBe(4);
    expect(items[2]).toMatchObject({ title: "Contoso Notes failed", detail: "0x80d03805", tone: "failed" });
  });

  it("keeps the first two and the end of a long story", () => {
    const many = Array.from({ length: 20 }, (_, i) => makeEvent({ eventId: `e${i}`, sequence: i, eventType: "esp_phase_changed", message: `phase ${i}`, timestamp: `2026-10-13T14:${String(i).padStart(2, "0")}:00Z` }));
    const { items, total } = keyEvents(many, [], 6);
    expect(total).toBe(20);
    expect(items.map((e) => e.eventId)).toEqual(["e0", "e1", "e16", "e17", "e18", "e19"]);
  });
});

describe("problemEvents", () => {
  it("lists errors and warnings in order and merges an event that arrived twice", () => {
    const rows = problemEvents([
      makeEvent({ eventId: "a", sequence: 1, severity: "Warning", eventType: "wu_scan_failed", message: "Windows Update scan failed", source: "Windows Update" }),
      makeEvent({ eventId: "b", sequence: 2, severity: "Error", eventType: "app_install_failed", message: "Contoso Notes failed", timestamp: "2026-10-13T14:19:46.100Z" }),
      makeEvent({ eventId: "c", sequence: 3, severity: "Error", eventType: "app_install_failed", message: "Contoso Notes failed", timestamp: "2026-10-13T14:19:46.900Z" }),
      makeEvent({ eventId: "d", sequence: 4, severity: "Info", eventType: "x" }),
    ]);
    expect(rows.map((r) => [r.eventId, r.count])).toEqual([
      ["a", 1],
      ["b", 2],
    ]);
  });
});
