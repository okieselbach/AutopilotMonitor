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
  Legend,
} from "recharts";
import { chartColors, tooltipStyle, axisTick, axisLine, legendStyle } from "./chartTheme";
import { RenderedTooltip } from "./RenderedTooltip";

export interface StackedSeries {
  dataKey: string;
  label: string;
  color: string;
}

export interface AppStackedBarChartProps {
  data: Array<Record<string, unknown>>;
  xKey: string;
  stacks: StackedSeries[];
  height?: number;
  formatXTick?: (value: unknown) => string;
  /** Replaces the tooltip content for the hovered row. */
  renderTooltip?: (row: Record<string, unknown>) => ReactNode;
}

/**
 * Stacked bar chart used by the App Health detail page for "Installs per
 * bucket (success vs failure)". Better than a failure-rate line when sample
 * size per bucket is small — the rate is binary (0 % / 100 %) which produces
 * useless spikes, while stacked counts immediately show both volume and
 * success/failure ratio.
 */
export default function AppStackedBarChart({
  data,
  xKey,
  stacks,
  height = 260,
  formatXTick,
  renderTooltip,
}: AppStackedBarChartProps) {
  return (
    <ResponsiveContainer width="100%" height={height}>
      <BarChart data={data} margin={{ top: 8, right: 16, left: 0, bottom: 4 }}>
        <CartesianGrid stroke={chartColors.gridLine} strokeDasharray="3 3" vertical={false} />
        <XAxis
          dataKey={xKey}
          tick={axisTick}
          axisLine={axisLine}
          tickLine={false}
          tickFormatter={formatXTick ? (v: unknown) => formatXTick(v) : undefined}
          minTickGap={24}
        />
        <YAxis
          tick={axisTick}
          axisLine={axisLine}
          tickLine={false}
          allowDecimals={false}
          width={32}
        />
        <Tooltip
          contentStyle={tooltipStyle}
          labelStyle={{ color: chartColors.tooltipText, marginBottom: 4 }}
          cursor={{ fill: chartColors.gridLine, opacity: 0.3 }}
          labelFormatter={formatXTick ? (l: unknown) => formatXTick(l) : undefined}
          {...(renderTooltip ? { content: <RenderedTooltip render={renderTooltip} /> } : {})}
        />
        <Legend wrapperStyle={legendStyle} iconType="square" />
        {stacks.map((s) => (
          <Bar
            key={s.dataKey}
            dataKey={s.dataKey}
            name={s.label}
            stackId="installs"
            fill={s.color}
            radius={[0, 0, 0, 0]}
          />
        ))}
      </BarChart>
    </ResponsiveContainer>
  );
}
