"use client";

import { useState } from "react";
import Link from "next/link";
import type { Route } from "next";
import type { ProblemEvent } from "../lib/eventLists";
import { timeOfDay } from "../lib/diagnosisFormat";

type Filter = "all" | "error" | "warning";

/** Errors and warnings of the session, collapsed; identical repeats are listed once. */
export default function ProblemsSection({ events, eventHref }: { events: ProblemEvent[]; eventHref: (eventId: string) => Route }) {
  const [filter, setFilter] = useState<Filter>("all");
  if (events.length === 0) return null;

  const isError = (e: ProblemEvent) => e.severity === "Error" || e.severity === "Critical";
  const errors = events.filter(isError).length;
  const warnings = events.length - errors;
  const shown = events.filter((e) => (filter === "all" ? true : filter === "error" ? isError(e) : !isError(e)));
  const options: { key: Filter; label: string }[] = [
    { key: "all", label: `All ${events.length}` },
    { key: "error", label: `Errors ${errors}` },
    { key: "warning", label: `Warnings ${warnings}` },
  ];

  return (
    <details className="rounded-lg bg-white shadow">
      <summary className="flex cursor-pointer flex-wrap items-center gap-x-2 gap-y-1 rounded-lg px-4 py-3 hover:bg-gray-50 sm:px-5">
        <span className="text-sm font-semibold text-gray-900">Errors and warnings</span>
        <span className="text-sm text-gray-500">
          {errors} {errors === 1 ? "error" : "errors"} · {warnings} {warnings === 1 ? "warning" : "warnings"}
        </span>
      </summary>
      <div className="border-t border-gray-200">
        <div className="flex justify-end px-4 pt-3 sm:px-5">
          <div className="flex rounded-md border border-gray-200 bg-gray-50 p-0.5 text-xs" role="group" aria-label="Filter">
            {options.map((o) => (
              <button
                key={o.key}
                type="button"
                aria-pressed={filter === o.key}
                onClick={() => setFilter(o.key)}
                className={`rounded px-2 py-0.5 ${filter === o.key ? "bg-white font-medium text-gray-900 shadow-sm" : "text-gray-700"}`}
              >
                {o.label}
              </button>
            ))}
          </div>
        </div>
        <ul className="divide-y divide-gray-100 text-sm">
          {shown.map((e) => (
            <li key={e.eventId} className="grid grid-cols-[4.75rem_1rem_minmax(0,1fr)] gap-x-3 px-4 py-2 sm:px-5 md:grid-cols-[4.75rem_1rem_10rem_minmax(0,1fr)]">
              <span className="text-xs tabular-nums text-gray-500">{timeOfDay(e.timestamp)}</span>
              <span className={`mt-1.5 h-2 w-2 rounded-full ${isError(e) ? "bg-red-500" : "bg-amber-500"}`} aria-label={e.severity} />
              <span className="hidden truncate text-xs text-gray-500 md:block">{e.source}</span>
              <Link href={eventHref(e.eventId)} className="min-w-0 break-words text-gray-700 hover:underline">
                {e.message}
                {e.count > 1 && <span className="ml-1 rounded bg-gray-100 px-1.5 text-[11px] text-gray-600">{e.count}× merged</span>}
              </Link>
            </li>
          ))}
        </ul>
      </div>
    </details>
  );
}
