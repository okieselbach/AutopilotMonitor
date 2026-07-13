using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the terminal/reconcile transition matrix (tasks/enrollment-status-reclassification.md):
/// a genuine completion may upgrade a prior Failed/Incomplete/AwaitingUser verdict (late reconcile),
/// while the silent-terminal verdicts never overwrite an existing terminal. This is the pure core of
/// PR3 — the reconcile that lets a device which completed after a sweep-timeout heal to Succeeded.
/// </summary>
public class SessionStatusTransitionTests
{
    // ---- Succeeded may UPGRADE any non-Succeeded state (reconcile) ----
    [Theory]
    [InlineData("Failed", true)]
    [InlineData("Incomplete", true)]
    [InlineData("AwaitingUser", true)]
    [InlineData("InProgress", true)]
    [InlineData("Stalled", true)]
    [InlineData("Succeeded", false)] // idempotent — already succeeded
    public void Succeeded_upgrades_everything_except_an_existing_success(string existing, bool allowed)
    {
        Assert.Equal(allowed, TableStorageService.IsTerminalTransitionAllowed(existing, SessionStatus.Succeeded));
    }

    // ---- Failed / Incomplete never overwrite an existing terminal ----
    [Theory]
    [InlineData("Succeeded", false)]
    [InlineData("Failed", false)]
    [InlineData("Incomplete", false)]
    [InlineData("AwaitingUser", true)]  // AwaitingUser is non-terminal
    [InlineData("InProgress", true)]
    [InlineData("Stalled", true)]
    public void Failed_and_Incomplete_never_overwrite_a_terminal(string existing, bool allowed)
    {
        Assert.Equal(allowed, TableStorageService.IsTerminalTransitionAllowed(existing, SessionStatus.Failed));
        Assert.Equal(allowed, TableStorageService.IsTerminalTransitionAllowed(existing, SessionStatus.Incomplete));
    }

    // ---- AwaitingUser never regresses a terminal ----
    [Theory]
    [InlineData("Succeeded", false)]
    [InlineData("Failed", false)]
    [InlineData("Incomplete", false)]
    [InlineData("InProgress", true)]
    [InlineData("Stalled", true)]
    public void AwaitingUser_never_regresses_a_terminal(string existing, bool allowed)
    {
        Assert.Equal(allowed, TableStorageService.IsTerminalTransitionAllowed(existing, SessionStatus.AwaitingUser));
    }

    // ---- Non-terminal incoming is governed by downstream guards, allowed here ----
    [Theory]
    [InlineData(SessionStatus.InProgress)]
    [InlineData(SessionStatus.Stalled)]
    [InlineData(SessionStatus.Pending)]
    public void Non_terminal_incoming_is_allowed_here(SessionStatus incoming)
    {
        Assert.True(TableStorageService.IsTerminalTransitionAllowed("Failed", incoming));
        Assert.True(TableStorageService.IsTerminalTransitionAllowed("Succeeded", incoming));
    }

    // ================= ComputeReconcileReason =================
    // The reconcile hygiene clears FailureReason/FailureSnapshotJson on every Succeeded write;
    // ReconcileReason is what keeps a backend-declared success explainable (session 294ab5b4).

    // ---- The sweep reconcile passes its classifier verdict — persisted verbatim ----
    [Theory]
    [InlineData("InProgress")]
    [InlineData("Stalled")]
    [InlineData("AwaitingUser")]
    [InlineData("Incomplete")]
    public void Sweep_reconcile_reason_is_persisted_verbatim(string prior)
    {
        const string verdict = "Reconciled at timeout: user completed setup (desktop + Windows Hello) — agent went silent before reporting completion";
        Assert.Equal(verdict, TableStorageService.ComputeReconcileReason(prior, verdict, adminMarkedAction: null));
    }

    // ---- Admin-marked successes are attributed via AdminMarkedAction, never ReconcileReason ----
    [Theory]
    [InlineData("InProgress", "Manually marked as succeeded by administrator")]
    [InlineData("Failed", "Manually marked as succeeded by administrator")]
    [InlineData("Failed", null)]
    public void Admin_marked_success_never_gets_a_reconcile_reason(string prior, string? reason)
    {
        Assert.Null(TableStorageService.ComputeReconcileReason(prior, reason, adminMarkedAction: "Succeeded"));
    }

    // ---- Reason-less late-completion upgrade of a prior backend/failure verdict → synthesized text ----
    [Theory]
    [InlineData("Failed")]
    [InlineData("Incomplete")]
    [InlineData("AwaitingUser")]
    public void Late_completion_upgrade_synthesizes_a_reason_naming_the_prior_verdict(string prior)
    {
        var reason = TableStorageService.ComputeReconcileReason(prior, reason: null, adminMarkedAction: null);
        Assert.NotNull(reason);
        Assert.Contains("Late completion report received", reason);
        Assert.Contains($"'{prior}'", reason);
    }

    // ---- Normal agent completions stay unmarked ----
    [Theory]
    [InlineData("InProgress")]
    [InlineData("Stalled")]
    [InlineData("Pending")]
    [InlineData(null)]
    public void Normal_agent_completion_gets_no_reconcile_reason(string? prior)
    {
        Assert.Null(TableStorageService.ComputeReconcileReason(prior, reason: null, adminMarkedAction: null));
        Assert.Null(TableStorageService.ComputeReconcileReason(prior, reason: "", adminMarkedAction: null));
    }
}
