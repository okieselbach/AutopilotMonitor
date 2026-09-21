"use client";

interface SlaGaugeProps {
  value: number;
  target: number;
  label: string;
  unit: string;
  invert?: boolean;
  /** Nothing finished in the period: neutral card — an empty period is neither on target nor breached. */
  noData?: boolean;
}

/**
 * SLA compliance gauge using a simple arc visualization.
 * Matches the FleetStatCard design language (border-l-4 + colored backgrounds).
 */
export function SlaGauge({ value, target, label, unit, invert = false, noData = false }: SlaGaugeProps) {
  if (noData) {
    return (
      <div className="bg-white dark:bg-gray-800 overflow-hidden shadow rounded-lg border-l-4 border-gray-300 dark:border-gray-600 p-5">
        <div className="flex items-center justify-between mb-3">
          <span className="text-sm font-medium text-gray-500 dark:text-gray-400">{label}</span>
          <span className="text-xs font-medium px-2 py-0.5 rounded-full bg-gray-100 dark:bg-gray-700 text-gray-600 dark:text-gray-300">
            No data
          </span>
        </div>
        <div className="text-3xl font-bold text-gray-400 dark:text-gray-500 mb-1">—</div>
        <div className="w-full h-2 bg-gray-100 dark:bg-gray-700 rounded-full mt-2 mb-2" />
        <div className="text-xs text-gray-400 dark:text-gray-500">
          Target: {target}{unit}
        </div>
      </div>
    );
  }

  const getStatus = (): "met" | "warning" | "breached" => {
    if (invert) {
      if (value <= target) return "met";
      if (value <= target * 1.1) return "warning";
      return "breached";
    } else {
      if (value >= target) return "met";
      if (value >= target * 0.95) return "warning";
      return "breached";
    }
  };

  const status = getStatus();

  const colors = {
    met: { border: "border-green-500", bg: "bg-green-50 dark:bg-green-900/20", text: "text-green-700 dark:text-green-400", badge: "bg-green-100 dark:bg-green-900/40 text-green-700 dark:text-green-400", ring: "#22c55e" },
    warning: { border: "border-yellow-500", bg: "bg-yellow-50 dark:bg-yellow-900/20", text: "text-yellow-700 dark:text-yellow-400", badge: "bg-yellow-100 dark:bg-yellow-900/40 text-yellow-700 dark:text-yellow-400", ring: "#eab308" },
    breached: { border: "border-red-500", bg: "bg-red-50 dark:bg-red-900/20", text: "text-red-700 dark:text-red-400", badge: "bg-red-100 dark:bg-red-900/40 text-red-700 dark:text-red-400", ring: "#ef4444" },
  };

  const c = colors[status];
  const statusLabel = status === "met" ? "On Target" : status === "warning" ? "At Risk" : "Breached";

  // Progress bar percentage (clamped 0-100)
  const progress = invert
    ? Math.max(0, Math.min(100, ((target * 2 - value) / (target * 2)) * 100))
    : Math.max(0, Math.min(100, value));

  return (
    <div className={`bg-white dark:bg-gray-800 overflow-hidden shadow rounded-lg border-l-4 ${c.border} p-5`}>
      <div className="flex items-center justify-between mb-3">
        <span className="text-sm font-medium text-gray-500 dark:text-gray-400">{label}</span>
        <span className={`text-xs font-medium px-2 py-0.5 rounded-full ${c.badge}`}>
          {statusLabel}
        </span>
      </div>
      <div className={`text-3xl font-bold ${c.text} mb-1`}>
        {value.toFixed(1)}<span className="text-lg font-normal ml-0.5">{unit}</span>
      </div>
      <div className="w-full h-2 bg-gray-100 dark:bg-gray-700 rounded-full overflow-hidden mt-2 mb-2">
        <div
          className="h-full rounded-full transition-all duration-500"
          style={{ width: `${progress}%`, backgroundColor: c.ring }}
        />
      </div>
      <div className="text-xs text-gray-400 dark:text-gray-500">
        Target: {target}{unit}
      </div>
    </div>
  );
}
