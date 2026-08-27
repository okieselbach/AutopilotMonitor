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
        var (status, reason, _) = ClassifyWithRule(events, hoursSinceStart, grace);
        return (status, reason);
    }

    private static (SessionStatus, string, string) ClassifyWithRule(IReadOnlyList<EnrollmentEvent> events, double hoursSinceStart = 6, int grace = 72)
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
        var (status, reason, _) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
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
    public void Classify_agent_timeout_terminalizes_immediately_instead_of_parking()
    {
        // The watchdog firing proves the agent is gone for good — an AwaitingUser park from this
        // state is a countdown, not a wait: all 5 observed maxlife:r5_awaiting episodes expired
        // unhealed (calibration read 2026-08-27). Still never Failed, and late straggler
        // telemetry can heal the Incomplete (TryLateTelemetryReconcileAsync).
        var timeout = Evt("enrollment_failed",
            data: new Dictionary<string, object> { ["failureType"] = "agent_timeout" });
        var (status, reason) = Classify(new[] { Esp(DeviceSetup44), timeout }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Incomplete, status);
        Assert.Contains("max-lifetime watchdog", reason);

        // The V2 shutdown shape carries the same fact.
        var shutdown = Evt("agent_shutting_down",
            data: new Dictionary<string, object> { ["reason"] = "max_lifetime" });
        var (viaShutdown, _) = Classify(new[] { Esp(DeviceSetup44), shutdown }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Incomplete, viaShutdown);

        // WhiteGlove Part 2 (rule 1b) is deliberately untouched: a sealed/powered-off device
        // between technician and end user stays AwaitingUser — that park DOES heal
        // (the observed maxlife:r1b_awaiting episode resolved to Succeeded).
        var rollup = EnrollmentTimeoutClassifier.ExtractRollup(new[] { Esp(DeviceSetup44), timeout });
        var (wgStatus, _, wgRule) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
            rollup, Start, Start.AddHours(6), 72, isPreProvisioned: true, resumedAt: Start.AddHours(1));
        Assert.Equal(SessionStatus.AwaitingUser, wgStatus);
        Assert.Equal(ClassifierRules.R1bWhiteGloveAwaiting, wgRule);
    }

    // -------- Rule 5a: completed (assumed) — calibration read 2026-08-27 --------

    [Fact]
    public void ExtractRollup_detects_app_failures_and_maxlife_timeout()
    {
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("app_install_failed") }).HasAppInstallFailure);
        var timeout = Evt("enrollment_failed", data: new Dictionary<string, object> { ["failureType"] = "agent_timeout" });
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { timeout }).HasAgentMaxLifetimeTimeout);
        var shutdown = Evt("agent_shutting_down", data: new Dictionary<string, object> { ["reason"] = "max_lifetime" });
        Assert.True(EnrollmentTimeoutClassifier.ExtractRollup(new[] { shutdown }).HasAgentMaxLifetimeTimeout);
        // Other shutdown reasons say nothing about max lifetime.
        var ctrlC = Evt("agent_shutting_down", data: new Dictionary<string, object> { ["reason"] = "ctrl_c" });
        Assert.False(EnrollmentTimeoutClassifier.ExtractRollup(new[] { ctrlC }).HasAgentMaxLifetimeTimeout);
        // A real failure event does not raise the app flag.
        Assert.False(EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("enrollment_failed") }).HasAppInstallFailure);
    }

    [Fact]
    public void Classify_desktop_without_hello_past_grace_is_assumed_complete()
    {
        // The healthy half of the r5 population (drill-down 2026-08-27): desktop reached, apps
        // clean, agent died before the Hello terminal. Past the full grace "user phase still
        // running" is no longer a possible explanation — completed (assumed), not a red Incomplete.
        var (status, reason, rule) = ClassifyWithRule(new[]
        {
            Esp(DeviceSetup44), Esp(AccountSetup15), Evt("desktop_arrived"),
        }, hoursSinceStart: 80, grace: 72);
        Assert.Equal(SessionStatus.Succeeded, status);
        Assert.Equal(ClassifierRules.R5DesktopAssumed, rule);
        Assert.Contains("treating the enrollment as completed", reason);
        Assert.Contains("Agent last reported", reason); // silence-transparency clause
    }

    [Fact]
    public void Classify_desktop_with_app_failure_past_grace_stays_Incomplete()
    {
        // "Completed" must not overclaim a session whose ESP payload provably failed
        // (drill-down session b1edc6f8: desktop reached but .NET 3.5 install failed).
        var (status, _) = Classify(new[]
        {
            Esp(DeviceSetup44), Evt("desktop_arrived"), Evt("app_install_failed"),
        }, hoursSinceStart: 80, grace: 72);
        Assert.Equal(SessionStatus.Incomplete, status);
    }

    [Fact]
    public void Classify_maxlife_with_desktop_and_clean_apps_is_assumed_complete_within_grace()
    {
        // Agent provably gone + desktop + clean apps: no reason to park — the assumption is as
        // good now as it would be after the 51h grace.
        var timeout = Evt("enrollment_failed", data: new Dictionary<string, object> { ["failureType"] = "agent_timeout" });
        var (status, _, rule) = ClassifyWithRule(new[]
        {
            Esp(DeviceSetup44), Evt("desktop_arrived"), timeout,
        }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Succeeded, status);
        Assert.Equal(ClassifierRules.R5DesktopAssumed, rule);
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

    // -------- WhiteGlove Part-2 awaiting-user gate (fairstone.ca analysis 2026-08-21) --------

    private static readonly DateTime Resumed = Start.AddMinutes(20);

    private static (SessionStatus, string) ClassifyWhiteGlove(
        IReadOnlyList<EnrollmentEvent> events, double hoursSinceStart = 6, int grace = 51)
    {
        var rollup = EnrollmentTimeoutClassifier.ExtractRollup(events);
        var now = Start.AddHours(hoursSinceStart);
        var (status, reason, _) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
            rollup, Start, now, grace, isPreProvisioned: true, resumedAt: Resumed);
        return (status, reason);
    }

    /// <summary>The fairstone event shape: Part 1 sealed, Part 2 resumed, nobody signed in.</summary>
    private static EnrollmentEvent[] WgParkedEvents() => new[]
    {
        Esp(DeviceSetup44), Evt("whiteglove_complete"), Esp(AccountSetup05),
    };

    [Fact]
    public void ExtractRollup_separates_enrollment_complete_from_whiteglove_complete()
    {
        var wgOnly = EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("whiteglove_complete") });
        Assert.True(wgOnly.HasTerminalComplete);
        Assert.False(wgOnly.HasEnrollmentComplete);

        var real = EnrollmentTimeoutClassifier.ExtractRollup(new[] { Evt("enrollment_complete") });
        Assert.True(real.HasTerminalComplete);
        Assert.True(real.HasEnrollmentComplete);
    }

    [Fact]
    public void WhiteGloveGate_requires_preprovisioned_and_resume_and_device_setup()
    {
        var parked = EnrollmentTimeoutClassifier.ExtractRollup(WgParkedEvents());
        Assert.True(EnrollmentTimeoutClassifier.IsWhiteGloveAwaitingUser(parked, true, Resumed));
        Assert.False(EnrollmentTimeoutClassifier.IsWhiteGloveAwaitingUser(parked, false, Resumed));
        Assert.False(EnrollmentTimeoutClassifier.IsWhiteGloveAwaitingUser(parked, true, null));

        // Powered off before Device Setup ever provisioned → not the parking state.
        var early = EnrollmentTimeoutClassifier.ExtractRollup(new[]
        {
            Esp("ESP provisioning status: DeviceSetup — 3 of 4 subcategories completed"),
        });
        Assert.False(EnrollmentTimeoutClassifier.IsWhiteGloveAwaitingUser(early, true, Resumed));
    }

    [Fact]
    public void WhiteGloveGate_any_user_evidence_or_terminal_disarms_it()
    {
        foreach (var evidence in new[]
        {
            Esp(AccountSetup15),                    // Account Setup progressed → user was there
            Evt("desktop_arrived"),                 // real-user desktop
            Evt("hello_provisioning_completed"),    // positive Hello terminal
            Evt("enrollment_complete"),             // real completion
            Evt("enrollment_failed"),               // explicit failure
        })
        {
            var rollup = EnrollmentTimeoutClassifier.ExtractRollup(new[]
            {
                Esp(DeviceSetup44), Evt("whiteglove_complete"), Esp(AccountSetup05), evidence,
            });
            Assert.False(EnrollmentTimeoutClassifier.IsWhiteGloveAwaitingUser(rollup, true, Resumed));
        }
    }

    [Fact]
    public void Classify_whiteglove_part2_parked_within_grace_is_AwaitingUser()
    {
        // The fairstone shape: technician powers the device off at the logon screen minutes
        // after the reseal-reboot. Part-1 whiteglove_complete must NOT reconcile to Succeeded.
        var (status, reason) = ClassifyWhiteGlove(WgParkedEvents(), hoursSinceStart: 6);
        Assert.Equal(SessionStatus.AwaitingUser, status);
        Assert.Contains("sealed or powered off awaiting the end user", reason);
        Assert.Contains("(Account Setup 0/5)", reason);
    }

    [Fact]
    public void Classify_whiteglove_part2_parked_past_grace_reconciles_with_honest_reason()
    {
        var (status, reason) = ClassifyWhiteGlove(WgParkedEvents(), hoursSinceStart: 60, grace: 51);
        Assert.Equal(SessionStatus.Succeeded, status);
        Assert.Contains("pre-provisioning (WhiteGlove Part 1) completed", reason);
        Assert.DoesNotContain("Account Setup completed", reason);
    }

    [Fact]
    public void Classify_whiteglove_part2_with_partial_user_progress_is_not_reconciled_on_part1_evidence()
    {
        // User signed in and Account Setup progressed to 1/5, then silence. The Part-1
        // whiteglove_complete previously reconciled this to Succeeded claiming "Account Setup
        // completed" — on a resumed session only a real enrollment_complete proves the outcome,
        // so this honest shape falls through to the AwaitingUser/Incomplete rules.
        var events = new[]
        {
            Esp(DeviceSetup44), Evt("whiteglove_complete"), Esp(AccountSetup15),
        };
        var (withinGrace, _) = ClassifyWhiteGlove(events, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.AwaitingUser, withinGrace);

        var (pastGrace, reason) = ClassifyWhiteGlove(events, hoursSinceStart: 60, grace: 51);
        Assert.Equal(SessionStatus.Incomplete, pastGrace);
        Assert.Contains("1/5", reason);
    }

    [Fact]
    public void Classify_whiteglove_part2_with_desktop_and_hello_still_reconciles_via_user_evidence()
    {
        // Both Classic completion prerequisites in → the user provably finished; the
        // user-completed reconcile (rule 4) owns this, not the parking gate.
        var (status, reason) = ClassifyWhiteGlove(new[]
        {
            Esp(DeviceSetup44), Evt("whiteglove_complete"), Esp(AccountSetup15),
            Evt("desktop_arrived"), Evt("hello_provisioning_completed"),
        }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Succeeded, status);
        Assert.Contains("user completed setup", reason);
    }

    [Fact]
    public void Classify_whiteglove_part2_real_enrollment_complete_still_reconciles_to_Succeeded()
    {
        var (status, _) = ClassifyWhiteGlove(new[]
        {
            Esp(DeviceSetup44), Evt("whiteglove_complete"), Esp(AccountSetup05), Evt("enrollment_complete"),
        }, hoursSinceStart: 6);
        Assert.Equal(SessionStatus.Succeeded, status);
    }

    [Fact]
    public void Classify_unresumed_session_keeps_terminal_complete_reconcile()
    {
        // No resumedAt (plain sessions, or a sealed Part 1 whose Pending write was lost):
        // whiteglove_complete keeps reconciling to Succeeded exactly as before.
        var rollup = EnrollmentTimeoutClassifier.ExtractRollup(new[]
        {
            Esp(DeviceSetup44), Evt("whiteglove_complete"), Esp(AccountSetup05),
        });
        var (status, _, _) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
            rollup, Start, Start.AddHours(6), 51, isPreProvisioned: true, resumedAt: null);
        Assert.Equal(SessionStatus.Succeeded, status);
    }

    // -------- Self-deploying profile gate (kiosk tenant aebdce78, audit 2026-08-23) --------

    private static (SessionStatus, string) ClassifySelfDeploying(
        IReadOnlyList<EnrollmentEvent> events, double hoursSinceStart = 3, int grace = 51)
    {
        var rollup = EnrollmentTimeoutClassifier.ExtractRollup(events);
        var (status, reason, _) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
            rollup, Start, Start.AddHours(hoursSinceStart), grace,
            lastEventAtUtc: Start.AddMinutes(4), isSelfDeployingProfile: true);
        return (status, reason);
    }

    // Session 195593e2 replay: DeviceSetup 4/4 (fallback-confirmed), AccountSetup registry
    // at 0/5 (the IME false positive — user ESP never runs on this profile), Hello disabled,
    // SkipUser=True, then silence after the post-ESP reboot. No user will ever sign in.
    private static readonly EnrollmentEvent[] KioskSilentStream =
    {
        Esp(DeviceSetup44), Esp(AccountSetup05), Esp(DeviceSetupFallback),
        HelloPolicy(enabled: false), EspConfig(skipUser: true),
    };

    [Fact]
    public void IsSelfDeployingProvisioned_requires_flag_device_setup_and_no_failure()
    {
        var provisioned = EnrollmentTimeoutClassifier.ExtractRollup(KioskSilentStream);
        Assert.True(EnrollmentTimeoutClassifier.IsSelfDeployingProvisioned(provisioned, isSelfDeployingProfile: true));
        Assert.False(EnrollmentTimeoutClassifier.IsSelfDeployingProvisioned(provisioned, isSelfDeployingProfile: false));

        var beforeDeviceSetup = EnrollmentTimeoutClassifier.ExtractRollup(new[] { Esp(AccountSetup05) });
        Assert.False(EnrollmentTimeoutClassifier.IsSelfDeployingProvisioned(beforeDeviceSetup, isSelfDeployingProfile: true));

        var failed = EnrollmentTimeoutClassifier.ExtractRollup(new[] { Esp(DeviceSetup44), Evt("enrollment_failed") });
        Assert.False(EnrollmentTimeoutClassifier.IsSelfDeployingProvisioned(failed, isSelfDeployingProfile: true));
    }

    [Fact]
    public void Classify_self_deploying_silent_after_device_setup_reconciles_to_Succeeded_within_grace()
    {
        var (status, reason) = ClassifySelfDeploying(KioskSilentStream, hoursSinceStart: 3);
        Assert.Equal(SessionStatus.Succeeded, status);
        Assert.Contains("self-deploying profile", reason);
        Assert.Contains("no user / Account Setup phase", reason);
        Assert.Contains("Agent last reported 2026-07-06 15:04 UTC", reason);
        Assert.DoesNotContain("Account Setup completed", reason);
    }

    [Fact]
    public void Classify_self_deploying_silent_after_device_setup_reconciles_to_Succeeded_past_grace()
    {
        // Grace is a user-phase concept; it does not apply when there is no user phase.
        var (status, _) = ClassifySelfDeploying(KioskSilentStream, hoursSinceStart: 60, grace: 51);
        Assert.Equal(SessionStatus.Succeeded, status);
    }

    [Fact]
    public void Classify_same_stream_without_self_deploying_flag_keeps_user_phase_verdicts()
    {
        // Guard against the gate leaking into user-driven sessions: the identical event shape
        // on a user-driven profile is still "awaiting user" within grace / Incomplete past it.
        var rollup = EnrollmentTimeoutClassifier.ExtractRollup(KioskSilentStream);
        var (within, _, _) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(rollup, Start, Start.AddHours(3), 51);
        var (past, _, _) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(rollup, Start, Start.AddHours(60), 51);
        Assert.Equal(SessionStatus.AwaitingUser, within);
        Assert.Equal(SessionStatus.Incomplete, past);
    }

    [Fact]
    public void Classify_self_deploying_explicit_failure_stays_Failed()
    {
        var (status, _) = ClassifySelfDeploying(new[] { Esp(DeviceSetup44), Evt("enrollment_failed") });
        Assert.Equal(SessionStatus.Failed, status);
    }

    [Fact]
    public void Classify_self_deploying_silent_before_device_setup_is_Incomplete()
    {
        // The profile flag alone proves nothing — Device ESP must have finished.
        var (status, _) = ClassifySelfDeploying(new[] { Esp(AccountSetup05), Evt("agent_started") });
        Assert.Equal(SessionStatus.Incomplete, status);
    }
}
