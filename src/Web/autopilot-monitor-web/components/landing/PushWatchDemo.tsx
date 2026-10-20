"use client";

import { useEffect, useRef, useState } from "react";
import { useInViewActive } from "@/hooks/useInViewActive";
import { useMediaQuery } from "@/hooks/useMediaQuery";
import { BrandMark } from "../BrandMark";
import { BellIcon, LockIcon } from "../ai/icons";
import { PhoneFrame } from "./PhoneFrame";

type Cursor = "away" | "over" | "press" | "gone";

interface Frame {
  ms: number;
  /** Apps installed, out of TOTAL_APPS. */
  apps: number;
  cursor: Cursor;
  watching: boolean;
  done: boolean;
  notified: boolean;
  /** The stage fades out before the loop starts over. */
  fade: boolean;
}

const TOTAL_APPS = 12;
const DEVICE = "CONTOSO-4711";
// The real watch push (PushSessionWatchService): title plus device name with the masked serial.
const ALERT_TITLE = "Enrollment finished";
const ALERT_BODY = `${DEVICE} (…8F2K) finished enrollment.`;
const CAPTION = `Animation: in the portal, a click on Notify me when done switches the running enrollment ${DEVICE} to Watching. When it completes, a locked phone shows a notification from AM Alerts: ${ALERT_TITLE}. ${ALERT_BODY} End-to-end encrypted.`;
const MAX_LOOPS = 3;

/** One loop of about ten seconds: watch a running enrollment, let it finish, get the push. */
export const FRAMES: readonly Frame[] = [
  { ms: 1200, apps: 9, cursor: "away", watching: false, done: false, notified: false, fade: false },
  { ms: 900, apps: 9, cursor: "over", watching: false, done: false, notified: false, fade: false },
  { ms: 250, apps: 9, cursor: "press", watching: false, done: false, notified: false, fade: false },
  { ms: 900, apps: 9, cursor: "gone", watching: true, done: false, notified: false, fade: false },
  { ms: 700, apps: 10, cursor: "gone", watching: true, done: false, notified: false, fade: false },
  { ms: 700, apps: 11, cursor: "gone", watching: true, done: false, notified: false, fade: false },
  { ms: 900, apps: 12, cursor: "gone", watching: true, done: true, notified: false, fade: false },
  { ms: 3800, apps: 12, cursor: "gone", watching: true, done: true, notified: true, fade: false },
  { ms: 500, apps: 12, cursor: "gone", watching: true, done: true, notified: true, fade: true },
];

/** Reduced motion shows this frame for good, and the last loop comes to rest on it. */
export const STILL_FRAME = 7;

const CURSOR_CLASS: Record<Cursor, string> = {
  away: "translate-x-10 translate-y-8 opacity-0 duration-700",
  over: "opacity-100 duration-700",
  press: "scale-90 opacity-100 duration-150",
  gone: "opacity-0 duration-300",
};

const FADE = "transition-opacity duration-300 motion-reduce:transition-none";
const PILL =
  "col-start-1 row-start-1 inline-flex items-center gap-1.5 justify-self-start whitespace-nowrap rounded-full border px-3 py-1 text-[12px] font-medium transition duration-300 motion-reduce:transition-none";

/** The portal's Notify me when done button: idle, pressed, Watching, gone once the session ends. */
function WatchPill({ frame }: { frame: Frame }) {
  const idle = !frame.watching && !frame.done;
  const watching = frame.watching && !frame.done;
  const pressed = frame.cursor === "press" ? "scale-[0.97] ring-2 ring-[var(--lp-accent-line)]" : "";
  return (
    <span className="relative inline-grid">
      <span className={`${PILL} border-[var(--lp-line)] bg-[var(--lp-surface)] text-[var(--lp-ink-soft)] ${idle ? "opacity-100" : "opacity-0"} ${pressed}`}>
        <BellIcon className="h-3.5 w-3.5 shrink-0" />
        Notify me when done
      </span>
      <span className={`${PILL} border-[var(--lp-accent-line)] bg-[var(--lp-accent-soft)] text-[var(--lp-accent-ink)] ${watching ? "opacity-100" : "opacity-0"}`}>
        <BellIcon className="h-3.5 w-3.5 shrink-0" />
        Watching
      </span>
      <svg
        className={`absolute left-[70%] top-[55%] h-5 w-5 origin-top-left text-[var(--lp-ink)] transition ease-out motion-reduce:transition-none ${CURSOR_CLASS[frame.cursor]}`}
        viewBox="0 0 20 20"
        aria-hidden="true"
      >
        <path
          d="M3 1.5v14l3.8-3.5 2.5 5.6 2.3-1-2.5-5.4h5.1z"
          fill="currentColor"
          className="stroke-[var(--lp-surface)]"
          strokeWidth={1.2}
          strokeLinejoin="round"
        />
      </svg>
    </span>
  );
}

function SessionCard({ frame }: { frame: Frame }) {
  const percent = Math.round((frame.apps / TOTAL_APPS) * 100);
  return (
    <div className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-bg)] p-4 sm:self-start">
      <div className="flex items-center gap-2">
        <span className="grid h-4 w-4 shrink-0 place-items-center">
          <span className={`col-start-1 row-start-1 h-2 w-2 rounded-full bg-[var(--lp-accent)] ${FADE} ${frame.done ? "opacity-0" : "lp-live-dot"}`} />
          <span
            className={`col-start-1 row-start-1 grid h-4 w-4 place-items-center rounded-full bg-[var(--lp-accent)] text-white transition duration-300 motion-reduce:transition-none ${frame.done ? "opacity-100" : "scale-50 opacity-0"}`}
          >
            <svg className="h-2.5 w-2.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3.5} aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" d="M5 13l4 4L19 7" />
            </svg>
          </span>
        </span>
        <span className="truncate text-[13px] font-semibold text-[var(--lp-ink)]">{DEVICE}</span>
      </div>
      <p className="mt-1 truncate text-[11px] text-[var(--lp-ink-faint)]">ThinkPad X1 Carbon · User-driven</p>
      <div className="mt-3 flex items-center justify-between gap-3 text-[12px]">
        <span className="grid min-w-0">
          <span className={`col-start-1 row-start-1 truncate tabular-nums text-[var(--lp-ink-soft)] ${FADE} ${frame.done ? "opacity-0" : "opacity-100"}`}>
            {`Installing apps ${frame.apps}/${TOTAL_APPS}`}
          </span>
          <span className={`col-start-1 row-start-1 font-semibold text-[var(--lp-accent-ink)] ${FADE} ${frame.done ? "opacity-100" : "opacity-0"}`}>
            Completed
          </span>
        </span>
        <span className="shrink-0 font-semibold tabular-nums text-[var(--lp-ink)]">{`${percent}%`}</span>
      </div>
      <div className="mt-1.5 h-1.5 overflow-hidden rounded-full bg-[var(--lp-surface-2)]">
        <div
          className="h-full rounded-full bg-[var(--lp-accent)] transition-[width] duration-500 ease-out motion-reduce:transition-none"
          style={{ width: `${percent}%` }}
        />
      </div>
      <div className="mt-4">
        <WatchPill frame={frame} />
      </div>
    </div>
  );
}

/** The push as the lock screen shows it. Mounting it plays the slide-in. */
function LockScreenAlert() {
  return (
    <div className="lp-event-in flex items-start gap-2 rounded-2xl bg-[var(--lp-surface)] p-2.5 shadow-lg shadow-black/30">
      <span className="grid h-7 w-7 shrink-0 place-items-center rounded-lg border border-[var(--lp-line-soft)] bg-[var(--lp-surface)]">
        <BrandMark className="h-3.5 w-3.5" />
      </span>
      <div className="min-w-0 flex-1">
        <p className="flex items-baseline justify-between gap-2 text-[10px] leading-tight text-[var(--lp-ink-faint)]">
          <span className="font-medium">AM Alerts</span>
          <span>now</span>
        </p>
        <p className="mt-0.5 text-[12px] font-semibold leading-snug text-[var(--lp-ink)]">{ALERT_TITLE}</p>
        <p className="text-[11px] leading-snug text-[var(--lp-ink-soft)]">{ALERT_BODY}</p>
      </div>
    </div>
  );
}

/**
 * Next to the phone, never inside the push: the real notification shows no such badge. Below sm it
 * sits between card and phone; from sm it hangs left of the phone, level with the notification.
 */
function EncryptedBadge({ shown }: { shown: boolean }) {
  return (
    <span
      className={`mb-4 inline-flex items-center gap-1.5 whitespace-nowrap rounded-full border border-[var(--lp-accent-line)] bg-[var(--lp-accent-soft)] px-2.5 py-1 text-[11px] font-semibold text-[var(--lp-accent-ink)] transition-opacity duration-500 motion-reduce:transition-none sm:absolute sm:right-full sm:top-[192px] sm:mb-0 sm:mr-4 ${shown ? "opacity-100" : "opacity-0"}`}
    >
      <LockIcon className="h-3 w-3 shrink-0" />
      end-to-end encrypted
    </span>
  );
}

/**
 * Landing demo of Notify me when done: a running enrollment gets watched, finishes, and the
 * phone gets the push. A setTimeout state machine like McpTerminalDemo, at most MAX_LOOPS loops.
 */
export function PushWatchDemo() {
  const stageRef = useRef<HTMLDivElement>(null);
  const reduced = useMediaQuery("(prefers-reduced-motion: reduce)");
  // Starts once half the stage is visible; no timers while it is off screen or the tab is hidden.
  const active = useInViewActive(stageRef, { startThreshold: 0.5, disabled: reduced });
  const [index, setIndex] = useState(0);
  const [loop, setLoop] = useState(1);
  const resting = loop >= MAX_LOOPS && index === STILL_FRAME;

  useEffect(() => {
    // `active` stays true when reduced motion is switched on mid-loop, hence the extra check.
    if (!active || reduced || resting) return;
    const timer = window.setTimeout(() => {
      const next = (index + 1) % FRAMES.length;
      if (next === 0) setLoop(n => n + 1);
      setIndex(next);
    }, FRAMES[index].ms);
    return () => window.clearTimeout(timer);
  }, [active, reduced, resting, index]);

  const shown = reduced ? STILL_FRAME : index;
  const frame = FRAMES[shown];
  // Frame 0 restarts the loop: everything snaps back without transitions while the stage fades in.
  const restart = shown === 0 ? "[&_*]:transition-none" : "";

  return (
    <figure>
      <div
        ref={stageRef}
        aria-hidden="true"
        className="select-none overflow-hidden rounded-3xl border border-[var(--lp-line)] bg-[var(--lp-surface)]"
      >
        <div
          className={`grid gap-5 px-4 pt-4 sm:grid-cols-[minmax(0,1fr)_232px] sm:gap-6 sm:px-8 sm:pt-8 ${FADE} ${frame.fade ? "opacity-0" : "opacity-100"} ${restart}`}
        >
          <SessionCard frame={frame} />
          <div className="relative flex flex-col items-center">
            <EncryptedBadge shown={frame.notified && !frame.fade} />
            <div className="h-[300px] overflow-hidden sm:h-[360px]">
              <PhoneFrame>{frame.notified && <LockScreenAlert />}</PhoneFrame>
            </div>
          </div>
        </div>
      </div>
      <figcaption className="sr-only">{CAPTION}</figcaption>
    </figure>
  );
}
