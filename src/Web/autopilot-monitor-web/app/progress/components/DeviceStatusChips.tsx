"use client";

import { DeviceStatus, DeviceNetworkStatus, DevicePowerStatus } from "../hooks/deviceStatus";

/**
 * Taskbar-style power/network status chips for the progress page header.
 *
 * Values are "last reported by the agent", not continuously sampled: power
 * events ride the normal batched upload cadence (only warning-level
 * transitions upload immediately), so AC/battery flips can lag by tens of
 * seconds — and an agent that dies cannot report going offline.
 */

function BatteryIcon({ percent, low }: { percent: number | null; low: boolean }) {
  // Fill width scales into the 13-unit body (x 4..17); null percent renders outline only.
  const fillWidth = percent === null ? 0 : Math.max(0, Math.min(13, (13 * percent) / 100));
  return (
    <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
      <rect x="2.5" y="7.5" width="16" height="9" rx="2" />
      <path strokeLinecap="round" d="M21 10.5v3" />
      {fillWidth > 0 && (
        <rect
          x="4.5"
          y="9.5"
          width={fillWidth}
          height="5"
          rx="0.5"
          fill="currentColor"
          stroke="none"
          className={low ? "text-red-600" : ""}
        />
      )}
    </svg>
  );
}

function BoltIcon() {
  return (
    <svg className="w-3 h-3" fill="currentColor" viewBox="0 0 24 24" stroke="none">
      <path d="M13 10V3L4 14h7v7l9-11h-7z" />
    </svg>
  );
}

function WifiIcon({ signalPercent, offline }: { signalPercent?: number; offline?: boolean }) {
  // Arc tiers mirror the taskbar: unknown signal renders as fully connected.
  const tier = offline ? 0 : signalPercent === undefined ? 3 : signalPercent >= 66 ? 3 : signalPercent >= 33 ? 2 : signalPercent > 0 ? 1 : 0;
  return (
    <svg
      className="w-4 h-4"
      fill="none"
      viewBox="0 0 24 24"
      stroke="currentColor"
      strokeWidth={1.5}
      strokeLinecap="round"
    >
      <path d="M2.25 8.51a13.5 13.5 0 0 1 19.5 0" className={tier >= 3 ? "" : "opacity-25"} />
      <path d="M5.13 11.7a9.38 9.38 0 0 1 13.74 0" className={tier >= 2 ? "" : "opacity-25"} />
      <path d="M8.01 14.89a5.25 5.25 0 0 1 7.98 0" className={tier >= 1 ? "" : "opacity-25"} />
      <path d="M12 18.75h.008v.008H12z" strokeWidth={2.5} />
      {offline && <path d="M4 4l16 16" />}
    </svg>
  );
}

function EthernetIcon() {
  return (
    <svg
      className="w-4 h-4"
      fill="none"
      viewBox="0 0 24 24"
      stroke="currentColor"
      strokeWidth={1.5}
      strokeLinecap="round"
      strokeLinejoin="round"
    >
      <path d="M4 9h16v8a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V9z" />
      <path d="M8 9V6h8v3" />
      <path d="M7.5 12.5v2M10.5 12.5v2M13.5 12.5v2M16.5 12.5v2" />
    </svg>
  );
}

function chipClass(danger?: boolean) {
  return `inline-flex items-center gap-1 rounded-full bg-white/70 border border-black/5 px-2 py-0.5 text-xs ${
    danger ? "text-red-600" : "text-gray-600"
  }`;
}

function PowerChip({ power }: { power: DevicePowerStatus }) {
  // Windows-tray parity: desktops/VMs without a battery show no power chip.
  if (!power.hasBattery) return null;
  const pct = power.batteryPercent;
  const low = pct !== null && pct < 20 && !power.onAcPower;
  return (
    <span
      className={chipClass(low)}
      title={
        pct === null
          ? "Battery level unknown"
          : power.onAcPower
          ? `On AC power${power.isCharging ? ", charging" : ""}`
          : "On battery"
      }
    >
      <BatteryIcon percent={pct} low={low} />
      {power.onAcPower && <BoltIcon />}
      {pct !== null && <span className="tabular-nums">{pct}%</span>}
    </span>
  );
}

function NetworkChip({ network }: { network: DeviceNetworkStatus }) {
  if (network.type === "None") {
    return (
      <span className={chipClass(true)} title="No network connection reported">
        <WifiIcon offline />
        <span>Offline</span>
      </span>
    );
  }
  if (network.type === "Ethernet") {
    return (
      <span className={chipClass()} title="Ethernet">
        <EthernetIcon />
        <span>Ethernet</span>
      </span>
    );
  }
  return (
    <span className={chipClass()} title={network.ssid ? `WiFi: ${network.ssid}` : "WiFi"}>
      <WifiIcon signalPercent={network.signalPercent} />
      <span>WiFi</span>
      {network.signalPercent !== undefined && (
        <span className="tabular-nums text-gray-400">{network.signalPercent}%</span>
      )}
    </span>
  );
}

export function DeviceStatusChips({ status }: { status: DeviceStatus }) {
  const showPower = status.power !== null && status.power.hasBattery;
  // Old agents emit none of the source events — render nothing, not an empty row.
  if (!showPower && status.network === null) return null;
  return (
    <div className="mt-2 flex items-center justify-center gap-2">
      {status.network !== null && <NetworkChip network={status.network} />}
      {status.power !== null && <PowerChip power={status.power} />}
    </div>
  );
}
