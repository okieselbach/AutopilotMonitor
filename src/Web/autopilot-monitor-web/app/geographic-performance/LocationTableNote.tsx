import { DocsLink } from "@/components/DocsLink";
import { DOCS_PATHS } from "@/lib/docsPaths";

/**
 * What the duration columns of the Location Performance table are computed over. Mirrors the
 * backend aggregation (succeeded sessions per location; the benchmark line is the reader's own
 * fleet) so a reader never has to guess whether "vs Global" is avg, median or P95 based, or
 * whether "global" means other customers.
 */
export const LOCATION_TABLE_NOTE = {
  duration: "Avg Duration and P95 = enrollment duration of the succeeded sessions at the location in the selected range",
  vsGlobal: "vs Global = Avg Duration against the benchmark line above (your own fleet, same range; positive = slower)",
} as const;

export function LocationTableNote() {
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-t border-gray-200 px-4 py-2.5 text-xs text-gray-500">
      <span>{LOCATION_TABLE_NOTE.duration}</span>
      <span className="text-gray-300" aria-hidden="true">
        ·
      </span>
      <span>{LOCATION_TABLE_NOTE.vsGlobal}</span>
      <DocsLink path={DOCS_PATHS.geographicPerformanceNumbers} label="How the numbers are calculated" />
      <DocsLink path={DOCS_PATHS.statistics} label="Averages, medians & percentiles" />
    </div>
  );
}
