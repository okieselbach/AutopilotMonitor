"use client";

import { useEffect, useRef, useState } from "react";
import { useInViewActive } from "@/hooks/useInViewActive";
import { useMediaQuery } from "@/hooks/useMediaQuery";
import { ArrowRightIcon } from "./icons";

const QUESTIONS = [
  "Why did CONTOSO-3812 fail this morning?",
  "Which app keeps device setup waiting the longest?",
  "How many devices enrolled right the first time last month?",
  "Which CVEs on new devices should we fix first?",
  "Write this week's enrollment report for the change board.",
];

const HOLD_MS = 2800;
const ERASE_MS = 16;
const TYPE_MS = 34;
const NEXT_MS = 260;

type Phase = "hold" | "erase" | "type";

/**
 * The hero's "ask" bar: cycles through example questions with a typing effect and links to the
 * gallery. The first question is prerendered and stays put under reduced motion; timers run only
 * while the bar is on screen in a visible tab.
 */
export function AskBar() {
  const ref = useRef<HTMLAnchorElement>(null);
  const reduced = useMediaQuery("(prefers-reduced-motion: reduce)");
  const active = useInViewActive(ref, { startThreshold: 0.5, disabled: reduced });
  const [index, setIndex] = useState(0);
  const [chars, setChars] = useState(QUESTIONS[0].length);
  const [phase, setPhase] = useState<Phase>("hold");

  useEffect(() => {
    if (!active) return;
    let timer: number;
    if (phase === "hold") {
      timer = window.setTimeout(() => setPhase("erase"), HOLD_MS);
    } else if (phase === "erase") {
      timer =
        chars > 0
          ? window.setTimeout(() => setChars(c => Math.max(0, c - 3)), ERASE_MS)
          : window.setTimeout(() => {
              setIndex(i => (i + 1) % QUESTIONS.length);
              setPhase("type");
            }, NEXT_MS);
    } else {
      timer =
        chars < QUESTIONS[index].length
          ? window.setTimeout(() => setChars(c => c + 1), TYPE_MS)
          : window.setTimeout(() => setPhase("hold"), 0);
    }
    return () => window.clearTimeout(timer);
  }, [active, phase, chars, index]);

  return (
    <a
      ref={ref}
      href="#questions"
      data-track="hero_ask_bar"
      aria-label="See example questions and answers"
      className="flex items-center gap-3.5 rounded-[18px] border border-[var(--lp-line)] bg-[var(--lp-surface)] py-3.5 pl-5 pr-3.5 text-[var(--lp-ink)] shadow-[0_22px_44px_-22px_rgba(24,24,27,0.22),0_1px_2px_rgba(24,24,27,0.04)] transition-[border-color,box-shadow] hover:border-[var(--lp-accent-line)] hover:shadow-[0_24px_48px_-22px_rgba(30,138,76,0.32)]"
    >
      <span aria-hidden="true" className="shrink-0 font-mono text-lg text-[var(--lp-accent)]">
        ❯
      </span>
      <span aria-hidden="true" className="min-w-0 flex-1 truncate text-left text-[15px] font-medium sm:text-[17px]">
        {QUESTIONS[index].slice(0, chars)}
        <span className="lp-cursor" />
      </span>
      <span
        aria-hidden="true"
        className="inline-flex shrink-0 items-center gap-1.5 rounded-[10px] bg-[var(--lp-accent-soft)] px-3 py-2 text-[13px] font-semibold text-[var(--lp-accent-ink)]"
      >
        <span className="hidden sm:inline">See answers</span>
        <ArrowRightIcon />
      </span>
    </a>
  );
}
