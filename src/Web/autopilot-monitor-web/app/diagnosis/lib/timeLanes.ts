import type { DiagnosisCulprit, SessionTimeBreakdown } from "@/lib/generated/wire-types.generated";
import { formatDuration } from "@/lib/formatting";
import { shortTime } from "./diagnosisFormat";

export type BarTone = "done" | "failed" | "signin" | "asleep" | "restart" | "update" | "app" | "appFailed";

export interface LaneBar {
  /** Position and width in percent of the observed window. */
  left: number;
  width: number;
  tone: BarTone;
  label?: string;
  title: string;
}

export interface Lane {
  key: string;
  label: string;
  /** The lane of a culprit app. */
  emphasis: boolean;
  bars: LaneBar[];
}

export interface LaneMarker {
  left: number;
  label: string;
  /** Where the label sits against its line, so a label at an edge stays inside the chart. */
  align: "start" | "center" | "end";
}

export interface TimeLanes {
  lanes: Lane[];
  /** `minor` ticks are left out on narrow screens. */
  ticks: { left: number; label: string; minor: boolean }[];
  /** The failure lines in time order. */
  markers: LaneMarker[];
}

const PHASES: Record<string, { label: string; category: string }> = {
  device_prep: { label: "Device preparation", category: "DevicePreparation" },
  esp_apps: { label: "Device setup", category: "DeviceSetup" },
  user_esp: { label: "Account setup", category: "AccountSetup" },
};

const SIGN_IN: Record<string, string> = {
  identity_hello: "Sign-in and Hello",
  awaiting_sign_in: "Waiting for sign-in",
};

/**
 * "Where the time went" from the pre-computed breakdown: one lane for the ESP phases, one for sign-in,
 * one per ESP-blocking app (culprits first), and one for the device (sleep, restarts, updates). Null
 * without a breakdown or without a measurable window.
 */
export function timeLanes(
  breakdown: SessionTimeBreakdown | null | undefined,
  opts: {
    failedCategory?: string | null;
    /** The failure line: when, and what it is called ("ESP failed", "Failed"). */
    failure?: { at: string; label: string } | null;
    culprits?: DiagnosisCulprit[] | null;
    maxApps?: number;
  }
): TimeLanes | null {
  if (!breakdown || breakdown.segments.length === 0) return null;
  const spans = [
    ...breakdown.segments,
    ...breakdown.blockingApps,
    ...breakdown.sleepSpans,
    ...breakdown.rebootSpans,
    ...breakdown.osUpdates,
  ];
  const start = Math.min(...spans.map((s) => Date.parse(s.startUtc)));
  const end = Math.max(...spans.map((s) => Date.parse(s.endUtc)));
  if (!Number.isFinite(start) || !Number.isFinite(end) || end <= start) return null;

  const pct = (iso: string) => Math.min(100, Math.max(0, ((Date.parse(iso) - start) / (end - start)) * 100));
  const bar = (s: { startUtc: string; endUtc: string; seconds: number }, tone: BarTone, name: string, label?: string): LaneBar => {
    const left = pct(s.startUtc);
    const width = Math.max(0.5, pct(s.endUtc) - left);
    return { left: Math.min(left, 100 - width), width, tone, label, title: `${name} · ${formatDuration(s.seconds)}` };
  };

  const lanes: Lane[] = [];
  const phaseBars = breakdown.segments
    .filter((s) => PHASES[s.segmentKey])
    .map((s) => {
      const phase = PHASES[s.segmentKey];
      return bar(s, phase.category === opts.failedCategory ? "failed" : "done", phase.label, phase.label);
    });
  if (phaseBars.length > 0) lanes.push({ key: "esp", label: "ESP", emphasis: false, bars: phaseBars });

  const signInBars = breakdown.segments.filter((s) => SIGN_IN[s.segmentKey]).map((s) => bar(s, "signin", SIGN_IN[s.segmentKey], formatDuration(s.seconds)));
  if (signInBars.length > 0) lanes.push({ key: "sign-in", label: "Sign-in and Hello", emphasis: false, bars: signInBars });

  const isCulprit = (appId: string, appName: string) =>
    (opts.culprits ?? []).some((c) => c.kind === "app" && ((c.key && c.key.toLowerCase() === appId.toLowerCase()) || c.name === appName));
  const byApp = new Map<string, { name: string; culprit: boolean; first: number; intervals: typeof breakdown.blockingApps }>();
  for (const interval of breakdown.blockingApps) {
    const key = interval.appId || interval.appName;
    const entry = byApp.get(key) ?? { name: interval.appName, culprit: isCulprit(interval.appId, interval.appName), first: Date.parse(interval.startUtc), intervals: [] };
    entry.intervals.push(interval);
    entry.first = Math.min(entry.first, Date.parse(interval.startUtc));
    byApp.set(key, entry);
  }
  [...byApp.entries()]
    .sort(([, a], [, b]) => Number(b.culprit) - Number(a.culprit) || a.first - b.first)
    .slice(0, opts.maxApps ?? 6)
    .sort(([, a], [, b]) => a.first - b.first)
    .forEach(([key, app]) =>
      lanes.push({
        key: `app:${key}`,
        label: app.name,
        emphasis: app.culprit,
        bars: app.intervals.map((i) => bar(i, app.culprit ? "appFailed" : "app", app.name)),
      })
    );

  const deviceBars = [
    ...breakdown.sleepSpans.map((s) => bar(s, "asleep", "Asleep", "Asleep")),
    ...breakdown.rebootSpans.map((s) => bar(s, "restart", "Restart", "Restart")),
    ...breakdown.osUpdates.map((s) => bar(s, "update", s.kbs.length > 0 ? `Windows update ${s.kbs.join(", ")}` : "Windows update", "Update")),
  ];
  if (deviceBars.length > 0) lanes.push({ key: "device", label: "Device", emphasis: false, bars: deviceBars });

  const ticks = [0, 1, 2, 3, 4].map((i) => {
    const ms = start + ((end - start) * i) / 4;
    return { left: i * 25, label: shortTime(new Date(ms).toISOString()), minor: i % 2 === 1 };
  });

  const lines: { left: number; label: string }[] = [];
  const appFailedAt = (opts.culprits ?? []).find((c) => c.kind === "app" && c.failedAt)?.failedAt;
  if (appFailedAt) lines.push({ left: pct(appFailedAt), label: `App failed ${shortTime(appFailedAt)}` });
  if (opts.failure) lines.push({ left: pct(opts.failure.at), label: `${opts.failure.label} ${shortTime(opts.failure.at)}` });
  const markers: LaneMarker[] = lines
    .sort((a, b) => a.left - b.left)
    .map((line) => ({ ...line, align: line.left < 15 ? "start" : line.left > 85 ? "end" : "center" }));

  return { lanes, ticks, markers };
}
