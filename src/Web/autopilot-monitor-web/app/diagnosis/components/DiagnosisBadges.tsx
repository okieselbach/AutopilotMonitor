"use client";

export function ConfidenceBadge({
  score,
  compact = false,
}: {
  score: number;
  compact?: boolean;
}) {
  const color =
    score >= 80
      ? "text-red-600 bg-red-50 border-red-200"
      : score >= 60
      ? "text-orange-600 bg-orange-50 border-orange-200"
      : score >= 40
      ? "text-yellow-600 bg-yellow-50 border-yellow-200"
      : "text-blue-600 bg-blue-50 border-blue-200";

  if (compact) {
    return (
      <span
        className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-bold border ${color}`}
      >
        {score}%
      </span>
    );
  }

  return (
    <div className={`inline-flex items-center space-x-1 px-3 py-1 rounded-lg border ${color}`}>
      <span className="text-sm font-bold">{score}%</span>
      <span className="text-xs opacity-75">CONFIDENCE</span>
    </div>
  );
}

export function SeverityBadge({
  severity,
  compact = false,
}: {
  severity: string;
  compact?: boolean;
}) {
  const colors: Record<string, string> = {
    critical: "bg-red-100 text-red-800 border-red-200",
    high: "bg-orange-100 text-orange-800 border-orange-200",
    warning: "bg-yellow-100 text-yellow-800 border-yellow-200",
    info: "bg-blue-100 text-blue-800 border-blue-200",
  };

  const c = colors[severity] || colors.info;

  return (
    <span
      className={`inline-flex items-center px-2 py-0.5 rounded text-xs font-medium border ${c}`}
    >
      {compact ? severity.charAt(0).toUpperCase() : severity.toUpperCase()}
    </span>
  );
}
