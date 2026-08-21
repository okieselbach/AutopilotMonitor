import { EnrollmentEvent } from "@/types";

export interface DevicePowerStatus {
  onAcPower: boolean;
  hasBattery: boolean;
  /** null when the agent reported the literal "unknown". */
  batteryPercent: number | null;
  isCharging: boolean;
}

export type NetworkKind = "WiFi" | "Ethernet" | "None";

export interface DeviceNetworkStatus {
  type: NetworkKind;
  ssid?: string;
  signalPercent?: number;
}

export interface DeviceStatus {
  power: DevicePowerStatus | null;
  network: DeviceNetworkStatus | null;
}

// Some agent paths serialize payload values as strings, so every read is coerced.
function asBool(value: unknown): boolean {
  return value === true || value === "true" || value === "True";
}

function asIntOrNull(value: unknown): number | null {
  if (typeof value === "number" && Number.isFinite(value)) return Math.round(value);
  if (typeof value === "string") {
    const parsed = parseInt(value, 10);
    if (!isNaN(parsed)) return parsed;
  }
  return null;
}

// Non-empty string, filtering the agent's "n/a"/"None" placeholder sentinels.
function asStr(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const trimmed = value.trim();
  if (!trimmed || trimmed === "n/a" || trimmed === "None") return undefined;
  return trimmed;
}

function asNetworkKind(value: unknown): NetworkKind {
  return value === "WiFi" || value === "Ethernet" ? value : "None";
}

// Snapshot of the WiFi details worth carrying across events that don't restate
// them. Extracted as a typed helper — inlining these reads into the `network`
// assignments makes TS's control-flow analysis circular (TS7022).
function wifiCarry(prev: DeviceNetworkStatus | null): { ssid?: string; signalPercent?: number } {
  return prev !== null && prev.type === "WiFi"
    ? { ssid: prev.ssid, signalPercent: prev.signalPercent }
    : {};
}

/**
 * Latest-known device power + network state, folded from the raw event stream
 * (already merged by eventId and sorted by sequence — later events win).
 *
 * Sources: power_state_check (once per agent start), power_state_change (live
 * watcher, battery devices only), network_interface_info (start + enrollment
 * end), network_state_change (live), wifi_signal_info (WiFi NICs).
 * Sessions from agents without these events yield { power: null, network: null }.
 */
export function computeDeviceStatus(events: EnrollmentEvent[]): DeviceStatus {
  let power: DevicePowerStatus | null = null;
  let network: DeviceNetworkStatus | null = null;

  for (const evt of events) {
    const d = evt.data;
    if (!d) continue;

    switch (evt.eventType) {
      case "power_state_check": {
        // A failed probe carries defaults, not truth — keep the previous state.
        if (d.error !== undefined && d.error !== null) break;
        power = {
          onAcPower: asBool(d.onAcPower),
          hasBattery: asBool(d.hasBattery),
          batteryPercent: asIntOrNull(d.batteryPercent),
          isCharging: asBool(d.isCharging),
        };
        break;
      }
      case "power_state_change": {
        // Emitted only on devices with a battery; the payload carries no hasBattery.
        power = {
          onAcPower: asBool(d.onAcPower),
          hasBattery: true,
          batteryPercent: asIntOrNull(d.batteryPercent),
          isCharging: asBool(d.isCharging),
        };
        break;
      }
      case "network_interface_info": {
        if (d.status === "no_active_interface") {
          network = { type: "None" };
          break;
        }
        const type = asNetworkKind(d.connectionType);
        if (type === "None") break; // malformed/legacy payload — keep previous state
        if (type === "WiFi") {
          // This event never carries an SSID; keep the one wifi_signal_info gave us
          // so the enrollment-end re-emit doesn't blank the chip.
          const prior = wifiCarry(network);
          network = { type, ssid: prior.ssid, signalPercent: prior.signalPercent };
        } else {
          network = { type };
        }
        break;
      }
      case "network_state_change": {
        if (d.hasNetwork === false || d.hasNetwork === "false" || d.hasNetwork === "False") {
          network = { type: "None" };
          break;
        }
        const type = asNetworkKind(d.after_connectionType);
        if (type === "WiFi") {
          const prior = wifiCarry(network);
          const ssid = asStr(d.after_wifiSsid) ?? prior.ssid;
          network = {
            type,
            ssid,
            // A signal reading only stays meaningful on the same network.
            signalPercent: prior.ssid === ssid ? prior.signalPercent : undefined,
          };
        } else {
          network = { type };
        }
        break;
      }
      case "wifi_signal_info": {
        // Existence of this event implies the active NIC is WiFi.
        const prior = wifiCarry(network);
        network = {
          type: "WiFi",
          ssid: asStr(d.wifiSsid) ?? prior.ssid,
          signalPercent: asIntOrNull(d.wifiSignalPercent) ?? prior.signalPercent,
        };
        break;
      }
    }
  }

  return { power, network };
}
