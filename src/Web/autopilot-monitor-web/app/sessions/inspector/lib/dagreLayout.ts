import dagre from "dagre";
import type { Edge, Node } from "@xyflow/react";
import type { DecisionGraphNode, DecisionGraphEdge } from "../types";

/** Logical category of a graph node — drives renderer + colour. */
export type NodeCategory = "stage" | "terminal-success" | "terminal-failed" | "terminal-paused";

/**
 * Aggregated dead-end reason for a single stage. Many self-loops at one stage
 * typically share a small set of guards (e.g. `guard:hello_pending` × 12),
 * so we count by reason instead of listing every individual block.
 */
export interface BlockedReason {
  reason: string;
  count: number;
}

export interface InspectorNodeData {
  label: string;
  stageId: string;
  category: NodeCategory;
  /** Inter-stage transitions that landed on this node (taken). */
  entered: number;
  /** Self-loop steps inside this stage (taken, from==to). */
  internal: number;
  /** Self-loop dead-ends — guards that blocked while staying in this stage. */
  blocked: number;
  /** Aggregated dead-end reasons for self-loop blocks (sorted by count desc). */
  blockedReasons: BlockedReason[];
  terminalOutcome: string | null;
  [key: string]: unknown;
}

export interface InspectorEdgeData {
  stepIndex: number;
  trigger: string;
  taken: boolean;
  deadEndReason: string | null;
  classifierVerdictId: string | null;
  classifierHypothesisLevel: string | null;
  signalOrdinalRef: number;
  occurredAtUtc: string;
  [key: string]: unknown;
}

const NODE_WIDTH = 200;
// Node height grows when blocked > 0 (extra row for the badge). Layout uses
// the larger value so dagre reserves room either way; the renderer paints the
// actual height.
const NODE_HEIGHT = 78;

/**
 * Default Dagre config for the Inspector. `TB` = top→bottom, matches the
 * Endkunden EventTimeline reading direction (Plan §M6).
 */
export const DAGRE_CONFIG = {
  rankdir: "TB" as const,
  ranksep: 60,
  nodesep: 40,
  marginx: 20,
  marginy: 20,
};

/**
 * Pure function — translates the backend's DecisionGraphProjection into
 * positioned ReactFlow nodes + edges. Self-loops (`from==to`) are stripped
 * from the rendered edge list and aggregated onto the source node instead;
 * dagre would otherwise produce 0-pixel edges that overlap the node and hide
 * the dead-end label.
 */
export function buildLayout(
  nodes: DecisionGraphNode[],
  edges: DecisionGraphEdge[],
): { nodes: Node<InspectorNodeData>[]; edges: Edge<InspectorEdgeData>[] } {
  // Per-stage counters projected from the edge list before we touch dagre.
  const enteredByStage = new Map<string, number>();
  const internalByStage = new Map<string, number>();
  const blockedByStage = new Map<string, Map<string, number>>(); // stage → reason → count

  const interStageEdges: DecisionGraphEdge[] = [];

  for (const e of edges) {
    const isSelfLoop = e.fromStage === e.toStage;
    if (isSelfLoop) {
      if (e.taken) {
        internalByStage.set(e.fromStage, (internalByStage.get(e.fromStage) ?? 0) + 1);
      } else {
        const reason = e.deadEndReason ?? "(unknown)";
        let inner = blockedByStage.get(e.fromStage);
        if (!inner) {
          inner = new Map<string, number>();
          blockedByStage.set(e.fromStage, inner);
        }
        inner.set(reason, (inner.get(reason) ?? 0) + 1);
      }
      continue;
    }
    interStageEdges.push(e);
    if (e.taken) {
      enteredByStage.set(e.toStage, (enteredByStage.get(e.toStage) ?? 0) + 1);
    }
  }

  // Layout only on the inter-stage edges — keeps the canvas readable.
  const g = new dagre.graphlib.Graph();
  g.setGraph(DAGRE_CONFIG);
  g.setDefaultEdgeLabel(() => ({}));

  for (const n of nodes) {
    g.setNode(n.id, { width: NODE_WIDTH, height: NODE_HEIGHT });
  }
  for (const e of interStageEdges) {
    g.setEdge(e.fromStage, e.toStage, { weight: e.taken ? 2 : 1 });
  }
  dagre.layout(g);

  const layoutNodes: Node<InspectorNodeData>[] = nodes.map((n) => {
    const pos = g.node(n.id);
    const blockedMap = blockedByStage.get(n.id);
    const blockedReasons: BlockedReason[] = blockedMap
      ? Array.from(blockedMap.entries())
          .map(([reason, count]) => ({ reason, count }))
          .sort((a, b) => b.count - a.count)
      : [];
    const blocked = blockedReasons.reduce((acc, r) => acc + r.count, 0);

    return {
      id: n.id,
      type: "inspectorNode",
      position: pos
        ? { x: pos.x - NODE_WIDTH / 2, y: pos.y - NODE_HEIGHT / 2 }
        : { x: 0, y: 0 },
      data: {
        label: n.id,
        stageId: n.id,
        category: classifyNode(n),
        entered: enteredByStage.get(n.id) ?? 0,
        internal: internalByStage.get(n.id) ?? 0,
        blocked,
        blockedReasons,
        terminalOutcome: n.terminalOutcome,
      },
    };
  });

  const layoutEdges: Edge<InspectorEdgeData>[] = interStageEdges.map((e) => ({
    // StepIndex is monotonic per session, so it's a stable unique id even when
    // multiple edges share (from, to) pairs (e.g. a guard rejected the same
    // transition twice before another signal unblocked it).
    id: `e${e.stepIndex}`,
    source: e.fromStage,
    target: e.toStage,
    type: "inspectorEdge",
    animated: false,
    data: {
      stepIndex: e.stepIndex,
      trigger: e.trigger,
      taken: e.taken,
      deadEndReason: e.deadEndReason,
      classifierVerdictId: e.classifierVerdictId,
      classifierHypothesisLevel: e.classifierHypothesisLevel,
      signalOrdinalRef: e.signalOrdinalRef,
      occurredAtUtc: e.occurredAtUtc,
    },
  }));

  return { nodes: layoutNodes, edges: layoutEdges };
}

function classifyNode(n: DecisionGraphNode): NodeCategory {
  if (!n.isTerminal) return "stage";
  switch (n.terminalOutcome) {
    case "Succeeded":
      return "terminal-success";
    case "Failed":
      return "terminal-failed";
    case "PausedForPart2":
      return "terminal-paused";
    default:
      return "stage";
  }
}

/**
 * Aggregate counters across all edges of a session — drives the header
 * statistics ("4 transitions · 234 internal · 1 blocked self-loop" instead
 * of the misleading "238 taken · 1 dead-end").
 */
export interface GraphStats {
  totalEdges: number;
  interStageTaken: number;
  interStageBlocked: number;
  selfLoopTaken: number;
  selfLoopBlocked: number;
}

export function computeGraphStats(edges: DecisionGraphEdge[]): GraphStats {
  const stats: GraphStats = {
    totalEdges: edges.length,
    interStageTaken: 0,
    interStageBlocked: 0,
    selfLoopTaken: 0,
    selfLoopBlocked: 0,
  };
  for (const e of edges) {
    const isSelfLoop = e.fromStage === e.toStage;
    if (isSelfLoop) {
      if (e.taken) stats.selfLoopTaken++;
      else stats.selfLoopBlocked++;
    } else {
      if (e.taken) stats.interStageTaken++;
      else stats.interStageBlocked++;
    }
  }
  return stats;
}
