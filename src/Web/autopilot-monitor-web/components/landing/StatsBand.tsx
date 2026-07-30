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

/**
 * Sample values for local development only, where no stats blob is
 * configured. Production NEVER shows these: real numbers or no band —
 * fabricated figures must not appear as facts (customer-facing claims).
 */
const DEV_SAMPLE_STATS: StatItem[] = [
  { label: "enrollments monitored", value: "12,481" },
  { label: "success rate", value: "94.1%" },
  { label: "organisations", value: "87" },
  { label: "device models", value: "214" },
  { label: "events processed", value: "8,341,206" },
];

/**
 * Live platform stats under the hero. Skeleton while loading; on fetch
 * failure the band shows dev sample data locally and disappears entirely
 * in production (never an endless skeleton, never fake numbers).
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
        if (payload.totalEnrollments && payload.successfulEnrollments) {
          const rate = (payload.successfulEnrollments / payload.totalEnrollments) * 100;
          items.push({ label: "success rate", value: `${rate.toFixed(1)}%` });
        }
        if (payload.totalSignedUpTenants) {
          items.push({ label: "organisations", value: payload.totalSignedUpTenants.toLocaleString("en-US") });
        }
        if (payload.uniqueDeviceModels) {
          items.push({ label: "device models", value: payload.uniqueDeviceModels.toLocaleString("en-US") });
        }
        if (payload.totalEventsProcessed) {
          items.push({ label: "events processed", value: payload.totalEventsProcessed.toLocaleString("en-US") });
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
    <div className="flex flex-wrap items-center justify-center gap-x-8 gap-y-4">
      {stats
        ? stats.map(item => (
            <div key={item.label} className="lp-event-in text-center">
              <p className="text-xl sm:text-2xl font-bold tracking-tight text-[var(--lp-ink)]">{item.value}</p>
              <p className="mt-0.5 text-[11px] uppercase tracking-[0.14em] text-[var(--lp-ink-faint)]">{item.label}</p>
            </div>
          ))
        : Array.from({ length: 5 }, (_, i) => (
            <div key={i} className="text-center animate-pulse">
              <div className="h-7 w-16 mx-auto rounded bg-[var(--lp-surface-2)]" />
              <div className="mt-1.5 h-3 w-24 mx-auto rounded bg-[var(--lp-surface-2)]" />
            </div>
          ))}
    </div>
  );
}
