using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Verdict calibration instrumentation (docs/backend/verdict-calibration.md): every status write
/// declares its origin, overriding a prior verdict preserves it, the classifier names the rule
/// behind each verdict, and pre-instrumentation rows derive an honest path from their literals.
/// </summary>
public class VerdictPathTests
{
    private static readonly DateTime Start = new(2026, 8, 20, 8, 0, 0, DateTimeKind.Utc);

    // ---- ComputePriorVerdict: the correction stream ----

    [Theory]
    [InlineData("Succeeded", "agent:complete", "Failed", true)]        // admin mark over an agent success
    [InlineData("Incomplete", "sweep:r5_incomplete", "Succeeded", true)] // late agent completion upgrades a sweep verdict
    [InlineData("AwaitingUser", "sweep:r5_awaiting", "Incomplete", true)] // grace expired
    [InlineData("Failed", "agent:failed", "Incomplete", true)]        // retro reclassification
    [InlineData("Stalled", "sweep:stalled", "InProgress", false)]      // stall heal is not a correction
    [InlineData("Stalled", "sweep:stalled", "Succeeded", false)]
    [InlineData("InProgress", "register:new", "Succeeded", false)]
    [InlineData("Pending", "agent:whiteglove_pending", "InProgress", false)]
    [InlineData("AwaitingUser", "sweep:wg_awaiting", "AwaitingUser", false)] // same-status refresh keeps the original prior
    public void Prior_is_preserved_only_when_a_verdict_is_overridden(string existing, string existingPath, string incoming, bool expectPrior)
    {
        var prior = TableStorageService.ComputePriorVerdict(existing, existingPath, Enum.Parse<SessionStatus>(incoming));
        if (expectPrior)
        {
            Assert.NotNull(prior);
            Assert.Equal(existing, prior!.Value.PriorStatus);
            Assert.Equal(existingPath, prior.Value.PriorVerdictPath);
        }
        else
        {
            Assert.Null(prior);
        }
    }

    [Fact]
    public void Prior_needs_a_stamped_path_on_the_existing_row()
    {
        // Pre-instrumentation rows carry no path — there is nothing trustworthy to preserve.
        Assert.Null(TableStorageService.ComputePriorVerdict("Incomplete", null, SessionStatus.Succeeded));
        Assert.Null(TableStorageService.ComputePriorVerdict("Incomplete", "", SessionStatus.Succeeded));
        Assert.Null(TableStorageService.ComputePriorVerdict(null, "sweep:r6", SessionStatus.Succeeded));
    }

    // ---- VerdictPaths vocabulary ----

    [Fact]
    public void Compose_and_Origin_round_trip()
    {
        var path = VerdictPaths.Compose(VerdictPaths.OriginSweep, ClassifierRules.R5DeviceSetupIncomplete);
        Assert.Equal("sweep:r5_incomplete", path);
        Assert.Equal("sweep", VerdictPaths.Origin(path));
        Assert.True(VerdictPaths.IsClassifierPath(path));
        Assert.True(VerdictPaths.IsClassifierPath("sweep:r5_assumed"));
        Assert.True(VerdictPaths.IsClassifierPath("maxlife:r1"));
        Assert.True(VerdictPaths.IsClassifierPath("late:r4"));
        Assert.True(VerdictPaths.IsClassifierPath("retro:r6"));
        Assert.False(VerdictPaths.IsClassifierPath(VerdictPaths.AgentComplete));
        Assert.False(VerdictPaths.IsClassifierPath(VerdictPaths.SweepStalled));
        Assert.False(VerdictPaths.IsClassifierPath(VerdictPaths.RetroSuperseded));
        Assert.False(VerdictPaths.IsClassifierPath("legacy:r6"));
        Assert.False(VerdictPaths.IsClassifierPath(VerdictPaths.ManualFailed));
        Assert.Throws<ArgumentException>(() => VerdictPaths.Compose("", "r1"));
        Assert.Throws<ArgumentException>(() => VerdictPaths.Compose("sweep", " "));
    }

    // ---- Classifier: every return names its rule ----

    private static EnrollmentEvent Evt(string type, string? message = null) => new()
    {
        EventType = type, Timestamp = Start, Source = "test", Message = message ?? type,
    };

    private static EnrollmentEvent Esp(string message) => Evt("esp_provisioning_status", message);
    private const string DeviceSetup44 = "ESP provisioning status: DeviceSetup — 4 of 4 subcategories completed";
    private const string AccountSetup55 = "ESP provisioning status: AccountSetup — 5 of 5 subcategories completed";
    private const string AccountSetup15 = "ESP provisioning status: AccountSetup — 1 of 5 subcategories completed";

    private static string RuleOf(IReadOnlyList<EnrollmentEvent> events, double hours = 6, int grace = 72,
        bool preProvisioned = false, DateTime? resumedAt = null, bool selfDeploying = false)
    {
        var rollup = EnrollmentTimeoutClassifier.ExtractRollup(events);
        var (_, _, rule) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
            rollup, Start, Start.AddHours(hours), grace, isPreProvisioned: preProvisioned,
            resumedAt: resumedAt, isSelfDeployingProfile: selfDeploying);
        return rule;
    }

    [Fact]
    public void Classifier_returns_the_rule_id_behind_each_verdict()
    {
        Assert.Equal(ClassifierRules.R1ExplicitFailure, RuleOf(new[] { Evt("enrollment_failed") }));
        Assert.Equal(ClassifierRules.R1bWhiteGloveAwaiting, RuleOf(
            new[] { Esp(DeviceSetup44), Evt("whiteglove_complete") }, hours: 6, grace: 51, preProvisioned: true, resumedAt: Start.AddHours(1)));
        Assert.Equal(ClassifierRules.R1bWhiteGloveSucceeded, RuleOf(
            new[] { Esp(DeviceSetup44), Evt("whiteglove_complete") }, hours: 60, grace: 51, preProvisioned: true, resumedAt: Start.AddHours(1)));
        Assert.Equal(ClassifierRules.R1cSelfDeploying, RuleOf(new[] { Esp(DeviceSetup44) }, selfDeploying: true));
        Assert.Equal(ClassifierRules.R2AccountSetupComplete, RuleOf(new[] { Esp(DeviceSetup44), Esp(AccountSetup55) }));
        Assert.Equal(ClassifierRules.R3EmergencyBreak, RuleOf(new[] { Esp(DeviceSetup44), Evt("agent_emergency_break") }));
        Assert.Equal(ClassifierRules.R4DesktopHello, RuleOf(new[] { Esp(DeviceSetup44), Evt("desktop_arrived"), Evt("hello_provisioning_completed") }));
        Assert.Equal(ClassifierRules.R5DeviceSetupAwaiting, RuleOf(new[] { Esp(DeviceSetup44), Esp(AccountSetup15) }, hours: 6, grace: 72));
        Assert.Equal(ClassifierRules.R5DesktopAssumed, RuleOf(new[] { Esp(DeviceSetup44), Esp(AccountSetup15), Evt("desktop_arrived") }, hours: 80, grace: 72));
        Assert.Equal(ClassifierRules.R5DeviceSetupIncomplete, RuleOf(new[] { Esp(DeviceSetup44), Esp(AccountSetup15) }, hours: 80, grace: 72));
        Assert.Equal(ClassifierRules.R6Fallthrough, RuleOf(Array.Empty<EnrollmentEvent>()));
    }

    // ---- Derivation for pre-instrumentation rows ----

    private static SessionSummary Row(SessionStatus status, string? failureReason = null, string? reconcileReason = null,
        string? failureSource = null, string? adminMarked = null, bool soft = false, string? verdictPath = null) => new()
    {
        SessionId = "s", TenantId = "t", Status = status,
        FailureReason = failureReason ?? string.Empty, ReconcileReason = reconcileReason ?? string.Empty,
        FailureSource = failureSource ?? string.Empty, AdminMarkedAction = adminMarked, EspSoftFailure = soft,
        VerdictPath = verdictPath,
    };

    private static string Classified(IReadOnlyList<EnrollmentEvent> events, double hours = 80, int grace = 72, bool selfDeploying = false)
    {
        var rollup = EnrollmentTimeoutClassifier.ExtractRollup(events);
        var (_, reason, _) = EnrollmentTimeoutClassifier.ClassifyTimedOutSession(
            rollup, Start, Start.AddHours(hours), grace, isSelfDeployingProfile: selfDeploying);
        return reason;
    }

    [Fact]
    public void Stamped_rows_are_returned_verbatim()
    {
        var (path, derived) = VerdictPathDerivation.Derive(Row(SessionStatus.Incomplete, failureReason: "anything", verdictPath: "sweep:r6"));
        Assert.Equal("sweep:r6", path);
        Assert.False(derived);
    }

    [Fact]
    public void Derivation_uses_the_real_classifier_literals()
    {
        // Reasons come straight from the classifier so a wording change there surfaces here.
        Assert.Equal(("legacy:r6", true), VerdictPathDerivation.Derive(Row(SessionStatus.Incomplete,
            failureReason: Classified(Array.Empty<EnrollmentEvent>()))));
        Assert.Equal(("legacy:r5_incomplete", true), VerdictPathDerivation.Derive(Row(SessionStatus.Incomplete,
            failureReason: Classified(new[] { Esp(DeviceSetup44), Esp(AccountSetup15) }))));
        Assert.Equal(("legacy:r5_awaiting", true), VerdictPathDerivation.Derive(Row(SessionStatus.AwaitingUser,
            failureReason: Classified(new[] { Esp(DeviceSetup44), Esp(AccountSetup15) }, hours: 6))));
        Assert.Equal(("legacy:r3", true), VerdictPathDerivation.Derive(Row(SessionStatus.Incomplete,
            failureReason: Classified(new[] { Esp(DeviceSetup44), Evt("agent_emergency_break") }))));
        Assert.Equal(("legacy:r2", true), VerdictPathDerivation.Derive(Row(SessionStatus.Succeeded,
            reconcileReason: Classified(new[] { Esp(DeviceSetup44), Esp(AccountSetup55) }))));
        Assert.Equal(("legacy:r4", true), VerdictPathDerivation.Derive(Row(SessionStatus.Succeeded,
            reconcileReason: Classified(new[] { Esp(DeviceSetup44), Evt("desktop_arrived"), Evt("hello_provisioning_completed") }))));
        Assert.Equal(("legacy:r1c", true), VerdictPathDerivation.Derive(Row(SessionStatus.Succeeded,
            reconcileReason: Classified(new[] { Esp(DeviceSetup44) }, selfDeploying: true))));
        Assert.Equal(("legacy:r1", true), VerdictPathDerivation.Derive(Row(SessionStatus.Failed,
            failureReason: Classified(new[] { Evt("enrollment_failed") }))));
    }

    [Fact]
    public void Derivation_recovers_the_origin_from_writer_suffixes()
    {
        var r6 = Classified(Array.Empty<EnrollmentEvent>());
        Assert.Equal(("maxlife:r6", true), VerdictPathDerivation.Derive(Row(SessionStatus.Incomplete,
            failureReason: r6 + " Verdict triggered by the agent's max-lifetime watchdog shutdown.")));
        Assert.Equal(("retro:r6", true), VerdictPathDerivation.Derive(Row(SessionStatus.Incomplete,
            failureReason: r6 + " Retro-reclassified from the legacy blanket timeout verdict.")));
    }

    [Fact]
    public void Derivation_attributes_unambiguous_non_classifier_paths()
    {
        Assert.Equal((VerdictPaths.ManualFailed, true), VerdictPathDerivation.Derive(Row(SessionStatus.Failed,
            failureReason: "Manually marked as failed by administrator", failureSource: "manual", adminMarked: "Failed")));
        Assert.Equal((VerdictPaths.ManualSucceeded, true), VerdictPathDerivation.Derive(Row(SessionStatus.Succeeded, adminMarked: "Succeeded")));
        Assert.Equal(("rule:ANALYZE-ESP-001", true), VerdictPathDerivation.Derive(Row(SessionStatus.Failed,
            failureReason: "Rule: ESP timeout", failureSource: "rule:ANALYZE-ESP-001")));
        Assert.Equal((VerdictPaths.AgentComplete, true), VerdictPathDerivation.Derive(Row(SessionStatus.Succeeded)));
        Assert.Equal((VerdictPaths.AgentComplete, true), VerdictPathDerivation.Derive(Row(SessionStatus.Succeeded,
            reconcileReason: "Late completion report received — upgraded prior 'Incomplete' verdict")));
        Assert.Equal((VerdictPaths.AgentCompleteSoft, true), VerdictPathDerivation.Derive(Row(SessionStatus.Succeeded, soft: true)));
        Assert.Equal((VerdictPaths.AgentFailed, true), VerdictPathDerivation.Derive(Row(SessionStatus.Failed, failureReason: "ESP timeout (0x800705B4)")));
        Assert.Equal((VerdictPaths.AgentEspFailureFallback, true), VerdictPathDerivation.Derive(Row(SessionStatus.Failed, failureReason: "ESP failure (backend fallback)")));
        Assert.Equal((VerdictPaths.SweepStalled, true), VerdictPathDerivation.Derive(Row(SessionStatus.Stalled, failureReason: "Agent silent for 130min (detected by maintenance sweep)")));
        Assert.Equal((VerdictPaths.AgentStallProbe, true), VerdictPathDerivation.Derive(Row(SessionStatus.Stalled, failureReason: "Agent reported stall after 60min without progress (stall_probe)")));
        Assert.Equal(("legacy:superseded", true), VerdictPathDerivation.Derive(Row(SessionStatus.Incomplete, failureReason: "Superseded by session abc: re-registered")));
        Assert.Equal((VerdictPaths.AgentWhiteGlovePending, true), VerdictPathDerivation.Derive(Row(SessionStatus.Pending)));
    }

    [Fact]
    public void Derivation_never_invents_a_path()
    {
        Assert.Equal((VerdictPaths.LegacyUnknown, true), VerdictPathDerivation.Derive(Row(SessionStatus.Failed, failureReason: "Session timed out after 5 hours (started at 2026-01-01 UTC)")));
        Assert.Equal((VerdictPaths.LegacyUnknown, true), VerdictPathDerivation.Derive(Row(SessionStatus.Incomplete)));
        Assert.Equal((VerdictPaths.LegacyUnknown, true), VerdictPathDerivation.Derive(Row(SessionStatus.InProgress)));
        Assert.Equal((VerdictPaths.LegacyUnknown, true), VerdictPathDerivation.Derive(Row(SessionStatus.Succeeded, reconcileReason: "Some future wording")));
    }
}
