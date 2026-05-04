using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Pure projection from the persisted journal (<see cref="DecisionTransitionRecord"/>) to
    /// the renderable <see cref="DecisionGraphProjection"/> the Inspector consumes (Plan §M5 /
    /// §M6). No IO, no service dependencies — unit-testable against canned transition lists.
    /// </summary>
    internal static class DecisionGraphBuilder
    {
        public static DecisionGraphProjection Build(
            string tenantId,
            string sessionId,
            IReadOnlyList<DecisionTransitionRecord> transitions)
        {
            var projection = new DecisionGraphProjection
            {
                TenantId  = tenantId,
                SessionId = sessionId,
                Nodes     = new List<DecisionGraphNode>(),
                Edges     = new List<DecisionGraphEdge>(),
            };

            if (transitions == null || transitions.Count == 0) return projection;

            // ReducerVersion is consistent across a session (enforced by the agent-side Journal
            // contract); pick the first row's value.
            projection.ReducerVersion = transitions[0].ReducerVersion ?? string.Empty;

            // Build nodes by de-duplicating From+To stages. Visit count = how many edges land on
            // the node (in-degree). Also track terminal flag + outcome derived from the ToStage.
            var nodeMap = new Dictionary<string, DecisionGraphNode>(StringComparer.Ordinal);

            DecisionGraphNode EnsureNode(string stage)
            {
                if (!nodeMap.TryGetValue(stage, out var node))
                {
                    node = new DecisionGraphNode
                    {
                        Id              = stage,
                        IsTerminal      = IsTerminalStage(stage),
                        TerminalOutcome = DeriveTerminalOutcome(stage),
                        VisitCount      = 0,
                    };
                    nodeMap[stage] = node;
                }
                return node;
            }

            // Edges come out in StepIndex order (repository already sorts). Record them as-is —
            // preserving dead-ends because the Inspector renders them in a distinct style.
            foreach (var t in transitions.OrderBy(x => x.StepIndex))
            {
                // Lazy-init both endpoints so isolated nodes (never-visited stages that show up
                // as FromStage only) are still rendered.
                var fromNode = EnsureNode(t.FromStage ?? string.Empty);
                var toNode   = EnsureNode(t.ToStage ?? string.Empty);

                projection.Edges.Add(new DecisionGraphEdge
                {
                    StepIndex                 = t.StepIndex,
                    FromStage                 = t.FromStage ?? string.Empty,
                    ToStage                   = t.ToStage ?? string.Empty,
                    Trigger                   = t.Trigger ?? string.Empty,
                    Taken                     = t.Taken,
                    DeadEndReason             = t.DeadEndReason,
                    SignalOrdinalRef          = t.SignalOrdinalRef,
                    OccurredAtUtc             = t.OccurredAtUtc,
                    ClassifierVerdictId       = t.ClassifierVerdictId,
                    ClassifierHypothesisLevel = t.ClassifierHypothesisLevel,
                });

                // VisitCount counts *taken* transitions — dead-ends don't advance the visit graph.
                // Matches the UI semantics: heat-map should show where execution actually went.
                if (t.Taken) toNode.VisitCount++;
            }

            // Emit nodes in deterministic order: by first-seen StepIndex. Graph renderers use
            // insertion order as a hint when they haven't computed a layout yet.
            projection.Nodes = nodeMap.Values.ToList();
            return projection;
        }

        /// <summary>
        /// Mirror of the agent-side <c>SessionStageExtensions.IsTerminal()</c> (DecisionCore) +
        /// the <see cref="Functions.Ingest.TelemetryPayloadParser"/> lookup. Three sources of
        /// truth stay in sync via the commit-time contract call-out (commit message +
        /// feedback_table_storage_serialization).
        /// </summary>
        internal static bool IsTerminalStage(string stage)
        {
            switch (stage)
            {
                case "Completed":
                case "Failed":
                case "WhiteGloveSealed":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Maps a terminal stage to a coarse outcome label for the Inspector. WhiteGloveSealed
        /// is Part-1 pause (reboot expected, agent exits but session is not finished) — labelled
        /// distinctly so the UI can render it with a different colour than the other terminals.
        /// </summary>
        internal static string? DeriveTerminalOutcome(string stage)
        {
            switch (stage)
            {
                case "Completed":
                    return "Succeeded";
                case "Failed":
                    return "Failed";
                case "WhiteGloveSealed":
                    return "PausedForPart2";
                default:
                    return null;
            }
        }
    }
}
