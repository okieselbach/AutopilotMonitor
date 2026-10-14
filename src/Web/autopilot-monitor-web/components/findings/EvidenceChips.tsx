import Link from "next/link";
import type { Route } from "next";
import { evidenceChips } from "@/lib/evidenceChips";

const CHIP =
  "inline-flex max-w-full items-center gap-1.5 rounded border border-amber-200 bg-amber-50 px-2 py-1 text-xs font-medium text-amber-700 transition-colors hover:bg-amber-100";

/**
 * The events behind a finding as chips. `href` links each chip (another page, e.g. the session
 * timeline); `onSelect` handles the click in place (the timeline on the same page).
 */
export function EvidenceChips({
  matchedConditions,
  href,
  onSelect,
  className,
}: {
  matchedConditions: Record<string, unknown> | null | undefined;
  href?: (eventId: string) => Route;
  onSelect?: (eventId: string) => void;
  className?: string;
}) {
  const chips = evidenceChips(matchedConditions);
  if (chips.length === 0) return null;

  return (
    <div className={className ?? "mb-2 flex flex-wrap gap-2"}>
      {chips.map((chip) => {
        const content = (
          <>
            <svg className="h-3 w-3 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 7l5 5m0 0l-5 5m5-5H6" />
            </svg>
            {chip.timestamp && <span className="tabular-nums text-amber-600 dark:text-amber-300">{formatChipTime(chip.timestamp)}</span>}
            <span className="truncate">{chip.label}</span>
          </>
        );
        const title = `Open the event in the timeline (${chip.eventId})`;
        return href ? (
          <Link key={chip.eventId} href={href(chip.eventId)} className={CHIP} title={title}>
            {content}
          </Link>
        ) : (
          <button key={chip.eventId} type="button" onClick={() => onSelect?.(chip.eventId)} className={CHIP} title={title}>
            {content}
          </button>
        );
      })}
    </div>
  );
}

function formatChipTime(iso: string): string {
  const ms = Date.parse(iso);
  return Number.isFinite(ms) ? new Date(ms).toLocaleTimeString() : iso;
}
