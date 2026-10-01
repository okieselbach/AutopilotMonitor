"use client";

import { useEffect, useRef, type MouseEvent, type ReactNode } from "react";
import { trackEvent } from "@/lib/appInsights";
import {
  MARKETING_CTA_CLICKED,
  MARKETING_SECTION_VIEWED,
  isSectionSeen,
  trackIdOf,
} from "@/lib/clickTracking";

const THRESHOLDS = [0, 0.1, 0.2, 1 / 3];

/**
 * Anonymous usage counts for one public marketing page: one event per click on an element marked
 * data-track="<id>", and one per section marked data-track-section="<id>" the first time it is
 * seen in this page load. `display: contents` keeps the page layout as it is, and the sections
 * stay server components.
 */
export function MarketingTracker({ page, children }: { page: string; children: ReactNode }) {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const root = ref.current;
    if (!root || typeof IntersectionObserver === "undefined") return;
    const seen = new Set<string>();
    const observer = new IntersectionObserver(
      entries => {
        for (const entry of entries) {
          const section = (entry.target as HTMLElement).dataset.trackSection;
          if (!section || seen.has(section) || !entry.isIntersecting) continue;
          if (!isSectionSeen(entry.intersectionRatio, entry.intersectionRect.height, window.innerHeight)) continue;
          seen.add(section);
          observer.unobserve(entry.target);
          trackEvent(MARKETING_SECTION_VIEWED, { page, section });
        }
      },
      { threshold: THRESHOLDS }
    );
    root.querySelectorAll<HTMLElement>("[data-track-section]").forEach(el => observer.observe(el));
    return () => observer.disconnect();
  }, [page]);

  const onClick = (e: MouseEvent<HTMLDivElement>) => {
    const button = trackIdOf(e.target as Element);
    if (button) trackEvent(MARKETING_CTA_CLICKED, { page, button });
  };

  return (
    <div ref={ref} className="contents" onClick={onClick}>
      {children}
    </div>
  );
}
