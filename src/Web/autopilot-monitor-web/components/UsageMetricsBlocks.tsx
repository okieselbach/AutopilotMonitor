import type { ReactNode } from "react";
import type {
  AppScriptMetrics,
  DeploymentTypeMetrics,
  HardwareCount,
  HardwareMetrics,
  PerformanceMetrics,
  SessionMetrics,
} from "@/lib/generated/wire-types.generated";
import { ENROLLMENT_CLASS_LABELS } from "@/lib/enrollmentClass";

// The blocks the tenant Usage Metrics page and the platform Usage Metrics section share.

function StatCard({ label, title, valueClass, note, children }: {
  label: string;
  title?: string;
  valueClass: string;
  note?: string;
  children: ReactNode;
}) {
  return (
    <div className="bg-white rounded-lg shadow p-6">
      <div className="text-sm text-gray-500 mb-1" title={title}>{label}</div>
      <div className={`text-3xl font-bold ${valueClass}`}>{children}</div>
      {note != null && <p className="text-xs text-gray-400 mt-1">{note}</p>}
    </div>
  );
}

/** Today / last 7 / last 30 days: three cards for the page's own session grid. */
export function SessionRecencyCards({ sessions }: { sessions: SessionMetrics }) {
  return (
    <>
      <StatCard label="Today" valueClass="text-blue-600">{sessions.today.toLocaleString()}</StatCard>
      <StatCard label="Last 7 Days" valueClass="text-indigo-600">{sessions.last7Days.toLocaleString()}</StatCard>
      <StatCard label="Last 30 Days" valueClass="text-purple-600">{sessions.last30Days.toLocaleString()}</StatCard>
    </>
  );
}

export function SessionOutcomeCards({ sessions }: { sessions: SessionMetrics }) {
  return (
    <div className="grid grid-cols-1 md:grid-cols-5 gap-4 mt-4">
      <StatCard label="Succeeded" valueClass="text-green-600">{sessions.succeeded.toLocaleString()}</StatCard>
      <StatCard label="Failed" valueClass="text-red-600">{sessions.failed.toLocaleString()}</StatCard>
      <StatCard
        label="Incomplete"
        title="Terminal, non-failure: timed out with no completion or failure signal. Excluded from the success rate."
        valueClass="text-slate-500"
      >
        {sessions.incomplete.toLocaleString()}
      </StatCard>
      <StatCard label="In Progress" valueClass="text-yellow-600">{sessions.inProgress.toLocaleString()}</StatCard>
      <StatCard label="Success Rate" valueClass="text-gray-900">{sessions.successRate}%</StatCard>
    </div>
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
    <div className="mb-6">
      <h2 className="text-xl font-semibold text-gray-900 mb-4">Deployment Types</h2>
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
    </div>
  );
}

export function AppScriptsBlock({ appScripts }: { appScripts: AppScriptMetrics }) {
  return (
    <div className="mb-6">
      <h2 className="text-xl font-semibold text-gray-900 mb-4">Apps & Scripts</h2>
      <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
        <StatCard
          label="Avg Apps / Session"
          valueClass="text-blue-600"
          note={`${appScripts.totalUniqueApps.toLocaleString()} unique apps total`}
        >
          {appScripts.avgAppsPerSession}
        </StatCard>
        <StatCard
          label="Avg Platform Scripts / Session"
          valueClass="text-teal-600"
          note={`${appScripts.totalPlatformScripts.toLocaleString()} executions total`}
        >
          {appScripts.avgPlatformScriptsPerSession}
        </StatCard>
        <StatCard
          label="Avg Remediation Scripts / Session"
          valueClass="text-amber-600"
          note={`${appScripts.totalRemediationScripts.toLocaleString()} executions total`}
        >
          {appScripts.avgRemediationScriptsPerSession}
        </StatCard>
      </div>
    </div>
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
    <div className="mb-6">
      <h2 className="text-xl font-semibold text-gray-900 mb-4">Performance</h2>
      <div className="grid grid-cols-1 md:grid-cols-4 gap-4">
        <StatCard label="Average Duration" valueClass="text-gray-900">{formatDuration(performance.avgDurationMinutes)}</StatCard>
        <StatCard label="Median Duration" valueClass="text-blue-600">{formatDuration(performance.medianDurationMinutes)}</StatCard>
        <StatCard label="P95 Duration" valueClass="text-orange-600">{formatDuration(performance.p95DurationMinutes)}</StatCard>
        <StatCard label="P99 Duration" valueClass="text-red-600">{formatDuration(performance.p99DurationMinutes)}</StatCard>
      </div>
    </div>
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
      <h2 className="text-xl font-semibold text-gray-900 mb-1">Hardware</h2>
      <p className="text-sm text-gray-500 mb-4">
        Ranked by enrollment sessions. A device that was reset and enrolled again adds a session each time but counts as one device.
      </p>
      <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
        <TopHardwareList title="Top Manufacturers" items={hardware.topManufacturers} barClass="bg-blue-600" />
        <TopHardwareList title="Top Models" items={hardware.topModels} barClass="bg-indigo-600" />
      </div>
    </div>
  );
}
