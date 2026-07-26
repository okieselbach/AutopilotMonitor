using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// PR0 (2026-07-26, source-data audit §0.5 of tasks/insights-expansion-spec.md).
///
/// Ingest side: the agent emits <c>app_install_completed</c> for EVERY terminal transition —
/// including Skipped/Postponed no-ops (V1 wire parity; production: 78 % of zero-duration rows
/// were skips). The payload's <c>state</c> field must be persisted as
/// <see cref="AppInstallSummary.TerminalState"/> so metrics can exclude skips from durations
/// and rates.
///
/// Store side: <see cref="TableStorageService.ReconcileAppInstallSummaryWithExisting"/> pins the
/// cross-batch contract — earliest StartedAt wins, terminal status is sticky, and the Q4
/// out-of-order case (terminal batch first, started batch later) recomputes the duration.
/// </summary>
public class AppInstallTerminalStateAndReconcileTests
{
    private static readonly DateTime T0 = new(2026, 7, 26, 10, 0, 0, DateTimeKind.Utc);

    // ── ingest: TerminalState fold ──────────────────────────────────────────

    private static Dictionary<string, AppInstallAggregationState> Aggregate(params EnrollmentEvent[] events)
    {
        var summaries = new Dictionary<string, AppInstallAggregationState>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in events)
            EventIngestProcessor.AggregateAppInstallEvent(evt, "tenant", "session", summaries);
        return summaries;
    }

    private static EnrollmentEvent AppEvent(string eventType, DateTime ts, string? state = null, string appName = "App")
    {
        var evt = new EnrollmentEvent
        {
            EventType = eventType,
            Timestamp = ts,
            Data = new Dictionary<string, object> { ["appName"] = appName },
        };
        if (state != null) evt.Data["state"] = state;
        return evt;
    }

    [Theory]
    [InlineData("Installed")]
    [InlineData("Skipped")]
    [InlineData("Postponed")]
    public void Completed_PersistsPayloadState(string state)
    {
        var s = Aggregate(AppEvent("app_install_completed", T0, state))["App"].Summary;

        Assert.Equal("Succeeded", s.Status);          // backward-compatible status
        Assert.Equal(state, s.TerminalState);          // new precision column
    }

    [Fact]
    public void Completed_WithoutState_LeavesSentinelEmpty_NeverGuessed()
    {
        var s = Aggregate(AppEvent("app_install_completed", T0))["App"].Summary;
        Assert.Equal(string.Empty, s.TerminalState);
    }

    [Fact]
    public void Completed_WithNonTerminalOrGarbageState_LeavesSentinelEmpty()
    {
        // A payload state outside the terminal set (bug or drift) must not be persisted.
        var s = Aggregate(AppEvent("app_install_completed", T0, "Downloading"))["App"].Summary;
        Assert.Equal(string.Empty, s.TerminalState);
    }

    [Fact]
    public void Failed_SetsError()
    {
        var s = Aggregate(AppEvent("app_install_failed", T0))["App"].Summary;
        Assert.Equal("Failed", s.Status);
        Assert.Equal("Error", s.TerminalState);
    }

    [Fact]
    public void FailedThenCompleted_RetrySucceeds_LastTerminalWins()
    {
        // IME retry path (audit Q1): Error → retry → Installed. The later completed event is
        // the authoritative outcome for Status AND TerminalState.
        var s = Aggregate(
            AppEvent("app_install_failed", T0),
            AppEvent("app_install_completed", T0.AddMinutes(10), "Installed"))["App"].Summary;

        Assert.Equal("Succeeded", s.Status);
        Assert.Equal("Installed", s.TerminalState);
    }

    [Fact]
    public void SkippedEvent_SetsSkipped_ButNeverOverridesStrongerTerminal()
    {
        var fresh = Aggregate(AppEvent("app_install_skipped", T0))["App"].Summary;
        Assert.Equal("Succeeded", fresh.Status);
        Assert.Equal("Skipped", fresh.TerminalState);

        var afterFailure = Aggregate(
            AppEvent("app_install_failed", T0),
            AppEvent("app_install_skipped", T0.AddSeconds(5)))["App"].Summary;
        Assert.Equal("Error", afterFailure.TerminalState);
    }

    // ── store: cross-batch reconcile ────────────────────────────────────────

    [Fact]
    public void Reconcile_Q4_OutOfOrder_RecomputesDurationFromStoredCompletedAt()
    {
        // Terminal batch landed first: row has CompletedAt but no usable duration.
        var existing = new TableEntity("tenant", "session_App")
        {
            ["Status"] = "Succeeded",
            ["StartedAt"] = new DateTimeOffset(T0.AddMinutes(5)),   // late (completed-event) stamp
            ["CompletedAt"] = new DateTimeOffset(T0.AddMinutes(5)),
        };
        // Started batch arrives now with the true (earlier) start.
        var summary = new AppInstallSummary
        {
            AppName = "App", SessionId = "session", TenantId = "tenant",
            Status = "InProgress", StartedAt = T0,
        };

        TableStorageService.ReconcileAppInstallSummaryWithExisting(summary, existing);

        Assert.Equal(T0, summary.StartedAt);                        // earlier start kept
        Assert.Equal(T0.AddMinutes(5), summary.CompletedAt);        // endpoint adopted from the row
        Assert.Equal(300, summary.DurationSeconds);                 // recomputed — was permanently unset before PR0
        Assert.Equal("Succeeded", summary.Status);                  // sticky terminal (below)
    }

    [Fact]
    public void Reconcile_TerminalStatusIsSticky_AgainstLateInProgressBatch()
    {
        var existing = new TableEntity("tenant", "session_App") { ["Status"] = "Failed" };
        var summary = new AppInstallSummary { AppName = "App", Status = "InProgress", StartedAt = T0 };

        TableStorageService.ReconcileAppInstallSummaryWithExisting(summary, existing);

        Assert.Equal("Failed", summary.Status);
    }

    [Fact]
    public void Reconcile_TerminalToTerminal_StaysWithIncomingEvidence()
    {
        // Failed row + succeeded batch (successful retry) → the new terminal wins.
        var existing = new TableEntity("tenant", "session_App") { ["Status"] = "Failed" };
        var summary = new AppInstallSummary { AppName = "App", Status = "Succeeded", StartedAt = T0 };

        TableStorageService.ReconcileAppInstallSummaryWithExisting(summary, existing);

        Assert.Equal("Succeeded", summary.Status);
    }

    [Fact]
    public void Reconcile_Q4Guard_DoesNotFireWithoutStoredCompletedAt()
    {
        var existing = new TableEntity("tenant", "session_App") { ["Status"] = "InProgress" };
        var summary = new AppInstallSummary { AppName = "App", Status = "InProgress", StartedAt = T0 };

        TableStorageService.ReconcileAppInstallSummaryWithExisting(summary, existing);

        Assert.Null(summary.CompletedAt);
        Assert.Equal(0, summary.DurationSeconds);
    }

    [Fact]
    public void Reconcile_Q4Guard_IgnoresStoredCompletedAtBeforeIncomingStart()
    {
        // Negative interval (clock mess) must not produce a negative duration.
        var existing = new TableEntity("tenant", "session_App")
        {
            ["Status"] = "Succeeded",
            ["CompletedAt"] = new DateTimeOffset(T0.AddMinutes(-10)),
        };
        var summary = new AppInstallSummary { AppName = "App", Status = "InProgress", StartedAt = T0 };

        TableStorageService.ReconcileAppInstallSummaryWithExisting(summary, existing);

        Assert.Null(summary.CompletedAt);
        Assert.Equal(0, summary.DurationSeconds);
    }

    // ── entity builder: sentinel gating for the new column ─────────────────

    [Fact]
    public void EntityBuilder_TerminalState_IsSentinelGated()
    {
        var withState = new AppInstallSummary { AppName = "App", TerminalState = "Skipped" };
        var entity = TableStorageService.BuildAppInstallSummaryEntity(withState, "rk");
        Assert.Equal("Skipped", entity.GetString("TerminalState"));

        var withoutState = new AppInstallSummary { AppName = "App" };
        var empty = TableStorageService.BuildAppInstallSummaryEntity(withoutState, "rk");
        // Column absent → Merge-mode preserves a prior batch's terminal state.
        Assert.False(empty.ContainsKey("TerminalState"));
    }
}
