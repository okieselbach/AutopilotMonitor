"use client";

import type { ReactNode } from "react";
import { tooltipStyle } from "./chartTheme";

interface RenderedTooltipProps {
  render: (row: Record<string, unknown>) => ReactNode;
  // Injected by recharts when it clones the content element.
  active?: boolean;
  payload?: ReadonlyArray<{ payload?: unknown }>;
}

/**
 * Tooltip content that hands the hovered row to a caller's render function. Module level on purpose:
 * recharts clones this element per hover, so its type must stay stable.
 */
export function RenderedTooltip({ render, active, payload }: RenderedTooltipProps) {
  const row = active ? payload?.[0]?.payload : undefined;
  if (!row || typeof row !== "object") return null;
  return <div style={tooltipStyle}>{render(row as Record<string, unknown>)}</div>;
}
