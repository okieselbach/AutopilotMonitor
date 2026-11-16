"use client";

import { CardSkeleton } from "@/components/skeletons/PageSkeleton";
import {
  CompactStatPanel,
  CompactStatPanelSkeleton,
  StatRowSwitch,
  statValueText,
  type StatUnit,
} from "@/components/CompactStatPanel";

type FleetStatColor = "green" | "blue" | "red" | "yellow" | "slate";

/** One Fleet Health metric: a card on a wide row, a cell of the compact panel on a phone. */
export interface FleetStat {
  title: string;
  value: string;
  unit?: StatUnit;
  subtitle: string;
  color: FleetStatColor;
  /** The phone cell: a short label and at most one note line. */
  compact: { label: string; note?: string };
}

/** Two columns from 553 px of row width (below, the compact panel takes over), five on a large window. */
const GRID = "grid grid-cols-2 gap-5 lg:grid-cols-5";

const colorClasses: Record<FleetStatColor, string> = {
  green: "border-green-500 bg-green-50",
  blue: "border-blue-500 bg-blue-50",
  red: "border-red-500 bg-red-50",
  yellow: "border-yellow-500 bg-yellow-50",
  slate: "border-slate-400 bg-slate-50",
};

const valueColors: Record<FleetStatColor, string> = {
  green: "text-green-700",
  blue: "text-blue-700",
  red: "text-red-700",
  yellow: "text-yellow-700",
  slate: "text-slate-600",
};

function FleetStatCard({ title, value, subtitle, color }: { title: string; value: string; subtitle: string; color: FleetStatColor }) {
  return (
    <div
      className={`bg-white overflow-hidden shadow rounded-lg border-l-4 ${colorClasses[color]}`}
    >
      <div className="p-5">
        <div className="text-sm font-medium text-gray-500">{title}</div>
        <div className={`text-3xl font-bold mt-1 ${valueColors[color]}`}>
          {value}
        </div>
        <div className="text-xs text-gray-400 mt-1">{subtitle}</div>
      </div>
    </div>
  );
}

/** The Fleet Health stat row: the cards on a wide row, one compact panel on a phone. */
export function FleetStatRow({ stats, className }: { stats: readonly FleetStat[]; className?: string }) {
  return (
    <StatRowSwitch
      className={className}
      wide={
        <div className={GRID}>
          {stats.map((stat) => (
            <FleetStatCard
              key={stat.title}
              title={stat.title}
              value={statValueText(stat.value, stat.unit)}
              subtitle={stat.subtitle}
              color={stat.color}
            />
          ))}
        </div>
      }
      compact={
        <CompactStatPanel
          stats={stats.map((stat) => ({
            label: stat.compact.label,
            value: stat.value,
            unit: stat.unit,
            note: stat.compact.note,
            tone: stat.color,
          }))}
        />
      }
    />
  );
}

/** Loading placeholder in the shape of the row at either width. */
export function FleetStatRowSkeleton({ count }: { count: number }) {
  return (
    <StatRowSwitch
      wide={
        <div className={GRID}>
          {Array.from({ length: count }).map((_, i) => (
            <CardSkeleton key={i} />
          ))}
        </div>
      }
      compact={<CompactStatPanelSkeleton count={count} />}
    />
  );
}
