// Small, dependency-free formatters for session rows. Mirror of the page-local copies in
// app/geographic-performance/sessions/page.tsx (kept inline there to avoid touching that page);
// new read-only views (Fleet drill-in) consume these shared helpers instead of adding a third copy.

/** Duration as "N min" / "Hh Mm"; em-dash for missing/zero. */
export function formatDurationShort(seconds: number | null | undefined): string {
  if (!seconds || seconds <= 0) return "—";
  const mins = Math.round(seconds / 60);
  if (mins < 60) return `${mins} min`;
  const hrs = Math.floor(mins / 60);
  const remainMins = mins % 60;
  return `${hrs}h ${remainMins}m`;
}

/** Absolute local date + time, e.g. "Jun 25, 2026 14:03". Em-dash for missing/invalid. */
export function formatDateTime(dateStr: string | null | undefined): string {
  if (!dateStr) return "—";
  const d = new Date(dateStr);
  if (Number.isNaN(d.getTime())) return "—";
  return (
    d.toLocaleDateString(undefined, { month: "short", day: "numeric", year: "numeric" }) +
    " " +
    d.toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" })
  );
}
