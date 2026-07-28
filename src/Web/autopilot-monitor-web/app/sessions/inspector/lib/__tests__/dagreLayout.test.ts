import { describe, expect, it } from "vitest";
import { buildLayout, computeGraphStats } from "../dagreLayout";
import type { DecisionGraphNode, DecisionGraphEdge } from "../../types";

const node = (id: string, isTerminal = false, terminalOutcome: string | null = null): DecisionGraphNode => ({
  id,
  isTerminal,
  terminalOutcome,
  visitCount: 0,
});

const edge = (
  stepIndex: number,
  fromStage: string,
  toStage: string,
  taken: boolean,
  deadEndReason: string | null = null,
): DecisionGraphEdge => ({
  stepIndex,
  fromStage,
  toStage,
  trigger: `trigger_${stepIndex}`,
  taken,
  deadEndReason,
  signalOrdinalRef: stepIndex,
  occurredAtUtc: "2026-04-27T00:00:00.000Z",
  classifierVerdictId: null,
  classifierHypothesisLevel: null,
});

describe("buildLayout", () => {
  it("returns empty layout for empty input", () => {
    const result = buildLayout([], []);
    expect(result.nodes).toEqual([]);
    expect(result.edges).toEqual([]);
  });

  it("classifies a non-terminal node as 'stage'", () => {
    const result = buildLayout([node("Started")], []);
    expect(result.nodes[0].data.category).toBe("stage");
  });

  it("classifies terminal nodes by outcome", () => {
    const result = buildLayout(
      [
        node("Completed", true, "Succeeded"),
        node("Failed", true, "Failed"),
        node("WhiteGloveSealed", true, "PausedForPart2"),
      ],
      [],
    );
    const byId = Object.fromEntries(result.nodes.map((n) => [n.id, n.data.category]));
    expect(byId["Completed"]).toBe("terminal-success");
    expect(byId["Failed"]).toBe("terminal-failed");
    expect(byId["WhiteGloveSealed"]).toBe("terminal-paused");
  });

  it("falls back to 'stage' for terminal node with unknown outcome", () => {
    const result = buildLayout([node("MysteryEnd", true, "ZorkOutcome")], []);
    expect(result.nodes[0].data.category).toBe("stage");
  });

  it("assigns positive y-coordinates with TB layout (top→bottom)", () => {
    const nodes = [node("A"), node("B")];
    const edges = [edge(1, "A", "B", true)];
    const result = buildLayout(nodes, edges);
    const positions = Object.fromEntries(result.nodes.map((n) => [n.id, n.position]));
    // A is the source; in TB layout A.y < B.y.
    expect(positions["A"].y).toBeLessThan(positions["B"].y);
  });

  it("preserves StepIndex as the edge id (so duplicate from→to pairs stay distinct)", () => {
    const nodes = [node("A"), node("B")];
    const edges = [edge(1, "A", "B", false, "blocked"), edge(2, "A", "B", true)];
    const result = buildLayout(nodes, edges);
    const ids = result.edges.map((e) => e.id).sort();
    expect(ids).toEqual(["e1", "e2"]);
  });

  it("propagates dead-end metadata onto inter-stage edges", () => {
    const result = buildLayout(
      [node("A"), node("B")],
      [edge(1, "A", "B", false, "guard:esp_gate_blocking")],
    );
    expect(result.edges[0].data?.taken).toBe(false);
    expect(result.edges[0].data?.deadEndReason).toBe("guard:esp_gate_blocking");
  });

  it("propagates trigger + classifier verdict on a taken edge", () => {
    const e = edge(7, "A", "B", true);
    e.classifierVerdictId = "WhiteGloveSealing";
    e.classifierHypothesisLevel = "Confirmed";
    const result = buildLayout([node("A"), node("B")], [e]);
    expect(result.edges[0].data?.trigger).toBe("trigger_7");
    expect(result.edges[0].data?.classifierVerdictId).toBe("WhiteGloveSealing");
    expect(result.edges[0].data?.classifierHypothesisLevel).toBe("Confirmed");
  });

  it("renders both taken and dead-end branches when given an inter-stage fork", () => {
    // Reproduces a 2-stage WhiteGlove pattern: from EspInProgress, the agent
    // attempts WhiteGloveSealed (taken) but a guard initially rejects it
    // (dead-end). Both must appear in the layout output as edges.
    const nodes = [node("EspInProgress"), node("WhiteGloveSealed", true, "PausedForPart2")];
    const edges = [
      edge(10, "EspInProgress", "WhiteGloveSealed", false, "guard:hello_pending"),
      edge(11, "EspInProgress", "WhiteGloveSealed", true),
    ];
    const result = buildLayout(nodes, edges);
    expect(result.edges).toHaveLength(2);
    expect(result.edges.some((e) => e.data?.taken === false)).toBe(true);
    expect(result.edges.some((e) => e.data?.taken === true)).toBe(true);
  });

  // ── Self-loop aggregation (the WG-modelling fix) ────────────────────────

  it("strips self-loops from the rendered edge list", () => {
    // Self-loops must not appear as ReactFlow edges — dagre would render
    // them as 0-px segments that overlap the node and hide the dead-end
    // label. They get aggregated into NodeData instead.
    const result = buildLayout(
      [node("EspDeviceSetup")],
      [
        edge(1, "EspDeviceSetup", "EspDeviceSetup", true),
        edge(2, "EspDeviceSetup", "EspDeviceSetup", false, "guard:hello_pending"),
      ],
    );
    expect(result.edges).toHaveLength(0);
  });

  it("counts self-loop taken steps as 'internal' on the source node", () => {
    const result = buildLayout(
      [node("EspDeviceSetup")],
      [
        edge(1, "EspDeviceSetup", "EspDeviceSetup", true),
        edge(2, "EspDeviceSetup", "EspDeviceSetup", true),
        edge(3, "EspDeviceSetup", "EspDeviceSetup", true),
      ],
    );
    expect(result.nodes[0].data.internal).toBe(3);
    expect(result.nodes[0].data.blocked).toBe(0);
  });

  it("counts self-loop dead-ends as 'blocked' and aggregates reasons", () => {
    const result = buildLayout(
      [node("EspDeviceSetup")],
      [
        edge(1, "EspDeviceSetup", "EspDeviceSetup", false, "guard:hello_pending"),
        edge(2, "EspDeviceSetup", "EspDeviceSetup", false, "guard:hello_pending"),
        edge(3, "EspDeviceSetup", "EspDeviceSetup", false, "guard:esp_settling"),
      ],
    );
    const data = result.nodes[0].data;
    expect(data.blocked).toBe(3);
    // Reasons are sorted by count descending so the dominant guard surfaces first.
    expect(data.blockedReasons).toEqual([
      { reason: "guard:hello_pending", count: 2 },
      { reason: "guard:esp_settling", count: 1 },
    ]);
  });

  it("counts inter-stage taken transitions as 'entered' on the target node", () => {
    // Three taken transitions A→B (e.g. retry path) plus one self-loop on B
    // — entered must be 3 (A→B), internal must be 1.
    const result = buildLayout(
      [node("A"), node("B")],
      [
        edge(1, "A", "B", true),
        edge(2, "A", "B", true),
        edge(3, "A", "B", true),
        edge(4, "B", "B", true),
      ],
    );
    const byId = Object.fromEntries(result.nodes.map((n) => [n.id, n.data]));
    expect(byId["B"].entered).toBe(3);
    expect(byId["B"].internal).toBe(1);
    expect(byId["A"].entered).toBe(0);
  });

  it("substitutes '(unknown)' when a self-loop dead-end has no reason", () => {
    const result = buildLayout(
      [node("X")],
      [edge(1, "X", "X", false, null)],
    );
    expect(result.nodes[0].data.blockedReasons).toEqual([
      { reason: "(unknown)", count: 1 },
    ]);
  });
});

describe("computeGraphStats", () => {
  it("returns zeros for an empty edge list", () => {
    expect(computeGraphStats([])).toEqual({
      totalEdges: 0,
      interStageTaken: 0,
      interStageBlocked: 0,
      selfLoopTaken: 0,
      selfLoopBlocked: 0,
    });
  });

  it("classifies edges into the four buckets", () => {
    // Reproduces the screenshot scenario in miniature:
    //   3 inter-stage taken + 1 inter-stage dead-end + 5 self-loop taken
    //   + 2 self-loop dead-ends.
    const edges = [
      edge(1, "A", "B", true),
      edge(2, "B", "C", true),
      edge(3, "C", "D", true),
      edge(4, "C", "D", false, "guard:foo"),
      edge(5, "B", "B", true),
      edge(6, "B", "B", true),
      edge(7, "B", "B", true),
      edge(8, "B", "B", true),
      edge(9, "B", "B", true),
      edge(10, "B", "B", false, "guard:hello"),
      edge(11, "B", "B", false, "guard:hello"),
    ];
    expect(computeGraphStats(edges)).toEqual({
      totalEdges: 11,
      interStageTaken: 3,
      interStageBlocked: 1,
      selfLoopTaken: 5,
      selfLoopBlocked: 2,
    });
  });
});
