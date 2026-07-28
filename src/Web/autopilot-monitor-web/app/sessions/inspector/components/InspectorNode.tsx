"use client";

import { Handle, Position, type NodeProps } from "@xyflow/react";
import type { Node } from "@xyflow/react";
import type { InspectorNodeData } from "../lib/dagreLayout";

const STYLE_BY_CATEGORY: Record<
  InspectorNodeData["category"],
  { ring: string; bg: string; text: string; label: string }
> = {
  stage: { ring: "ring-blue-300", bg: "bg-white", text: "text-gray-800", label: "Stage" },
  "terminal-success": {
    ring: "ring-green-500 ring-offset-2",
    bg: "bg-green-50",
    text: "text-green-800",
    label: "Succeeded",
  },
  "terminal-failed": {
    ring: "ring-red-500 ring-offset-2",
    bg: "bg-red-50",
    text: "text-red-800",
    label: "Failed",
  },
  "terminal-paused": {
    ring: "ring-purple-500 ring-offset-2",
    bg: "bg-purple-50",
    text: "text-purple-800",
    label: "Paused (WG Part 2 pending)",
  },
};

export function InspectorNode({ data, selected }: NodeProps<Node<InspectorNodeData>>) {
  const style = STYLE_BY_CATEGORY[data.category];
  return (
    <div
      className={`rounded-lg border border-gray-200 ring-2 ${style.ring} ${style.bg} px-3 py-2 shadow-sm ${
        selected ? "ring-4" : ""
      }`}
      style={{ width: 200 }}
    >
      <Handle type="target" position={Position.Top} className="!bg-gray-400" />
      <div className={`text-sm font-medium ${style.text}`}>{data.label}</div>
      <div className="mt-1 flex items-center justify-between text-[10px] text-gray-500">
        <span>{style.label}</span>
        <span title="Inter-stage transitions that landed on this stage (taken).">
          entered: {data.entered}
        </span>
      </div>
      <div className="mt-0.5 flex items-center justify-between text-[10px] text-gray-500">
        <span title="Self-loop steps inside this stage — reducer-step that didn't change the stage.">
          internal: {data.internal}
        </span>
        {data.blocked > 0 ? (
          <span
            className="rounded border border-amber-400 bg-amber-50 px-1 text-[10px] font-medium text-amber-800"
            title={`${data.blocked} self-loop dead-ends — click to inspect reasons`}
          >
            🚫 blocked: {data.blocked}
          </span>
        ) : (
          <span className="text-gray-400">blocked: 0</span>
        )}
      </div>
      <Handle type="source" position={Position.Bottom} className="!bg-gray-400" />
    </div>
  );
}
