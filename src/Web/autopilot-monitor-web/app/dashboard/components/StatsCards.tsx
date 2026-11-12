"use client";

import type { ReactNode } from "react";
import type { Route } from "next";
import Link from "next/link";

export type StatsCardIcon = "activity" | "success" | "duration" | "today" | "failed" | "shield" | "shieldAlert";

const SHIELD = "M12 21.5s7.5-3.6 7.5-9.4V5.4L12 2.5 4.5 5.4v6.7c0 5.8 7.5 9.4 7.5 9.4z";

// Outline glyphs on a 24px grid, one per metric.
const iconShapes: Record<StatsCardIcon, ReactNode> = {
  activity: <polyline points="2 12 6 12 9 4 15 20 18 12 22 12" />,
  success: (
    <>
      <circle cx="12" cy="12" r="9.5" />
      <path d="m8.5 12.2 2.4 2.4 4.6-4.9" />
    </>
  ),
  duration: (
    <>
      <circle cx="12" cy="14" r="8" />
      <path d="M10 2h4" />
      <path d="M12 14l3-3" />
    </>
  ),
  today: (
    <>
      <rect x="3" y="4" width="18" height="18" rx="2" />
      <path d="M8 2v4" />
      <path d="M16 2v4" />
      <path d="M3 10h18" />
    </>
  ),
  failed: (
    <>
      <circle cx="12" cy="12" r="9.5" />
      <path d="m15 9-6 6" />
      <path d="m9 9 6 6" />
    </>
  ),
  shield: <path d={SHIELD} />,
  shieldAlert: (
    <>
      <path d={SHIELD} />
      <path d="M12 8v4.5M12 15.8h.01" />
    </>
  ),
};

/**
 * Columns of the stat row; the wrapper around it carries `stats-row` (the size container). Six cards
 * follow the width the row itself gets, set in app/globals.css (`stats-grid-6`: six columns from
 * 1126 px, which a 1440 px window with the full sidebar reaches, three from 553 px, two from 362 px).
 * Five cards (the cross-tenant views) keep the window breakpoints.
 */
export function statsGridClassName(count: number): string {
  return count >= 6 ? "stats-grid-6" : "grid grid-cols-1 gap-5 sm:grid-cols-2 lg:grid-cols-5";
}

/** The link stretched over the whole card; the card is its positioning context. */
const STRETCHED_LINK = "after:absolute after:inset-0 after:rounded-lg focus:outline-none";

export function StatsCard({
  title,
  value,
  description,
  icon,
  alert = false,
  muted = false,
  href,
  linkOn = "title",
  onClick,
}: {
  title: string;
  value: string;
  /** One line, or parts that wrap as whole units, separated by a dot that never starts a line. */
  description: string | readonly string[];
  icon: StatsCardIcon;
  /** Colors value and icon red — the only color on the row, reserved for a metric that needs attention. */
  alert?: boolean;
  /** Gray value: the metric is off or not available. */
  muted?: boolean;
  /** Makes the whole card a link. */
  href?: Route;
  /** "description": the description is the visible call to action (green); otherwise the title carries the link. */
  linkOn?: "title" | "description";
  onClick?: () => void;
}) {
  const linked = !!href;
  const titleLink = linked && linkOn === "title";
  const descriptionLink = linked && linkOn === "description";
  const parts = typeof description === "string" ? [description] : description;

  return (
    <div
      className={`relative bg-white overflow-hidden shadow rounded-lg${
        linked
          ? " group transition-shadow hover:shadow-md hover:ring-1 hover:ring-gray-300 dark:hover:ring-gray-600 has-[:focus-visible]:ring-2 has-[:focus-visible]:ring-green-600 motion-reduce:transition-none"
          : ""
      }`}
    >
      <div className="p-4">
        <dl className="min-w-0">
          <dt className="flex min-w-0 items-center gap-2 text-sm font-medium text-gray-500">
            <span
              className={`flex min-w-0 flex-1 items-center gap-1${
                titleLink ? " group-hover:text-gray-900 dark:group-hover:text-gray-100" : ""
              }`}
            >
              {titleLink && href ? (
                <>
                  <Link href={href} onClick={onClick} className={`truncate ${STRETCHED_LINK}`}>
                    {title}
                  </Link>
                  <svg
                    className="h-3.5 w-3.5 shrink-0 -translate-x-1 opacity-0 transition group-hover:translate-x-0 group-hover:opacity-100 group-focus-within:translate-x-0 group-focus-within:opacity-100 motion-reduce:transition-none"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                    strokeWidth={2}
                    strokeLinecap="round"
                    strokeLinejoin="round"
                    aria-hidden="true"
                  >
                    <path d="M5 12h14M13 6l6 6-6 6" />
                  </svg>
                </>
              ) : (
                <span className="truncate">{title}</span>
              )}
            </span>
            <svg
              className={`h-4 w-4 shrink-0 ${alert ? "text-red-600" : "text-gray-500"}`}
              fill="none"
              viewBox="0 0 24 24"
              stroke="currentColor"
              strokeWidth={2}
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
            >
              {iconShapes[icon]}
            </svg>
          </dt>
          <dd className={`text-2xl font-semibold ${alert ? "text-red-600" : muted ? "text-gray-500" : "text-gray-900"}`}>
            {value}
          </dd>
        </dl>
        <div className="mt-2 text-sm text-gray-500">
          {descriptionLink && href ? (
            <Link
              href={href}
              onClick={onClick}
              className={`font-medium text-green-700 hover:text-green-800 dark:text-green-400 ${STRETCHED_LINK}`}
            >
              {parts.join(" · ")}
            </Link>
          ) : parts.length === 1 ? (
            parts[0]
          ) : (
            // Each part wraps as a whole; the dot before a part that starts a line sits in the clipped margin.
            <div className="overflow-hidden">
              <div className="-ml-[15px] flex flex-wrap">
                {parts.map((part, i) => (
                  <span key={`${i}-${part}`} className="flex min-w-0">
                    <span aria-hidden="true" className="w-[15px] shrink-0 text-center">·</span>
                    {i > 0 && <span className="sr-only">, </span>}
                    <span className="min-w-0">{part}</span>
                  </span>
                ))}
              </div>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
