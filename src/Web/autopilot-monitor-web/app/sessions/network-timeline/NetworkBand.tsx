'use client';

// ─────────────────────────────────────────────────────────────────────────────
// Network timeline — the band chart (SVG).
//
// Phase context lane, one colored segment per network, offline gaps hatched,
// lifecycle + connectivity-check markers, hover/tap tooltips. Mobile keeps a
// minimum width and scrolls horizontally.
// ─────────────────────────────────────────────────────────────────────────────

import { useCallback, useRef, useState } from 'react';
import {
  fmtDuration,
  fmtTime,
  HotspotHint,
  NetworkModel,
  OFFLINE_COLOR,
  segmentTitle,
  SegmentKind,
  UNKNOWN_COLOR,
  W,
} from './networkTimelineModel';

interface TooltipState {
  x: number;
  y: number;
  maxLeft: number;
  content: React.ReactNode;
}

export default function NetworkBand({ model }: { model: NetworkModel }) {
  const wrapRef = useRef<HTMLDivElement>(null);
  const [tooltip, setTooltip] = useState<TooltipState | null>(null);

  const showTooltip = useCallback((e: React.MouseEvent, content: React.ReactNode) => {
    const rect = wrapRef.current?.getBoundingClientRect();
    if (!rect) return;
    setTooltip({ x: e.clientX - rect.left, y: e.clientY - rect.top, maxLeft: rect.width - 260, content });
  }, []);
  const hideTooltip = useCallback(() => setTooltip(null), []);

  const { segments, colors, scale, phases, checks, lifeMarkers } = model;

  const PHASE_Y = 8;
  const PHASE_H = 22;
  const BAND_Y = 40;
  const BAND_H = 58;
  const TICK_Y = BAND_Y + BAND_H;
  const H = 172;

  // Boundary ticks: real times at every segment boundary, staggered rows,
  // labels suppressed when they would collide.
  const ticks: { x: number; t: number; row: number }[] = [];
  const lastX = [-100, -100];
  scale.tB.forEach((t, i) => {
    const x = scale.xB[i];
    const row = ticks.length % 2;
    if (x - lastX[row] > 60) {
      ticks.push({ x, t, row });
      lastX[row] = x;
    }
  });

  // Legend entries in first-seen order; the strongest hotspot hint wins.
  const legend: { identity: string; label: string; color: string; kind: SegmentKind; hotspot?: HotspotHint }[] = [];
  for (const seg of segments) {
    const existing = legend.find((l) => l.identity === seg.identity);
    if (existing) {
      if (seg.hotspot && (!existing.hotspot || (seg.hotspot.confident && !existing.hotspot.confident))) {
        existing.hotspot = seg.hotspot;
      }
      continue;
    }
    legend.push({
      identity: seg.identity,
      label: segmentTitle(seg),
      color: colors.get(seg.identity) ?? UNKNOWN_COLOR,
      kind: seg.kind,
      hotspot: seg.hotspot,
    });
  }

  return (
    <div className="mt-3">
      <div className="flex flex-wrap gap-x-4 gap-y-1 mb-2 text-xs text-gray-700">
        {legend.map((l) => (
          <span key={l.identity} className="inline-flex items-center gap-1.5">
            {l.kind === 'offline' ? (
              <svg width="14" height="10" className="rounded-sm">
                <rect width="14" height="10" fill="none" stroke={OFFLINE_COLOR} strokeWidth="1" />
                <line x1="0" y1="10" x2="10" y2="0" stroke={OFFLINE_COLOR} strokeWidth="1.5" />
                <line x1="6" y1="12" x2="16" y2="2" stroke={OFFLINE_COLOR} strokeWidth="1.5" />
              </svg>
            ) : (
              <span className="w-3.5 h-2.5 rounded-sm inline-block" style={{ backgroundColor: l.color }} />
            )}
            {l.label}
            {l.hotspot && (
              <span className="text-[10px] px-1 py-px rounded bg-amber-100 text-amber-800 font-medium">
                {l.hotspot.confident ? 'Hotspot' : 'Hotspot?'}
              </span>
            )}
          </span>
        ))}
        <span className="inline-flex items-center gap-1.5 ml-auto text-gray-500">
          <span className="inline-flex w-3.5 h-3.5 rounded-full bg-green-600 text-white items-center justify-center text-[9px] leading-none">
            ✓
          </span>
          Connectivity check after switch
        </span>
      </div>

      {/* Mobile: horizontal scroll for the band. The tooltip lives OUTSIDE the
          scroll container (on the relative wrapper) so it can neither be
          clipped nor spawn scrollbars when it extends past the band. */}
      <div ref={wrapRef} className="relative">
      <div className="overflow-x-auto">
      <div className="min-w-[700px]">
        <svg viewBox={`0 0 ${W} ${H}`} className="w-full select-none" role="img" aria-label="Netzwerk-Timeline">
          <defs>
            <pattern id="offlineHatch" width="8" height="8" patternTransform="rotate(45)" patternUnits="userSpaceOnUse">
              <rect width="8" height="8" fill="transparent" />
              <line x1="0" y1="0" x2="0" y2="8" stroke={OFFLINE_COLOR} strokeWidth="2.5" />
            </pattern>
          </defs>

          {/* Phase context lane */}
          {phases.map((p, i) => {
            const x0 = scale.x(p.start);
            const x1 = scale.x(p.end);
            if (x1 - x0 < 1) return null;
            const tip = (
              <div>
                <div className="font-semibold">{p.name}</div>
                <div>
                  {fmtTime(p.start)} – {fmtTime(p.end)} ({fmtDuration(p.end - p.start)})
                </div>
              </div>
            );
            return (
              <g key={`ph-${i}`}>
                <rect
                  x={x0}
                  y={PHASE_Y}
                  width={x1 - x0}
                  height={PHASE_H}
                  className={i % 2 === 0 ? 'fill-gray-100' : 'fill-gray-200'}
                  onMouseMove={(e) => showTooltip(e, tip)}
                  onClick={(e) => showTooltip(e, tip)}
                  onMouseLeave={hideTooltip}
                />
                {x1 - x0 > 70 && (
                  <text
                    x={(x0 + x1) / 2}
                    y={PHASE_Y + PHASE_H / 2 + 4}
                    textAnchor="middle"
                    fontSize="11"
                    className="fill-gray-600 pointer-events-none"
                  >
                    {p.name}
                  </text>
                )}
              </g>
            );
          })}

          {/* Network band */}
          {segments.map((seg, i) => {
            const x0 = scale.xB[i];
            const x1 = scale.xB[i + 1];
            const color = colors.get(seg.identity) ?? UNKNOWN_COLOR;
            const w = x1 - x0 - 2; // 2px spacer between fills
            // Hotspot classification lives ONLY in the legend badge (and tooltip) —
            // no marker on the segment itself, and no emoji in SVG <text> (renders broken).
            const label = segmentTitle(seg);
            const sub =
              seg.kind === 'wifi' && seg.signalPercent != null
                ? `${seg.signalPercent}%`
                : seg.linkSpeedMbps
                  ? `${seg.linkSpeedMbps} Mbit`
                  : '';
            const tip = (
              <div className="space-y-0.5">
                <div className="font-semibold">{segmentTitle(seg)}</div>
                <div>
                  {fmtTime(seg.start)} – {fmtTime(seg.end)} · {fmtDuration(seg.end - seg.start)}
                </div>
                {seg.ip && <div>IP: {seg.ip}</div>}
                {seg.gateway && <div>Gateway: {seg.gateway}</div>}
                {seg.linkSpeedMbps != null && <div>Link: {seg.linkSpeedMbps} Mbit/s</div>}
                {seg.signalPercent != null && (
                  <div>
                    Signal: {seg.signalPercent}%{seg.radioType ? ` (${seg.radioType})` : ''}
                  </div>
                )}
                {seg.adapterDescription && <div>{seg.adapterDescription}</div>}
                {seg.hotspot && (
                  <div className="text-amber-300">
                    {`${seg.hotspot.confident ? 'Smartphone hotspot' : 'Probable smartphone hotspot'} (${seg.hotspot.vendor}) — ${seg.hotspot.reason}`}
                  </div>
                )}
                {seg.ssidInferred && <div className="text-amber-300">SSID inferred from matching gateway</div>}
              </div>
            );
            return (
              <g key={`seg-${i}`}>
                <rect
                  x={x0 + 1}
                  y={BAND_Y}
                  width={Math.max(w, 4)}
                  height={BAND_H}
                  rx="4"
                  fill={seg.kind === 'offline' ? 'url(#offlineHatch)' : color}
                  stroke={seg.kind === 'offline' ? OFFLINE_COLOR : 'none'}
                  strokeWidth={seg.kind === 'offline' ? 1.5 : 0}
                  fillOpacity={seg.kind === 'offline' ? 1 : 0.92}
                  onMouseMove={(e) => showTooltip(e, tip)}
                  onClick={(e) => showTooltip(e, tip)}
                  onMouseLeave={hideTooltip}
                />
                {w > 60 && (
                  <text
                    x={x0 + (x1 - x0) / 2}
                    y={BAND_Y + BAND_H / 2 - 2}
                    textAnchor="middle"
                    fontSize="12"
                    fontWeight="600"
                    fill="#ffffff"
                    className="pointer-events-none"
                    style={seg.kind === 'offline' ? { fill: OFFLINE_COLOR } : undefined}
                  >
                    {label}
                  </text>
                )}
                {w > 60 && sub && seg.kind !== 'offline' && (
                  <text
                    x={x0 + (x1 - x0) / 2}
                    y={BAND_Y + BAND_H / 2 + 16}
                    textAnchor="middle"
                    fontSize="10"
                    fill="#ffffff"
                    fillOpacity="0.85"
                    className="pointer-events-none"
                  >
                    {sub}
                  </text>
                )}
              </g>
            );
          })}

          {/* Lifecycle markers (reboot / desktop / terminal) */}
          {lifeMarkers.map((m, i) => {
            const x = Math.min(Math.max(scale.x(m.t), 6), W - 6);
            const tip = (
              <div>
                <div className="font-semibold">{m.label}</div>
                <div>{fmtTime(m.t)}</div>
              </div>
            );
            return (
              <g key={`life-${i}`}>
                <line
                  x1={x}
                  y1={PHASE_Y}
                  x2={x}
                  y2={TICK_Y}
                  stroke="currentColor"
                  strokeDasharray="3 3"
                  strokeWidth="1"
                  className="text-gray-400"
                />
                <circle
                  cx={x}
                  cy={PHASE_Y - 1}
                  r="4.5"
                  className="fill-gray-500"
                  onMouseMove={(e) => showTooltip(e, tip)}
                  onClick={(e) => showTooltip(e, tip)}
                  onMouseLeave={hideTooltip}
                />
              </g>
            );
          })}

          {/* Connectivity check markers */}
          {checks.map((c, i) => {
            // Clamp so the circle (r=8 + 2 stroke) never clips at the viewBox edges
            const x = Math.min(Math.max(scale.x(c.t), 11), W - 11);
            const tip = (
              <div>
                <div className="font-semibold mb-1">
                  Connectivity check · {c.reachable}/{c.total} reachable
                </div>
                <div>{fmtTime(c.t)}</div>
                {c.results.map((r) => (
                  <div key={r.endpoint} className="flex gap-2">
                    <span>{r.reachable ? '✓' : '✗'}</span>
                    <span>{r.endpoint}</span>
                    <span className="opacity-70">
                      {r.reachable ? `${r.latencyMs} ms` : (r.error ?? 'unreachable')}
                    </span>
                  </div>
                ))}
              </div>
            );
            return (
              <g
                key={`chk-${i}`}
                onMouseMove={(e) => showTooltip(e, tip)}
                onClick={(e) => showTooltip(e, tip)}
                onMouseLeave={hideTooltip}
              >
                <circle
                  cx={x}
                  cy={BAND_Y + BAND_H - 1}
                  r="8"
                  fill={c.ok ? '#16a34a' : '#d97706'}
                  stroke="#ffffff"
                  strokeWidth="2"
                />
                <text
                  x={x}
                  y={BAND_Y + BAND_H + 3}
                  textAnchor="middle"
                  fontSize="10"
                  fontWeight="700"
                  fill="#ffffff"
                  className="pointer-events-none"
                >
                  {c.ok ? '✓' : '!'}
                </text>
              </g>
            );
          })}

          {/* Boundary ticks with real times */}
          {ticks.map((tk, i) => {
            // Edge ticks anchor their label flush to the tick line instead of
            // clamping a centered label sideways (which reads as a stray dash).
            const anchor = tk.x < 30 ? 'start' : tk.x > W - 30 ? 'end' : 'middle';
            return (
              <g key={`tick-${i}`}>
                <line
                  x1={tk.x}
                  y1={TICK_Y}
                  x2={tk.x}
                  y2={TICK_Y + 6}
                  stroke="currentColor"
                  strokeWidth="1"
                  className="text-gray-400"
                />
                <text
                  x={tk.x}
                  y={TICK_Y + 20 + tk.row * 14}
                  textAnchor={anchor}
                  fontSize="10"
                  className="fill-gray-500"
                >
                  {fmtTime(tk.t)}
                </text>
              </g>
            );
          })}
        </svg>
      </div>
      </div>

        {tooltip && (
          <div
            className="absolute z-10 pointer-events-none bg-gray-900/95 text-gray-100 text-xs rounded-md px-3 py-2 shadow-lg max-w-xs"
            style={{ left: Math.max(0, Math.min(tooltip.x + 12, tooltip.maxLeft)), top: tooltip.y + 14 }}
          >
            {tooltip.content}
          </div>
        )}
      </div>

      <p className="mt-2 text-[11px] text-gray-400">
        Time axis: linear within each segment; very short segments are stretched to a minimum width — the ticks at the
        segment boundaries show the real times. * = inferred SSID.
      </p>
    </div>
  );
}
