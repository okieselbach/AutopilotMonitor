/**
 * One MCP quota window as a bar: used of limit, indigo up to 80 %, amber from 80 %, red at the limit.
 * A used value below 0 means the counter was unavailable ("n/a"); a limit of 0 means unlimited. The
 * optional breakdown names the plan base plus purchased delegation slots behind a grown limit.
 */
export function QuotaBar({ label, used, limit, breakdown }: { label: string; used: number; limit: number; breakdown?: string | null }) {
  const unavailable = used < 0;
  const unlimited = limit <= 0;
  const pct = unavailable || unlimited ? 0 : Math.min(100, Math.round((used / limit) * 100));
  const tone = pct >= 100 ? "bg-red-500" : pct >= 80 ? "bg-amber-500" : "bg-indigo-500";
  return (
    <div>
      <div className="flex items-start justify-between text-xs text-gray-600 mb-1">
        <span>{label}</span>
        <span className="font-mono text-right">
          {unavailable ? "n/a" : used.toLocaleString()}
          {" / "}
          {unlimited ? "unlimited" : limit.toLocaleString()}
          {breakdown && (
            <span className="block text-gray-400" title="Plan base + purchased delegation slots × growth per slot">
              {breakdown}
            </span>
          )}
        </span>
      </div>
      <div className="bg-gray-100 rounded-full h-2">
        <div className={`${tone} h-2 rounded-full transition-all`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}
