"use client";

import { MapContainer, TileLayer, CircleMarker, Tooltip } from "react-leaflet";
import "leaflet/dist/leaflet.css";
import { chartColors } from "@/components/charts/chartTheme";
import type { RegionCount } from "../../components/tenantRegions";

type PlacedRegion = RegionCount & { anchor: readonly [number, number] };

/** Bubble radius in px: the area grows with the count, so the largest region does not swallow the others. */
function radius(count: number, max: number): number {
  return max > 0 ? 10 + 26 * Math.sqrt(count / max) : 10;
}

/**
 * Customers per Entra region: one bubble per continent with its count written on it. The initial view
 * fits every bubble into the card; regions without a place (Worldwide, unknown codes) are listed below
 * the map by the caller.
 */
export default function CustomerRegionMap({ regions }: { regions: RegionCount[] }) {
  const placed = regions.filter((r): r is PlacedRegion => r.anchor != null);
  const max = Math.max(0, ...placed.map((r) => r.count));
  const view =
    placed.length > 1
      ? { bounds: placed.map((r) => [r.anchor[0], r.anchor[1]] as [number, number]), boundsOptions: { padding: [48, 48] as [number, number] } }
      : { center: placed[0] ? ([placed[0].anchor[0], placed[0].anchor[1]] as [number, number]) : ([25, 10] as [number, number]), zoom: placed[0] ? 3 : 1 };

  return (
    <MapContainer
      {...view}
      scrollWheelZoom={false}
      style={{ height: "100%", width: "100%", borderRadius: "0.5rem", zIndex: 0 }}
    >
      <TileLayer
        attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
        url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
      />
      {placed.map((r) => (
        <CircleMarker
          key={r.code}
          center={[r.anchor[0], r.anchor[1]]}
          radius={radius(r.count, max)}
          pathOptions={{ color: chartColors.primary, fillColor: chartColors.primary, fillOpacity: 0.55, weight: 1.5 }}
        >
          <Tooltip permanent direction="center" opacity={1}>
            <span className="text-xs font-semibold tabular-nums">{r.count.toLocaleString()}</span>
          </Tooltip>
        </CircleMarker>
      ))}
    </MapContainer>
  );
}
