using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for <see cref="DecisionGraphBuilder"/> — pure projection, zero IO. Covers node
/// de-duplication, terminal classification, dead-end preservation, visit-count semantics, and
/// empty / out-of-order inputs.
/// </summary>
public class DecisionGraphBuilderTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static DecisionTransitionRecord Transition(
        int step, string from, string to, bool taken = true, string? deadEnd = null, string trigger = "t",
        string reducer = "1.0.0")
        => new DecisionTransitionRecord
        {
            TenantId       = TenantId,
            SessionId      = SessionId,
            StepIndex      = step,
            FromStage      = from,
            ToStage        = to,
            Taken          = taken,
            DeadEndReason  = deadEnd,
            Trigger        = trigger,
            ReducerVersion = reducer,
            IsTerminal     = DecisionGraphBuilder.IsTerminalStage(to),
            OccurredAtUtc  = new DateTime(2026, 4, 21, 10, step, 0, DateTimeKind.Utc),
        };

    // ============================================================ Empty input

    [Fact]
    public void Build_returns_empty_projection_when_no_transitions()
    {
        var graph = DecisionGraphBuilder.Build(TenantId, SessionId, Array.Empty<DecisionTransitionRecord>());

        Assert.Equal(TenantId, graph.TenantId);
        Assert.Equal(SessionId, graph.SessionId);
        Assert.Empty(graph.Nodes);
        Assert.Empty(graph.Edges);
        Assert.Equal(string.Empty, graph.ReducerVersion);
    }

    // ============================================================ Node de-duplication

    [Fact]
    public void Build_deduplicates_stages_into_unique_nodes()
    {
        // Same stage visited twice through different paths — appears once in Nodes.
        var transitions = new[]
        {
            Transition(0, "SessionStarted", "EspInProgress"),
            Transition(1, "EspInProgress",   "AccountSetup"),
            Transition(2, "AccountSetup",    "EspInProgress"), // back-edge (hypothetical)
            Transition(3, "EspInProgress",   "Completed"),
        };

        var graph = DecisionGraphBuilder.Build(TenantId, SessionId, transitions);

        // Unique stages: SessionStarted, EspInProgress, AccountSetup, Completed = 4 nodes.
        Assert.Equal(4, graph.Nodes.Count);
        Assert.Contains(graph.Nodes, n => n.Id == "SessionStarted");
        Assert.Contains(graph.Nodes, n => n.Id == "EspInProgress");
        Assert.Contains(graph.Nodes, n => n.Id == "AccountSetup");
        Assert.Contains(graph.Nodes, n => n.Id == "Completed");
    }

    // ============================================================ Terminal classification

    [Theory]
    [InlineData("Completed",                 "Succeeded")]
    [InlineData("Failed",                    "Failed")]
    [InlineData("WhiteGloveSealed",          "PausedForPart2")]
    public void Build_marks_terminal_stages_and_derives_outcome(string terminalStage, string expectedOutcome)
    {
        var transitions = new[]
        {
            Transition(0, "Start", terminalStage),
        };

        var graph = DecisionGraphBuilder.Build(TenantId, SessionId, transitions);
        var node = graph.Nodes.Single(n => n.Id == terminalStage);

        Assert.True(node.IsTerminal);
        Assert.Equal(expectedOutcome, node.TerminalOutcome);
    }

    [Fact]
    public void Build_leaves_non_terminal_nodes_unmarked()
    {
        var transitions = new[] { Transition(0, "SessionStarted", "EspInProgress") };

        var graph = DecisionGraphBuilder.Build(TenantId, SessionId, transitions);

        Assert.All(graph.Nodes, n =>
        {
            Assert.False(n.IsTerminal);
            Assert.Null(n.TerminalOutcome);
        });
    }

    // ============================================================ Edges

    [Fact]
    public void Build_preserves_all_edges_including_dead_ends()
    {
        var transitions = new[]
        {
            Transition(0, "EspInProgress", "Completed", taken: true),
            Transition(1, "EspInProgress", "EspInProgress", taken: false,
                deadEnd: "hybrid_reboot_gate_blocking", trigger: "EspExiting"),
        };

        var graph = DecisionGraphBuilder.Build(TenantId, SessionId, transitions);

        Assert.Equal(2, graph.Edges.Count);
        Assert.Contains(graph.Edges, e => e.Taken && e.ToStage == "Completed");
        var deadEnd = graph.Edges.Single(e => !e.Taken);
        Assert.Equal("hybrid_reboot_gate_blocking", deadEnd.DeadEndReason);
        Assert.Equal("EspExiting", deadEnd.Trigger);
    }

    [Fact]
    public void Build_orders_edges_by_StepIndex_even_if_input_is_shuffled()
    {
        // Repository sorts, but defend anyway — the builder is used directly in tests and
        // could be fed arbitrary lists.
        var transitions = new[]
        {
            Transition(2, "AccountSetup", "Completed"),
            Transition(0, "SessionStarted", "EspInProgress"),
            Transition(1, "EspInProgress", "AccountSetup"),
        };

        var graph = DecisionGraphBuilder.Build(TenantId, SessionId, transitions);

        Assert.Equal(new[] { 0, 1, 2 }, graph.Edges.Select(e => e.StepIndex));
    }

    // ============================================================ VisitCount

    [Fact]
    public void Build_counts_only_taken_transitions_toward_VisitCount()
    {
        var transitions = new[]
        {
            Transition(0, "Start",          "EspInProgress", taken: true),
            Transition(1, "EspInProgress",  "EspInProgress", taken: false, deadEnd: "blocked"),
            Transition(2, "EspInProgress",  "Completed",     taken: true),
            Transition(3, "Start",          "EspInProgress", taken: true), // hypothetical retry
        };

        var graph = DecisionGraphBuilder.Build(TenantId, SessionId, transitions);

        var esp = graph.Nodes.Single(n => n.Id == "EspInProgress");
        Assert.Equal(2, esp.VisitCount); // two taken → EspInProgress; the dead-end does not count

        var completed = graph.Nodes.Single(n => n.Id == "Completed");
        Assert.Equal(1, completed.VisitCount);
    }

    [Fact]
    public void Build_exposes_ReducerVersion_from_first_row()
    {
        var transitions = new[]
        {
            Transition(0, "Start", "EspInProgress", reducer: "1.4.2"),
            Transition(1, "EspInProgress", "Completed", reducer: "1.4.2"),
        };

        var graph = DecisionGraphBuilder.Build(TenantId, SessionId, transitions);

        Assert.Equal("1.4.2", graph.ReducerVersion);
    }
}
