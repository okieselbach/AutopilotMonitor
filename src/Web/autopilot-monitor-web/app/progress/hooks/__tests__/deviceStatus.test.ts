import { describe, it, expect } from "vitest";
import { computeDeviceStatus } from "../deviceStatus";
import { EnrollmentEvent } from "@/types";
import { makeEvent } from "@/test/factories";

/**
 * computeDeviceStatus feeds the progress page's taskbar-style power/network
 * chips: a sequence-ordered fold where later events win, "unknown" battery
 * percent maps to null, and sessions from agents without these events must
 * yield nulls so the chips simply don't render.
 */

function ev(
  sequence: number,
  eventType: string,
  data?: Record<string, unknown>,
): EnrollmentEvent {
  return makeEvent({
    eventId: `evt-${sequence}`,
    timestamp: "2026-08-21T10:00:00Z",
    eventType,
    phase: 2,
    message: `event ${sequence}`,
    sequence,
    // Deliberately undefined when omitted — the "ignores events without a data
    // payload" test pins the legacy-event tolerance of computeDeviceStatus.
    data,
  });
}

describe("computeDeviceStatus", () => {
  it("returns nulls for no events or only unrelated events", () => {
    expect(computeDeviceStatus([])).toEqual({ power: null, network: null });
    expect(
      computeDeviceStatus([ev(1, "info_event", { foo: "bar" }), ev(2, "download_progress")]),
    ).toEqual({ power: null, network: null });
  });

  it("captures a desktop baseline (hasBattery false) from power_state_check", () => {
    const { power } = computeDeviceStatus([
      ev(1, "power_state_check", { onAcPower: true, hasBattery: false, isCharging: false }),
    ]);
    expect(power).toEqual({
      onAcPower: true,
      hasBattery: false,
      batteryPercent: null,
      isCharging: false,
    });
  });

  it("lets a later power_state_change win over the baseline and implies hasBattery", () => {
    const { power } = computeDeviceStatus([
      ev(1, "power_state_check", {
        onAcPower: true,
        hasBattery: true,
        batteryPercent: 90,
        isCharging: true,
      }),
      ev(2, "power_state_change", {
        transition: "ac_to_battery",
        onAcPower: false,
        batteryPercent: 87,
        isCharging: false,
      }),
    ]);
    expect(power).toEqual({
      onAcPower: false,
      hasBattery: true,
      batteryPercent: 87,
      isCharging: false,
    });
  });

  it("tracks threshold_crossed percent updates while on battery", () => {
    const { power } = computeDeviceStatus([
      ev(1, "power_state_change", {
        transition: "ac_to_battery",
        onAcPower: false,
        batteryPercent: 55,
        isCharging: false,
      }),
      ev(2, "power_state_change", {
        transition: "threshold_crossed",
        thresholdPercent: 30,
        onAcPower: false,
        batteryPercent: 29,
        isCharging: false,
      }),
    ]);
    expect(power?.batteryPercent).toBe(29);
    expect(power?.onAcPower).toBe(false);
  });

  it('maps the literal "unknown" battery percent to null on both event types', () => {
    const check = computeDeviceStatus([
      ev(1, "power_state_check", { onAcPower: false, hasBattery: true, batteryPercent: "unknown" }),
    ]);
    expect(check.power?.batteryPercent).toBeNull();

    const change = computeDeviceStatus([
      ev(1, "power_state_change", { onAcPower: false, batteryPercent: "unknown" }),
    ]);
    expect(change.power?.batteryPercent).toBeNull();
  });

  it("skips a power_state_check that carries a probe error", () => {
    const { power } = computeDeviceStatus([
      ev(1, "power_state_change", { onAcPower: false, batteryPercent: 42, isCharging: false }),
      ev(2, "power_state_check", { onAcPower: true, hasBattery: false, error: "GetSystemPowerStatus failed" }),
    ]);
    expect(power).toEqual({
      onAcPower: false,
      hasBattery: true,
      batteryPercent: 42,
      isCharging: false,
    });
  });

  it("reads Ethernet from network_interface_info", () => {
    const { network } = computeDeviceStatus([
      ev(1, "network_interface_info", { connectionType: "Ethernet", linkSpeedMbps: 1000 }),
    ]);
    expect(network).toEqual({ type: "Ethernet" });
  });

  it("merges wifi_signal_info into a WiFi connection and clears on switch to Ethernet", () => {
    const events = [
      ev(1, "network_interface_info", { connectionType: "WiFi" }),
      ev(2, "wifi_signal_info", { wifiSsid: "CorpNet", wifiSignalPercent: 84 }),
    ];
    expect(computeDeviceStatus(events).network).toEqual({
      type: "WiFi",
      ssid: "CorpNet",
      signalPercent: 84,
    });

    events.push(
      ev(3, "network_state_change", {
        changeType: "type_change",
        after_connectionType: "Ethernet",
        hasNetwork: true,
      }),
    );
    expect(computeDeviceStatus(events).network).toEqual({ type: "Ethernet" });
  });

  it("goes offline on hasNetwork false and recovers on network_restored", () => {
    const events = [
      ev(1, "network_interface_info", { connectionType: "WiFi" }),
      ev(2, "network_state_change", {
        changeType: "network_lost",
        after_connectionType: "None",
        hasNetwork: false,
      }),
    ];
    expect(computeDeviceStatus(events).network).toEqual({ type: "None" });

    events.push(
      ev(3, "network_state_change", {
        changeType: "network_restored",
        after_connectionType: "WiFi",
        after_wifiSsid: "CorpNet",
        hasNetwork: true,
      }),
    );
    expect(computeDeviceStatus(events).network).toEqual({ type: "WiFi", ssid: "CorpNet" });
  });

  it('treats the "n/a" SSID sentinel as absent and carries the prior ssid', () => {
    const { network } = computeDeviceStatus([
      ev(1, "wifi_signal_info", { wifiSsid: "CorpNet", wifiSignalPercent: 70 }),
      ev(2, "network_state_change", {
        changeType: "ip_change",
        after_connectionType: "WiFi",
        after_wifiSsid: "n/a",
        hasNetwork: true,
      }),
    ]);
    expect(network).toEqual({ type: "WiFi", ssid: "CorpNet", signalPercent: 70 });
  });

  it("keeps ssid/signal across the enrollment-end network_interface_info re-emit", () => {
    const { network } = computeDeviceStatus([
      ev(1, "network_interface_info", { connectionType: "WiFi" }),
      ev(2, "wifi_signal_info", { wifiSsid: "CorpNet", wifiSignalPercent: 61 }),
      ev(3, "network_interface_info", { connectionType: "WiFi", linkSpeedMbps: 866 }),
    ]);
    expect(network).toEqual({ type: "WiFi", ssid: "CorpNet", signalPercent: 61 });
  });

  it("maps no_active_interface to None", () => {
    const { network } = computeDeviceStatus([
      ev(1, "network_interface_info", { status: "no_active_interface" }),
    ]);
    expect(network).toEqual({ type: "None" });
  });

  it("coerces string-serialized booleans and numbers", () => {
    const { power, network } = computeDeviceStatus([
      ev(1, "power_state_check", {
        onAcPower: "true",
        hasBattery: "true",
        batteryPercent: "73",
        isCharging: "false",
      }),
      ev(2, "wifi_signal_info", { wifiSsid: "CorpNet", wifiSignalPercent: "58" }),
      ev(3, "network_state_change", {
        changeType: "network_lost",
        after_connectionType: "None",
        hasNetwork: "false",
      }),
    ]);
    expect(power).toEqual({
      onAcPower: true,
      hasBattery: true,
      batteryPercent: 73,
      isCharging: false,
    });
    expect(network).toEqual({ type: "None" });
  });

  it("ignores events without a data payload", () => {
    expect(
      computeDeviceStatus([ev(1, "power_state_change"), ev(2, "network_state_change")]),
    ).toEqual({ power: null, network: null });
  });
});
