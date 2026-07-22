using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pure-function tests for the runaway-session auto-action decision gate.
/// A full <see cref="MaintenanceService"/> smoke test would have to build ~16
/// dependencies; the boundary logic that decides Block vs Kill vs no-action is
/// the part most likely to silently break (off-by-one, casing drift, mis-cast
/// of an "Off" mode), so it lives behind <see cref="MaintenanceService.DecideAutoAction"/>
/// — analog to <c>ClassifyCertExpiryTier</c>.
/// </summary>
public class MaintenanceServiceExcessiveEventAutoActionTests
{
    [Fact]
    public void DecideAutoAction_ModeOff_ReturnsNull()
    {
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount: 9999, autoMode: "Off", autoThreshold: 2500));
    }

    [Fact]
    public void DecideAutoAction_ThresholdZero_IsAlwaysDisabled()
    {
        // Defensive: 0 disables the feature even when the mode is set, so an admin can
        // park the mode without losing the value, and the warn path keeps running alone.
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount: 99999, autoMode: "Block", autoThreshold: 0));
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount: 99999, autoMode: "Kill", autoThreshold: 0));
    }

    [Fact]
    public void DecideAutoAction_NegativeThreshold_IsDisabled()
    {
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount: 99999, autoMode: "Block", autoThreshold: -1));
    }

    [Theory]
    [InlineData(2500)]   // exactly at threshold → not yet
    [InlineData(2499)]
    [InlineData(0)]
    public void DecideAutoAction_BelowOrAtThreshold_ReturnsNull(int eventCount)
    {
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount, autoMode: "Block", autoThreshold: 2500));
    }

    [Theory]
    [InlineData(2501)]
    [InlineData(3000)]
    [InlineData(int.MaxValue)]
    public void DecideAutoAction_AboveThreshold_Block_ReturnsBlock(int eventCount)
    {
        Assert.Equal("Block", MaintenanceService.DecideAutoAction(eventCount, autoMode: "Block", autoThreshold: 2500));
    }

    [Fact]
    public void DecideAutoAction_AboveThreshold_Kill_ReturnsKill()
    {
        Assert.Equal("Kill", MaintenanceService.DecideAutoAction(eventCount: 5000, autoMode: "Kill", autoThreshold: 2500));
    }

    [Theory]
    [InlineData("block")]
    [InlineData("BLOCK")]
    [InlineData(" Block ")]
    public void DecideAutoAction_ToleratesCasingAndPaddingForBlock(string mode)
    {
        // Storage round-trips may surface different casings, and admin imports could carry
        // padding from CSV cells. Normalize so the gate isn't silently disabled.
        Assert.Equal("Block", MaintenanceService.DecideAutoAction(eventCount: 5000, autoMode: mode, autoThreshold: 2500));
    }

    [Theory]
    [InlineData("kill")]
    [InlineData("KILL")]
    public void DecideAutoAction_ToleratesCasingForKill(string mode)
    {
        Assert.Equal("Kill", MaintenanceService.DecideAutoAction(eventCount: 5000, autoMode: mode, autoThreshold: 2500));
    }

    [Theory]
    [InlineData("Suspend")]
    [InlineData("Quarantine")]
    [InlineData("")]
    [InlineData(null)]
    public void DecideAutoAction_UnknownMode_FailsClosed(string? mode)
    {
        // Unknown values (typo, future enum extension not yet wired here) must NOT execute
        // — better to no-op than block/kill on an unintended config.
        Assert.Null(MaintenanceService.DecideAutoAction(eventCount: 5000, autoMode: mode, autoThreshold: 2500));
    }

    // ── Status eligibility ────────────────────────────────────────────────────
    // A device may only be blocked over a session that can still upload. Without this gate
    // a session that finished days ago with a high EventCount got its device blocked on the
    // next sweep, long after it stopped sending.

    [Fact]
    public void IsAutoActionEligible_InProgress_IsEligible()
    {
        Assert.True(MaintenanceService.IsAutoActionEligible(SessionStatus.InProgress));
    }

    [Theory]
    [InlineData(SessionStatus.Succeeded)]
    [InlineData(SessionStatus.Failed)]
    [InlineData(SessionStatus.Incomplete)]
    [InlineData(SessionStatus.Stalled)]
    [InlineData(SessionStatus.AwaitingUser)]
    [InlineData(SessionStatus.Pending)]
    [InlineData(SessionStatus.Unknown)]
    public void IsAutoActionEligible_EverythingElse_IsRejected(SessionStatus status)
    {
        // Matches the time-window watchdog's `Status eq 'InProgress'` filter, so neither
        // detector can block a device over a session that no longer sends. Stalled and
        // AwaitingUser can heal back to InProgress — then the next sweep acts.
        Assert.False(MaintenanceService.IsAutoActionEligible(status));
    }

    // ── Status parsing feeding that gate ──────────────────────────────────────
    // The runaway query projects a subset of columns. SessionStatus.InProgress is ordinal 0,
    // so an unparsed Status would leave every row looking in-progress and the gate above
    // silently always-true — these pin the fail-closed mapping.

    [Theory]
    [InlineData("InProgress", SessionStatus.InProgress)]
    [InlineData("inprogress", SessionStatus.InProgress)]
    [InlineData("Succeeded", SessionStatus.Succeeded)]
    [InlineData("AwaitingUser", SessionStatus.AwaitingUser)]
    public void ParseStatusForAutoAction_KnownNames_RoundTrip(string stored, SessionStatus expected)
    {
        Assert.Equal(expected, TableStorageService.ParseStatusForAutoAction(stored));
    }

    [Theory]
    [InlineData(null)]        // column absent from the projection or the row
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Enrolling")] // status name that never existed
    [InlineData("0")]         // Enum.TryParse would map this onto InProgress and open the gate
    [InlineData("3")]
    [InlineData("-1")]
    [InlineData("InProgress,Failed")] // flags-style combination is not a real status
    public void ParseStatusForAutoAction_UnusableInput_IsUnknown(string? stored)
    {
        Assert.Equal(SessionStatus.Unknown, TableStorageService.ParseStatusForAutoAction(stored));
        Assert.False(MaintenanceService.IsAutoActionEligible(TableStorageService.ParseStatusForAutoAction(stored)));
    }
}
