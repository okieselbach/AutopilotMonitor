using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.DecisionCore.Classifiers;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Serialization;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// Codex PR-1 pass-1 review (Hoch) — <see cref="EnrollmentOrchestrator.Start"/> must post
    /// <see cref="DecisionSignalKind.EspConfigDetected"/> synchronously before any collector
    /// host starts. Otherwise on SkipUser=true (device-only) flows the tracker's Shell-Core
    /// <c>esp_exiting</c> → <c>EspPhaseChanged(FinalizingSetup)</c> forward would race with
    /// <c>DeviceInfoHost.CollectAll</c> (fire-and-forget on ThreadPool) and the reducer's
    /// <c>ShouldTransitionToAwaitingHello</c> guard would block the legitimate promotion —
    /// leaving the session stuck, because <see cref="AutopilotMonitor.Agent.V2.Core.SignalAdapters.EspAndHelloTrackerAdapter"/>
    /// forwards Finalizing exactly once.
    /// <para>
    /// PR1 (Session 4fa5a2d4, 2026-05-22) — bootstrap migrated from <c>EspSkipConfigurationProbe.Read()</c>
    /// (Tuple SkipUser/SkipDevice only) to <c>ReadFull()</c> so EspAllowContinueAnyway +
    /// EspSyncFailureTimeoutMinutes are synchronously available before any EspTerminalFailure
    /// signal can arrive. Tests migrated from <see cref="EspSkipConfigurationProbe.ScopedOverride"/>
    /// to <see cref="EspSkipConfigurationProbe.ScopedFullOverride"/>. Both overrides set a
    /// <b>static</b> slot on the probe (<c>TestOverride</c>/<c>FullTestOverride</c>) — the
    /// class is gated behind <c>[Collection("SerialThreading")]</c> so parallel orchestrator
    /// tests in other classes do not race on the static slot.
    /// </para>
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class EnrollmentOrchestratorEspConfigBootstrapTests
    {
        private static DateTime At => new DateTime(2026, 4, 24, 10, 0, 0, DateTimeKind.Utc);

        private static IReadOnlyList<DecisionSignal> ReadSignalLog(string stateDir)
        {
            var path = Path.Combine(stateDir, "signal-log.jsonl");
            if (!File.Exists(path)) return Array.Empty<DecisionSignal>();
            var signals = new List<DecisionSignal>();
            foreach (var line in File.ReadAllLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                signals.Add(SignalSerializer.Deserialize(line));
            }
            return signals;
        }

        [Fact]
        public void Start_posts_EspConfigDetected_before_collectors_when_FirstSync_available()
        {
            using var rig = new EnrollmentOrchestratorRig(At);
            using var _ = new EspSkipConfigurationProbe.ScopedFullOverride(
                _log => new EspFirstSyncSnapshot(
                    skipUser: true,
                    skipDevice: false,
                    blockInStatusPage: null,
                    syncFailureTimeoutMinutes: null));
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = ReadSignalLog(rig.StateDir);
            var espConfig = Assert.Single(signals, s => s.Kind == DecisionSignalKind.EspConfigDetected);
            Assert.Equal("EnrollmentOrchestrator", espConfig.SourceOrigin);
            Assert.Equal("true", espConfig.Payload![SignalPayloadKeys.SkipUserEsp]);
            Assert.Equal("false", espConfig.Payload![SignalPayloadKeys.SkipDeviceEsp]);
        }

        [Fact]
        public void Start_skips_EspConfigDetected_post_when_registry_returns_null_null()
        {
            // Defensive: on a machine where FirstSync has not yet been populated (edge case on
            // very early boot) the bootstrap no-ops so the SignalLog does not carry a
            // meaningless signal. The collector's later CollectAll is the backup path.
            using var rig = new EnrollmentOrchestratorRig(At);
            using var _ = new EspSkipConfigurationProbe.ScopedFullOverride(_log => EspFirstSyncSnapshot.Empty);
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = ReadSignalLog(rig.StateDir);
            Assert.DoesNotContain(signals, s => s.Kind == DecisionSignalKind.EspConfigDetected);
        }

        [Fact]
        public void Start_posts_EspConfigDetected_with_only_skipDevice_when_SkipUser_registry_missing()
        {
            // Partial payload is valid — the reducer's per-fact set-once fills in SkipUserEsp
            // later from a subsequent post (DeviceInfoCollector.CollectEspConfiguration or
            // CollectAtEnrollmentStart).
            using var rig = new EnrollmentOrchestratorRig(At);
            using var _ = new EspSkipConfigurationProbe.ScopedFullOverride(
                _log => new EspFirstSyncSnapshot(
                    skipUser: null,
                    skipDevice: false,
                    blockInStatusPage: null,
                    syncFailureTimeoutMinutes: null));
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = ReadSignalLog(rig.StateDir);
            var espConfig = Assert.Single(signals, s => s.Kind == DecisionSignalKind.EspConfigDetected);
            Assert.False(espConfig.Payload!.ContainsKey(SignalPayloadKeys.SkipUserEsp));
            Assert.Equal("false", espConfig.Payload[SignalPayloadKeys.SkipDeviceEsp]);
        }

        [Fact]
        public void Start_posts_EspConfigDetected_before_SessionStarted_ingest_reaches_reducer()
        {
            // The bootstrap post must precede any Classic reducer signal that could land
            // AwaitingHello (EspPhaseChanged(Finalizing) / EspExiting). Ordering via ordinal: the
            // bootstrap signal's SessionSignalOrdinal must be the lowest among any signal whose
            // kind could drive Classic stage promotion. SessionStarted itself is always first
            // (ordinal 0); EspConfigDetected should come right after.
            using var rig = new EnrollmentOrchestratorRig(At);
            using var _ = new EspSkipConfigurationProbe.ScopedFullOverride(
                _log => new EspFirstSyncSnapshot(
                    skipUser: true,
                    skipDevice: false,
                    blockInStatusPage: null,
                    syncFailureTimeoutMinutes: null));
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = ReadSignalLog(rig.StateDir);
            var espConfig = signals.First(s => s.Kind == DecisionSignalKind.EspConfigDetected);

            // Nothing of Kind EspPhaseChanged or EspExiting may precede EspConfigDetected.
            Assert.DoesNotContain(
                signals.TakeWhile(s => s.SessionSignalOrdinal < espConfig.SessionSignalOrdinal),
                s => s.Kind == DecisionSignalKind.EspPhaseChanged || s.Kind == DecisionSignalKind.EspExiting);
        }

        // ============================================================ PR1 (4fa5a2d4) =====

        [Fact]
        public void Start_posts_EspConfigDetected_with_ContinueAnywayAndSyncFailureTimeout_FromReadFull()
        {
            // PR1 (Session 4fa5a2d4, 2026-05-22) — ReadFull-Switch carries EspAllowContinueAnyway +
            // EspSyncFailureTimeoutMinutes synchronously, so Tier 1 advisory-defang in
            // HandleEspTerminalFailureV1 has the ContinueAnyway fact before any EspTerminalFailure
            // signal can arrive (otherwise the fire-and-forget DeviceInfoCollector.CollectAll
            // would race and the reducer would see ScenarioObservations.EspAllowContinueAnyway==null).
            //
            // BlockInStatusPage=6 → AllowContinueAnyway=true, AllowTryAgain=true, AllowReset=false.
            using var rig = new EnrollmentOrchestratorRig(At);
            using var _ = new EspSkipConfigurationProbe.ScopedFullOverride(
                _log => new EspFirstSyncSnapshot(
                    skipUser: false,
                    skipDevice: false,
                    blockInStatusPage: 6,
                    syncFailureTimeoutMinutes: 30));
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = ReadSignalLog(rig.StateDir);
            var espConfig = Assert.Single(signals, s => s.Kind == DecisionSignalKind.EspConfigDetected);
            Assert.Equal("EnrollmentOrchestrator", espConfig.SourceOrigin);
            Assert.Equal("false", espConfig.Payload![SignalPayloadKeys.SkipUserEsp]);
            Assert.Equal("false", espConfig.Payload![SignalPayloadKeys.SkipDeviceEsp]);
            Assert.Equal("true", espConfig.Payload![SignalPayloadKeys.EspAllowContinueAnyway]);
            Assert.Equal("30", espConfig.Payload![SignalPayloadKeys.EspSyncFailureTimeoutMinutes]);
        }

        [Fact]
        public void Start_posts_EspConfigDetected_with_ContinueAnywayFalse_when_BlockInStatusPage_zero()
        {
            // Strict ESP profile: BlockInStatusPage=0 disables all three user buttons (Reset,
            // TryAgain, ContinueAnyway). The bootstrap surfaces "false" verbatim so the reducer
            // can distinguish "ContinueAnyway explicitly disabled" from "FirstSync unread".
            using var rig = new EnrollmentOrchestratorRig(At);
            using var _ = new EspSkipConfigurationProbe.ScopedFullOverride(
                _log => new EspFirstSyncSnapshot(
                    skipUser: false,
                    skipDevice: false,
                    blockInStatusPage: 0,
                    syncFailureTimeoutMinutes: 30));
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = ReadSignalLog(rig.StateDir);
            var espConfig = Assert.Single(signals, s => s.Kind == DecisionSignalKind.EspConfigDetected);
            Assert.Equal("false", espConfig.Payload![SignalPayloadKeys.EspAllowContinueAnyway]);
        }

        [Fact]
        public void Start_skips_EspConfigDetected_post_when_all_four_fields_null()
        {
            // No-op guard updated for ReadFull(): if SkipUser, SkipDevice, BlockInStatusPage AND
            // SyncFailureTimeout are all null, the bootstrap stays silent — the collector's later
            // CollectAll is the backup path.
            using var rig = new EnrollmentOrchestratorRig(At);
            using var _ = new EspSkipConfigurationProbe.ScopedFullOverride(_log => EspFirstSyncSnapshot.Empty);
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = ReadSignalLog(rig.StateDir);
            Assert.DoesNotContain(signals, s => s.Kind == DecisionSignalKind.EspConfigDetected);
        }

        [Fact]
        public void Start_posts_EspConfigDetected_when_only_ContinueAnyway_field_present()
        {
            // Edge case: FirstSync registry has only BlockInStatusPage set (e.g. partial write
            // from Windows or trimmed registry). The bootstrap still posts because
            // ContinueAnyway is decoded from BlockInStatusPage, even though SkipUser/SkipDevice
            // remain null. Confirms the no-op guard checks all four fields.
            using var rig = new EnrollmentOrchestratorRig(At);
            using var _ = new EspSkipConfigurationProbe.ScopedFullOverride(
                _log => new EspFirstSyncSnapshot(
                    skipUser: null,
                    skipDevice: null,
                    blockInStatusPage: 6,
                    syncFailureTimeoutMinutes: null));
            using var orchestrator = rig.Build();

            orchestrator.Start();
            orchestrator.Stop();

            var signals = ReadSignalLog(rig.StateDir);
            var espConfig = Assert.Single(signals, s => s.Kind == DecisionSignalKind.EspConfigDetected);
            Assert.False(espConfig.Payload!.ContainsKey(SignalPayloadKeys.SkipUserEsp));
            Assert.False(espConfig.Payload!.ContainsKey(SignalPayloadKeys.SkipDeviceEsp));
            Assert.Equal("true", espConfig.Payload![SignalPayloadKeys.EspAllowContinueAnyway]);
        }
    }
}
