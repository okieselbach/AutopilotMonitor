"use client";

import type { ReactNode } from "react";

export type StatsCardIcon = "activity" | "success" | "duration" | "today" | "failed";

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
};

export function StatsCard({
  title,
  value,
  description,
  icon,
  alert = false,
}: {
  title: string;
  value: string;
  description: string;
  icon: StatsCardIcon;
  /** Colors value and icon red — the only color on the row, reserved for a metric that needs attention. */
  alert?: boolean;
}) {
  return (
    <div className="bg-white overflow-hidden shadow rounded-lg">
      <div className="p-5">
        <div className="flex items-start justify-between gap-3">
          <dl className="min-w-0">
            <dt className="text-sm font-medium text-gray-500 truncate">{title}</dt>
            <dd className={`text-2xl font-semibold ${alert ? "text-red-600" : "text-gray-900"}`}>{value}</dd>
          </dl>
          <svg
            className={`h-5 w-5 flex-shrink-0 ${alert ? "text-red-600" : "text-gray-500"}`}
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            strokeWidth={1.6}
            strokeLinecap="round"
            strokeLinejoin="round"
            aria-hidden="true"
          >
            {iconShapes[icon]}
          </svg>
        </div>
        <div className="mt-2">
          <div className="text-sm text-gray-500">{description}</div>
        </div>
      </div>
    </div>
  );
}
