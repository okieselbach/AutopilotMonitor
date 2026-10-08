import type { TraceRecord } from "./pushCore";

/** "just now", "5m ago", "3h ago", "2d ago"; falls back to the date for anything older than a week. */
export function formatRelativeTime(iso: string, nowMs: number): string {
  const time = Date.parse(iso);
  if (Number.isNaN(time)) return "";
  const diffMinutes = Math.floor((nowMs - time) / 60_000);
  if (diffMinutes < 1) return "just now";
  if (diffMinutes < 60) return `${diffMinutes}m ago`;
  const diffHours = Math.floor(diffMinutes / 60);
  if (diffHours < 24) return `${diffHours}h ago`;
  const diffDays = Math.floor(diffHours / 24);
  if (diffDays < 7) return `${diffDays}d ago`;
  return new Date(time).toLocaleDateString();
}

export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return "—";
  const time = Date.parse(iso);
  return Number.isNaN(time) ? "—" : new Date(time).toLocaleString();
}

export function describeDeviceStatus(status: string): string {
  switch (status) {
    case "Active":
      return "Alerts are delivered to this device.";
    case "Pending":
      return "Waiting for the confirmation on your computer.";
    case "Paused":
      return "Paused: the owner has not signed in to the portal for a while. A portal sign-in resumes delivery.";
    case "Stale":
      return "The push service refused the last delivery. Opening this app repairs the subscription.";
    default:
      return "";
  }
}

/** One worker trace record as one line: when, what, and the fields that explain the outcome. */
export function formatTraceRecord(record: TraceRecord): string {
  const parts = [formatDateTime(record.at), record.event];
  for (const key of ["source", "type", "result"]) {
    if (record[key]) parts.push(record[key]);
  }
  if (record.shown && record.shown !== "shown") parts.push(`display ${record.shown}`);
  if (record.id) parts.push(`#${record.id.slice(0, 8)}`);
  return parts.join(" · ");
}
