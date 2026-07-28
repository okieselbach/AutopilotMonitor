"use client";

import { BaseEdge, EdgeLabelRenderer, getBezierPath, type EdgeProps } from "@xyflow/react";
import type { InspectorEdgeData } from "../lib/dagreLayout";

/**
 * Custom edge that distinguishes taken vs. dead-end transitions.
 * Dead-ends render dashed + amber, with a 🚫 marker on the label so the
 * Inspector visualises *why* a path was blocked (Plan §M6 — primary
 * use case for 2-stage WhiteGlove modelling).
 */
export function InspectorEdge(props: EdgeProps) {
  const {
    id,
    sourceX,
    sourceY,
    targetX,
    targetY,
    sourcePosition,
    targetPosition,
    data,
    selected,
  } = props;

  const edgeData = data as InspectorEdgeData | undefined;
  const taken = edgeData?.taken ?? true;

  const [path, labelX, labelY] = getBezierPath({
    sourceX,
    sourceY,
    sourcePosition,
    targetX,
    targetY,
    targetPosition,
  });

  const stroke = taken ? "#3b82f6" : "#f59e0b"; // blue-500 / amber-500
  const strokeDasharray = taken ? undefined : "6 4";
  const strokeWidth = selected ? 3 : taken ? 2 : 1.5;

  return (
    <>
      <BaseEdge id={id} path={path} style={{ stroke, strokeWidth, strokeDasharray }} />
      {edgeData && !taken && edgeData.deadEndReason && (
        <EdgeLabelRenderer>
          <div
            style={{
              position: "absolute",
              transform: `translate(-50%, -50%) translate(${labelX}px,${labelY}px)`,
              pointerEvents: "all",
            }}
            className="rounded border border-amber-400 bg-amber-50 px-1.5 py-0.5 text-[10px] font-medium text-amber-800 shadow-sm"
            title={`Step ${edgeData.stepIndex} · trigger=${edgeData.trigger} · ${edgeData.deadEndReason}`}
          >
            🚫 {edgeData.deadEndReason}
          </div>
        </EdgeLabelRenderer>
      )}
      {edgeData && taken && (
        <EdgeLabelRenderer>
          <div
            style={{
              position: "absolute",
              transform: `translate(-50%, -50%) translate(${labelX}px,${labelY}px)`,
              pointerEvents: "all",
            }}
            className="rounded border border-blue-200 bg-white px-1.5 py-0.5 text-[10px] text-gray-700 shadow-sm"
            title={`Step ${edgeData.stepIndex} · trigger=${edgeData.trigger}`}
          >
            {edgeData.trigger}
          </div>
        </EdgeLabelRenderer>
      )}
    </>
  );
}
