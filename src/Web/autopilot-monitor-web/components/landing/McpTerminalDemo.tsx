"use client";

import { useEffect, useRef, useState } from "react";
import { useMediaQuery } from "@/hooks/useMediaQuery";

type Step =
  | { kind: "user"; text: string }
  | { kind: "tool"; text: string }
  | { kind: "out"; text: string; accent?: "success" | "warn" | "high" | "dim" }
  | { kind: "gap" };

/**
 * Scripted MCP session, based on a real production analysis (anonymized):
 * a failed first attempt whose ESP HRESULT decodes to an unmapped
 * installer exit code — the kind of root cause that costs a human an
 * afternoon. Tool names mirror the actual MCP server.
 */
const SCRIPT: Step[] = [
  { kind: "user", text: "CONTOSO-3812 failed enrollment this morning — what happened?" },
  { kind: "tool", text: 'search_sessions({ device: "CONTOSO-3812" })' },
  { kind: "tool", text: 'get_session_summary({ sessionId: "e6a1…f43b" })' },
  { kind: "tool", text: 'get_session_summary({ sessionId: "98db…16af" })' },
  { kind: "gap" },
  { kind: "out", text: "Two attempts on Jul 29 — first failed, the retry succeeded:" },
  { kind: "out", text: "  #1  11:05–11:17  ✗ esp_terminal_failure (0x8007EA61) · 7/8 apps", accent: "high" },
  { kind: "out", text: "  #2  12:05–12:20  ✓ Succeeded · 8/8 apps · same network, same package", accent: "success" },
  { kind: "gap" },
  { kind: "out", text: "Root cause — Contoso CRM App:" },
  { kind: "out", text: '  11:14:58  IME: "Admin did NOT set mapping for lpExitCode: 60001"', accent: "dim" },
  { kind: "out", text: "  ESP HRESULT 0x8007EA61 = Win32 error 0xEA61 = 60001 decimal — the ESP", accent: "warn" },
  { kind: "out", text: "  escalated the unmapped installer exit code to a terminal failure.", accent: "warn" },
  { kind: "out", text: "  60001 is PSADT's generic unhandled-exception code. It's transient:", accent: "dim" },
  { kind: "out", text: "  the same package installed cleanly in 75 s on the retry.", accent: "dim" },
  { kind: "gap" },
  { kind: "out", text: "Recommendation  Map exit code 60001 in the Win32 app — better, fix the", accent: "success" },
  { kind: "out", text: "                PSADT package to log and handle the underlying error.", accent: "success" },
  { kind: "gap" },
  { kind: "out", text: "Want me to pull the diagnostics ZIP and extract the installer log?" },
];

const TYPE_MS = 32;
const TOOL_MS = 950;
const OUT_MS = 240;
const RESTART_PAUSE_MS = 7000;

export function McpTerminalDemo() {
  const ref = useRef<HTMLDivElement>(null);
  const [started, setStarted] = useState(false);
  const reduced = useMediaQuery("(prefers-reduced-motion: reduce)");
  const [stepIndex, setStepIndex] = useState(0);
  const [charCount, setCharCount] = useState(0);
  // Reduced motion shows the finished transcript immediately — derived, not seeded.
  const effectiveStepIndex = reduced ? SCRIPT.length : stepIndex;

  useEffect(() => {
    if (reduced) return;
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
  }, [reduced]);

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

  const finished = effectiveStepIndex >= SCRIPT.length;

  const renderStep = (step: Step, index: number) => {
    if (index > effectiveStepIndex) return null;
    const isCurrent = index === effectiveStepIndex;

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
      <div className="px-4 sm:px-5 py-4 font-mono text-[11px] sm:text-[12.5px] leading-[1.7] h-[430px] sm:h-[460px] overflow-y-hidden overflow-x-auto">
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
