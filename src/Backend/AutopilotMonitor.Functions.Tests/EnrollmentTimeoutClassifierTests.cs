using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the timeout reclassification (tasks/enrollment-status-reclassification.md).
/// The maintenance sweep must stop labelling every silent session Failed: a session whose
/// Account Setup rollup reached all-succeeded reconciles to Succeeded; one that finished
/// Device Setup but whose user phase never completed is AwaitingUser (within grace) then
/// Incomplete; and a session silent before Device Setup with no explicit failure is Incomplete
/// — never Failed without an explicit failure signal.
/// </summary>
public class EnrollmentTimeoutClassifierTests
{
    private static readonly DateTime Start = new(2026, 7, 6, 15, 0, 0, DateTimeKind.Utc);

    private static EnrollmentEvent Evt(string type, string? message = null,
        Dictionary<string, object>? data = null) => new()
    {
        EventType = type,
        Timestamp = Start,
        Source = "test",
        Message = message ?? type,
        Data = data!,
    };

    private static EnrollmentEvent HelloPolicy(bool enabled) =>
        Evt("hello_policy_detected", data: new Dictionary<string, object> { ["helloEnabled"] = enabled.ToString() });

    private static EnrollmentEvent EspConfig(bool skipUser) =>
        Evt("esp_config_detected", data: new Dictionary<string, object> { ["skipUserStatusPage"] = skipUser });

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
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("agent_emergency_break") }).HasAgentEmergencyBreak);
    }

    [Fact]
    public void ExtractRollup_detects_desktop_hello_and_realmjoin_evidence()
    {
        var r = EnrollmentTimeoutClassifier.ExtractRollup(new[]
        {
            Evt("desktop_arrived"), Evt("hello_provisioning_completed"), Evt("realmjoin_detected"),
        });
        Assert.True(r.DesktopArrived);
        Assert.True(r.HelloResolved);
        Assert.True(r.RealmJoinDetected);
        Assert.False(r.RealmJoinResolved);

        // hello_skipped is the other positive Hello terminal (agent raises HelloResolved for it).
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("hello_skipped") }).HelloResolved);
        // Negative Hello terminals leave the agent waiting — must NOT count as resolved.
        Assert.False(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("hello_provisioning_failed") }).HelloResolved);
        Assert.False(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("hello_completion_timeout") }).HelloResolved);
        // All RealmJoin gate terminals count as resolved (phase 110, aborted first
        // deployment — session 224b2087 — or 60-min hard timeout).
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("realmjoin_resolved") }).RealmJoinResolved);
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("realmjoin_timeout") }).RealmJoinResolved);
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("realmjoin_first_deployment_incomplete") }).RealmJoinResolved);
    }

    [Fact]
    public void ExtractRollup_reads_hello_policy_and_skip_user_esp_from_event_data()
    {
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { HelloPolicy(enabled: false) }).HelloPolicyDisabled);
        Assert.False(EnrollmentTimeoutClassifier.ExtractRollup(new[] { HelloPolicy(enabled: true) }).HelloPolicyDisabled);
        // Contradicting observations resolve pessimistically → treat as enabled (keep demanding
        // the Hello terminal).
        Assert.False(EnrollmentTimeoutClassifier.ExtractRollup(
            new[] { HelloPolicy(enabled: false), HelloPolicy(enabled: true) }).HelloPolicyDisabled);

        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { EspConfig(skipUser: true) }).SkipUserEsp);
        Assert.False(EnrollmentTimeoutClassifier.ExtractRollup(new[] { EspConfig(skipUser: false) }).SkipUserEsp);
        Assert.False(EnrollmentTimeoutClassifier.ExtractRollup(
            new[] { EspConfig(skipUser: true), EspConfig(skipUser: false) }).SkipUserEsp);
        // No policy events at all → both false (pessimistic default).
        var bare = EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("agent_started") });
        Assert.False(bare.HelloPolicyDisabled);
        Assert.False(bare.SkipUserEsp);
    }

    [Fact]
    public void Classify_emergency_break_skips_grace_and_is_Incomplete()
    {
        // Agent reported its absolute-age break → it's gone. Even DeviceSetup-done + well within grace
        // must NOT wait as AwaitingUser; the honest verdict is Incomplete right now.
        var (status, reason) = Classify(
            new[] { Esp(DeviceSetup44), Esp(AccountSetup05), Evt("agent_emergency_break") }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Incomplete, status);
        Assert.Contains("emergency break", reason);
    }

    [Fact]
    public void Classify_emergency_break_still_yields_to_a_real_completion()
    {
        // If the session actually completed, that wins over the break marker.
        var (status, _) = Classify(
            new[] { Esp(DeviceSetup44), Esp(AccountSetup55), Evt("agent_emergency_break") }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Succeeded, status);
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

    // -------- "user completed setup" reconcile (session 294ab5b4) --------

    [Fact]
    public void Classify_desktop_plus_hello_with_unresolved_realmjoin_reconciles_to_Succeeded()
    {
        // Session 294ab5b4 replay: DeviceSetup 4/4, AccountSetup frozen at 1/5 after the user
        // hit the desktop, Hello provisioned, RealmJoin detected but never resolved — agent
        // went silent mid-deployment. The user was provably there; "AwaitingUser" is wrong.
        var (status, reason) = Classify(new[]
        {
            Esp(DeviceSetup44), Esp(AccountSetup15),
            Evt("desktop_arrived"), Evt("hello_provisioning_completed"), Evt("realmjoin_detected"),
        }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Succeeded, status);
        Assert.Contains("RealmJoin", reason);
    }

    [Fact]
    public void Classify_reconcile_reason_carries_silence_timing_transparency()
    {
        // Transparency (session efbc17ff): the reconcile reason must name the last agent
        // contact, the silence duration, and the exact moment the platform declared success —
        // so a customer can tell "user powered the device off" apart from "declared too early".
        var lastEvent = Start.AddMinutes(37);      // agent last reported 37 min after start
        var now = Start.AddHours(5);               // sweep declared success 5h after start
        var rollup = EnrollmentTimeoutClassifier.ExtractRollup(new[]
        {
            Esp(DeviceSetup44), Esp(AccountSetup15),
            Evt("desktop_arrived"), Evt("hello_provisioning_completed"), Evt("realmjoin_detected"),
        });
        var (status, reason) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
            rollup, Start, now, graceHours: 72, lastEventAtUtc: lastEvent);

        Assert.Equal(SessionStatus.Succeeded, status);
        Assert.Contains("RealmJoin", reason);                                   // core verdict preserved
        Assert.Contains($"Agent last reported {lastEvent:yyyy-MM-dd HH:mm} UTC", reason);
        Assert.Contains("silent ~4h 23m", reason);                             // 5h - 37m
        Assert.Contains($"declared this success at {now:yyyy-MM-dd HH:mm} UTC", reason);
    }

    [Fact]
    public void Classify_reconcile_reason_timing_falls_back_to_start_when_last_event_unknown()
    {
        // No last-contact time → anchor on StartedAt (same fallback the stalled-marker uses),
        // and the suffix is still emitted so the badge is never left timestamp-less.
        var now = Start.AddHours(6);
        var (_, reason) = Classify(new[] { Esp(DeviceSetup44), Esp(AccountSetup55) }, hoursSinceStart: 6);
        Assert.Contains($"Agent last reported {Start:yyyy-MM-dd HH:mm} UTC", reason);
        Assert.Contains("silent ~6h 0m", reason);
        Assert.Contains($"declared this success at {now:yyyy-MM-dd HH:mm} UTC", reason);
    }

    [Fact]
    public void Classify_desktop_plus_hello_without_realmjoin_reconciles_to_Succeeded()
    {
        // Both Classic completion prerequisites in and no gate pending: the agent died in the
        // narrow window before it could report enrollment_complete.
        var (status, reason) = Classify(new[]
        {
            Esp(DeviceSetup44), Esp(AccountSetup15),
            Evt("desktop_arrived"), Evt("hello_provisioning_completed"),
        }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Succeeded, status);
        Assert.Contains("silent before reporting completion", reason);
    }

    [Fact]
    public void Classify_desktop_plus_hello_skipped_also_reconciles_to_Succeeded()
    {
        var (status, _) = Classify(new[]
        {
            Esp(DeviceSetup44), Evt("desktop_arrived"), Evt("hello_skipped"),
        }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Succeeded, status);
    }

    [Fact]
    public void Classify_hello_disabled_plus_skip_user_esp_plus_desktop_reconciles_to_Succeeded()
    {
        // Mirror of the agent's Hello-disabled fast-path: HelloPolicyEnabled==false +
        // SkipUserEsp==true + desktop arrival completes on the device, so a silent session
        // with the same evidence reconciles to Succeeded — no Hello terminal can ever exist
        // in this configuration.
        var (status, reason) = Classify(new[]
        {
            Esp(DeviceSetup44), Evt("desktop_arrived"),
            HelloPolicy(enabled: false), EspConfig(skipUser: true),
        }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Succeeded, status);
        Assert.Contains("User ESP skipped, Windows Hello disabled", reason);
    }

    [Fact]
    public void Classify_hello_disabled_without_skip_user_esp_stays_AwaitingUser()
    {
        // Hello disabled but User ESP required: the agent's strong post-AccountSetup gate
        // (session 08c99638) blocks its fast-path too — completion needs the AccountSetup
        // rollup (rule 2) there, so the backend must keep waiting as well.
        var (status, _) = Classify(new[]
        {
            Esp(DeviceSetup44), Esp(AccountSetup15), Evt("desktop_arrived"),
            HelloPolicy(enabled: false), EspConfig(skipUser: false),
        }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.AwaitingUser, status);
    }

    [Fact]
    public void Classify_skip_user_esp_with_hello_enabled_still_requires_hello_terminal()
    {
        // SkipUserEsp only waives the AccountSetup evidence, never the Hello wizard — with
        // Hello enabled the user still has to finish/skip the wizard on the device.
        var (status, _) = Classify(new[]
        {
            Esp(DeviceSetup44), Evt("desktop_arrived"),
            HelloPolicy(enabled: true), EspConfig(skipUser: true),
        }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.AwaitingUser, status);
    }

    [Fact]
    public void Classify_desktop_without_hello_terminal_stays_AwaitingUser()
    {
        // desktop_arrived alone is explicitly NOT a completion signal (design doc) — the user
        // may still be mid Hello wizard / user phase. Falls through to the AwaitingUser rule.
        var (status, _) = Classify(new[]
        {
            Esp(DeviceSetup44), Esp(AccountSetup15), Evt("desktop_arrived"),
        }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.AwaitingUser, status);
    }

    [Fact]
    public void Classify_explicit_failure_beats_desktop_plus_hello()
    {
        var (status, _) = Classify(new[]
        {
            Esp(DeviceSetup44), Evt("desktop_arrived"), Evt("hello_provisioning_completed"),
            Evt("enrollment_failed"),
        }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Failed, status);
    }

    [Fact]
    public void Classify_emergency_break_beats_desktop_plus_hello()
    {
        // The break means the agent stayed alive to the 48h absolute cap WITHOUT completing —
        // despite both prerequisites being in, something blocked completion for two days.
        // That is not a success; the honest verdict stays Incomplete.
        var (status, _) = Classify(new[]
        {
            Esp(DeviceSetup44), Evt("desktop_arrived"), Evt("hello_provisioning_completed"),
            Evt("agent_emergency_break"),
        }, hoursSinceStart: 50);
        Assert.Equal(SessionStatus.Incomplete, status);
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

    // -------- Misclassification audit 2026-07-16 --------

    [Fact]
    public void ExtractRollup_agent_timeout_enrollment_failed_is_not_explicit_failure()
    {
        // The max-lifetime watchdog's enrollment_failed(failureType=agent_timeout) is "the agent
        // gave up waiting", not a failure verdict — it must not poison rule 1 (tenant a53e67ec).
        var timeout = Evt("enrollment_failed",
            data: new Dictionary<string, object> { ["failureType"] = "agent_timeout" });
        Assert.False(EnrollmentTimeoutClassifier.ExtractRollup(new[] { timeout }).HasExplicitFailure);

        // Any OTHER failureType stays an explicit failure.
        var genuine = Evt("enrollment_failed",
            data: new Dictionary<string, object> { ["failureType"] = "esp_terminal" });
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { genuine }).HasExplicitFailure);
    }

    [Fact]
    public void Classify_agent_timeout_failed_session_classifies_honestly()
    {
        // WG Part-2 shape (session 1506ce9f): DeviceSetup 4/4, no user phase, watchdog fired at
        // 6h — honest verdict is AwaitingUser (within grace), never Failed.
        var timeout = Evt("enrollment_failed",
            data: new Dictionary<string, object> { ["failureType"] = "agent_timeout" });
        var (status, _) = Classify(new[] { Esp(DeviceSetup44), timeout }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.AwaitingUser, status);
    }

    [Fact]
    public void ExtractRollup_account_0ofN_observation_records_total()
    {
        // A "0 of 5" rollup was previously dropped entirely (the strongest-observation fold only
        // kept n > 0), making the Incomplete reason read "0/0" — session 08ddbeec.
        var r = EnrollmentTimeoutClassifier.ExtractRollup(new[] { Esp(DeviceSetup44), Esp(AccountSetup05) });
        Assert.Equal(0, r.AccountSetupSucceededCount);
        Assert.Equal(5, r.AccountSetupTotal);
        Assert.False(r.AccountSetupAllSucceeded);
    }

    [Fact]
    public void Classify_incomplete_reason_shows_observed_account_rollup()
    {
        var (status, reason) = Classify(new[] { Esp(DeviceSetup44), Esp(AccountSetup05) }, hoursSinceStart: 80, grace: 72);
        Assert.Equal(SessionStatus.Incomplete, status);
        Assert.Contains("0/5", reason);
        Assert.DoesNotContain("0/0", reason);
    }

    [Fact]
    public void Classify_incomplete_reason_names_never_observed_account_rollup()
    {
        // No AccountSetup rollup at all — the reason must say so instead of a fabricated "0/0".
        var (status, reason) = Classify(new[] { Esp(DeviceSetup44) }, hoursSinceStart: 80, grace: 72);
        Assert.Equal(SessionStatus.Incomplete, status);
        Assert.Contains("Account Setup progress never observed", reason);
    }

    // -------- ResolveGraceHours --------

    [Theory]
    [InlineData(null, null, 51)] // defaults: 48 + 3
    [InlineData(0, null, 51)]    // 0 override = auto-derive
    [InlineData(0, 48, 51)]      // explicit agent cap = default
    [InlineData(0, 96, 99)]      // bigger agent cap → grace follows (96 + 3)
    [InlineData(0, 36, 51)]      // override BELOW the real agent default is clamped up to 48 (agent isn't wired yet)
    [InlineData(0, 0, 51)]       // agent cap 0/invalid → fall back to default 48
    [InlineData(90, 48, 90)]     // override ABOVE the floor wins
    [InlineData(30, 48, 51)]     // override BELOW the floor is clamped up to the floor
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
