"use client";

export default function FleetStatCard({
  title,
  value,
  subtitle,
  color,
}: {
  title: string;
  value: string;
  subtitle: string;
  color: "green" | "blue" | "red" | "yellow" | "slate";
}) {
  const colorClasses = {
    green: "border-green-500 bg-green-50",
    blue: "border-blue-500 bg-blue-50",
    red: "border-red-500 bg-red-50",
    yellow: "border-yellow-500 bg-yellow-50",
    slate: "border-slate-400 bg-slate-50",
  };

  const valueColors = {
    green: "text-green-700",
    blue: "text-blue-700",
    red: "text-red-700",
    yellow: "text-yellow-700",
    slate: "text-slate-600",
  };

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
