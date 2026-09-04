import {
  MAP_LEGEND_NOTE,
  NO_BUCKET_FILTER,
  bucketFilterAllows,
  toggleBucketFilter,
  type BucketFilter,
  type MapColorMode,
} from "./mapColorModes";

interface MapLegendProps {
  mode: MapColorMode;
  /** Buckets currently shown on the map; empty = all. */
  filter: BucketFilter;
  onFilterChange: (next: BucketFilter) => void;
}

/**
 * Legend for the Performance Map: one swatch per bucket of the active colour mode plus the fixed
 * size/selection note. Swatches take their colour from the same `hex` Leaflet paints with, so the
 * legend cannot drift from the markers. Every bucket is a toggle: clicking narrows the map to the
 * selected buckets, clicking the last selected one (or "Show all") returns to the full map.
 * Module-level component (react-hooks/static-components).
 */
export function MapLegend({ mode, filter, onFilterChange }: MapLegendProps) {
  const filtered = filter.size > 0;
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-gray-600">
      <span className="font-medium text-gray-700">{mode.label}</span>
      {mode.buckets.map((b) => {
        const shown = bucketFilterAllows(filter, b);
        return (
          <button
            key={b.label}
            type="button"
            aria-pressed={filtered && shown}
            title={shown && !filtered ? "Show only this bucket" : shown ? "Hide this bucket" : "Show this bucket"}
            onClick={() => onFilterChange(toggleBucketFilter(filter, b))}
            className={`inline-flex items-center rounded px-1 py-0.5 hover:bg-gray-100 focus:outline-none focus:ring-2 focus:ring-green-500 ${
              shown ? "" : "opacity-40"
            }`}
          >
            <span
              className="inline-block w-2.5 h-2.5 rounded-full mr-1.5"
              // Markers draw at fillOpacity 0.7; a fully opaque swatch would read darker than the map.
              style={{ backgroundColor: b.hex, opacity: 0.8 }}
            />
            {b.label}
          </button>
        );
      })}
      {filtered && (
        <button
          type="button"
          onClick={() => onFilterChange(NO_BUCKET_FILTER)}
          className="text-blue-600 hover:underline"
        >
          Show all
        </button>
      )}
      <span className="text-gray-400">{MAP_LEGEND_NOTE}</span>
    </div>
  );
}
