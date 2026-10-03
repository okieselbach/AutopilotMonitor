"use client";

import { useEffect, useState } from "react";
import {
  fetchLivePlatformStats,
  toStatItems,
  type PlatformStatsSnapshot,
  type StatItem,
} from "@/lib/platformStats";

/**
 * Sample values for local development only, where no stats blob is
 * configured. Production NEVER shows these: real numbers or no band —
 * fabricated figures must not appear as facts (customer-facing claims).
 */
const DEV_SAMPLE_STATS: StatItem[] = [
  { label: "enrollments monitored", value: "12,481" },
  { label: "issues detected", value: "1,847" },
  { label: "organisations", value: "87" },
  { label: "device models", value: "214" },
  { label: "events processed", value: "8.3M" },
];

/**
 * Full-bleed platform stats band under the hero shot. Starts with the
 * numbers the build baked in (`snapshot`, lib/platformStats.ts), so the
 * static HTML carries them for crawlers and LLM fetchers, then swaps in the
 * live numbers. A failed live read keeps the baked numbers; without either,
 * it shows dev sample data locally and disappears entirely in production
 * (never an endless skeleton, never fake numbers). The skeleton only shows
 * while a page built without a snapshot loads.
 */
export function StatsBand({ snapshot }: { snapshot: PlatformStatsSnapshot | null }) {
  const [live, setLive] = useState<StatItem[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;

    const loadLiveStats = async () => {
      const payload = await fetchLivePlatformStats();
      if (cancelled) return;
      const items = payload ? toStatItems(payload) : [];
      if (items.length > 0) {
        setLive(items);
      } else {
        setFailed(true);
      }
    };

    loadLiveStats();
    return () => {
      cancelled = true;
    };
  }, []);

  const devFallback = failed && process.env.NODE_ENV === "development" ? DEV_SAMPLE_STATS : null;
  const stats = live ?? snapshot?.items ?? devFallback;
  if (!stats && failed) {
    return null;
  }

  return (
    <section
      data-track-section="stats"
      // Which build snapshot the static HTML carries; deploy-web.yml warns when it is missing.
      data-stats-snapshot={snapshot ? (snapshot.asOf ?? "") : undefined}
      className="border-y border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)]"
    >
      <div className="max-w-7xl mx-auto px-6 grid grid-cols-2 sm:grid-cols-3 lg:grid-cols-5 gap-y-6 py-8 lg:divide-x lg:divide-[var(--lp-line)]">
        {(stats ?? Array.from({ length: 5 }, () => null)).map((item, i) =>
          item ? (
            <div key={item.label} className="lg:px-8 lg:first:pl-0">
              <p className="text-2xl sm:text-3xl font-bold tracking-tight text-[var(--lp-ink)]">{item.value}</p>
              <p className="mt-1 text-[11px] uppercase tracking-[0.14em] text-[var(--lp-ink-faint)]">{item.label}</p>
            </div>
          ) : (
            <div key={i} className="lg:px-8 lg:first:pl-0 animate-pulse">
              <div className="h-8 w-20 rounded bg-[var(--lp-line-soft)]" />
              <div className="mt-2 h-3 w-28 rounded bg-[var(--lp-line-soft)]" />
            </div>
          )
        )}
      </div>
    </section>
  );
}
