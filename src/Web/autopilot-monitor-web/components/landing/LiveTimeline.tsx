"use client";

import { useEffect, useRef, useState } from "react";

interface StreamEvent {
  time: string;
  type: string;
  message: string;
  severity: "info" | "success" | "warning";
  phase?: string;
}

/**
 * Scripted event stream for the hero visual. Types and messages mirror the
 * real event vocabulary (phase_transition, app_install_*, download_progress,
 * enrollment_complete) so the demo is faithful to the product.
 */
const EVENTS: StreamEvent[] = [
  { time: "09:02:14", type: "phase_transition", message: "Device Preparation started", severity: "info", phase: "Device Preparation" },
  { time: "09:02:31", type: "enrollment_type_detected", message: "User-driven Autopilot deployment", severity: "info" },
  { time: "09:03:05", type: "phase_transition", message: "Device Setup started", severity: "info", phase: "Device Setup" },
  { time: "09:03:22", type: "app_install_started", message: "Company Portal (blocking)", severity: "info" },
  { time: "09:04:48", type: "download_progress", message: "Company Portal — 64% · 8.2 MB/s", severity: "info" },
  { time: "09:06:11", type: "app_install_completed", message: "Company Portal — 2m 49s", severity: "success" },
  { time: "09:06:12", type: "app_install_started", message: "Defender for Endpoint (blocking)", severity: "info" },
  { time: "09:09:37", type: "app_install_completed", message: "Defender for Endpoint — 3m 25s", severity: "success" },
  { time: "09:12:03", type: "phase_transition", message: "Account Setup started", severity: "info", phase: "Account Setup" },
  { time: "09:14:29", type: "completion_check", message: "All blocking apps installed", severity: "success" },
  { time: "09:15:02", type: "enrollment_complete", message: "Enrollment completed — 12m 48s", severity: "success" },
];

const PHASES = ["Device Preparation", "Device Setup", "Account Setup"];

const TICK_MS = 1100;
const RESTART_PAUSE_MS = 5000;

export function LiveTimeline() {
  const ref = useRef<HTMLDivElement>(null);
  const [started, setStarted] = useState(false);
  const [reduced, setReduced] = useState(false);
  const [count, setCount] = useState(0);

  useEffect(() => {
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      setReduced(true);
      setCount(EVENTS.length);
      return;
    }
    const el = ref.current;
    if (!el) return;
    const observer = new IntersectionObserver(
      entries => {
        if (entries.some(e => e.isIntersecting)) {
          setStarted(true);
          observer.disconnect();
        }
      },
      { threshold: 0.3 }
    );
    observer.observe(el);
    return () => observer.disconnect();
  }, []);

  useEffect(() => {
    if (!started || reduced) return;
    const timer = window.setTimeout(
      () => setCount(c => (c >= EVENTS.length ? 0 : c + 1)),
      count >= EVENTS.length ? RESTART_PAUSE_MS : TICK_MS
    );
    return () => window.clearTimeout(timer);
  }, [started, reduced, count]);

  const visible = EVENTS.slice(0, count);
  const currentPhase = [...visible].reverse().find(e => e.phase)?.phase;
  const phaseIndex = currentPhase ? PHASES.indexOf(currentPhase) : -1;
  const done = count >= EVENTS.length;

  return (
    <div
      ref={ref}
      className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-surface)] shadow-2xl shadow-black/[0.07] overflow-hidden text-left"
    >
      {/* Window header */}
      <div className="flex items-center justify-between px-4 py-2.5 border-b border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)]">
        <div className="flex items-center gap-2 min-w-0">
          <span className={`w-2 h-2 rounded-full shrink-0 ${done ? "bg-[var(--lp-accent)]" : "bg-[var(--lp-accent)] lp-live-dot"}`} />
          <span className="text-xs font-semibold text-[var(--lp-ink)] truncate">CONTOSO-4711</span>
          <span className="hidden sm:inline text-[10px] text-[var(--lp-ink-faint)] font-mono truncate">Lenovo ThinkPad X1 Carbon G12</span>
        </div>
        <span className={`text-[10px] font-semibold px-2 py-0.5 rounded-full ${done ? "text-[var(--lp-accent-ink)] bg-[var(--lp-accent-soft)]" : "text-[var(--lp-ink-soft)] bg-[var(--lp-surface)] border border-[var(--lp-line)]"}`}>
          {done ? "Completed" : "Enrolling"}
        </span>
      </div>

      {/* Phase rail */}
      <div className="flex items-center gap-2 px-4 py-3 border-b border-[var(--lp-line-soft)]">
        {PHASES.map((phase, i) => {
          const reached = i <= phaseIndex || done;
          const active = i === phaseIndex && !done;
          return (
            <div key={phase} className="flex items-center gap-2 flex-1 min-w-0">
              <span
                className={`w-4 h-4 rounded-full flex items-center justify-center shrink-0 transition-colors duration-500 ${
                  reached ? "bg-[var(--lp-accent)]" : "bg-[var(--lp-surface-2)] border border-[var(--lp-line)]"
                }`}
              >
                {reached && !active && (
                  <svg className="w-2.5 h-2.5 text-white" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3.5}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
                  </svg>
                )}
                {active && <span className="w-1.5 h-1.5 rounded-full bg-white" />}
              </span>
              <span className={`text-[11px] font-medium truncate transition-colors duration-500 ${reached ? "text-[var(--lp-ink)]" : "text-[var(--lp-ink-faint)]"}`}>
                {phase}
              </span>
              {i < PHASES.length - 1 && <span className="hidden sm:block flex-1 h-px bg-[var(--lp-line)]" />}
            </div>
          );
        })}
      </div>

      {/* Event stream */}
      <div className="px-4 py-3 h-[248px] overflow-hidden flex flex-col justify-end">
        <div className="space-y-1.5">
          {visible.slice(-8).map(event => (
            <div key={`${event.time}-${event.type}`} className="lp-event-in flex items-baseline gap-2.5 text-[12px] leading-5">
              <span className="font-mono text-[10px] text-[var(--lp-ink-faint)] shrink-0">{event.time}</span>
              <span
                className={`w-1.5 h-1.5 rounded-full shrink-0 translate-y-[-1px] ${
                  event.severity === "success" ? "bg-[var(--lp-accent)]" : event.severity === "warning" ? "bg-[var(--lp-warn)]" : "bg-[var(--lp-ink-faint)]"
                }`}
              />
              <span className="font-mono text-[10px] text-[var(--lp-ink-faint)] shrink-0 hidden sm:inline w-44 truncate">{event.type}</span>
              <span className={`truncate ${event.severity === "success" ? "text-[var(--lp-accent-ink)] font-medium" : "text-[var(--lp-ink-soft)]"}`}>
                {event.message}
              </span>
            </div>
          ))}
          {!done && started && (
            <div className="flex items-center gap-2.5 text-[12px] leading-5">
              <span className="font-mono text-[10px] text-transparent select-none shrink-0">00:00:00</span>
              <span className="lp-cursor" />
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
