using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the timeout reclassification (docs/design/enrollment-status-reclassification.md).
/// The maintenance sweep must stop labelling every silent session Failed: a session whose
/// Account Setup rollup reached all-succeeded reconciles to Succeeded; one that finished
/// Device Setup but whose user phase never completed is AwaitingUser (within grace) then
/// Incomplete; and a session silent before Device Setup with no explicit failure is Incomplete
/// — never Failed without an explicit failure signal.
/// </summary>
public class EnrollmentTimeoutClassifierTests
{
    private static readonly DateTime Start = new(2026, 7, 6, 15, 0, 0, DateTimeKind.Utc);

    private static EnrollmentEvent Evt(string type, string? message = null) => new()
    {
        EventType = type,
        Timestamp = Start,
        Source = "test",
        Message = message ?? type,
    };

    private static EnrollmentEvent Esp(string message) => Evt("esp_provisioning_status", message);

    private const string DeviceSetup44 = "ESP provisioning status: DeviceSetup — 4 of 4 subcategories completed";
    private const string DeviceSetupFallback =
        "ESP provisioning status: DeviceSetup — all 4 subcategories succeeded but categorySucceeded was not confirmed by Windows — treating as complete (fallback after 30s)";
    private const string AccountSetup05 = "ESP provisioning status: AccountSetup — 0 of 5 subcategories completed";
    private const string AccountSetup15 = "ESP provisioning status: AccountSetup — 1 of 5 subcategories completed";
    private const string AccountSetup55 = "ESP provisioning status: AccountSetup — 5 of 5 subcategories completed";
    private const string AccountSetupFallback =
        "ESP provisioning status: AccountSetup — all 5 subcategories succeeded but categorySucceeded was not confirmed by Windows — treating as complete (fallback after 30s)";

    // -------- ExtractRollup --------

    [Fact]
    public void ExtractRollup_empty_is_all_false()
    {
        var r = EnrollmentTimeoutClassifier.ExtractRollup(null);
        Assert.False(r.DeviceSetupAllSucceeded);
        Assert.False(r.AccountSetupAllSucceeded);
        Assert.Equal(0, r.AccountSetupSucceededCount);
        Assert.False(r.HasExplicitFailure);
        Assert.False(r.HasTerminalComplete);
    }

    [Fact]
    public void ExtractRollup_reads_device_and_account_rollups()
    {
        var r = EnrollmentTimeoutClassifier.ExtractRollup(new[]
        {
            Esp(DeviceSetup44), Esp(AccountSetup05), Esp(AccountSetup15),
        });
        Assert.True(r.DeviceSetupAllSucceeded);
        Assert.Equal(1, r.AccountSetupSucceededCount);   // strongest observation wins
        Assert.Equal(5, r.AccountSetupTotal);
        Assert.False(r.AccountSetupAllSucceeded);
    }

    [Fact]
    public void ExtractRollup_account_5of5_is_all_succeeded()
    {
        var r = EnrollmentTimeoutClassifier.ExtractRollup(new[] { Esp(DeviceSetup44), Esp(AccountSetup55) });
        Assert.True(r.AccountSetupAllSucceeded);
        Assert.Equal(5, r.AccountSetupSucceededCount);
    }

    [Fact]
    public void ExtractRollup_honours_fallback_complete_messages()
    {
        var r = EnrollmentTimeoutClassifier.ExtractRollup(new[] { Esp(DeviceSetupFallback), Esp(AccountSetupFallback) });
        Assert.True(r.DeviceSetupAllSucceeded);
        Assert.True(r.AccountSetupAllSucceeded);
    }

    [Fact]
    public void ExtractRollup_detects_failure_and_complete_events()
    {
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("enrollment_failed") }).HasExplicitFailure);
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("esp_failure") }).HasExplicitFailure);
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("enrollment_complete") }).HasTerminalComplete);
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("whiteglove_complete") }).HasTerminalComplete);
    }

    // -------- ClassifyTimedOutSession --------

    private static (SessionStatus, string) Classify(IReadOnlyList<EnrollmentEvent> events, double hoursSinceStart = 6, int grace = 72)
    {
        var rollup = EnrollmentTimeoutClassifier.ExtractRollup(events);
        var now = Start.AddHours(hoursSinceStart);
        return EnrollmentTimeoutClassifier.ClassifyTimedOutSession(rollup, Start, now, grace);
    }

    [Fact]
    public void Classify_explicit_failure_is_Failed()
    {
        var (status, _) = Classify(new[] { Esp(DeviceSetup44), Evt("enrollment_failed") });
        Assert.Equal(SessionStatus.Failed, status);
    }

    [Fact]
    public void Classify_account_setup_complete_reconciles_to_Succeeded()
    {
        var (status, _) = Classify(new[] { Esp(DeviceSetup44), Esp(AccountSetup55) });
        Assert.Equal(SessionStatus.Succeeded, status);
    }

    [Fact]
    public void Classify_enrollment_complete_reconciles_to_Succeeded()
    {
        var (status, _) = Classify(new[] { Esp(DeviceSetup44), Esp(AccountSetup05), Evt("enrollment_complete") });
        Assert.Equal(SessionStatus.Succeeded, status);
    }

    [Fact]
    public void Classify_device_provisioned_user_phase_pending_within_grace_is_AwaitingUser()
    {
        // The dominant crcins.com case: DeviceSetup 4/4, AccountSetup 0/5, silent, 6h in.
        var (status, reason) = Classify(new[] { Esp(DeviceSetup44), Esp(AccountSetup05) }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.AwaitingUser, status);
        Assert.Contains("Device Setup completed", reason);
    }

    [Fact]
    public void Classify_device_provisioned_after_grace_is_Incomplete()
    {
        var (status, _) = Classify(new[] { Esp(DeviceSetup44), Esp(AccountSetup05) }, hoursSinceStart: 80, grace: 72);
        Assert.Equal(SessionStatus.Incomplete, status);
    }

    [Fact]
    public void Classify_silent_before_device_setup_is_Incomplete_not_Failed()
    {
        // No DeviceSetup all-succeeded, no failure event → Incomplete (we don't know), not Failed.
        var (status, _) = Classify(new[] { Evt("agent_started"), Esp("ESP provisioning status: DeviceSetup — 3 of 4 subcategories completed") });
        Assert.Equal(SessionStatus.Incomplete, status);
    }

    // -------- ResolveGraceHours --------

    [Theory]
    [InlineData(null, null, 60)] // defaults: 48 + 12
    [InlineData(0, null, 60)]    // 0 override = auto-derive
    [InlineData(0, 48, 60)]      // explicit agent cap = default
    [InlineData(0, 96, 108)]     // bigger agent cap → grace follows (96 + 12)
    [InlineData(0, 0, 60)]       // agent cap 0/invalid → fall back to default 48
    [InlineData(90, 48, 90)]     // override ABOVE the floor wins
    [InlineData(30, 48, 60)]     // override BELOW the floor is clamped up to the floor
    public void ResolveGraceHours_floors_at_agent_cap_plus_buffer(int? configured, int? absoluteMax, int expected)
    {
        Assert.Equal(expected, EnrollmentTimeoutClassifier.ResolveGraceHours(configured, absoluteMax));
    }

    [Fact]
    public void ResolveGraceHours_never_below_agent_absolute_cap()
    {
        // Property: whatever the inputs, the grace is at least the agent's absolute cap, so the backend
        // never terminalizes Incomplete while the agent could still legitimately be enrolling.
        foreach (var absMax in new int?[] { null, 6, 48, 72, 96 })
        {
            var cap = absMax.GetValueOrDefault(EnrollmentTimeoutClassifier.DefaultAbsoluteMaxSessionHours);
            var grace = EnrollmentTimeoutClassifier.ResolveGraceHours(0, absMax);
            Assert.True(grace >= cap, $"grace {grace} must be >= agent cap {cap}");
        }
    }

    [Fact]
    public void Classify_never_returns_Failed_without_explicit_failure()
    {
        // Guard the core invariant across a spread of non-failure inputs.
        foreach (var events in new[]
        {
            new[] { Esp(DeviceSetup44), Esp(AccountSetup05) },
            new[] { Esp(DeviceSetupFallback) },
            new[] { Evt("agent_started") },
        })
        {
            var (status, _) = Classify(events);
            Assert.NotEqual(SessionStatus.Failed, status);
        }
    }
}
