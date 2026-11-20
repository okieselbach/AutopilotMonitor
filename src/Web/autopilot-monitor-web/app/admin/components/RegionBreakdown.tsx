import { Fragment } from "react";
import type { RegionOverview } from "./tenantRegions";

/**
 * "Europe 412 · North America 63 · … · Not resolved 3": a region overview in one wrapping line, built
 * like the plan line above it (the separators are items of their own, so a wrap keeps them at line ends).
 */
export function RegionBreakdown({ overview, className = "" }: { overview: RegionOverview; className?: string }) {
  const items = [
    ...overview.regions.map((r) => ({ key: r.code, label: r.label, count: r.count })),
    ...(overview.unresolved > 0 ? [{ key: "none", label: "Not resolved", count: overview.unresolved }] : []),
  ];
  return (
    <div className={`flex items-center gap-x-3 gap-y-1 flex-wrap ${className}`} title="Entra tenant region, as Microsoft publishes it">
      {items.map((item, i) => (
        <Fragment key={item.key}>
          {i > 0 && <span aria-hidden="true">·</span>}
          <span>
            {item.label}{" "}
            <span className="font-semibold text-gray-800 tabular-nums">{item.count.toLocaleString()}</span>
          </span>
        </Fragment>
      ))}
    </div>
  );
}
