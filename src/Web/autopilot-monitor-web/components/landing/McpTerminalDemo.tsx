"use client";

import { useEffect, useRef, useState } from "react";

type Step =
  | { kind: "user"; text: string }
  | { kind: "tool"; text: string }
  | { kind: "out"; text: string; accent?: "success" | "warn" | "high" | "dim" }
  | { kind: "gap" };

/**
 * Scripted MCP session: the real power use case — one question, a full
 * enrollment debrief. Tool names and response fields mirror the actual
 * MCP server (get_session_summary, get_time_attribution).
 */
const SCRIPT: Step[] = [
  { kind: "user", text: "Analyze the enrollment of CONTOSO-4711 — full report." },
  { kind: "tool", text: 'get_session_summary({ sessionId: "…-4711" })' },
  { kind: "tool", text: 'get_time_attribution({ sessionId: "…-4711" })' },
  { kind: "gap" },
  { kind: "out", text: "Session report — CONTOSO-4711 · user-driven · ThinkPad X1 Carbon G12" },
  { kind: "out", text: "✔ Completed in 47 min — 18 min over the 7-day fleet average", accent: "success" },
  { kind: "gap" },
  { kind: "out", text: "Phases   Device Prep 4 min · Device Setup 39 min ⚠ · Account Setup 4 min" },
  { kind: "out", text: "Time     31 of 47 min spent in blocking app installs" },
  { kind: "out", text: "         top cost: Contoso VPN Client — 23 min (download stalled twice)", accent: "dim" },
  { kind: "out", text: "Gap      ~6 min unaccounted around the mid-ESP reboot", accent: "warn" },
  { kind: "out", text: "Issues   1 × HIGH — ANALYZE-ESP-001 · ESP Blocking App Timeout", accent: "high" },
  { kind: "gap" },
  { kind: "out", text: "Likely cause    Content download stall for Contoso VPN Client" },
  { kind: "out", text: "Recommendation  Make the app non-blocking, or pre-cache its", accent: "success" },
  { kind: "out", text: "                content with Delivery Optimization peers", accent: "success" },
];

const TYPE_MS = 32;
const TOOL_MS = 950;
const OUT_MS = 240;
const RESTART_PAUSE_MS = 7000;

export function McpTerminalDemo() {
  const ref = useRef<HTMLDivElement>(null);
  const [started, setStarted] = useState(false);
  const [reduced, setReduced] = useState(false);
  const [stepIndex, setStepIndex] = useState(0);
  const [charCount, setCharCount] = useState(0);

  useEffect(() => {
    if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
      setReduced(true);
      setStepIndex(SCRIPT.length);
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

    if (stepIndex >= SCRIPT.length) {
      const timer = window.setTimeout(() => {
        setStepIndex(0);
        setCharCount(0);
      }, RESTART_PAUSE_MS);
      return () => window.clearTimeout(timer);
    }

    const step = SCRIPT[stepIndex];
    if (step.kind === "user" && charCount < step.text.length) {
      const timer = window.setTimeout(() => setCharCount(c => c + 1), TYPE_MS);
      return () => window.clearTimeout(timer);
    }

    const delay = step.kind === "tool" ? TOOL_MS : step.kind === "user" ? 500 : OUT_MS;
    const timer = window.setTimeout(() => setStepIndex(i => i + 1), delay);
    return () => window.clearTimeout(timer);
  }, [started, reduced, stepIndex, charCount]);

  const finished = stepIndex >= SCRIPT.length;

  const renderStep = (step: Step, index: number) => {
    if (index > stepIndex) return null;
    const isCurrent = index === stepIndex;

    switch (step.kind) {
      case "user": {
        const text = isCurrent ? step.text.slice(0, charCount) : step.text;
        return (
          <div key={index} className="flex gap-2">
            <span className="text-[var(--lp-accent)] shrink-0">❯</span>
            <span className={`text-[var(--lp-term-ink)] ${isCurrent && !reduced ? "lp-cursor" : ""}`}>{text}</span>
          </div>
        );
      }
      case "tool": {
        const pending = isCurrent && !reduced;
        return (
          <div key={index} className="lp-event-in flex items-center gap-2 pl-4">
            <span className={pending ? "text-[var(--lp-term-faint)]" : "text-[var(--lp-accent)]"}>
              {pending ? "◌" : "✓"}
            </span>
            <span className="text-[var(--lp-term-faint)]">{step.text}</span>
          </div>
        );
      }
      case "gap":
        return <div key={index} className="h-2" />;
      case "out": {
        const color =
          step.accent === "success"
            ? "text-[#55c57f]"
            : step.accent === "warn"
              ? "text-[#f5a623]"
              : step.accent === "high"
                ? "text-[#ff9d66]"
                : step.accent === "dim"
                  ? "text-[var(--lp-term-faint)]"
                  : "text-[var(--lp-term-ink)]";
        return (
          <div key={index} className={`lp-event-in whitespace-pre-wrap ${color}`}>
            {step.text}
          </div>
        );
      }
    }
  };

  return (
    <div
      ref={ref}
      className="rounded-2xl border border-[var(--lp-term-line)] bg-[var(--lp-term-bg)] shadow-2xl shadow-black/20 overflow-hidden text-left"
    >
      <div className="flex items-center gap-2 px-4 py-2.5 border-b border-[var(--lp-term-line)]">
        <span className="w-2.5 h-2.5 rounded-full bg-[#f16057]" />
        <span className="w-2.5 h-2.5 rounded-full bg-[#f5bd4f]" />
        <span className="w-2.5 h-2.5 rounded-full bg-[#57c454]" />
        <span className="ml-2 text-[11px] text-[var(--lp-term-faint)] font-mono truncate">
          AI assistant — connected to Autopilot Monitor MCP
        </span>
      </div>
      <div className="px-4 sm:px-5 py-4 font-mono text-[11px] sm:text-[12.5px] leading-[1.7] h-[380px] sm:h-[400px] overflow-y-hidden overflow-x-auto">
        {SCRIPT.map(renderStep)}
        {finished && !reduced && (
          <div className="mt-2 flex gap-2">
            <span className="text-[var(--lp-accent)]">❯</span>
            <span className="lp-cursor" />
          </div>
        )}
      </div>
    </div>
  );
}
