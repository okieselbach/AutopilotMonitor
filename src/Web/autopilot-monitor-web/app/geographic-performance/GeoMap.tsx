"use client";

import { useEffect, useMemo, useRef } from "react";
import { MapContainer, TileLayer, CircleMarker, Tooltip, useMap } from "react-leaflet";
import "leaflet/dist/leaflet.css";

interface LocationMetric {
  locationKey: string;
  country: string;
  region: string;
  city: string;
  loc: string;
  sessionCount: number;
  succeeded: number;
  failed: number;
  avgDurationMinutes: number;
  appLoadScore: number;
  /** Over finished enrollments (succeeded + failed) only; 0 when nothing finished yet. */
  successRate: number;
  avgThroughputBytesPerSec: number;
  isOutlier: boolean;
  outlierDirection: string | null;
}

interface GeoMapProps {
  locations: LocationMetric[];
  globalAvgDuration: number;
  selectedLocation: string | null;
  onLocationSelect: (key: string | null) => void;
}

function parseCoords(loc: string): [number, number] | null {
  if (!loc) return null;
  const parts = loc.split(",").map((s) => parseFloat(s.trim()));
  if (parts.length === 2 && !isNaN(parts[0]) && !isNaN(parts[1])) {
    return [parts[0], parts[1]];
  }
  return null;
}

function getMarkerColor(avgDuration: number, globalAvg: number): string {
  if (globalAvg <= 0) return "#6B7280";
  const ratio = avgDuration / globalAvg;
  if (ratio <= 0.7) return "#059669"; // green-600
  if (ratio <= 0.9) return "#10B981"; // green-500
  if (ratio <= 1.1) return "#F59E0B"; // yellow-500
  if (ratio <= 1.3) return "#F97316"; // orange-500
  return "#EF4444"; // red-500
}

function getMarkerRadius(sessionCount: number, maxSessions: number): number {
  if (maxSessions <= 0) return 8;
  const normalized = sessionCount / maxSessions;
  return Math.max(6, Math.min(25, 6 + normalized * 19));
}

function formatThroughput(bytesPerSec: number): string {
  if (bytesPerSec <= 0) return "—";
  if (bytesPerSec >= 1024 * 1024) return `${(bytesPerSec / 1024 / 1024).toFixed(1)} MB/s`;
  if (bytesPerSec >= 1024) return `${(bytesPerSec / 1024).toFixed(0)} KB/s`;
  return `${bytesPerSec.toFixed(0)} B/s`;
}

// Clean up Leaflet map on unmount to prevent _leaflet_pos errors during client-side navigation.
// map.remove() fully destroys the instance, preventing stale transitionend callbacks.
function MapCleanup() {
  const map = useMap();
  const mapRef = useRef(map);
  mapRef.current = map;

  useEffect(() => {
    return () => {
      try { mapRef.current.remove(); } catch { /* already removed by react-leaflet */ }
    };
  }, []);

  return null;
}

// Component to auto-fit bounds when locations change
function FitBounds({ locations }: { locations: { coords: [number, number] }[] }) {
  const map = useMap();

  useEffect(() => {
    if (locations.length === 0) return;
    const bounds = locations.map((l) => l.coords) as [number, number][];
    // animate: false applies the camera move synchronously. An animated move schedules a
    // deferred requestAnimationFrame/transitionend callback that reads the map pane's
    // _leaflet_pos; if the component unmounts (client-side navigation) before it fires, the
    // pane is already torn down and the callback throws "Cannot read properties of undefined
    // (reading '_leaflet_pos')". A synchronous move leaves no such dangling callback.
    if (bounds.length === 1) {
      map.setView(bounds[0], 6, { animate: false });
    } else {
      map.fitBounds(bounds, { padding: [30, 30], maxZoom: 10, animate: false });
    }
  }, [locations, map]);

  return null;
}

export default function GeoMap({ locations, globalAvgDuration, selectedLocation, onLocationSelect }: GeoMapProps) {
  // Memoize so the derived array keeps a stable identity across re-renders. FitBounds' effect
  // depends on this list; a fresh array every render would re-run it (and re-fire the camera
  // move) on every parent re-render, not just when the location data actually changes.
  const mappableLocations = useMemo(
    () =>
      locations
        .map((loc) => {
          const coords = parseCoords(loc.loc);
          return coords ? { ...loc, coords } : null;
        })
        .filter((l): l is NonNullable<typeof l> => l !== null),
    [locations]
  );

  if (mappableLocations.length === 0) {
    return (
      <div className="flex items-center justify-center h-full text-gray-500">
        No location coordinates available for map display.
      </div>
    );
  }

  const maxSessions = Math.max(...mappableLocations.map((l) => l.sessionCount));

  return (
    <MapContainer
      center={[30, 0]}
      zoom={2}
      style={{ height: "100%", width: "100%", borderRadius: "0.5rem", zIndex: 0 }}
      scrollWheelZoom={true}
    >
      <MapCleanup />
      <TileLayer
        attribution='&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>'
        url="https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png"
      />
      <FitBounds locations={mappableLocations} />
      {mappableLocations.map((loc) => {
        const isSelected = selectedLocation === loc.locationKey;
        return (
          <CircleMarker
            key={loc.locationKey}
            center={loc.coords}
            radius={getMarkerRadius(loc.sessionCount, maxSessions)}
            pathOptions={{
              color: isSelected ? "#2563EB" : getMarkerColor(loc.avgDurationMinutes, globalAvgDuration),
              fillColor: getMarkerColor(loc.avgDurationMinutes, globalAvgDuration),
              fillOpacity: isSelected ? 0.9 : 0.7,
              weight: isSelected ? 3 : 1.5,
            }}
            eventHandlers={{
              click: () => {
                onLocationSelect(loc.locationKey);
                const el = document.getElementById(
                  `loc-${loc.locationKey.replace(/[^a-zA-Z0-9]/g, "-")}`
                );
                el?.scrollIntoView({ behavior: "smooth", block: "center" });
              },
            }}
          >
            <Tooltip direction="top" offset={[0, -10]} opacity={0.95}>
              <div className="text-xs">
                <div className="font-bold text-sm mb-1">{loc.locationKey}</div>
                <div>
                  <strong>Sessions:</strong> {loc.sessionCount}
                  {loc.succeeded + loc.failed > 0 ? ` (${loc.successRate}% success)` : " (none finished yet)"}
                </div>
                <div><strong>Avg Duration:</strong> {Math.round(loc.avgDurationMinutes)} min</div>
                {loc.appLoadScore > 0 && (
                  <div><strong>App-Load-Score:</strong> {Math.round(loc.appLoadScore)}</div>
                )}
                {loc.avgThroughputBytesPerSec > 0 && (
                  <div><strong>Throughput:</strong> {formatThroughput(loc.avgThroughputBytesPerSec)}</div>
                )}
                {loc.isOutlier && (
                  <div className="mt-1 font-bold text-red-600">
                    {loc.outlierDirection === "slow" ? "Slow Outlier" : "Fast Outlier"}
                  </div>
                )}
              </div>
            </Tooltip>
          </CircleMarker>
        );
      })}
    </MapContainer>
  );
}
