"use client";

import { useEffect, useRef, useState } from "react";

/**
 * Mission-control hero: a fleet cockpit with several enrollments running
 * at once. Deterministic ~23s choreography driven by a single tick —
 * progress bars advance, sessions complete, a new one pops in, and the
 * analysis engine visibly flags a stuck device (the wow moment).
 * Event vocabulary and rule ID are real.
 */

const TICK_MS = 900;
const LOOP_TICKS = 26;
/** Frozen frame for prefers-reduced-motion: alert fired, one session completed, new session visible. */
const STATIC_TICK = 12;

interface FeedEvent {
  atTick: number;
  time: string;
  device: string;
  type: string;
  message: string;
  severity: "info" | "success" | "warning";
}

const FEED: FeedEvent[] = [
  { atTick: 0, time: "11:41:02", device: "CONTOSO-0912", type: "download_progress", message: "Microsoft 365 Apps — 64% · 8.2 MB/s", severity: "info" },
  { atTick: 1, time: "11:41:11", device: "PARIS-LT-207", type: "app_install_started", message: "Company Portal (blocking)", severity: "info" },
  { atTick: 2, time: "11:41:19", device: "MUNICH-LT-114", type: "esp_phase_changed", message: "Account Setup — apps", severity: "info" },
  { atTick: 3, time: "11:41:30", device: "CONTOSO-0912", type: "app_install_completed", message: "Company Portal — 2m 49s", severity: "success" },
  { atTick: 5, time: "11:41:48", device: "CONTOSO-4711", type: "error_detected", message: "Download stalled — Contoso VPN Client", severity: "warning" },
  { atTick: 6, time: "11:42:01", device: "MUNICH-LT-114", type: "enrollment_complete", message: "Enrollment completed — 19m 02s", severity: "success" },
  { atTick: 8, time: "11:42:22", device: "OSLO-NB-052", type: "phase_transition", message: "Device Preparation started", severity: "info" },
  { atTick: 10, time: "11:42:40", device: "CONTOSO-0912", type: "app_install_completed", message: "Microsoft 365 Apps — 5m 51s", severity: "success" },
  { atTick: 12, time: "11:42:58", device: "PARIS-LT-207", type: "download_progress", message: "Company Portal — 58% · 6.1 MB/s", severity: "info" },
  { atTick: 14, time: "11:43:12", device: "CONTOSO-0912", type: "completion_check", message: "All blocking apps installed", severity: "success" },
  { atTick: 15, time: "11:43:21", device: "CONTOSO-0912", type: "enrollment_complete", message: "Enrollment completed — 16m 40s", severity: "success" },
  { atTick: 17, time: "11:43:38", device: "OSLO-NB-052", type: "phase_transition", message: "Device Setup started", severity: "info" },
  { atTick: 19, time: "11:43:55", device: "OSLO-NB-052", type: "app_install_started", message: "Company Portal (blocking)", severity: "info" },
  { atTick: 21, time: "11:44:10", device: "PARIS-LT-207", type: "download_progress", message: "Company Portal — 82% · 7.4 MB/s", severity: "info" },
  { atTick: 23, time: "11:44:27", device: "CONTOSO-4711", type: "error_detected", message: "Still no progress — Contoso VPN Client", severity: "warning" },
];

interface SessionView {
  device: string;
  model: string;
  visible: boolean;
  isNew: boolean;
  phase: string;
  progress: number;
  state: "running" | "completed" | "stuck";
  completedLabel?: string;
}

function sessionsAt(t: number): SessionView[] {
  const clamp = (v: number, max = 100) => Math.min(v, max);
  return [
    {
      device: "CONTOSO-4711",
      model: "ThinkPad X1 Carbon G12",
      visible: true,
      isNew: false,
      phase: "Device Setup",
      progress: 87,
      state: t >= 5 ? "stuck" : "running",
    },
    {
      device: "CONTOSO-0912",
      model: "Surface Laptop 7",
      visible: true,
      isNew: false,
      phase: clamp(58 + 3 * t) >= 100 ? "Completed" : "Device Setup",
      progress: clamp(58 + 3 * t),
      state: clamp(58 + 3 * t) >= 100 ? "completed" : "running",
      completedLabel: "16m 40s",
    },
    {
      device: "MUNICH-LT-114",
      model: "ThinkPad T14s G5",
      visible: true,
      isNew: false,
      phase: clamp(88 + 2 * t) >= 100 ? "Completed" : "Account Setup",
      progress: clamp(88 + 2 * t),
      state: clamp(88 + 2 * t) >= 100 ? "completed" : "running",
      completedLabel: "19m 02s",
    },
    {
      device: "PARIS-LT-207",
      model: "EliteBook 840 G11",
      visible: true,
      isNew: false,
      phase: "Device Setup",
      progress: clamp(30 + 2 * t, 86),
      state: "running",
    },
    {
      device: "OSLO-NB-052",
      model: "Surface Pro 11",
      visible: t >= 8,
      isNew: t >= 8 && t <= 10,
      phase: t >= 17 ? "Device Setup" : "Device Preparation",
      progress: t >= 8 ? clamp(4 * (t - 8), 52) : 0,
      state: "running",
    },
  ];
}

export function HeroCockpit() {
  const ref = useRef<HTMLDivElement>(null);
  const [tick, setTick] = useState(0);
  const [running, setRunning] = useState(false);

  useEffect(() => {
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      setTick(STATIC_TICK);
      return;
    }
    const el = ref.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      entries => {
        if (entries.some(e => e.isIntersecting)) {
          setRunning(true);
          observer.disconnect();
        }
      },
      { threshold: 0.25 }
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    if (!running) return;
    const timer = window.setInterval(() => setTick(t => (t >= LOOP_TICKS ? 0 : t + 1)), TICK_MS);
    return () => window.clearInterval(timer);
  }, [running]);

  const sessions = sessionsAt(tick);
  const completions = sessions.filter(s => s.state === "completed").length;
  const activeNow = sessions.filter(s => s.visible && s.state !== "completed").length;
  const feed = FEED.filter(e => e.atTick <= tick).slice(-8);

  return (
    <div
      ref={ref}
      className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-surface)] shadow-2xl shadow-black/[0.09] overflow-hidden text-left"
    >
      {/* Chrome bar */}
      <div className="flex items-center gap-1.5 px-4 py-2.5 border-b border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)]">
        <span className="w-2.5 h-2.5 rounded-full bg-[#f16057]" />
        <span className="w-2.5 h-2.5 rounded-full bg-[#f5bd4f]" />
        <span className="w-2.5 h-2.5 rounded-full bg-[#57c454]" />
        <span className="ml-3 flex-1 max-w-[320px] truncate rounded-md bg-[var(--lp-surface)] border border-[var(--lp-line-soft)] px-3 py-1 text-[10px] font-mono text-[var(--lp-ink-faint)]">
          portal.autopilotmonitor.com/fleet
        </span>
        <span className="ml-auto flex items-center gap-1.5 text-[10px] font-semibold text-[var(--lp-accent-ink)]">
          <span className="w-1.5 h-1.5 rounded-full bg-[var(--lp-accent)] lp-live-dot" />
          LIVE
        </span>
      </div>

      {/* KPI strip */}
      <div className="grid grid-cols-2 sm:grid-cols-4 border-b border-[var(--lp-line-soft)] divide-x divide-[var(--lp-line-soft)]">
        {[
          { label: "Active now", value: String(activeNow), live: true },
          { label: "Success · 7 days", value: "94.6%" },
          { label: "Avg. duration", value: "21 min" },
          { label: "Completed today", value: String(213 + completions) },
        ].map(kpi => (
          <div key={kpi.label} className="px-4 py-2.5">
            <p className="text-[9px] uppercase tracking-[0.14em] text-[var(--lp-ink-faint)]">{kpi.label}</p>
            <p className="mt-0.5 text-lg font-bold tracking-tight text-[var(--lp-ink)] flex items-center gap-1.5">
              {kpi.value}
              {kpi.live && <span className="w-1.5 h-1.5 rounded-full bg-[var(--lp-accent)] lp-live-dot" />}
            </p>
          </div>
        ))}
      </div>

      {/* Board */}
      <div className="grid lg:grid-cols-[7fr_5fr] lg:divide-x divide-[var(--lp-line-soft)]">
        {/* Live enrollments */}
        <div className="min-w-0">
          <p className="px-4 pt-3 pb-1.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-[var(--lp-ink-faint)]">
            Live enrollments
          </p>
          <div className="h-[368px] overflow-hidden">
            {sessions.filter(s => s.visible).map(s => (
              <div
                key={s.device}
                className={`px-4 py-2.5 border-b border-[var(--lp-line-soft)] ${s.isNew ? "lp-event-in" : ""} ${
                  s.state === "stuck" ? "bg-[var(--lp-warn-soft)]" : ""
                }`}
              >
                <div className="flex items-center gap-2.5">
                  <span
                    className={`w-2 h-2 rounded-full shrink-0 ${
                      s.state === "completed"
                        ? "bg-[var(--lp-accent)]"
                        : s.state === "stuck"
                          ? "bg-[var(--lp-warn)]"
                          : "bg-[var(--lp-accent)] lp-live-dot"
                    }`}
                  />
                  <span className="text-[12.5px] font-semibold text-[var(--lp-ink)] shrink-0">{s.device}</span>
                  <span className="hidden sm:inline font-mono text-[10px] text-[var(--lp-ink-faint)] truncate">{s.model}</span>
                  <span className="ml-auto text-[11px] shrink-0">
                    {s.state === "completed" ? (
                      <span className="text-[var(--lp-accent-ink)] font-semibold">✓ Completed · {s.completedLabel}</span>
                    ) : s.state === "stuck" ? (
                      <span className="text-[var(--lp-warn)] font-semibold">{s.phase} · stalled</span>
                    ) : (
                      <span className="text-[var(--lp-ink-soft)]">{s.phase}</span>
                    )}
                  </span>
                </div>
                <div className="mt-1.5 h-1 rounded-full bg-[var(--lp-line-soft)] overflow-hidden">
                  <div
                    className={`h-full rounded-full transition-all duration-700 ease-linear ${
                      s.state === "stuck" ? "bg-[var(--lp-warn)]" : "bg-[var(--lp-accent)]"
                    }`}
                    style={{ width: `${s.progress}%` }}
                  />
                </div>
                {/* The wow moment: analysis names the problem, live */}
                {s.state === "stuck" && (
                  <div className="lp-event-in mt-2 flex flex-wrap items-center gap-x-2 gap-y-1 text-[11px]">
                    <span className="px-1.5 py-0.5 text-[9px] font-bold uppercase rounded bg-[#ff7a45] text-white">High</span>
                    <span className="font-mono text-[10px] text-[var(--lp-ink-faint)]">ANALYZE-ESP-001</span>
                    <span className="font-semibold text-[var(--lp-ink)]">ESP Blocking App Timeout</span>
                    <span className="text-[var(--lp-ink-soft)]">— Contoso VPN Client stalled at 87%</span>
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>

        {/* Event stream */}
        <div className="min-w-0 hidden lg:block">
          <p className="px-4 pt-3 pb-1.5 text-[10px] font-semibold uppercase tracking-[0.14em] text-[var(--lp-ink-faint)] flex items-center gap-1.5">
            Event stream
            <span className="w-1 h-1 rounded-full bg-[var(--lp-accent)] lp-live-dot" />
          </p>
          <div className="px-4 pb-3 h-[368px] overflow-hidden flex flex-col justify-end">
            <div className="space-y-1.5">
              {feed.map(e => (
                <div key={`${e.atTick}-${e.device}-${e.type}`} className="lp-event-in text-[11px] leading-[1.5]">
                  <div className="flex items-baseline gap-2">
                    <span className="font-mono text-[9px] text-[var(--lp-ink-faint)] shrink-0">{e.time}</span>
                    <span
                      className={`w-1.5 h-1.5 rounded-full shrink-0 translate-y-[-1px] ${
                        e.severity === "success" ? "bg-[var(--lp-accent)]" : e.severity === "warning" ? "bg-[var(--lp-warn)]" : "bg-[var(--lp-ink-faint)]"
                      }`}
                    />
                    <span className="font-semibold text-[var(--lp-ink)] shrink-0">{e.device}</span>
                    <span className="font-mono text-[9px] text-[var(--lp-ink-faint)] truncate">{e.type}</span>
                  </div>
                  <p className={`pl-[74px] truncate ${e.severity === "success" ? "text-[var(--lp-accent-ink)]" : e.severity === "warning" ? "text-[var(--lp-warn)]" : "text-[var(--lp-ink-soft)]"}`}>
                    {e.message}
                  </p>
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
