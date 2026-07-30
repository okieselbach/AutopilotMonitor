"use client";

import { useEffect, useState } from "react";

interface PlatformStatsManifest {
  latest: string;
  generatedAtUtc: string;
}

interface PlatformStatsPayload {
  totalEnrollments?: number;
  totalUsers?: number;
  totalTenants?: number;
  totalSignedUpTenants?: number;
  uniqueDeviceModels?: number;
  totalEventsProcessed?: number;
  successfulEnrollments?: number;
  issuesDetected?: number;
  lastFullCompute?: string;
  lastUpdated?: string;
}

function resolvePlatformStatsManifestUrl(rawUrl?: string): string {
  const trimmed = rawUrl?.trim();
  if (!trimmed) {
    return "/platform-stats.json";
  }

  const hashIndex = trimmed.indexOf("#");
  const withoutHash = hashIndex >= 0 ? trimmed.slice(0, hashIndex) : trimmed;
  const hash = hashIndex >= 0 ? trimmed.slice(hashIndex) : "";

  const queryIndex = withoutHash.indexOf("?");
  const basePath = queryIndex >= 0 ? withoutHash.slice(0, queryIndex) : withoutHash;
  const query = queryIndex >= 0 ? withoutHash.slice(queryIndex) : "";

  if (/\.json$/i.test(basePath)) {
    return trimmed;
  }

  const normalizedBasePath = basePath.endsWith("/") ? basePath.slice(0, -1) : basePath;
  const manifestPath = `${normalizedBasePath}/platform-stats.json`;
  return `${manifestPath}${query}${hash}`;
}

const PLATFORM_STATS_MANIFEST_URL =
  resolvePlatformStatsManifestUrl(process.env.NEXT_PUBLIC_PLATFORM_STATS_MANIFEST_URL);

interface StatItem {
  label: string;
  value: string;
}

/** 8,341,206 → "8.3M"; smaller values keep their grouped form. */
function compact(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  return n.toLocaleString("en-US");
}

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
 * Full-bleed platform stats band under the hero shot. Skeleton while
 * loading; on fetch failure it shows dev sample data locally and
 * disappears entirely in production (never an endless skeleton, never
 * fake numbers).
 */
export function StatsBand() {
  const [stats, setStats] = useState<StatItem[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let cancelled = false;

    const fail = () => {
      if (cancelled) return;
      if (process.env.NODE_ENV === "development") {
        setStats(DEV_SAMPLE_STATS);
      } else {
        setFailed(true);
      }
    };

    const loadPlatformStats = async () => {
      try {
        const manifestResponse = await fetch(PLATFORM_STATS_MANIFEST_URL, { cache: "no-store" });
        if (!manifestResponse.ok) return fail();

        const manifest = (await manifestResponse.json()) as PlatformStatsManifest;
        if (!manifest?.latest) return fail();

        const versionedUrl = new URL(manifest.latest, manifestResponse.url).toString();
        const statsResponse = await fetch(versionedUrl, { cache: "force-cache" });
        if (!statsResponse.ok) return fail();

        const payload = (await statsResponse.json()) as PlatformStatsPayload;
        if (cancelled) return;

        const items: StatItem[] = [];
        if (payload.totalEnrollments) {
          items.push({ label: "enrollments monitored", value: payload.totalEnrollments.toLocaleString("en-US") });
        }
        if (payload.issuesDetected) {
          items.push({ label: "issues detected", value: payload.issuesDetected.toLocaleString("en-US") });
        }
        if (payload.totalSignedUpTenants) {
          items.push({ label: "organisations", value: payload.totalSignedUpTenants.toLocaleString("en-US") });
        }
        if (payload.uniqueDeviceModels) {
          items.push({ label: "device models", value: payload.uniqueDeviceModels.toLocaleString("en-US") });
        }
        if (payload.totalEventsProcessed) {
          items.push({ label: "events processed", value: compact(payload.totalEventsProcessed) });
        }
        if (items.length > 0) {
          setStats(items);
        } else {
          fail();
        }
      } catch {
        // The band is decorative and must never break the page.
        fail();
      }
    };

    loadPlatformStats();
    return () => {
      cancelled = true;
    };
  }, []);

  if (failed) {
    return null;
  }

  return (
    <section className="border-y border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)]">
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
