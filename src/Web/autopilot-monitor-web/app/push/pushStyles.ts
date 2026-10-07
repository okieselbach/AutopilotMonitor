import type { Severity } from "@/lib/push/pushCore";

// Class maps live under app/ so Tailwind's content scan (app, components, pages) emits them.

/** Left accent of a history row: green success, red failure, orange unclear, sky info. */
export const SEVERITY_ACCENT: Record<Severity, string> = {
  error: "border-red-500",
  warning: "border-orange-500",
  success: "border-green-500",
  info: "border-sky-500",
};

/** Device status chip: green active, orange unclear (pending/stale), gray paused; unknown → neutral. */
export const DEVICE_STATUS_CHIP: Record<string, string> = {
  Active: "bg-green-100 text-green-800 dark:bg-green-900/40 dark:text-green-300",
  Pending: "bg-orange-100 text-orange-800 dark:bg-orange-900/40 dark:text-orange-300",
  Stale: "bg-orange-100 text-orange-800 dark:bg-orange-900/40 dark:text-orange-300",
  Paused: "bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300",
};

export const NEUTRAL_CHIP = "bg-gray-100 text-gray-800 dark:bg-gray-700 dark:text-gray-300";
export const DANGER_CHIP = "bg-red-100 text-red-800 dark:bg-red-900/40 dark:text-red-300";
