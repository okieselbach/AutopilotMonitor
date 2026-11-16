"use client";

import { createContext, useContext, type ReactNode } from "react";
import type {
  AppScriptMetrics,
  DeploymentTypeMetrics,
  HardwareCount,
  HardwareMetrics,
  PerformanceMetrics,
  SessionMetrics,
  UserMetrics,
} from "@/lib/generated/wire-types.generated";
import { ENROLLMENT_CLASS_LABELS } from "@/lib/enrollmentClass";
import {
  CompactStatPanel,
  StatRowSwitch,
  statValueText,
  type CompactStat,
  type CompactStatTone,
  type StatUnit,
} from "@/components/CompactStatPanel";

// The blocks the tenant Usage Metrics page and the platform Usage Metrics section share.

/** One figure of a stat row: its card, and on a phone its cell in the compact panel. */
export interface UsageStat {
  label: string;
  value: string;
  unit?: StatUnit;
  /** The card's value color. The compact panel colors a value only by `compact.tone`, for outcomes. */
  valueClass: string;
  /** Hover text on the card's label. */
  title?: string;
  note?: string;
  compact: { label: string; note?: string; tone?: CompactStatTone };
}

function StatCard({ stat }: { stat: UsageStat }) {
  return (
    <div className="bg-white rounded-lg shadow p-6">
      <div className="text-sm text-gray-500 mb-1" title={stat.title}>{stat.label}</div>
      <div className={`text-3xl font-bold ${stat.valueClass}`}>{statValueText(stat.value, stat.unit)}</div>
      {stat.note != null && <p className="text-xs text-gray-400 mt-1">{stat.note}</p>}
    </div>
  );
}

/** Cards show from 553 px of row width: two or three to a row there, the full row from `md:`. */
const CARD_GRID: Partial<Record<number, string>> = {
  2: "grid-cols-2",
  3: "grid-cols-3",
  4: "grid-cols-2 md:grid-cols-4",
  5: "grid-cols-2 md:grid-cols-5",
};

function compactStat(stat: UsageStat): CompactStat {
  return { label: stat.compact.label, value: stat.value, unit: stat.unit, note: stat.compact.note, tone: stat.compact.tone };
}

/** A row of stat cards that folds into one compact panel on a phone (D-440, D-441). */
export function UsageStatRow({ stats, className }: { stats: readonly UsageStat[]; className?: string }) {
  return (
    <StatRowSwitch
      className={className}
      wide={
        <div className={`grid gap-4 ${CARD_GRID[stats.length] ?? "grid-cols-2"}`}>
          {stats.map((stat) => (
            <StatCard key={stat.label} stat={stat} />
          ))}
        </div>
      }
      compact={<CompactStatPanel stats={stats.map(compactStat)} />}
    />
  );
}

/**
 * Section headings are h2 under the tenant page's h1, and one step down (h3, smaller) inside an admin
 * metrics section, whose title row is already an h2.
 */
const NestedHeadings = createContext(false);

export function NestedUsageHeadings({ children }: { children: ReactNode }) {
  return <NestedHeadings.Provider value={true}>{children}</NestedHeadings.Provider>;
}

function SectionHeading({ title, description }: { title: string; description?: ReactNode }) {
  const nested = useContext(NestedHeadings);
  const Heading = nested ? "h3" : "h2";
  const size = nested ? "text-base" : "text-xl";
  if (description == null) return <Heading className={`${size} font-semibold text-gray-900 mb-4`}>{title}</Heading>;
  return (
    <>
      <Heading className={`${size} font-semibold text-gray-900 mb-1`}>{title}</Heading>
      <p className="text-sm text-gray-500 mb-4">{description}</p>
    </>
  );
}

/** A titled section of the page. */
export function UsageSection({ title, description, children }: { title: string; description?: ReactNode; children: ReactNode }) {
  return (
    <div className="mb-6">
      <SectionHeading title={title} description={description} />
      {children}
    </div>
  );
}

/** Today / last 7 / last 30 days, for the page's own first session row. */
export function sessionRecencyStats(sessions: SessionMetrics): UsageStat[] {
  return [
    { label: "Today", value: sessions.today.toLocaleString(), valueClass: "text-blue-600", compact: { label: "Today" } },
    {
      label: "Last 7 Days",
      value: sessions.last7Days.toLocaleString(),
      valueClass: "text-indigo-600",
      compact: { label: "Last 7 d" },
    },
    {
      label: "Last 30 Days",
      value: sessions.last30Days.toLocaleString(),
      valueClass: "text-purple-600",
      compact: { label: "Last 30 d" },
    },
  ];
}

export function SessionOutcomeRow({ sessions }: { sessions: SessionMetrics }) {
  return (
    <UsageStatRow
      className="mt-4"
      stats={[
        {
          label: "Succeeded",
          value: sessions.succeeded.toLocaleString(),
          valueClass: "text-green-600",
          compact: { label: "Succeeded", tone: "green" },
        },
        { label: "Failed", value: sessions.failed.toLocaleString(), valueClass: "text-red-600", compact: { label: "Failed", tone: "red" } },
        {
          label: "Incomplete",
          title: "Terminal, non-failure: timed out with no completion or failure signal. Excluded from the success rate.",
          value: sessions.incomplete.toLocaleString(),
          valueClass: "text-slate-500",
          compact: { label: "Incomplete", note: "not a failure", tone: "slate" },
        },
        {
          label: "In Progress",
          value: sessions.inProgress.toLocaleString(),
          valueClass: "text-yellow-600",
          compact: { label: "In progress", tone: "yellow" },
        },
        {
          label: "Success Rate",
          value: String(sessions.successRate),
          unit: "%",
          valueClass: "text-gray-900",
          compact: { label: "Success rate" },
        },
      ]}
    />
  );
}

function ShareRow({ label, count, percentage, barClass, devices }: {
  label: string;
  count: number;
  percentage: number;
  barClass: string;
  /** Hardware rows: the count then reads as sessions, followed by the distinct devices behind them. */
  devices?: number;
}) {
  return (
    <div className="p-4 hover:bg-gray-50 transition-colors">
      <div className="flex items-center justify-between gap-3 mb-2">
        <span className="font-medium text-gray-900">{label}</span>
        <div className="text-right whitespace-nowrap">
          <span className="text-sm font-semibold text-gray-900">
            {devices == null ? count.toLocaleString() : `${count.toLocaleString()} ${count === 1 ? "session" : "sessions"}`}
          </span>
          <span className="text-xs text-gray-500 ml-2">({percentage}%)</span>
          {devices != null && (
            <span className="text-xs text-gray-500 ml-2">{`· ${devices.toLocaleString()} ${devices === 1 ? "device" : "devices"}`}</span>
          )}
        </div>
      </div>
      <div className="w-full bg-gray-200 rounded-full h-2">
        <div
          className={`${barClass} h-2 rounded-full transition-all duration-300`}
          style={{ width: `${percentage}%` }}
        ></div>
      </div>
    </div>
  );
}

interface ShareGroup {
  title: string;
  hint: string;
  barClass: string;
  rows: { label: string; count: number; percentage: number }[];
}

function deploymentGroups(t: DeploymentTypeMetrics): ShareGroup[] {
  return [
    {
      title: "Enrollment Type",
      hint: "Share of all sessions in the window.",
      barClass: "bg-blue-600",
      rows: [
        { label: "Autopilot (v1)", count: t.autopilotV1, percentage: t.autopilotV1Percentage },
        { label: ENROLLMENT_CLASS_LABELS.device_preparation, count: t.devicePreparation, percentage: t.devicePreparationPercentage },
      ],
    },
    {
      title: "Join Type",
      hint: "Share of all sessions in the window.",
      barClass: "bg-indigo-600",
      rows: [
        { label: "Entra joined", count: t.entraJoin, percentage: t.entraJoinPercentage },
        { label: "Hybrid joined", count: t.hybridJoin, percentage: t.hybridJoinPercentage },
      ],
    },
    {
      title: "Autopilot Deployment Mode",
      hint: "Autopilot (v1) sessions by mode, as a share of all sessions in the window.",
      barClass: "bg-purple-600",
      rows: [
        { label: ENROLLMENT_CLASS_LABELS.user_driven, count: t.userDriven, percentage: t.userDrivenPercentage },
        { label: ENROLLMENT_CLASS_LABELS.self_deploying, count: t.selfDeploying, percentage: t.selfDeployingPercentage },
        { label: ENROLLMENT_CLASS_LABELS.whiteglove, count: t.whiteGlove, percentage: t.whiteGlovePercentage },
      ],
    },
  ];
}

/** Enrollment type, join type and Autopilot deployment mode of the sessions in the window. */
export function DeploymentTypeBreakdown({ deploymentTypes }: { deploymentTypes: DeploymentTypeMetrics }) {
  return (
    <UsageSection title="Deployment Types">
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        {deploymentGroups(deploymentTypes).map((group) => (
          <div key={group.title} className="bg-white rounded-lg shadow overflow-hidden">
            <div className="px-4 pt-4 text-sm text-gray-500" title={group.hint}>{group.title}</div>
            <div className="divide-y divide-gray-200">
              {group.rows.map((row) => (
                <ShareRow key={row.label} {...row} barClass={group.barClass} />
              ))}
            </div>
          </div>
        ))}
      </div>
    </UsageSection>
  );
}

export function AppScriptsBlock({ appScripts }: { appScripts: AppScriptMetrics }) {
  const uniqueApps = appScripts.totalUniqueApps.toLocaleString();
  const platformRuns = appScripts.totalPlatformScripts.toLocaleString();
  const remediationRuns = appScripts.totalRemediationScripts.toLocaleString();
  return (
    <UsageSection title="Apps & Scripts">
      <UsageStatRow
        stats={[
          {
            label: "Avg Apps / Session",
            value: String(appScripts.avgAppsPerSession),
            valueClass: "text-blue-600",
            note: `${uniqueApps} unique apps total`,
            compact: { label: "Apps / session", note: `${uniqueApps} unique apps` },
          },
          {
            label: "Avg Platform Scripts / Session",
            value: String(appScripts.avgPlatformScriptsPerSession),
            valueClass: "text-teal-600",
            note: `${platformRuns} executions total`,
            compact: { label: "Platform scripts / session", note: `${platformRuns} runs` },
          },
          {
            label: "Avg Remediation Scripts / Session",
            value: String(appScripts.avgRemediationScriptsPerSession),
            valueClass: "text-amber-600",
            note: `${remediationRuns} executions total`,
            compact: { label: "Remediations / session", note: `${remediationRuns} runs` },
          },
        ]}
      />
    </UsageSection>
  );
}

const USER_PLACEHOLDER_LABELS = ["Unique Users (90d)", "Daily Logins", "Active (7 Days)", "Active (30 Days)"];

/** Unique, daily and active users; a greyed placeholder while the tenant has no user activity yet. */
export function UsersBlock({ users }: { users: UserMetrics }) {
  const hasData = users.total > 0 || users.dailyLogins > 0 || users.active7Days > 0 || users.active30Days > 0;
  return (
    <UsageSection title="Users">
      {hasData ? (
        <>
          <UsageStatRow
            stats={[
              {
                label: "Unique Users (90d)",
                value: users.total.toLocaleString(),
                valueClass: "text-gray-900",
                compact: { label: "Unique users · 90 d" },
              },
              {
                label: "Daily Logins",
                value: users.dailyLogins.toLocaleString(),
                valueClass: "text-blue-600",
                compact: { label: "Daily logins" },
              },
              {
                label: "Active (Last 7 Days)",
                value: users.active7Days.toLocaleString(),
                valueClass: "text-indigo-600",
                compact: { label: "Active · 7 d" },
              },
              {
                label: "Active (Last 30 Days)",
                value: users.active30Days.toLocaleString(),
                valueClass: "text-purple-600",
                compact: { label: "Active · 30 d" },
              },
            ]}
          />
          {users.note && <p className="mt-3 text-xs text-gray-500">{users.note}</p>}
        </>
      ) : (
        <div className="bg-white rounded-lg shadow p-6">
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4 mb-4 opacity-40">
            {USER_PLACEHOLDER_LABELS.map((label) => (
              <div key={label}>
                <div className="text-sm text-gray-500 mb-1">{label}</div>
                <div className="text-3xl font-bold text-gray-400">--</div>
              </div>
            ))}
          </div>
          <div className="flex items-center space-x-3 pt-3 border-t border-gray-100">
            <svg className="h-5 w-5 text-amber-500 flex-shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <p className="text-sm text-gray-600">{users.note || "User metrics will be available when Entra ID authentication tracking is enabled."}</p>
          </div>
        </div>
      )}
    </UsageSection>
  );
}

function formatDuration(minutes: number): string {
  if (minutes < 60) {
    return `${minutes.toFixed(1)}m`;
  }
  const hours = Math.floor(minutes / 60);
  const mins = Math.round(minutes % 60);
  return `${hours}h ${mins}m`;
}

export function PerformanceBlock({ performance }: { performance: PerformanceMetrics }) {
  return (
    <UsageSection title="Performance">
      <UsageStatRow
        stats={[
          {
            label: "Average Duration",
            value: formatDuration(performance.avgDurationMinutes),
            valueClass: "text-gray-900",
            compact: { label: "Average" },
          },
          {
            label: "Median Duration",
            value: formatDuration(performance.medianDurationMinutes),
            valueClass: "text-blue-600",
            compact: { label: "Median" },
          },
          {
            label: "P95 Duration",
            value: formatDuration(performance.p95DurationMinutes),
            valueClass: "text-orange-600",
            compact: { label: "P95" },
          },
          {
            label: "P99 Duration",
            value: formatDuration(performance.p99DurationMinutes),
            valueClass: "text-red-600",
            compact: { label: "P99" },
          },
        ]}
      />
    </UsageSection>
  );
}

function TopHardwareList({ title, items, barClass }: { title: string; items: HardwareCount[]; barClass: string }) {
  return (
    <div className="bg-white rounded-lg shadow overflow-hidden">
      <div className="px-4 pt-4 text-sm text-gray-500">{title}</div>
      <div className="divide-y divide-gray-200">
        {items.length > 0 ? (
          items.map((item, index) => (
            <ShareRow
              key={index}
              label={item.name || "Unknown"}
              count={item.count}
              percentage={item.percentage}
              barClass={barClass}
              devices={item.deviceCount}
            />
          ))
        ) : (
          <div className="p-8 text-center text-gray-500">No data available</div>
        )}
      </div>
    </div>
  );
}

export function HardwareBlock({ hardware }: { hardware: HardwareMetrics }) {
  return (
    <div>
      <SectionHeading
        title="Hardware"
        description="Ranked by enrollment sessions. A device that was reset and enrolled again adds a session each time but counts as one device."
      />
      <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
        <TopHardwareList title="Top Manufacturers" items={hardware.topManufacturers} barClass="bg-blue-600" />
        <TopHardwareList title="Top Models" items={hardware.topModels} barClass="bg-indigo-600" />
      </div>
    </div>
  );
}
