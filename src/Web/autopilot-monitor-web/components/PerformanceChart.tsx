"use client";

import { useMemo, useState, useRef, useCallback, useEffect } from "react";

interface PerformanceEvent {
  timestamp: string;
  data?: Record<string, unknown>;
}

// Minimal structural view of the perf-sample payload: only the fields this chart reads
// are declared (the agent serializes them as strings); everything else stays `unknown`
// via the index signature.
interface PerformanceEventData {
  [key: string]: unknown;
  cpu_percent?: string;
  cpuPercent?: string;
  memory_available_mb?: string;
  memoryAvailableMb?: string;
  memory_total_mb?: string;
  memoryTotalMb?: string;
  disk_queue_length?: string;
  diskQueueLength?: string;
  disk_free_gb?: string;
  diskFreeGb?: string;
  disk_total_gb?: string;
  diskTotalGb?: string;
  net_receive_rate_kbps?: string;
  netReceiveRateKbps?: string;
  net_send_rate_kbps?: string;
  netSendRateKbps?: string;
}

interface PerformanceChartProps {
  events: PerformanceEvent[];
  expanded?: boolean;
  setExpanded?: (expanded: boolean) => void;
}

interface SparklineProps {
  values: number[];
  maxValue: number;
  color: string;
  timestamps: string[];
  unit: string;
  formatValue?: (v: number) => string;
}

function Sparkline({ values, maxValue, color, timestamps, unit, formatValue }: SparklineProps) {
  const [hoveredIndex, setHoveredIndex] = useState<number | null>(null);
  const [svgWidth, setSvgWidth] = useState(300);
  const containerRef = useRef<HTMLDivElement>(null);

  const height = 40;
  const padding = 2;
  const chartWidth = svgWidth - padding * 2;
  const chartHeight = height - padding * 2;

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const obs = new ResizeObserver((entries) => {
      for (const entry of entries) setSvgWidth(Math.round(entry.contentRect.width));
    });
    obs.observe(el);
    return () => obs.disconnect();
  }, []);

  const getX = useCallback((i: number) => padding + (i / Math.max(1, values.length - 1)) * chartWidth, [values.length, chartWidth]);
  const getY = useCallback((v: number) => padding + chartHeight - (Math.min(v, maxValue) / maxValue) * chartHeight, [chartHeight, maxValue]);

  const points = values.map((v, i) => `${getX(i)},${getY(v)}`);
  const areaPoints = [
    `${padding},${height - padding}`,
    ...points,
    `${svgWidth - padding},${height - padding}`,
  ];

  const handleMouseMove = useCallback((e: React.MouseEvent<SVGSVGElement>) => {
    const el = containerRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const mouseX = e.clientX - rect.left;
    const relX = mouseX - padding;
    if (relX < 0 || relX > chartWidth) {
      setHoveredIndex(null);
      return;
    }
    const idx = Math.round((relX / chartWidth) * (values.length - 1));
    setHoveredIndex(Math.max(0, Math.min(idx, values.length - 1)));
  }, [chartWidth, values.length]);

  if (values.length < 2) return null;

  const fmt = formatValue ?? ((v: number) => `${v.toFixed(1)}${unit}`);

  return (
    <div ref={containerRef}>
      <svg
        width={svgWidth}
        height={height}
        viewBox={`0 0 ${svgWidth} ${height}`}
        className="block cursor-crosshair"
        onMouseMove={handleMouseMove}
        onMouseLeave={() => setHoveredIndex(null)}
      >
        <polygon
          points={areaPoints.join(" ")}
          fill={color}
          fillOpacity="0.1"
        />
        <polyline
          points={points.join(" ")}
          fill="none"
          stroke={color}
          strokeWidth="1.5"
          strokeLinejoin="round"
          strokeLinecap="round"
        />
        {hoveredIndex !== null && (
          <>
            <line
              x1={getX(hoveredIndex)} y1={padding}
              x2={getX(hoveredIndex)} y2={padding + chartHeight}
              stroke="#6b7280" strokeWidth="0.5" strokeDasharray="2,2"
            />
            <circle cx={getX(hoveredIndex)} cy={getY(values[hoveredIndex])} r="3" fill={color} stroke="white" strokeWidth="1" />
          </>
        )}
      </svg>
      <div className={`text-[9px] mt-0.5 ${hoveredIndex !== null ? "text-gray-500" : "invisible"}`}>
        {hoveredIndex !== null
          ? <>{timestamps[hoveredIndex]}: <span className="font-semibold text-gray-700">{fmt(values[hoveredIndex])}</span></>
          : "\u00A0"}
      </div>
    </div>
  );
}

interface DiskFreeChartProps {
  values: number[];
  timestamps: string[];
  diskTotalGb: number;
}

function DiskFreeChart({ values, timestamps }: DiskFreeChartProps) {
  const [hoveredIndex, setHoveredIndex] = useState<number | null>(null);
  const [svgWidth, setSvgWidth] = useState(300);
  const containerRef = useRef<HTMLDivElement>(null);

  const height = 40;
  const padding = 2;
  const chartWidth = svgWidth - padding * 2;
  const chartHeight = height - padding * 2;

  const startVal = values[0] ?? 0;
  const endVal = values[values.length - 1] ?? 0;
  const delta = startVal - endVal;

  // Y-axis range: auto-scale with some padding
  const minVal = Math.min(...values);
  const maxVal = Math.max(...values);
  const yRange = maxVal - minVal;
  const yMin = Math.max(0, minVal - yRange * 0.15);
  const yMax = maxVal + yRange * 0.15;
  const effectiveYRange = yMax - yMin || 1;

  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const obs = new ResizeObserver((entries) => {
      for (const entry of entries) setSvgWidth(Math.round(entry.contentRect.width));
    });
    obs.observe(el);
    return () => obs.disconnect();
  }, []);

  const getX = useCallback((i: number) => padding + (i / Math.max(1, values.length - 1)) * chartWidth, [values.length, chartWidth]);
  const getY = useCallback((v: number) => padding + chartHeight - ((v - yMin) / effectiveYRange) * chartHeight, [chartHeight, yMin, effectiveYRange]);

  // Color based on latest value
  const color = endVal < 5 ? "#dc2626" : endVal < 20 ? "#ea580c" : "#16a34a";

  const points = values.map((v, i) => `${getX(i)},${getY(v)}`);
  const areaPoints = [
    `${padding},${padding + chartHeight}`,
    ...points,
    `${svgWidth - padding},${padding + chartHeight}`,
  ];

  const handleMouseMove = useCallback((e: React.MouseEvent<SVGSVGElement>) => {
    const el = containerRef.current;
    if (!el) return;
    const rect = el.getBoundingClientRect();
    const mouseX = e.clientX - rect.left;
    const relX = mouseX - padding;
    if (relX < 0 || relX > chartWidth) {
      setHoveredIndex(null);
      return;
    }
    const idx = Math.round((relX / chartWidth) * (values.length - 1));
    setHoveredIndex(Math.max(0, Math.min(idx, values.length - 1)));
  }, [chartWidth, values.length]);

  if (values.length < 2) return null;

  return (
    <div ref={containerRef}>
      <svg
        width={svgWidth}
        height={height}
        viewBox={`0 0 ${svgWidth} ${height}`}
        className="block cursor-crosshair"
        onMouseMove={handleMouseMove}
        onMouseLeave={() => setHoveredIndex(null)}
      >
        <polygon
          points={areaPoints.join(" ")}
          fill={color}
          fillOpacity="0.1"
        />
        <polyline
          points={points.join(" ")}
          fill="none"
          stroke={color}
          strokeWidth="1.5"
          strokeLinejoin="round"
          strokeLinecap="round"
        />
        {hoveredIndex !== null && (
          <>
            <line
              x1={getX(hoveredIndex)} y1={padding}
              x2={getX(hoveredIndex)} y2={padding + chartHeight}
              stroke="#6b7280" strokeWidth="0.5" strokeDasharray="2,2"
            />
            <circle cx={getX(hoveredIndex)} cy={getY(values[hoveredIndex])} r="3" fill={color} stroke="white" strokeWidth="1" />
          </>
        )}
      </svg>
      {hoveredIndex !== null ? (
        <div className="text-[9px] text-gray-500 mt-0.5">
          {timestamps[hoveredIndex]}: <span className="font-semibold text-gray-700">{values[hoveredIndex].toFixed(1)} GB</span>
        </div>
      ) : (
        <div className="flex items-center justify-between mt-0.5 text-[9px]">
          <span className="text-gray-400">{startVal.toFixed(0)}</span>
          <span className={`font-semibold ${delta > 0 ? "text-red-600" : "text-green-600"}`}>
            {delta > 0 ? "-" : "+"}{Math.abs(delta).toFixed(1)} GB
          </span>
          <span className={`font-semibold ${endVal < 5 ? "text-red-600" : endVal < 20 ? "text-orange-600" : "text-gray-700"}`}>{endVal.toFixed(0)}</span>
        </div>
      )}
    </div>
  );
}

export default function PerformanceChart({ events, expanded: expandedProp, setExpanded: setExpandedProp }: PerformanceChartProps) {
  const isControlled = expandedProp !== undefined;
  const [internalExpanded, setInternalExpanded] = useState(true);
  const expanded = isControlled ? expandedProp : internalExpanded;
  const setExpanded = isControlled ? setExpandedProp! : setInternalExpanded;
  const metrics = useMemo(() => {
    if (events.length === 0) return null;

    const cpuValues: number[] = [];
    const memoryUsedPercent: number[] = [];
    const diskQueueValues: number[] = [];
    const diskFreeValues: number[] = [];
    const netReceiveValues: number[] = [];
    const netSendValues: number[] = [];
    const timestamps: string[] = [];
    const diskTimestamps: string[] = [];
    const netTimestamps: string[] = [];

    for (const evt of events) {
      const d = evt.data as PerformanceEventData | undefined;
      if (!d) continue;

      timestamps.push(new Date(evt.timestamp).toLocaleTimeString());

      const cpu = parseFloat(d.cpu_percent ?? d.cpuPercent ?? "0");
      cpuValues.push(isNaN(cpu) ? 0 : cpu);

      const memAvail = parseFloat(d.memory_available_mb ?? d.memoryAvailableMb ?? "0");
      const memTotal = parseFloat(d.memory_total_mb ?? d.memoryTotalMb ?? "1");
      const memPct = memTotal > 0 ? ((memTotal - memAvail) / memTotal) * 100 : 0;
      memoryUsedPercent.push(Math.round(memPct));

      const dq = parseFloat(d.disk_queue_length ?? d.diskQueueLength ?? "0");
      diskQueueValues.push(isNaN(dq) ? 0 : dq);

      const dfg = parseFloat(d.disk_free_gb ?? d.diskFreeGb ?? "0");
      if (dfg > 0) {
        diskFreeValues.push(dfg);
        diskTimestamps.push(new Date(evt.timestamp).toLocaleTimeString());
      }

      // Network throughput (kbps → Mbps for display)
      const netRecv = parseFloat(d.net_receive_rate_kbps ?? d.netReceiveRateKbps ?? "");
      const netSend = parseFloat(d.net_send_rate_kbps ?? d.netSendRateKbps ?? "");
      if (!isNaN(netRecv) && !isNaN(netSend)) {
        netReceiveValues.push(netRecv / 1000); // kbps → Mbps
        netSendValues.push(netSend / 1000);
        netTimestamps.push(new Date(evt.timestamp).toLocaleTimeString());
      }
    }

    const last = events[events.length - 1]?.data as PerformanceEventData | undefined;
    const diskFreeGb = parseFloat(last?.disk_free_gb ?? last?.diskFreeGb ?? "0");
    const diskTotalGb = parseFloat(last?.disk_total_gb ?? last?.diskTotalGb ?? "256");

    return {
      cpuValues,
      memoryUsedPercent,
      diskQueueValues,
      diskFreeValues,
      netReceiveValues,
      netSendValues,
      diskTimestamps,
      netTimestamps,
      timestamps,
      latestCpu: cpuValues[cpuValues.length - 1] ?? 0,
      latestMemory: memoryUsedPercent[memoryUsedPercent.length - 1] ?? 0,
      latestDiskQueue: diskQueueValues[diskQueueValues.length - 1] ?? 0,
      latestNetReceive: netReceiveValues[netReceiveValues.length - 1] ?? 0,
      latestNetSend: netSendValues[netSendValues.length - 1] ?? 0,
      diskFreeGb,
      diskTotalGb,
      sampleCount: events.length,
    };
  }, [events]);

  if (!metrics || metrics.sampleCount < 2) {
    return null;
  }

  return (
    <div className="bg-white shadow rounded-lg p-6 mb-6">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center justify-between w-full text-left"
      >
        <div className="flex items-center space-x-2">
          <svg className="w-5 h-5 text-indigo-500" fill="none" viewBox="0 0 24 24" stroke="currentColor">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 19v-6a2 2 0 00-2-2H5a2 2 0 00-2 2v6a2 2 0 002 2h2a2 2 0 002-2zm0 0V9a2 2 0 012-2h2a2 2 0 012 2v10m-6 0a2 2 0 002 2h2a2 2 0 002-2m0 0V5a2 2 0 012-2h2a2 2 0 012 2v14a2 2 0 01-2 2h-2a2 2 0 01-2-2z" />
          </svg>
          <h2 className="text-lg font-semibold text-gray-900">Performance Metrics</h2>
          <span className="text-xs text-gray-400">({metrics.sampleCount} samples)</span>
        </div>
        <svg className={`w-5 h-5 text-gray-400 transition-transform duration-200 ${expanded ? 'rotate-90' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24">
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 5l7 7-7 7" />
        </svg>
      </button>

      {expanded && <div className={`grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 ${metrics.netReceiveValues.length >= 2 ? "lg:grid-cols-6" : "lg:grid-cols-4"} gap-4 mt-4`}>
        {/* CPU */}
        <div className="bg-gray-50 rounded-lg p-3">
          <div className="flex items-center justify-between mb-1">
            <span className="text-xs font-medium text-gray-500 uppercase">CPU</span>
            <span className={`text-sm font-semibold ${
              metrics.latestCpu > 90 ? "text-red-600" :
              metrics.latestCpu > 70 ? "text-orange-600" : "text-gray-900"
            }`}>
              {metrics.latestCpu.toFixed(0)}%
            </span>
          </div>
          <Sparkline
            values={metrics.cpuValues}
            maxValue={100}
            color={metrics.latestCpu > 90 ? "#dc2626" : metrics.latestCpu > 70 ? "#ea580c" : "#4f46e5"}
            timestamps={metrics.timestamps}
            unit="%"
            formatValue={(v) => `${v.toFixed(0)}%`}
          />
        </div>

        {/* Disk Queue - top right */}
        <div className="bg-gray-50 rounded-lg p-3">
          <div className="flex items-center justify-between mb-1">
            <span className="text-xs font-medium text-gray-500 uppercase">Disk Queue</span>
            <span className={`text-sm font-semibold ${
              metrics.latestDiskQueue > 5 ? "text-red-600" :
              metrics.latestDiskQueue > 2 ? "text-orange-600" : "text-gray-900"
            }`}>
              {metrics.latestDiskQueue.toFixed(1)}
            </span>
          </div>
          <Sparkline
            values={metrics.diskQueueValues}
            maxValue={Math.max(10, ...metrics.diskQueueValues)}
            color={metrics.latestDiskQueue > 5 ? "#dc2626" : metrics.latestDiskQueue > 2 ? "#ea580c" : "#16a34a"}
            timestamps={metrics.timestamps}
            unit=""
            formatValue={(v) => v.toFixed(1)}
          />
        </div>

        {/* Memory - bottom left */}
        <div className="bg-gray-50 rounded-lg p-3">
          <div className="flex items-center justify-between mb-1">
            <span className="text-xs font-medium text-gray-500 uppercase">Memory</span>
            <span className={`text-sm font-semibold ${
              metrics.latestMemory > 90 ? "text-red-600" :
              metrics.latestMemory > 75 ? "text-orange-600" : "text-gray-900"
            }`}>
              {metrics.latestMemory.toFixed(0)}%
            </span>
          </div>
          <Sparkline
            values={metrics.memoryUsedPercent}
            maxValue={100}
            color={metrics.latestMemory > 90 ? "#dc2626" : metrics.latestMemory > 75 ? "#ea580c" : "#0891b2"}
            timestamps={metrics.timestamps}
            unit="%"
            formatValue={(v) => `${v.toFixed(0)}%`}
          />
        </div>

        {/* Disk Free - bottom right (compact interactive chart) */}
        <div className="bg-gray-50 rounded-lg p-3">
          <div className="flex items-center justify-between mb-1">
            <span className="text-xs font-medium text-gray-500 uppercase">Disk Free</span>
            {metrics.diskFreeGb > 0 && (
              <span className={`text-sm font-semibold ${
                metrics.diskFreeGb < 5 ? "text-red-600" :
                metrics.diskFreeGb < 20 ? "text-orange-600" : "text-gray-900"
              }`}>
                {metrics.diskFreeGb.toFixed(1)} GB
              </span>
            )}
          </div>
          {metrics.diskFreeValues.length >= 2 ? (
            <DiskFreeChart
              values={metrics.diskFreeValues}
              timestamps={metrics.diskTimestamps}
              diskTotalGb={metrics.diskTotalGb}
            />
          ) : metrics.diskFreeGb > 0 ? (
            <div className="flex items-center mt-2">
              <div className="w-full h-3 bg-gray-200 rounded-full overflow-hidden">
                <div
                  className={`h-full rounded-full ${
                    metrics.diskFreeGb < 5 ? "bg-red-500" :
                    metrics.diskFreeGb < 20 ? "bg-orange-500" : "bg-green-500"
                  }`}
                  style={{ width: `${Math.min(100, (metrics.diskFreeGb / metrics.diskTotalGb) * 100)}%` }}
                />
              </div>
            </div>
          ) : (
            <div className="text-xs text-gray-400 mt-2">No data</div>
          )}
        </div>

        {/* Network Download */}
        {metrics.netReceiveValues.length >= 2 && (
          <div className="bg-gray-50 rounded-lg p-3">
            <div className="flex items-center justify-between mb-1">
              <span className="text-xs font-medium text-gray-500 uppercase">Net ↓</span>
              <span className="text-sm font-semibold text-gray-900">
                {metrics.latestNetReceive >= 1
                  ? `${metrics.latestNetReceive.toFixed(1)} Mbps`
                  : `${(metrics.latestNetReceive * 1000).toFixed(0)} kbps`}
              </span>
            </div>
            <Sparkline
              values={metrics.netReceiveValues}
              maxValue={Math.max(1, ...metrics.netReceiveValues)}
              color="#0891b2"
              timestamps={metrics.netTimestamps}
              unit=" Mbps"
              formatValue={(v) => v >= 1 ? `${v.toFixed(1)} Mbps` : `${(v * 1000).toFixed(0)} kbps`}
            />
          </div>
        )}

        {/* Network Upload */}
        {metrics.netSendValues.length >= 2 && (
          <div className="bg-gray-50 rounded-lg p-3">
            <div className="flex items-center justify-between mb-1">
              <span className="text-xs font-medium text-gray-500 uppercase">Net ↑</span>
              <span className="text-sm font-semibold text-gray-900">
                {metrics.latestNetSend >= 1
                  ? `${metrics.latestNetSend.toFixed(1)} Mbps`
                  : `${(metrics.latestNetSend * 1000).toFixed(0)} kbps`}
              </span>
            </div>
            <Sparkline
              values={metrics.netSendValues}
              maxValue={Math.max(1, ...metrics.netSendValues)}
              color="#7c3aed"
              timestamps={metrics.netTimestamps}
              unit=" Mbps"
              formatValue={(v) => v >= 1 ? `${v.toFixed(1)} Mbps` : `${(v * 1000).toFixed(0)} kbps`}
            />
          </div>
        )}
      </div>}
    </div>
  );
}
