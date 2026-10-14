"use client";

import Link from "next/link";
import type { Route } from "next";
import type { BarTone, LaneMarker, TimeLanes } from "../lib/timeLanes";
import type { KeyEvent, KeyEventTone } from "../lib/eventLists";
import { timeOfDay } from "../lib/diagnosisFormat";

// Literal class strings per tone; vivid fills read in both themes.
const BAR: Record<BarTone, string> = {
  done: "border border-green-200 bg-green-100 text-green-800",
  failed: "border border-red-200 bg-red-100 text-red-800",
  signin: "border border-sky-200 bg-sky-100 text-sky-900",
  asleep: "bg-gray-200 text-gray-600",
  restart: "border border-amber-200 bg-amber-100 text-amber-900",
  update: "border border-blue-200 bg-blue-100 text-blue-800",
  app: "bg-green-500",
  appFailed: "bg-red-500",
};

// The label and its line shift alike, so neither leaves the chart at an edge.
const MARKER_ALIGN: Record<LaneMarker["align"], string> = {
  start: "",
  center: "-translate-x-1/2",
  end: "-translate-x-full",
};

const DOT: Record<KeyEventTone, string> = {
  failed: "bg-red-500",
  warning: "bg-amber-500",
  done: "bg-green-500",
  neutral: "bg-gray-400",
};

/** Where the time went (lanes from the time breakdown) and the key events of the session. */
export default function TimeSection({
  lanes,
  keyEvents,
  keyEventTotal,
  eventHref,
  timelineHref,
}: {
  lanes: TimeLanes | null;
  keyEvents: KeyEvent[];
  keyEventTotal: number;
  eventHref: (eventId: string) => Route;
  timelineHref: Route;
}) {
  if (!lanes && keyEvents.length === 0) return null;
  // One label row per failure line, so labels never overlap, however narrow the chart.
  const labelRows = `${Math.max(1, lanes?.markers.length ?? 0) * 1.25}rem`;
  return (
    <section className="rounded-lg bg-white p-5 shadow sm:p-6" aria-labelledby="time-title">
      <h2 id="time-title" className="text-lg font-semibold text-gray-900">
        {lanes ? "Where the time went" : "What happened"}
      </h2>

      {lanes && (
        <div className="relative mt-4 pb-1">
          <div className="grid grid-cols-[6rem_minmax(0,1fr)] items-center gap-x-3 gap-y-2 sm:grid-cols-[9.5rem_minmax(0,1fr)]">
            <div />
            <div className="relative text-[11px] tabular-nums" style={{ height: labelRows }}>
              {lanes.markers.map((m, i) => (
                <span
                  key={m.label}
                  className={`absolute whitespace-nowrap font-semibold text-red-700 ${MARKER_ALIGN[m.align]}`}
                  style={{ left: `${m.left}%`, top: `${i * 1.25}rem` }}
                >
                  {m.label}
                </span>
              ))}
            </div>
            {lanes.lanes.map((lane) => (
              <LaneRow key={lane.key} lane={lane} />
            ))}
            <div />
            <div className="relative h-4 text-[10px] tabular-nums text-gray-500">
              {lanes.ticks.map((t, i) => (
                <span
                  key={i}
                  className={`absolute whitespace-nowrap ${i === 0 ? "" : i === lanes.ticks.length - 1 ? "-translate-x-full" : "-translate-x-1/2"} ${t.minor ? "hidden sm:block" : ""}`}
                  style={{ left: `${t.left}%` }}
                >
                  {t.label}
                </span>
              ))}
            </div>
          </div>
          <div
            className="pointer-events-none absolute bottom-5 left-[6.75rem] right-0 sm:left-[10.25rem]"
            style={{ top: `calc(${labelRows} + 0.25rem)` }}
            aria-hidden="true"
          >
            {lanes.markers.map((m) => (
              <div key={m.label} className={`absolute inset-y-0 border-l-2 border-red-500 ${MARKER_ALIGN[m.align]}`} style={{ left: `${m.left}%` }} />
            ))}
          </div>
        </div>
      )}

      {keyEvents.length > 0 && (
        <details className="mt-4 rounded-lg border border-gray-200" open={!lanes}>
          <summary className="flex cursor-pointer flex-wrap items-center gap-x-2 gap-y-1 rounded-lg px-4 py-3 text-sm hover:bg-gray-50">
            <span className="font-medium text-gray-900">Key events</span>
            <span className="text-gray-500">
              {keyEvents.length} of {keyEventTotal}
            </span>
            <Link href={timelineHref} className="ml-auto text-xs text-blue-700 hover:underline">
              Open the full timeline
            </Link>
          </summary>
          <ol className="border-t border-gray-200 px-4 pb-1 pt-4">
            {keyEvents.map((e, i) => (
              <li key={e.eventId} className="grid grid-cols-[4.75rem_1.25rem_minmax(0,1fr)] gap-x-3">
                <time className={`pt-0.5 text-right text-xs tabular-nums ${e.tone === "failed" ? "font-semibold text-red-700" : "text-gray-500"}`}>
                  {timeOfDay(e.timestamp)}
                </time>
                <div className="flex flex-col items-center">
                  <span className={`mt-1.5 h-2.5 w-2.5 rounded-full ${DOT[e.tone]}`} />
                  {i < keyEvents.length - 1 && <span className="w-px flex-1 bg-gray-200" />}
                </div>
                <div className="min-w-0 pb-4">
                  <Link href={eventHref(e.eventId)} className={`text-sm hover:underline ${e.tone === "failed" ? "font-semibold text-red-700" : "text-gray-900"}`}>
                    {e.title}
                  </Link>
                  {e.detail && <p className="font-mono text-xs text-gray-500">{e.detail}</p>}
                </div>
              </li>
            ))}
          </ol>
        </details>
      )}
    </section>
  );
}

function LaneRow({ lane }: { lane: TimeLanes["lanes"][number] }) {
  return (
    <>
      <div className={`truncate text-xs ${lane.emphasis ? "font-semibold text-red-700" : "font-medium text-gray-700"}`} title={lane.label}>
        {lane.label}
      </div>
      <div className="relative h-7 rounded bg-gray-50">
        {lane.bars.map((bar, i) => (
          <div
            key={i}
            title={bar.title}
            className={`absolute inset-y-1 flex flex-wrap items-center overflow-hidden rounded-sm text-[11px] font-medium ${BAR[bar.tone]}`}
            style={{ left: `${bar.left}%`, width: `${bar.width}%` }}
          >
            {bar.label && (
              <>
                {/* A full-height first item: a label too wide for the bar wraps onto a hidden second line instead of showing cut off. */}
                <span className="h-full w-0" />
                <span className="whitespace-nowrap px-1.5">{bar.label}</span>
              </>
            )}
          </div>
        ))}
      </div>
    </>
  );
}
