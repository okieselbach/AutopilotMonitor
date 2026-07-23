using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Xunit;

namespace AutopilotMonitor.DecisionCore.Tests
{
    /// <summary>
    /// Session 4910a5a5 (2026-07-23) — advisory recovery. Field failure: DeviceSetup/Apps
    /// failed (culprit app never started installing), the advisory defang armed the
    /// <c>advisory_completion</c> window, the user pressed "Try again" ~23 min later, the apps
    /// re-ran to 10/10 and <c>DeviceSetupProvisioningComplete</c> arrived — yet the window
    /// fired 30 min after the advisory and un-defanged the stale failure
    /// (<c>enrollment_failed: esp_terminal_failure</c>) on a live enrollment mid-AccountSetup,
    /// because the advisory variant was exempt from ALL re-arm/rebase guards. Coverage:
    /// <list type="bullet">
    ///   <item>The advisory records the failed category + culprit apps.</item>
    ///   <item>A ProvisioningComplete of the SAME category sets the resolved fact + emits
    ///         <c>esp_failure_advisory_resolved</c>; a different category does not.</item>
    ///   <item>Deadline fire after recovery + enforcement progress → re-arm, not Failed.</item>
    ///   <item>Deadline fire after recovery without progress → Failed with the truthful
    ///         <c>esp_recovered_without_completion_evidence</c> reason and
    ///         LastFailureTrigger=DeadlineFired (likely-stuck promotion off).</item>
    ///   <item>Unrecovered advisory keeps the legacy un-defang exactly (regression guard).</item>
    ///   <item>Reboot re-bases a recovered-advisory window (and still does NOT touch an
    ///         unrecovered one).</item>
    ///   <item>Snapshot round-trip + census surfacing of the new facts.</item>
    /// </list>
    /// </summary>
    public sealed class AdvisoryRecoveryTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 7, 23, 3, 30, 0, DateTimeKind.Utc);

        private static DecisionSignal MakeSignal(
            long ordinal,
            DecisionSignalKind kind,
            DateTime occurredAtUtc,
            IReadOnlyDictionary<string, string>? payload = null)
        {
            return new DecisionSignal(
                sessionSignalOrdinal: ordinal,
                sessionTraceOrdinal: ordinal,
                kind: kind,
                kindSchemaVersion: 1,
                occurredAtUtc: occurredAtUtc,
                sourceOrigin: "test",
                evidence: new Evidence(EvidenceKind.Synthetic, $"{kind}-{ordinal}", "test"),
                payload: payload);
        }

        private static DecisionSignal DeadlineFired(long ordinal, DateTime occurredAtUtc) =>
            MakeSignal(ordinal, DecisionSignalKind.DeadlineFired, occurredAtUtc,
                new Dictionary<string, string> { [SignalPayloadKeys.Deadline] = DeadlineNames.AdvisoryCompletion });

        private static DecisionEffect? FindTimelineEffect(DecisionStep step, string eventType) =>
            step.Effects.FirstOrDefault(e =>
                e.Kind == DecisionEffectKind.EmitEventTimelineEntry
                && e.Parameters != null
                && e.Parameters.TryGetValue("eventType", out var et)
                && et == eventType);

        private static ActiveDeadline? FindDeadline(DecisionState state, string name) =>
            state.Deadlines.FirstOrDefault(d => d.Name == name);

        /// <summary>
        /// Replays the session-4910a5a5 shape up to (but excluding) the ESP terminal failure:
        /// Classic UserDriven, ContinueAnyway profile, Hello disabled, DeviceSetup entered,
        /// AccountSetup entered (04:18, BEFORE the 04:34 DeviceSetup/Apps failure — the IME
        /// phase line landed while device apps were still at 9/10).
        /// </summary>
        private static DecisionState SetupSession(DecisionEngine engine)
        {
            var state = DecisionState.CreateInitial("sess-4910a5a5", "tenant-e46bc88e", T0);
            state = engine.Reduce(state, MakeSignal(0, DecisionSignalKind.SessionStarted, T0)).NewState;
            state = engine.Reduce(state, MakeSignal(
                5, DecisionSignalKind.EspConfigDetected, T0.AddMinutes(1),
                new Dictionary<string, string>
                {
                    [SignalPayloadKeys.SkipUserEsp] = "false",
                    [SignalPayloadKeys.SkipDeviceEsp] = "false",
                    [SignalPayloadKeys.EspSyncFailureTimeoutMinutes] = "60",
                    [SignalPayloadKeys.EspAllowContinueAnyway] = "true",
                })).NewState;
            state = engine.Reduce(state, MakeSignal(
                8, DecisionSignalKind.HelloPolicyDetected, T0.AddMinutes(2),
                new Dictionary<string, string> { [SignalPayloadKeys.HelloEnabled] = "false" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                10, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(6),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "DeviceSetup" })).NewState;
            state = engine.Reduce(state, MakeSignal(
                20, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(48),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;
            return state;
        }

        private static DecisionStep ApplyDeviceSetupAppsFailure(DecisionEngine engine, DecisionState state, long ordinal = 50) =>
            engine.Reduce(state, MakeSignal(
                ordinal, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(64),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_DeviceSetup_Apps_Failed",
                    ["failedSubcategory"] = "Apps",
                    ["category"] = "DeviceSetup",
                    ["likelyCulpritApps"] = "Oriflame Cosmectics - Teams Backgroundsv v2",
                    ["likelyCulpritAppCount"] = "1",
                }));

        private static DecisionStep ApplyDeviceSetupResolved(DecisionEngine engine, DecisionState state, long ordinal = 60) =>
            engine.Reduce(state, MakeSignal(
                ordinal, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(87)));

        // ================================================== advisory arming context ====

        [Fact]
        public void Advisory_RecordsFailedCategory_AndCulpritApps()
        {
            var engine = new DecisionEngine();
            var state = SetupSession(engine);

            var step = ApplyDeviceSetupAppsFailure(engine, state);

            Assert.NotNull(step.NewState.EspAdvisoryFailureRecordedUtc);
            Assert.Equal("DeviceSetup", step.NewState.EspAdvisoryFailureCategory?.Value);
            Assert.Null(step.NewState.EspAdvisoryFailureResolvedUtc);

            var advisory = FindTimelineEffect(step, "esp_failure_advisory");
            Assert.NotNull(advisory);
            Assert.Equal("Oriflame Cosmectics - Teams Backgroundsv v2", advisory!.Parameters!["likelyCulpritApps"]);
        }

        // ===================================================== recovery hook ====

        [Fact]
        public void DeviceSetupComplete_SameCategory_ResolvesAdvisory_AndEmitsStoryEvent()
        {
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            state = ApplyDeviceSetupAppsFailure(engine, state).NewState;

            var step = ApplyDeviceSetupResolved(engine, state);

            Assert.NotNull(step.NewState.EspAdvisoryFailureResolvedUtc);
            Assert.Equal(60, step.NewState.EspAdvisoryFailureResolvedUtc!.SourceSignalOrdinal);
            // The anchor itself stays — the advisory happened; it just no longer blocks.
            Assert.NotNull(step.NewState.EspAdvisoryFailureRecordedUtc);

            var resolved = FindTimelineEffect(step, "esp_failure_advisory_resolved");
            Assert.NotNull(resolved);
            Assert.Equal("DeviceSetup", resolved!.Parameters!["category"]);
            Assert.Equal("23", resolved.Parameters["minutesSinceAdvisory"]);
        }

        [Fact]
        public void DeviceSetupComplete_DifferentCategory_DoesNotResolve()
        {
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            // Advisory for AccountSetup — DeviceSetup resolving must not defuse it.
            state = engine.Reduce(state, MakeSignal(
                50, DecisionSignalKind.EspTerminalFailure, T0.AddMinutes(64),
                new Dictionary<string, string>
                {
                    ["failureType"] = "Provisioning_AccountSetup_Apps_Failed",
                    ["failedSubcategory"] = "Apps",
                    ["category"] = "AccountSetup",
                })).NewState;

            var step = ApplyDeviceSetupResolved(engine, state);

            Assert.Null(step.NewState.EspAdvisoryFailureResolvedUtc);
            Assert.Null(FindTimelineEffect(step, "esp_failure_advisory_resolved"));
        }

        [Fact]
        public void DeviceSetupComplete_Duplicate_ResolvesOnlyOnce()
        {
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            state = ApplyDeviceSetupAppsFailure(engine, state).NewState;
            state = ApplyDeviceSetupResolved(engine, state).NewState;

            // Post-reboot duplicate (AnchorAlreadySet passthrough branch).
            var duplicate = engine.Reduce(state, MakeSignal(
                65, DecisionSignalKind.DeviceSetupProvisioningComplete, T0.AddMinutes(92)));

            Assert.Equal(60, duplicate.NewState.EspAdvisoryFailureResolvedUtc!.SourceSignalOrdinal);
            Assert.Null(FindTimelineEffect(duplicate, "esp_failure_advisory_resolved"));
        }

        // ================================================ deadline fire semantics ====

        [Fact]
        public void DeadlineFire_AfterRecovery_WithEnforcementProgress_ReArmsInsteadOfFailing()
        {
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            state = ApplyDeviceSetupAppsFailure(engine, state).NewState;
            state = ApplyDeviceSetupResolved(engine, state).NewState;
            // Post-reboot IME re-assert of AccountSetup (session shape: 05:02:59, ordinal 654)
            // — user-phase enforcement demonstrably progressed since the arming at ordinal 50.
            state = engine.Reduce(state, MakeSignal(
                70, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(92),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            var step = engine.Reduce(state, DeadlineFired(80, T0.AddMinutes(94)));

            // Not failed — the window re-arms with fresh baselines instead.
            Assert.Null(step.NewState.Outcome);
            Assert.NotEqual(SessionStage.Failed, step.NewState.Stage);
            var rearmed = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(rearmed);
            Assert.Equal(T0.AddMinutes(94).AddMinutes(30), rearmed!.DueAtUtc);
            Assert.Contains(step.Effects, e => e.Kind == DecisionEffectKind.ScheduleDeadline);
        }

        [Fact]
        public void DeadlineFire_AfterRecovery_NoProgress_FailsWithRecoveredReason()
        {
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            state = ApplyDeviceSetupAppsFailure(engine, state).NewState;
            state = ApplyDeviceSetupResolved(engine, state).NewState;

            var step = engine.Reduce(state, DeadlineFired(80, T0.AddMinutes(94)));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            Assert.Equal(SessionOutcome.EnrollmentFailed, step.NewState.Outcome);

            var failed = FindTimelineEffect(step, "enrollment_failed");
            Assert.NotNull(failed);
            // Truthful reason: the original failure recovered — do not blame it.
            Assert.Equal("esp_recovered_without_completion_evidence", failed!.Parameters!["reason"]);
            Assert.Equal("true", failed.Parameters["failureRecovered"]);
            Assert.Equal("advisory_recovered_window_expired_without_completion_evidence", failed.Parameters["advisoryReason"]);
            // Likely-stuck app promotion stays off: apps are not stuck, they succeeded.
            Assert.Equal(nameof(DecisionSignalKind.DeadlineFired), step.NewState.LastFailureTrigger?.Value);
        }

        [Fact]
        public void DeadlineFire_WithoutRecovery_KeepsLegacyUndefang()
        {
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            state = ApplyDeviceSetupAppsFailure(engine, state).NewState;
            // No recovery — even with enforcement progress the advisory variant must
            // un-defang exactly as before (its anchor failure never un-happened).
            state = engine.Reduce(state, MakeSignal(
                70, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(92),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            var step = engine.Reduce(state, DeadlineFired(80, T0.AddMinutes(94)));

            Assert.Equal(SessionStage.Failed, step.NewState.Stage);
            var failed = FindTimelineEffect(step, "enrollment_failed");
            Assert.NotNull(failed);
            Assert.Equal("esp_terminal_failure", failed!.Parameters!["reason"]);
            Assert.Equal(nameof(DecisionSignalKind.EspTerminalFailure), step.NewState.LastFailureTrigger?.Value);
        }

        // ======================================================= reboot rebase ====

        [Fact]
        public void Reboot_AfterRecovery_RebasesAdvisoryWindow()
        {
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            state = ApplyDeviceSetupAppsFailure(engine, state).NewState;
            state = ApplyDeviceSetupResolved(engine, state).NewState;

            var step = engine.Reduce(state, MakeSignal(
                70, DecisionSignalKind.SystemRebootObserved, T0.AddMinutes(92)));

            Assert.Contains(step.Effects, e =>
                e.Kind == DecisionEffectKind.CancelDeadline
                && e.CancelDeadlineName == DeadlineNames.AdvisoryCompletion);
            var rebased = FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion);
            Assert.NotNull(rebased);
            Assert.Equal(T0.AddMinutes(92).AddMinutes(30), rebased!.DueAtUtc);
        }

        [Fact]
        public void Reboot_WithoutRecovery_DoesNotTouchAdvisoryWindow()
        {
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            state = ApplyDeviceSetupAppsFailure(engine, state).NewState;
            var originalDue = FindDeadline(state, DeadlineNames.AdvisoryCompletion)!.DueAtUtc;

            var step = engine.Reduce(state, MakeSignal(
                70, DecisionSignalKind.SystemRebootObserved, T0.AddMinutes(92)));

            Assert.DoesNotContain(step.Effects, e => e.Kind == DecisionEffectKind.CancelDeadline);
            Assert.Equal(originalDue, FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion)!.DueAtUtc);
        }

        // ============================================== full session-4910a5a5 replay ====

        [Fact]
        public void Session4910a5a5_FailureRecoveryRebootFire_DoesNotFailTheLiveEnrollment()
        {
            // Field ordinals: 527 EspTerminalFailure → 611 DeviceSetupProvisioningComplete
            // (post "Try again", apps 10/10) → 635 SystemRebootObserved → 654 AccountSetup
            // re-assert → 671 DeadlineFired. Pre-fix outcome: enrollment_failed at the fire.
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            state = ApplyDeviceSetupAppsFailure(engine, state, ordinal: 527).NewState;
            state = ApplyDeviceSetupResolved(engine, state, ordinal: 611).NewState;
            state = engine.Reduce(state, MakeSignal(
                635, DecisionSignalKind.SystemRebootObserved, T0.AddMinutes(92))).NewState;
            state = engine.Reduce(state, MakeSignal(
                654, DecisionSignalKind.EspPhaseChanged, T0.AddMinutes(93),
                new Dictionary<string, string> { [SignalPayloadKeys.EspPhase] = "AccountSetup" })).NewState;

            var step = engine.Reduce(state, DeadlineFired(671, T0.AddMinutes(94)));

            Assert.Null(step.NewState.Outcome);
            Assert.NotEqual(SessionStage.Failed, step.NewState.Stage);
            Assert.NotNull(FindDeadline(step.NewState, DeadlineNames.AdvisoryCompletion));
        }

        // =============================================== serialization + census ====

        [Fact]
        public void SnapshotRoundTrip_PreservesRecoveryFacts()
        {
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            state = ApplyDeviceSetupAppsFailure(engine, state).NewState;
            state = ApplyDeviceSetupResolved(engine, state).NewState;

            var roundtripped = StateSerializer.Deserialize(StateSerializer.Serialize(state));

            Assert.Equal("DeviceSetup", roundtripped.EspAdvisoryFailureCategory?.Value);
            Assert.Equal(state.EspAdvisoryFailureResolvedUtc!.Value, roundtripped.EspAdvisoryFailureResolvedUtc?.Value);
            Assert.Equal(state.EspAdvisoryFailureResolvedUtc.SourceSignalOrdinal, roundtripped.EspAdvisoryFailureResolvedUtc?.SourceSignalOrdinal);
        }

        [Fact]
        public void LegacySnapshot_WithoutRecoveryFacts_DeserializesToNull()
        {
            // Simulate a snapshot written by a pre-fix agent: strip the new properties from
            // the JSON entirely — deserialization must default them to null (additive compat).
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            state = ApplyDeviceSetupAppsFailure(engine, state).NewState;
            state = ApplyDeviceSetupResolved(engine, state).NewState;

            var legacy = Newtonsoft.Json.Linq.JObject.Parse(StateSerializer.Serialize(state));
            foreach (var prop in legacy.Properties()
                .Where(p => p.Name.IndexOf("EspAdvisoryFailureResolved", StringComparison.OrdinalIgnoreCase) >= 0
                         || p.Name.IndexOf("EspAdvisoryFailureCategory", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList())
            {
                prop.Remove();
            }

            var roundtripped = StateSerializer.Deserialize(legacy.ToString());
            Assert.Null(roundtripped.EspAdvisoryFailureResolvedUtc);
            Assert.Null(roundtripped.EspAdvisoryFailureCategory);
            // The pre-existing advisory anchor still round-trips.
            Assert.NotNull(roundtripped.EspAdvisoryFailureRecordedUtc);
        }

        [Fact]
        public void Census_SurfacesAdvisoryResolved()
        {
            var engine = new DecisionEngine();
            var state = SetupSession(engine);
            state = ApplyDeviceSetupAppsFailure(engine, state).NewState;
            state = ApplyDeviceSetupResolved(engine, state).NewState;

            var census = DecisionStateSignalCensus.Build(state);

            Assert.Contains("esp_advisory_failure_resolved", census.SignalsSeen);
            Assert.True(census.SignalTimestamps.ContainsKey("espAdvisoryFailureResolved"));
        }
    }
}
