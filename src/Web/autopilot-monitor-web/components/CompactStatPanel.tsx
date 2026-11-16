"use client";

import type { ReactNode } from "react";
import type { Route } from "next";
import Link from "next/link";
import { Bar } from "@/components/skeletons/PageSkeleton";

export type StatUnit = "%" | "min";

export type CompactStatTone = "default" | "muted" | "red" | "green" | "blue" | "yellow" | "slate";

/** One cell of the compact stat panel. */
export interface CompactStat {
  label: string;
  value: string;
  unit?: StatUnit;
  /** One short line under the value; the call to action when the link sits on it. */
  note?: string;
  tone?: CompactStatTone;
  /** Makes the whole cell a link. */
  href?: Route;
  linkOn?: "label" | "note";
  onClick?: () => void;
}

/** The value as the full-size cards print it: "94%", "38 min". */
export function statValueText(value: string, unit?: StatUnit): string {
  if (!unit) return value;
  return unit === "%" ? `${value}%` : `${value} ${unit}`;
}

/** Every class here has a dark-mode override in app/globals.css. */
const TONE_CLASS: Record<CompactStatTone, string> = {
  default: "text-gray-900",
  muted: "text-gray-500",
  red: "text-red-600",
  green: "text-green-700",
  blue: "text-blue-700",
  yellow: "text-yellow-700",
  slate: "text-slate-600",
};

const PANEL = "grid grid-cols-6 gap-px overflow-hidden rounded-lg bg-gray-200 shadow";
const CELL = "relative min-w-0 bg-white pb-2.5 pl-3 pr-2 pt-2";
const STRETCHED_LINK = "after:absolute after:inset-0 focus:outline-none";

/**
 * Six tracks, three cells a row; a last row of two splits in halves, a last single cell takes the row.
 * Four cells are two rows of two, which reads better than three and one.
 */
function spanClass(index: number, count: number): string {
  if (count === 4) return "col-span-3";
  const rest = count % 3;
  if (rest === 0 || index < count - rest) return "col-span-2";
  return rest === 2 ? "col-span-3" : "col-span-6";
}

/** The stat row on a phone: one panel, three cells a row, each a label, a value and at most one note. */
export function CompactStatPanel({ stats }: { stats: readonly CompactStat[] }) {
  return (
    <dl className={PANEL}>
      {stats.map((stat, i) => {
        const noteLink = !!stat.href && stat.linkOn === "note" && !!stat.note;
        const labelLink = !!stat.href && !noteLink;
        return (
          <div
            key={stat.label}
            className={`${CELL} ${spanClass(i, stats.length)}${
              stat.href ? " has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-inset has-[:focus-visible]:ring-green-600" : ""
            }`}
          >
            {/* A label longer than a narrow phone's cell wraps (hyphenated where the browser can), never cut off. */}
            <dt className="hyphens-auto text-xs font-medium text-gray-500 [overflow-wrap:anywhere]">
              {labelLink && stat.href ? (
                <Link href={stat.href} onClick={stat.onClick} className={STRETCHED_LINK}>
                  {stat.label}
                </Link>
              ) : (
                stat.label
              )}
            </dt>
            <dd className="flex items-center justify-between gap-1">
              <span className={`text-xl font-semibold tabular-nums ${TONE_CLASS[stat.tone ?? "default"]}`}>
                {stat.value}
                {stat.unit && <span className="ml-0.5 text-[13px] font-medium text-gray-500">{stat.unit}</span>}
              </span>
              {/* Always visible: a phone has no hover to reveal it. */}
              {labelLink && (
                <svg
                  className="h-3.5 w-3.5 shrink-0 text-gray-500"
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                  strokeWidth={2.5}
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  aria-hidden="true"
                >
                  <path d="m9 6 6 6-6 6" />
                </svg>
              )}
            </dd>
            {stat.note && (
              <dd className="text-xs text-gray-500">
                {noteLink && stat.href ? (
                  <Link
                    href={stat.href}
                    onClick={stat.onClick}
                    className={`font-medium text-green-700 dark:text-green-400 ${STRETCHED_LINK}`}
                  >
                    {stat.note}
                  </Link>
                ) : (
                  stat.note
                )}
              </dd>
            )}
          </div>
        );
      })}
    </dl>
  );
}

/** Placeholder in the shape of the compact panel, so the page does not jump when the figures arrive. */
export function CompactStatPanelSkeleton({ count }: { count: number }) {
  return (
    <div className={PANEL} aria-hidden="true">
      {Array.from({ length: count }).map((_, i) => (
        <div key={i} className={`${CELL} ${spanClass(i, count)}`}>
          <Bar className="mt-1 h-2.5 w-14" />
          <Bar className="mt-2.5 h-5 w-10" />
          <Bar className="mt-2 h-2 w-16" />
        </div>
      ))}
    </div>
  );
}

/**
 * A stat row that is its own size container (`stats-row`, app/globals.css): below 553 px of row width it
 * shows `compact`, from there `wide`. The hidden one is `display: none`, out of the tab order and the
 * accessibility tree.
 */
export function StatRowSwitch({ wide, compact, className = "" }: { wide: ReactNode; compact: ReactNode; className?: string }) {
  return (
    <div className={`stats-row${className ? ` ${className}` : ""}`}>
      <div className="stats-wide">{wide}</div>
      <div className="stats-compact">{compact}</div>
    </div>
  );
}
