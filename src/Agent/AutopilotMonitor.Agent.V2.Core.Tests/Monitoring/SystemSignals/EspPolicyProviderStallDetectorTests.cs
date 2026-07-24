#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Session 5d735290 — pure detection logic of <see cref="EspPolicyProviderStallDetector"/>:
    /// the CSP completion rule and the dwell/one-shot transition. No registry, no timers.
    /// </summary>
    public sealed class EspPolicyProviderStallLogicTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 7, 23, 10, 0, 0, DateTimeKind.Utc);
        private static readonly TimeSpan Dwell = TimeSpan.FromMinutes(EspPolicyProviderStallDetector.DwellMinutes);

        private static PolicyProviderState SetupApps(string name, int? tracking, string scope = "device") =>
            new PolicyProviderState(name, scope, EspPolicyProviderProbe.KindSetupApps, tracking, null);

        private static PolicyProviderState DevicePrep(string name, int? installState) =>
            new PolicyProviderState(name, "device", EspPolicyProviderProbe.KindDevicePreparation, null, installState);

        private static EspPolicyProviderSnapshot Snapshot(params PolicyProviderState[] providers) =>
            new EspPolicyProviderSnapshot(providers);

        // ------------------------------------------------- completion rule (CSP contract)

        [Theory]
        [InlineData(1, true)]   // TrackingPoliciesCreated=true
        [InlineData(0, false)]  // explicit false (the CSP default)
        [InlineData(null, false)] // value missing — the field case
        [InlineData(2, false)]  // anything else is not "true"
        public void SetupApps_provider_complete_only_at_tracking_1(int? tracking, bool expected)
            => Assert.Equal(expected, EspPolicyProviderProbe.IsProviderComplete(
                EspPolicyProviderProbe.KindSetupApps, tracking, null));

        [Theory]
        [InlineData(2, true)]   // NotRequired
        [InlineData(3, true)]   // Completed
        [InlineData(1, false)]  // NotInstalled
        [InlineData(4, false)]  // Error
        [InlineData(null, false)]
        public void DevicePreparation_provider_complete_at_state_2_or_3(int? state, bool expected)
            => Assert.Equal(expected, EspPolicyProviderProbe.IsProviderComplete(
                EspPolicyProviderProbe.KindDevicePreparation, null, state));

        [Fact]
        public void TryReadInt_tolerates_dword_string_and_bool_forms()
        {
            Assert.Equal(1, EspPolicyProviderProbe.TryReadInt(1));
            Assert.Equal(3, EspPolicyProviderProbe.TryReadInt(3L));
            Assert.Equal(1, EspPolicyProviderProbe.TryReadInt(" 1 "));
            Assert.Equal(1, EspPolicyProviderProbe.TryReadInt(true));
            Assert.Equal(0, EspPolicyProviderProbe.TryReadInt(false));
            Assert.Null(EspPolicyProviderProbe.TryReadInt(null));
            Assert.Null(EspPolicyProviderProbe.TryReadInt("not-a-number"));
            Assert.Null(EspPolicyProviderProbe.TryReadInt(new byte[] { 1 }));
        }

        // ------------------------------------------------- dwell transition

        [Fact]
        public void Empty_or_missing_snapshot_clears_dwell_state_and_never_stalls()
        {
            var previous = new Dictionary<string, DateTime> { ["setupApps|device|ConfigMgr"] = T0 };

            var missing = EspPolicyProviderStallDetector.EvaluateStallTransition(
                EspPolicyProviderSnapshot.Empty, previous, new HashSet<string>(), T0, false, T0.AddHours(2), Dwell);
            Assert.Empty(missing.UpdatedFirstSeenUtc);
            Assert.Empty(missing.NewlyStalled);
            Assert.Null(missing.UpdatedSidecarMissingSinceUtc); // arm 2 resets too
            Assert.Null(missing.SidecarMissingStalledForMinutes);

            var registeredButEmpty = EspPolicyProviderStallDetector.EvaluateStallTransition(
                Snapshot(), previous, new HashSet<string>(), T0, false, T0.AddHours(2), Dwell);
            Assert.Empty(registeredButEmpty.UpdatedFirstSeenUtc);
            Assert.Empty(registeredButEmpty.NewlyStalled);
            Assert.Null(registeredButEmpty.UpdatedSidecarMissingSinceUtc);
        }

        [Fact]
        public void Incomplete_provider_accrues_dwell_but_does_not_stall_before_threshold()
        {
            var snapshot = Snapshot(SetupApps("ConfigMgr", tracking: null));

            var round1 = EspPolicyProviderStallDetector.EvaluateStallTransition(
                snapshot, new Dictionary<string, DateTime>(), new HashSet<string>(), null, false, T0, Dwell);
            Assert.Equal(T0, round1.UpdatedFirstSeenUtc["setupApps|device|ConfigMgr"]);
            Assert.Empty(round1.NewlyStalled);

            var round2 = EspPolicyProviderStallDetector.EvaluateStallTransition(
                snapshot, round1.UpdatedFirstSeenUtc, new HashSet<string>(),
                round1.UpdatedSidecarMissingSinceUtc, false, T0.AddMinutes(14), Dwell);
            Assert.Equal(T0, round2.UpdatedFirstSeenUtc["setupApps|device|ConfigMgr"]); // first-seen preserved
            Assert.Empty(round2.NewlyStalled);
            Assert.Null(round2.SidecarMissingStalledForMinutes); // 14 min < dwell on arm 2 as well
        }

        [Fact]
        public void Provider_stalls_at_dwell_threshold_with_correct_minutes()
        {
            var snapshot = Snapshot(SetupApps("ConfigMgr", tracking: 0));
            var seen = new Dictionary<string, DateTime> { ["setupApps|device|ConfigMgr"] = T0 };

            var eval = EspPolicyProviderStallDetector.EvaluateStallTransition(
                snapshot, seen, new HashSet<string>(), null, false, T0.AddMinutes(15), Dwell);

            var stalled = Assert.Single(eval.NewlyStalled);
            Assert.Equal("ConfigMgr", stalled.Provider.Name);
            Assert.Equal(15.0, stalled.StalledForMinutes, precision: 3);
        }

        [Fact]
        public void Completion_or_disappearance_resets_the_dwell_clock()
        {
            var key = "setupApps|device|ConfigMgr";
            var seen = new Dictionary<string, DateTime> { [key] = T0 };

            // Completes mid-dwell → dropped from the map.
            var completed = EspPolicyProviderStallDetector.EvaluateStallTransition(
                Snapshot(SetupApps("ConfigMgr", tracking: 1)), seen, new HashSet<string>(), null, false, T0.AddMinutes(10), Dwell);
            Assert.Empty(completed.UpdatedFirstSeenUtc);

            // Re-appears incomplete much later → dwell restarts from the new now, no stall yet.
            var reappeared = EspPolicyProviderStallDetector.EvaluateStallTransition(
                Snapshot(SetupApps("ConfigMgr", tracking: 0)), completed.UpdatedFirstSeenUtc,
                new HashSet<string>(), null, false, T0.AddHours(3), Dwell);
            Assert.Equal(T0.AddHours(3), reappeared.UpdatedFirstSeenUtc[key]);
            Assert.Empty(reappeared.NewlyStalled);

            // Disappearance behaves the same as completion.
            var disappeared = EspPolicyProviderStallDetector.EvaluateStallTransition(
                Snapshot(SetupApps("Sidecar", tracking: 1)), new Dictionary<string, DateTime> { [key] = T0 },
                new HashSet<string>(), null, false, T0.AddMinutes(20), Dwell);
            Assert.False(disappeared.UpdatedFirstSeenUtc.ContainsKey(key));
        }

        [Fact]
        public void Already_fired_provider_is_never_reported_again()
        {
            var snapshot = Snapshot(SetupApps("ConfigMgr", tracking: null));
            var seen = new Dictionary<string, DateTime> { ["setupApps|device|ConfigMgr"] = T0 };
            var fired = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "setupApps|device|ConfigMgr" };

            var eval = EspPolicyProviderStallDetector.EvaluateStallTransition(
                snapshot, seen, fired, null, false, T0.AddHours(5), Dwell);

            Assert.Empty(eval.NewlyStalled);
            Assert.True(eval.UpdatedFirstSeenUtc.ContainsKey("setupApps|device|ConfigMgr")); // still tracked
        }

        [Fact]
        public void Mixed_kinds_and_scopes_are_tracked_under_distinct_keys()
        {
            var snapshot = Snapshot(
                SetupApps("ConfigMgr", tracking: null),
                SetupApps("ConfigMgr", tracking: null, scope: "user:S-1-5-21-1111"),
                DevicePrep("ConfigMgr", installState: 1));

            var eval = EspPolicyProviderStallDetector.EvaluateStallTransition(
                snapshot, new Dictionary<string, DateTime>(), new HashSet<string>(), null, false, T0, Dwell);

            Assert.Equal(3, eval.UpdatedFirstSeenUtc.Count);
            Assert.Contains("setupApps|device|ConfigMgr", eval.UpdatedFirstSeenUtc.Keys);
            Assert.Contains("setupApps|user:S-1-5-21-1111|ConfigMgr", eval.UpdatedFirstSeenUtc.Keys);
            Assert.Contains("devicePreparation|device|ConfigMgr", eval.UpdatedFirstSeenUtc.Keys);
        }

        [Fact]
        public void SidecarRegistered_matches_setupApps_sidecar_only()
        {
            Assert.True(Snapshot(SetupApps("Sidecar", tracking: 0)).SidecarRegistered);
            Assert.True(Snapshot(SetupApps("sidecar", tracking: 1, scope: "user:S-1-5-21-1")).SidecarRegistered);
            Assert.False(Snapshot(SetupApps("ConfigMgr", tracking: 1)).SidecarRegistered);
            Assert.False(Snapshot(DevicePrep("Sidecar", installState: 3)).SidecarRegistered); // wrong kind
            Assert.False(EspPolicyProviderSnapshot.Empty.SidecarRegistered);
        }

        // ------------------------------------------------- arm 2: sidecar-missing transition

        [Fact]
        public void Issue106_foreign_provider_complete_but_sidecar_missing_stalls_after_dwell()
        {
            // The actual field case: ConfigMgr with TrackingPoliciesCreated=1 — "complete" by the
            // CSP value contract, arm 1 never fires — yet the ESP waits on the absent Sidecar.
            var snapshot = Snapshot(SetupApps("ConfigMgr", tracking: 1));

            var round1 = EspPolicyProviderStallDetector.EvaluateStallTransition(
                snapshot, new Dictionary<string, DateTime>(), new HashSet<string>(), null, false, T0, Dwell);
            Assert.Empty(round1.NewlyStalled);                       // arm 1 stays silent...
            Assert.Equal(T0, round1.UpdatedSidecarMissingSinceUtc);  // ...but arm 2 starts its clock
            Assert.Null(round1.SidecarMissingStalledForMinutes);

            var round2 = EspPolicyProviderStallDetector.EvaluateStallTransition(
                snapshot, round1.UpdatedFirstSeenUtc, new HashSet<string>(),
                round1.UpdatedSidecarMissingSinceUtc, false, T0.AddMinutes(15), Dwell);
            Assert.Empty(round2.NewlyStalled);
            Assert.Equal(15.0, round2.SidecarMissingStalledForMinutes!.Value, precision: 3);
        }

        [Fact]
        public void Sidecar_registering_resets_the_sidecar_missing_clock()
        {
            // ConfigMgr first (legitimate startup ordering) → clock runs...
            var round1 = EspPolicyProviderStallDetector.EvaluateStallTransition(
                Snapshot(SetupApps("ConfigMgr", tracking: 1)),
                new Dictionary<string, DateTime>(), new HashSet<string>(), null, false, T0, Dwell);
            Assert.Equal(T0, round1.UpdatedSidecarMissingSinceUtc);

            // ...Sidecar joins → condition clears, state resets.
            var round2 = EspPolicyProviderStallDetector.EvaluateStallTransition(
                Snapshot(SetupApps("ConfigMgr", tracking: 1), SetupApps("Sidecar", tracking: 0)),
                round1.UpdatedFirstSeenUtc, new HashSet<string>(),
                round1.UpdatedSidecarMissingSinceUtc, false, T0.AddMinutes(10), Dwell);
            Assert.Null(round2.UpdatedSidecarMissingSinceUtc);
            Assert.Null(round2.SidecarMissingStalledForMinutes);

            // Sidecar gone again much later → clock restarts from the new now, no instant stall.
            var round3 = EspPolicyProviderStallDetector.EvaluateStallTransition(
                Snapshot(SetupApps("ConfigMgr", tracking: 1)),
                round2.UpdatedFirstSeenUtc, new HashSet<string>(),
                round2.UpdatedSidecarMissingSinceUtc, false, T0.AddHours(3), Dwell);
            Assert.Equal(T0.AddHours(3), round3.UpdatedSidecarMissingSinceUtc);
            Assert.Null(round3.SidecarMissingStalledForMinutes);
        }

        [Fact]
        public void Sidecar_missing_one_shot_and_deviceprep_only_scoping()
        {
            // Already fired → never re-reported, but the clock keeps running.
            var latched = EspPolicyProviderStallDetector.EvaluateStallTransition(
                Snapshot(SetupApps("ConfigMgr", tracking: 1)),
                new Dictionary<string, DateTime>(), new HashSet<string>(), T0, true, T0.AddHours(5), Dwell);
            Assert.Null(latched.SidecarMissingStalledForMinutes);
            Assert.Equal(T0, latched.UpdatedSidecarMissingSinceUtc);

            // DevicePreparation providers alone don't constitute the Setup/Apps wait — arm 2 off.
            var devicePrepOnly = EspPolicyProviderStallDetector.EvaluateStallTransition(
                Snapshot(DevicePrep("ConfigMgr", installState: 1)),
                new Dictionary<string, DateTime>(), new HashSet<string>(), null, false, T0, Dwell);
            Assert.Null(devicePrepOnly.UpdatedSidecarMissingSinceUtc);
        }
    }

    /// <summary>
    /// Emission surface of the detector via the real <see cref="InformationalEventPost"/> rail.
    /// Static probe override → serial collection (same contract as
    /// <c>EspConfigDetectedTrackingFieldsTests</c>).
    /// </summary>
    [Collection("SerialThreading")]
    public sealed class EspPolicyProviderStallDetectorEmissionTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();

        public void Dispose() => _tmp.Dispose();

        private (EspPolicyProviderStallDetector sut, FakeSignalIngressSink sink, VirtualClock clock)
            BuildDetector(StartupEventGate? gate = null)
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            var sink = new FakeSignalIngressSink();
            var clock = new VirtualClock(At);
            var post = new InformationalEventPost(sink, clock);
            var sut = new EspPolicyProviderStallDetector("S1", "T1", post, logger, clock, gate);
            return (sut, sink, clock);
        }

        private static EspPolicyProviderSnapshot ConfigMgrOnly() => new EspPolicyProviderSnapshot(new[]
        {
            new PolicyProviderState("ConfigMgr", "device", EspPolicyProviderProbe.KindSetupApps,
                trackingPoliciesCreated: null, installationState: null),
        });

        private static EspPolicyProviderSnapshot SidecarCompleteConfigMgrIncomplete() => new EspPolicyProviderSnapshot(new[]
        {
            new PolicyProviderState("Sidecar", "device", EspPolicyProviderProbe.KindSetupApps, 1, null),
            new PolicyProviderState("ConfigMgr", "device", EspPolicyProviderProbe.KindSetupApps, 0, null),
        });

        private static IReadOnlyList<FakeSignalIngressSink.PostedSignal> StalledEvents(FakeSignalIngressSink sink) =>
            sink.Posted.Where(p =>
                p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "esp_policy_provider_stalled").ToList();

        [Fact]
        public void CustomerScenario_configmgr_without_sidecar_emits_one_warning_after_dwell()
        {
            using var _probe = new EspPolicyProviderProbe.ScopedOverride(_ => ConfigMgrOnly());
            var (sut, sink, clock) = BuildDetector();

            sut.Tick();                                     // first sighting — dwell starts
            Assert.Empty(StalledEvents(sink));

            clock.Advance(TimeSpan.FromMinutes(EspPolicyProviderStallDetector.DwellMinutes));
            sut.Tick();                                     // threshold crossed → emit

            var evt = Assert.Single(StalledEvents(sink));
            Assert.Equal(DecisionSignalKind.InformationalEvent, evt.Kind);
            Assert.Equal("Warning", evt.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", evt.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal(EspPolicyProviderStallDetector.SourceLabel, evt.Payload[SignalPayloadKeys.Source]);

            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(evt.TypedPayload);
            // Both arms cross in the same round (ConfigMgr incomplete AND Sidecar absent) →
            // ONE event; sidecar-missing is the dominant reason.
            Assert.Equal(EspPolicyProviderStallDetector.ReasonSidecarMissing, data["reason"]);
            Assert.Equal(false, data["sidecarRegistered"]);
            Assert.Equal(15.0, data["sidecarMissingForMinutes"]);
            Assert.Equal(EspPolicyProviderStallDetector.DwellMinutes, data["dwellMinutes"]);
            var stalled = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object>>>(data["stalledProviders"]);
            var entry = Assert.Single(stalled);
            Assert.Equal("ConfigMgr", entry["name"]);
            Assert.Equal(EspPolicyProviderProbe.KindSetupApps, entry["kind"]);
            Assert.Equal(15.0, entry["stalledForMinutes"]);
        }

        [Fact]
        public void Issue106_configmgr_complete_without_sidecar_still_emits_sidecar_missing()
        {
            // Regression test for the customer's follow-up on issue #106: TrackingPoliciesCreated=1
            // WAS present under ConfigMgr — the provider looks "complete", arm 1 never fires, yet
            // the ESP hangs because Sidecar is absent. Arm 2 must catch exactly this.
            var configMgrComplete = new EspPolicyProviderSnapshot(new[]
            {
                new PolicyProviderState("ConfigMgr", "device", EspPolicyProviderProbe.KindSetupApps,
                    trackingPoliciesCreated: 1, installationState: null),
            });
            using var _probe = new EspPolicyProviderProbe.ScopedOverride(_ => configMgrComplete);
            var (sut, sink, clock) = BuildDetector();

            sut.Tick();
            Assert.Empty(StalledEvents(sink));

            clock.Advance(TimeSpan.FromMinutes(EspPolicyProviderStallDetector.DwellMinutes));
            sut.Tick();

            var evt = Assert.Single(StalledEvents(sink));
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(evt.TypedPayload);
            Assert.Equal(EspPolicyProviderStallDetector.ReasonSidecarMissing, data["reason"]);
            Assert.Equal(false, data["sidecarRegistered"]);
            Assert.Equal(15.0, data["sidecarMissingForMinutes"]);
            Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object>>>(data["stalledProviders"]));
            var providers = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object?>>>(data["providers"]);
            Assert.Equal(true, Assert.Single(providers)["complete"]); // the misleading "complete" is visible
        }

        [Fact]
        public void Sidecar_complete_configmgr_incomplete_reports_only_configmgr_with_sidecar_flag()
        {
            using var _probe = new EspPolicyProviderProbe.ScopedOverride(_ => SidecarCompleteConfigMgrIncomplete());
            var (sut, sink, clock) = BuildDetector();

            sut.Tick();
            clock.Advance(TimeSpan.FromMinutes(EspPolicyProviderStallDetector.DwellMinutes));
            sut.Tick();

            var evt = Assert.Single(StalledEvents(sink));
            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(evt.TypedPayload);
            Assert.Equal(EspPolicyProviderStallDetector.ReasonProviderIncomplete, data["reason"]); // arm 2 off — Sidecar is there
            Assert.Equal(true, data["sidecarRegistered"]);
            Assert.False(data.ContainsKey("sidecarMissingForMinutes"));
            var stalled = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object>>>(data["stalledProviders"]);
            Assert.Equal("ConfigMgr", Assert.Single(stalled)["name"]);
            var providers = Assert.IsAssignableFrom<IReadOnlyList<Dictionary<string, object?>>>(data["providers"]);
            Assert.Equal(2, providers.Count); // full table always attached
        }

        [Fact]
        public void Latch_prevents_second_emission_for_the_same_provider()
        {
            using var _probe = new EspPolicyProviderProbe.ScopedOverride(_ => ConfigMgrOnly());
            var (sut, sink, clock) = BuildDetector();

            sut.Tick();
            clock.Advance(TimeSpan.FromMinutes(EspPolicyProviderStallDetector.DwellMinutes));
            sut.Tick();
            clock.Advance(TimeSpan.FromMinutes(30));
            sut.Tick();
            sut.Tick();

            Assert.Single(StalledEvents(sink));
        }

        [Fact]
        public void Startup_gate_suppresses_same_stall_across_restart_but_reports_new_provider_set()
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);

            // Run 1: fires and persists the fingerprint.
            using (var _probe = new EspPolicyProviderProbe.ScopedOverride(_ => ConfigMgrOnly()))
            {
                var (sut, sink, clock) = BuildDetector(new StartupEventGate(_tmp.Path, logger));
                sut.Tick();
                clock.Advance(TimeSpan.FromMinutes(EspPolicyProviderStallDetector.DwellMinutes));
                sut.Tick();
                Assert.Single(StalledEvents(sink));
            }

            // Run 2 (simulated restart — fresh detector, gate reloaded from the same state dir):
            // identical stall re-accrues dwell but lands on the same fingerprint → suppressed.
            using (var _probe = new EspPolicyProviderProbe.ScopedOverride(_ => ConfigMgrOnly()))
            {
                var (sut, sink, clock) = BuildDetector(new StartupEventGate(_tmp.Path, logger));
                sut.Tick();
                clock.Advance(TimeSpan.FromMinutes(EspPolicyProviderStallDetector.DwellMinutes));
                sut.Tick();
                Assert.Empty(StalledEvents(sink));
            }

            // Run 3: a SECOND provider joins the stall → fingerprint changes → reported.
            var widened = new EspPolicyProviderSnapshot(new[]
            {
                new PolicyProviderState("ConfigMgr", "device", EspPolicyProviderProbe.KindSetupApps, null, null),
                new PolicyProviderState("Contoso", "device", EspPolicyProviderProbe.KindSetupApps, 0, null),
            });
            using (var _probe = new EspPolicyProviderProbe.ScopedOverride(_ => widened))
            {
                var (sut, sink, clock) = BuildDetector(new StartupEventGate(_tmp.Path, logger));
                sut.Tick();
                clock.Advance(TimeSpan.FromMinutes(EspPolicyProviderStallDetector.DwellMinutes));
                sut.Tick();
                Assert.Single(StalledEvents(sink));
            }
        }

        [Fact]
        public void Healthy_or_absent_tracking_never_emits()
        {
            var healthy = new EspPolicyProviderSnapshot(new[]
            {
                new PolicyProviderState("Sidecar", "device", EspPolicyProviderProbe.KindSetupApps, 1, null),
                new PolicyProviderState("ConfigMgr", "device", EspPolicyProviderProbe.KindDevicePreparation, null, 3),
            });
            using (var _probe = new EspPolicyProviderProbe.ScopedOverride(_ => healthy))
            {
                var (sut, sink, clock) = BuildDetector();
                sut.Tick();
                clock.Advance(TimeSpan.FromHours(4));
                sut.Tick();
                Assert.Empty(StalledEvents(sink));
            }

            using (var _probe = new EspPolicyProviderProbe.ScopedOverride(_ => EspPolicyProviderSnapshot.Empty))
            {
                var (sut, sink, clock) = BuildDetector();
                sut.Tick();
                clock.Advance(TimeSpan.FromHours(4));
                sut.Tick();
                Assert.Empty(StalledEvents(sink));
            }
        }
    }
}
