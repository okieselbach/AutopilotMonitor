import { MAP_LEGEND_NOTE, type MapColorMode } from "./mapColorModes";

/**
 * Legend for the Performance Map: one swatch per bucket of the active colour mode plus the fixed
 * size/selection note. Swatches take their colour from the same `hex` Leaflet paints with, so the
 * legend cannot drift from the markers. Module-level component (react-hooks/static-components).
 */
export function MapLegend({ mode }: { mode: MapColorMode }) {
  return (
    <div className="flex flex-wrap items-center gap-x-4 gap-y-1 text-xs text-gray-600">
      <span className="font-medium text-gray-700">{mode.label}</span>
      {mode.buckets.map((b) => (
        <span key={b.label} className="inline-flex items-center">
          <span
            className="inline-block w-2.5 h-2.5 rounded-full mr-1.5"
            // Markers draw at fillOpacity 0.7; a fully opaque swatch would read darker than the map.
            style={{ backgroundColor: b.hex, opacity: 0.8 }}
          />
          {b.label}
        </span>
      ))}
      <span className="text-gray-400">{MAP_LEGEND_NOTE}</span>
    </div>
  );
}
