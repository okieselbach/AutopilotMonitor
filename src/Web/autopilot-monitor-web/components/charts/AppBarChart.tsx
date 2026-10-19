"use client";

import type { ReactNode } from "react";
import {
  ResponsiveContainer,
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Cell,
} from "recharts";
import { chartColors, tooltipStyle, axisTick, axisLine } from "./chartTheme";

export interface AppBarChartProps {
  data: Array<Record<string, unknown>>;
  categoryKey: string;
  valueKey: string;
  height?: number;
  /** Horizontal bars (category on Y axis). Default: vertical. */
  horizontal?: boolean;
  /** Bar colour or a function to derive per-bar colour from the row. */
  barColor?: string | ((row: Record<string, unknown>) => string);
  valueFormatter?: (value: number) => string;
  /** Category label for the ticks and the default tooltip (vertical bars); thins crowded ticks. */
  formatCategory?: (category: string) => string;
  /** Replaces the tooltip content for the hovered row. */
  renderTooltip?: (row: Record<string, unknown>) => ReactNode;
  /** Click anywhere in a category's column (not just on its bar, which may be zero-height). */
  onCategoryClick?: (row: Record<string, unknown>) => void;
  /** Whole numbers only on the value axis. */
  integerTicks?: boolean;
}

interface RenderedTooltipProps {
  render: (row: Record<string, unknown>) => ReactNode;
  // Injected by recharts when it clones the content element.
  active?: boolean;
  payload?: ReadonlyArray<{ payload?: unknown }>;
}

// Module level on purpose: recharts clones this element per hover, so its type must stay stable.
function RenderedTooltip({ render, active, payload }: RenderedTooltipProps) {
  const row = active ? payload?.[0]?.payload : undefined;
  if (!row || typeof row !== "object") return null;
  return <div style={tooltipStyle}>{render(row as Record<string, unknown>)}</div>;
}

/**
 * Reusable bar chart used by the App Dashboard detail page and the GA Customers section.
 * Used for "Top Failure Codes" and "Installer Phase Breakdown" etc.
 */
export default function AppBarChart({
  data,
  categoryKey,
  valueKey,
  height = 240,
  horizontal = false,
  barColor = chartColors.primary,
  valueFormatter,
  formatCategory,
  renderTooltip,
  onCategoryClick,
  integerTicks,
}: AppBarChartProps) {
  const wholeNumbers = integerTicks ? { allowDecimals: false } : {};
  return (
    <ResponsiveContainer width="100%" height={height}>
      <BarChart
        data={data}
        layout={horizontal ? "vertical" : "horizontal"}
        margin={{ top: 8, right: 16, left: horizontal ? 80 : 0, bottom: 4 }}
        {...(onCategoryClick
          ? {
              style: { cursor: "pointer" },
              onClick: (state: { activePayload?: Array<{ payload?: unknown }> } | null) => {
                const row = state?.activePayload?.[0]?.payload;
                if (row && typeof row === "object") onCategoryClick(row as Record<string, unknown>);
              },
            }
          : {})}
      >
        <CartesianGrid
          stroke={chartColors.gridLine}
          strokeDasharray="3 3"
          horizontal={!horizontal}
          vertical={horizontal}
        />
        {horizontal ? (
          <>
            <XAxis
              type="number"
              tick={axisTick}
              axisLine={axisLine}
              tickLine={false}
              tickFormatter={valueFormatter}
              {...wholeNumbers}
            />
            <YAxis
              type="category"
              dataKey={categoryKey}
              tick={axisTick}
              axisLine={axisLine}
              tickLine={false}
              width={120}
            />
          </>
        ) : (
          <>
            <XAxis
              dataKey={categoryKey}
              tick={axisTick}
              axisLine={axisLine}
              tickLine={false}
              {...(formatCategory
                ? { tickFormatter: (category: unknown) => formatCategory(String(category)), minTickGap: 12 }
                : {})}
            />
            <YAxis
              tick={axisTick}
              axisLine={axisLine}
              tickLine={false}
              tickFormatter={valueFormatter}
              width={48}
              {...wholeNumbers}
            />
          </>
        )}
        <Tooltip
          contentStyle={tooltipStyle}
          cursor={{ fill: chartColors.gridLine, opacity: 0.3 }}
          formatter={(value: number) => [valueFormatter ? valueFormatter(value) : value, ""]}
          {...(formatCategory && !horizontal ? { labelFormatter: (label: unknown) => formatCategory(String(label)) } : {})}
          {...(renderTooltip ? { content: <RenderedTooltip render={renderTooltip} /> } : {})}
        />
        <Bar dataKey={valueKey} radius={[4, 4, 0, 0]}>
          {data.map((row, idx) => (
            <Cell
              key={idx}
              fill={typeof barColor === "function" ? barColor(row) : barColor}
            />
          ))}
        </Bar>
      </BarChart>
    </ResponsiveContainer>
  );
}
