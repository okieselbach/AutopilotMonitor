"use client";

import { useMemo, useState } from "react";
import {
  ReactFlow,
  Background,
  Controls,
  MiniMap,
  type EdgeMouseHandler,
  type NodeMouseHandler,
} from "@xyflow/react";
import "@xyflow/react/dist/style.css";

import type { DecisionGraphProjection } from "../types";
import {
  buildLayout,
  computeGraphStats,
  type BlockedReason,
} from "../lib/dagreLayout";
import { InspectorNode } from "./InspectorNode";
import { InspectorEdge } from "./InspectorEdge";

interface DecisionGraphProps {
  graph: DecisionGraphProjection;
  truncated: boolean;
}

const NODE_TYPES = { inspectorNode: InspectorNode } as const;
const EDGE_TYPES = { inspectorEdge: InspectorEdge } as const;

type Selection =
  | { kind: "edge"; id: string }
  | { kind: "node"; id: string }
  | null;

export function DecisionGraph({ graph, truncated }: DecisionGraphProps) {
  const [showInterStageDeadEnds, setShowInterStageDeadEnds] = useState(true);
  const [selection, setSelection] = useState<Selection>(null);

  const stats = useMemo(() => computeGraphStats(graph.edges), [graph.edges]);

  // Pre-filter edges before layout. Self-loops are stripped inside
  // buildLayout regardless; this toggle controls visibility of
  // *inter-stage* dead-ends (cross-stage attempts that a guard blocked).
  const filteredEdges = useMemo(
    () =>
      showInterStageDeadEnds
        ? graph.edges
        : graph.edges.filter((e) => e.taken || e.fromStage === e.toStage),
    [graph.edges, showInterStageDeadEnds],
  );

  const layout = useMemo(
    () => buildLayout(graph.nodes, filteredEdges),
    [graph.nodes, filteredEdges],
  );

  const styledNodes = useMemo(
    () =>
      layout.nodes.map((n) => ({
        ...n,
        selected: selection?.kind === "node" && selection.id === n.id,
      })),
    [layout.nodes, selection],
  );

  const styledEdges = useMemo(
    () =>
      layout.edges.map((e) => ({
        ...e,
        selected: selection?.kind === "edge" && selection.id === e.id,
      })),
    [layout.edges, selection],
  );

  const onEdgeClick: EdgeMouseHandler = (_evt, edge) => {
    setSelection({ kind: "edge", id: edge.id });
  };
  const onNodeClick: NodeMouseHandler = (_evt, node) => {
    setSelection({ kind: "node", id: node.id });
  };

  const selectedEdge = useMemo(
    () =>
      selection?.kind === "edge"
        ? layout.edges.find((e) => e.id === selection.id) ?? null
        : null,
    [layout.edges, selection],
  );
  const selectedNode = useMemo(
    () =>
      selection?.kind === "node"
        ? layout.nodes.find((n) => n.id === selection.id) ?? null
        : null,
    [layout.nodes, selection],
  );

  return (
    <div className="grid grid-cols-1 gap-4 lg:grid-cols-[1fr_360px]">
      <div className="rounded border border-gray-200 bg-white">
        <div className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b border-gray-200 p-3 text-sm">
          <span className="text-gray-700">{graph.nodes.length} stages</span>
          <span className="text-gray-400">·</span>
          <span className="text-gray-700">
            {stats.interStageTaken} transition{stats.interStageTaken === 1 ? "" : "s"}
          </span>
          {stats.interStageBlocked > 0 && (
            <>
              <span className="text-gray-400">·</span>
              <span className="text-amber-700">
                {stats.interStageBlocked} blocked transition
                {stats.interStageBlocked === 1 ? "" : "s"}
              </span>
            </>
          )}
          <span className="text-gray-400">·</span>
          <span className="text-gray-500" title="Self-loop steps — reducer ran but stage didn't change.">
            {stats.selfLoopTaken} internal
          </span>
          {stats.selfLoopBlocked > 0 && (
            <>
              <span className="text-gray-400">·</span>
              <span className="text-amber-700" title="Self-loop dead-ends aggregated onto each stage's badge.">
                {stats.selfLoopBlocked} blocked self-loop
                {stats.selfLoopBlocked === 1 ? "" : "s"}
              </span>
            </>
          )}
          {truncated && <span className="ml-2 text-amber-600">(truncated)</span>}
          <label className="ml-auto flex items-center gap-1.5 text-xs">
            <input
              type="checkbox"
              checked={showInterStageDeadEnds}
              onChange={(e) => setShowInterStageDeadEnds(e.target.checked)}
            />
            Show blocked transitions
          </label>
        </div>

        <div style={{ height: "70vh" }}>
          <ReactFlow
            nodes={styledNodes}
            edges={styledEdges}
            nodeTypes={NODE_TYPES}
            edgeTypes={EDGE_TYPES}
            fitView
            fitViewOptions={{ padding: 0.15 }}
            proOptions={{ hideAttribution: true }}
            onEdgeClick={onEdgeClick}
            onNodeClick={onNodeClick}
            onPaneClick={() => setSelection(null)}
          >
            <Background gap={20} />
            <Controls />
            <MiniMap pannable zoomable nodeStrokeWidth={2} />
          </ReactFlow>
        </div>
      </div>

      <DetailPanel
        edge={
          selectedEdge && selectedEdge.data
            ? {
                stepIndex: selectedEdge.data.stepIndex,
                trigger: selectedEdge.data.trigger,
                taken: selectedEdge.data.taken,
                deadEndReason: selectedEdge.data.deadEndReason,
                classifierVerdictId: selectedEdge.data.classifierVerdictId,
                classifierHypothesisLevel: selectedEdge.data.classifierHypothesisLevel,
                signalOrdinalRef: selectedEdge.data.signalOrdinalRef,
                occurredAtUtc: selectedEdge.data.occurredAtUtc,
                from: selectedEdge.source,
                to: selectedEdge.target,
              }
            : null
        }
        node={
          selectedNode && selectedNode.data
            ? {
                stageId: selectedNode.data.stageId,
                category: selectedNode.data.category,
                entered: selectedNode.data.entered,
                internal: selectedNode.data.internal,
                blocked: selectedNode.data.blocked,
                blockedReasons: selectedNode.data.blockedReasons,
                terminalOutcome: selectedNode.data.terminalOutcome,
              }
            : null
        }
      />
    </div>
  );
}

interface EdgeDetail {
  stepIndex: number;
  trigger: string;
  taken: boolean;
  deadEndReason: string | null;
  classifierVerdictId: string | null;
  classifierHypothesisLevel: string | null;
  signalOrdinalRef: number;
  occurredAtUtc: string;
  from: string;
  to: string;
}

interface NodeDetail {
  stageId: string;
  category: string;
  entered: number;
  internal: number;
  blocked: number;
  blockedReasons: BlockedReason[];
  terminalOutcome: string | null;
}

function DetailPanel({ edge, node }: { edge: EdgeDetail | null; node: NodeDetail | null }) {
  if (edge) return <EdgeDetailPanel edge={edge} />;
  if (node) return <NodeDetailPanel node={node} />;
  return (
    <div className="rounded border border-gray-200 bg-gray-50 p-4 text-sm text-gray-500">
      Click a stage (node) to see entered/internal/blocked counts and aggregated guard
      reasons. Click a transition (edge) to see its trigger, source signal, and any
      guard reason for cross-stage blocks.
    </div>
  );
}

function EdgeDetailPanel({ edge }: { edge: EdgeDetail }) {
  return (
    <div className="rounded border border-gray-200 bg-white p-4 text-sm space-y-2">
      <div className="flex items-baseline justify-between">
        <h3 className="font-semibold">Step {edge.stepIndex}</h3>
        <span
          className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${
            edge.taken ? "bg-blue-100 text-blue-800" : "bg-amber-100 text-amber-800"
          }`}
        >
          {edge.taken ? "TAKEN" : "DEAD-END"}
        </span>
      </div>
      <KV k="From" v={edge.from} />
      <KV k="To" v={edge.to} />
      <KV k="Trigger" v={edge.trigger} mono />
      <KV k="Time (UTC)" v={edge.occurredAtUtc} mono />
      <KV k="Signal ord ref" v={String(edge.signalOrdinalRef)} mono />
      {edge.deadEndReason && <KV k="Dead-end reason" v={edge.deadEndReason} mono />}
      {edge.classifierVerdictId && (
        <KV k="Classifier verdict" v={edge.classifierVerdictId} mono />
      )}
      {edge.classifierHypothesisLevel && (
        <KV k="Hypothesis level" v={edge.classifierHypothesisLevel} mono />
      )}
    </div>
  );
}

function NodeDetailPanel({ node }: { node: NodeDetail }) {
  const totalSelfLoops = node.internal + node.blocked;
  return (
    <div className="rounded border border-gray-200 bg-white p-4 text-sm space-y-3">
      <div className="flex items-baseline justify-between">
        <h3 className="font-semibold">{node.stageId}</h3>
        {node.terminalOutcome && (
          <span
            className={`rounded px-1.5 py-0.5 text-[10px] font-medium ${
              node.terminalOutcome === "Succeeded"
                ? "bg-green-100 text-green-800"
                : node.terminalOutcome === "Failed"
                  ? "bg-red-100 text-red-800"
                  : "bg-purple-100 text-purple-800"
            }`}
          >
            {node.terminalOutcome.toUpperCase()}
          </span>
        )}
      </div>

      <div>
        <div className="mb-1 text-[10px] uppercase tracking-wide text-gray-500">
          Activity
        </div>
        <div className="grid grid-cols-3 gap-2 text-center">
          <Stat label="entered" value={node.entered} hint="Inter-stage taken" />
          <Stat label="internal" value={node.internal} hint="Self-loops taken" />
          <Stat
            label="blocked"
            value={node.blocked}
            hint="Self-loop dead-ends"
            tone={node.blocked > 0 ? "amber" : "neutral"}
          />
        </div>
      </div>

      {node.blockedReasons.length > 0 ? (
        <div>
          <div className="mb-1 text-[10px] uppercase tracking-wide text-gray-500">
            Blocked self-loops by reason
          </div>
          <ul className="space-y-1">
            {node.blockedReasons.map((r) => (
              <li
                key={r.reason}
                className="flex items-center justify-between gap-2 rounded border border-amber-200 bg-amber-50 px-2 py-1 text-xs"
              >
                <span className="break-all font-mono text-amber-900">🚫 {r.reason}</span>
                <span className="shrink-0 rounded bg-amber-200 px-1.5 py-0.5 text-[10px] font-medium text-amber-900">
                  ×{r.count}
                </span>
              </li>
            ))}
          </ul>
        </div>
      ) : totalSelfLoops > 0 ? (
        <div className="text-xs text-gray-500">
          All {node.internal} self-loop steps at this stage went through (no guard blocks).
        </div>
      ) : null}
    </div>
  );
}

function Stat({
  label,
  value,
  hint,
  tone = "neutral",
}: {
  label: string;
  value: number;
  hint: string;
  tone?: "neutral" | "amber";
}) {
  const toneClass =
    tone === "amber" && value > 0
      ? "bg-amber-50 border-amber-200 text-amber-900"
      : "bg-gray-50 border-gray-200 text-gray-800";
  return (
    <div className={`rounded border px-2 py-1.5 ${toneClass}`} title={hint}>
      <div className="text-base font-semibold">{value}</div>
      <div className="text-[10px] uppercase tracking-wide text-gray-500">{label}</div>
    </div>
  );
}

function KV({ k, v, mono = false }: { k: string; v: string; mono?: boolean }) {
  return (
    <div className="grid grid-cols-[110px_1fr] gap-2">
      <span className="text-xs text-gray-500">{k}</span>
      <span className={`text-xs ${mono ? "font-mono" : ""} break-all text-gray-800`}>{v}</span>
    </div>
  );
}
