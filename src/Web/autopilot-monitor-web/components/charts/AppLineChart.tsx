"use client";

import {
  ResponsiveContainer,
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
} from "recharts";
import { chartColors, tooltipStyle, axisTick, axisLine, legendStyle } from "./chartTheme";

export interface LineSeries {
  dataKey: string;
  label: string;
  color?: string;
}

export interface AppLineChartProps {
  data: Array<Record<string, unknown>>;
  xKey: string;
  series: LineSeries[];
  height?: number;
  yUnit?: string;
  yDomain?: [number | "auto", number | "auto"];
  formatXTick?: (value: unknown) => string;
  /** No legend — for a single series the card title already names. */
  hideLegend?: boolean;
  /** Whole numbers only on the Y axis. */
  integerTicks?: boolean;
}

/**
 * Reusable line chart for the App Dashboard detail page.
 * Handles failure-rate-over-time and avg-duration-over-time.
 * Multi-series support so baseline/target lines can be added later.
 */
export default function AppLineChart({
  data,
  xKey,
  series,
  height = 260,
  yUnit,
  yDomain,
  formatXTick,
  hideLegend,
  integerTicks,
}: AppLineChartProps) {
  return (
    <ResponsiveContainer width="100%" height={height}>
      <LineChart data={data} margin={{ top: 8, right: 16, left: 0, bottom: 4 }}>
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
          domain={yDomain}
          tickFormatter={yUnit ? (v: number) => `${v}${yUnit}` : undefined}
          width={48}
          {...(integerTicks ? { allowDecimals: false } : {})}
        />
        <Tooltip
          contentStyle={tooltipStyle}
          labelStyle={{ color: chartColors.tooltipText, marginBottom: 4 }}
          cursor={{ stroke: chartColors.gridLine, strokeDasharray: "3 3" }}
          labelFormatter={formatXTick ? (l: unknown) => formatXTick(l) : undefined}
          formatter={(value: number, name: string) => [
            yUnit ? `${value}${yUnit}` : value,
            name,
          ]}
        />
        {!hideLegend && <Legend wrapperStyle={legendStyle} iconType="plainline" />}
        {series.map((s, idx) => (
          <Line
            key={s.dataKey}
            type="monotone"
            dataKey={s.dataKey}
            name={s.label}
            stroke={s.color ?? [chartColors.primary, chartColors.success, chartColors.warning][idx % 3]}
            strokeWidth={2}
            dot={false}
            activeDot={{ r: 4 }}
          />
        ))}
      </LineChart>
    </ResponsiveContainer>
  );
}
