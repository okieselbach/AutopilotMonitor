"use client";

import type { MouseEvent, ReactNode } from "react";
import { trackEvent } from "@/lib/appInsights";
import { EVENT_FOLLOWUP_CLICKED, trackIdOf } from "../eventClicks";

/**
 * Sends one App Insights event per click on an element marked data-track="<id>".
 * Delegated from this one wrapper, so the page itself stays a server component.
 */
export function EventClickTracker({
  event,
  className,
  children,
}: {
  event: string;
  className?: string;
  children: ReactNode;
}) {
  const onClick = (e: MouseEvent<HTMLDivElement>) => {
    const button = trackIdOf(e.target as Element);
    if (button) trackEvent(EVENT_FOLLOWUP_CLICKED, { event, button });
  };
  return (
    <div className={className} onClick={onClick}>
      {children}
    </div>
  );
}
