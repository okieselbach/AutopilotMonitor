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
}
