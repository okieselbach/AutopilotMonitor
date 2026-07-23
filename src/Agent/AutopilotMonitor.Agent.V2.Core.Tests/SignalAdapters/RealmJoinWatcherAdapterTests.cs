using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using Microsoft.Win32;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    public sealed class RealmJoinWatcherAdapterTests
    {
        private static readonly DateTime Fixed = new DateTime(2026, 5, 21, 10, 0, 0, DateTimeKind.Utc);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            public AgentLogger Logger { get; }
            public RealmJoinWatcher Watcher { get; }
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(Fixed);

            public Fixture()
            {
                Logger = new AgentLogger(Tmp.Path, AgentLogLevel.Info);
                Watcher = new RealmJoinWatcher(Logger);
            }

            public void Dispose()
            {
                Watcher.Dispose();
                Tmp.Dispose();
            }
        }

        [Fact]
        public void TriggerDetectedFromTest_emits_RealmJoinDetected_signal_and_informational_event()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerDetectedFromTest(phase: 100);

            // 1) Decision signal that mutates engine state.
            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinDetected);
            Assert.Equal("RealmJoinWatcher", decision.SourceOrigin);
            Assert.Equal("100", decision.Payload![DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase]);

            // 2) Dual-emit informational event so the timeline shows realmjoin_detected.
            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinDetected);
            Assert.Equal("RealmJoinWatcher", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal("true", info.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("100", info.Payload["deploymentPhase"]);
            Assert.Contains("HKLM", info.Payload["registryKey"]);
        }

        [Fact]
        public void TriggerDetectedFromTest_includes_RealmJoin_ProductVersion_when_provided()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerDetectedFromTest(phase: 100, productVersion: "3.5.21.0");

            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinDetected);
            Assert.Equal("3.5.21.0", decision.Payload![DecisionEngine.RealmJoinPayloadKeys.ProductVersion]);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinDetected);
            Assert.Equal("3.5.21.0", info.Payload!["productVersion"]);
            Assert.Contains("version=3.5.21.0", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void TriggerDetectedFromTest_includes_ReleaseChannel_when_provided()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerDetectedFromTest(phase: 100, productVersion: "4.21.6", releaseChannel: "canary");

            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinDetected);
            Assert.Equal("4.21.6", decision.Payload![DecisionEngine.RealmJoinPayloadKeys.ProductVersion]);
            Assert.Equal("canary", decision.Payload[DecisionEngine.RealmJoinPayloadKeys.ReleaseChannel]);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinDetected);
            Assert.Equal("canary", info.Payload!["releaseChannel"]);
            Assert.Contains("version=4.21.6", info.Payload[SignalPayloadKeys.Message]);
            Assert.Contains("channel=canary", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void TriggerDetectedFromTest_omits_ProductVersion_when_null_or_empty()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerDetectedFromTest(phase: 100, productVersion: null);

            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinDetected);
            Assert.False(decision.Payload!.ContainsKey(DecisionEngine.RealmJoinPayloadKeys.ProductVersion));
            Assert.False(decision.Payload.ContainsKey(DecisionEngine.RealmJoinPayloadKeys.ReleaseChannel));

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinDetected);
            Assert.False(info.Payload!.ContainsKey("productVersion"));
            Assert.False(info.Payload.ContainsKey("releaseChannel"));
            Assert.DoesNotContain("version=", info.Payload[SignalPayloadKeys.Message]);
            Assert.DoesNotContain("channel=", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void TriggerResolvedFromTest_emits_RealmJoinResolved_signal_and_informational_event()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerResolvedFromTest(phase: 110);

            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinResolved);
            Assert.Equal("110", decision.Payload![DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase]);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinResolved);
            Assert.Equal("RealmJoinWatcher", info.Payload![SignalPayloadKeys.Source]);
            Assert.Equal("110", info.Payload["deploymentPhase"]);
        }

        [Fact]
        public void TriggerPhaseChangedFromTest_emits_typed_signal_and_informational_event()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerPhaseChangedFromTest(prev: 100, curr: 200);

            // 1) Typed signal — persists LastDeploymentPhase and drives the aborted-RJ-ESP
            //    detection in the reducer (session 224b2087). Not Detected/Resolved.
            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinDetected);
            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinResolved);
            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinPhaseChanged);
            Assert.Equal("RealmJoinWatcher", decision.SourceOrigin);
            Assert.Equal("200", decision.Payload![DecisionEngine.RealmJoinPayloadKeys.DeploymentPhase]);
            Assert.Equal("100", decision.Payload[DecisionEngine.RealmJoinPayloadKeys.PreviousPhase]);

            // 2) Dual-emit informational event keeps the existing timeline entry.
            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinPhaseChanged);
            // Raw ints stay for downstream filters / KQL.
            Assert.Equal("200", info.Payload!["deploymentPhase"]);
            Assert.Equal("100", info.Payload["previousPhase"]);
            // RJ enum names alongside — UI / message readable without a lookup table.
            Assert.Equal("RunningDeployment", info.Payload["deploymentPhaseName"]);
            Assert.Equal("RunningFirstDeployment", info.Payload["previousPhaseName"]);
            Assert.Contains("RunningFirstDeployment -> RunningDeployment", info.Payload[SignalPayloadKeys.Message]);
        }

        [Fact]
        public void TriggerPhaseChangedFromTest_falls_back_to_numeric_when_phase_is_unknown_RJ_enum_value()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            adapter.TriggerPhaseChangedFromTest(prev: 100, curr: 999);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinPhaseChanged);
            Assert.Equal("RunningFirstDeployment", info.Payload!["previousPhaseName"]);
            // Future / unknown RJ phase values stay observable as their numeric form rather
            // than being collapsed to "Unknown".
            Assert.Equal("999", info.Payload["deploymentPhaseName"]);
        }

        [Fact]
        public void TriggerPackageStartedFromTest_emits_signal_and_event_with_packageId_scope()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            var snap = new RealmJoinPackageSnapshot(
                packageId: "generic-vlc",
                displayName: "VLC media player",
                version: "3.0.21.0",
                success: null,
                lastExitCode: null);
            adapter.TriggerPackageStartedFromTest(scope: RealmJoinPackageFact.ScopeMachine, snap: snap);

            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinPackageStarted);
            Assert.Equal("generic-vlc", decision.Payload![DecisionEngine.RealmJoinPayloadKeys.PackageId]);
            Assert.Equal("VLC media player", decision.Payload[DecisionEngine.RealmJoinPayloadKeys.DisplayName]);
            Assert.Equal("3.0.21.0", decision.Payload[DecisionEngine.RealmJoinPayloadKeys.Version]);
            Assert.Equal("machine", decision.Payload[DecisionEngine.RealmJoinPayloadKeys.Scope]);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinPackageStarted);
            Assert.Equal("generic-vlc", info.Payload!["packageId"]);
            Assert.Equal("machine", info.Payload["scope"]);
        }

        [Fact]
        public void TriggerPackageCompletedFromTest_emits_signal_and_event_with_success_and_exit_code()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            var snap = new RealmJoinPackageSnapshot(
                packageId: "generic-vlc",
                displayName: "VLC media player",
                version: "3.0.21.0",
                success: true,
                lastExitCode: 0);
            adapter.TriggerPackageCompletedFromTest(scope: RealmJoinPackageFact.ScopeMachine, snap: snap);

            var decision = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinPackageCompleted);
            Assert.Equal("true", decision.Payload![DecisionEngine.RealmJoinPayloadKeys.Success]);
            Assert.Equal("0", decision.Payload[DecisionEngine.RealmJoinPayloadKeys.LastExitCode]);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinPackageCompleted);
            Assert.Equal("true", info.Payload!["success"]);
            Assert.Equal("0", info.Payload["lastExitCode"]);
        }

        [Fact]
        public void Package_watchers_stay_disarmed_until_phase_reaches_RunningThreshold()
        {
            // Pre-RJ window: IME may still be writing to HKLM\SOFTWARE\RealmJoin while the
            // RJ service key is already present but DeploymentPhase is still Blank (0).
            // Package watchers must NOT arm yet — otherwise IME's in-flight package writes
            // would be misattributed to RJ's lifecycle.
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            // Service-appearance fires Detected without a phase value — must NOT arm.
            f.Watcher.TriggerNotifyRealmJoinPresenceFromTest(phase: null);
            Assert.False(f.Watcher.PackageWatchersArmedForTest);

            // Phase = 0 (Blank) — still no arm; RJ is installed but not deploying.
            f.Watcher.TriggerNotifyRealmJoinPresenceFromTest(phase: 0);
            Assert.False(f.Watcher.PackageWatchersArmedForTest);

            // Phase transitions to 100 (RunningFirstDeployment) — package watchers MUST arm now.
            f.Watcher.TriggerNotifyRealmJoinPresenceFromTest(phase: 100);
            Assert.True(f.Watcher.PackageWatchersArmedForTest);
        }

        [Fact]
        public void Package_watchers_stay_armed_once_phase_subsequently_drops_back_below_threshold()
        {
            // Set-once semantics — even if a later phase reading were below threshold
            // (defensive against partial / racy registry writes), the watcher does not
            // disarm and re-introduce a window where post-running events could be lost.
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            f.Watcher.TriggerNotifyRealmJoinPresenceFromTest(phase: 100);
            Assert.True(f.Watcher.PackageWatchersArmedForTest);

            // Hypothetical regression: phase reads as 0 again — must not flip the flag.
            f.Watcher.TriggerNotifyRealmJoinPresenceFromTest(phase: 0);
            Assert.True(f.Watcher.PackageWatchersArmedForTest);
        }

        [Fact]
        public void Package_watchers_arm_immediately_when_first_phase_observed_is_completed()
        {
            // Agent boots into a session where RJ is already at CompletedFirstDeployment
            // (110) — e.g. pre-installed on an image, or recovered after a long absence.
            // The first phase observation is already past threshold, so package watchers
            // arm in that same notify pass. Historic package rows that exist at arming
            // time are seeded into the dedup sets by AttachMachineRealmJoinSubtreeWatcher
            // and are intentionally suppressed — only sub-keys that appear AFTER arming
            // surface as started/completed events.
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            f.Watcher.TriggerNotifyRealmJoinPresenceFromTest(phase: 110);

            Assert.True(f.Watcher.PackageWatchersArmedForTest);
            // Resolved fires on the same pass.
            Assert.Contains(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinResolved);
        }

        [Fact]
        public void Seeded_pre_existing_machine_package_does_not_fire_started_or_completed()
        {
            // Models the ESP-leftover case: when the package watcher arms, RJ has already
            // installed packages during ESP and their sub-keys are present. The seed pass
            // pre-fills both dedup sets so the historical rows are silently absorbed.
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            f.Watcher.SeedMachinePackageIdsForTest("generic-esp-leftover");

            var snap = new RealmJoinPackageSnapshot(
                packageId: "generic-esp-leftover",
                displayName: "Pre-ESP package",
                version: "1.0.0",
                success: true,
                lastExitCode: 0);
            f.Watcher.TriggerMachinePackageObservationFromTest(snap);

            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinPackageStarted);
            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinPackageCompleted);
        }

        [Fact]
        public void Seed_only_suppresses_seeded_ids_genuinely_new_machine_packages_still_fire()
        {
            // Seed suppression is per-PackageId — a sibling package that appears AFTER arming
            // must still fire normally. Guards against an over-broad implementation that
            // would mute the whole hive once any seed entry is present.
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            f.Watcher.SeedMachinePackageIdsForTest("generic-esp-leftover");

            var snap = new RealmJoinPackageSnapshot(
                packageId: "generic-new-after-arming",
                displayName: "New package",
                version: "2.0.0",
                success: null,
                lastExitCode: null);
            f.Watcher.TriggerMachinePackageObservationFromTest(snap);

            var started = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinPackageStarted);
            Assert.Equal("generic-new-after-arming", started.Payload![DecisionEngine.RealmJoinPayloadKeys.PackageId]);
        }

        [Fact]
        public void Seeded_pre_existing_user_package_does_not_fire_started_or_completed()
        {
            // Same semantics as the machine variant — user-hive packages enumerated under
            // HKU\<sid>\SOFTWARE\RealmJoin\Packages at arming time must be silently absorbed.
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            f.Watcher.SeedUserPackageIdsForTest("generic-user-leftover");

            var snap = new RealmJoinPackageSnapshot(
                packageId: "generic-user-leftover",
                displayName: "Pre-existing user package",
                version: "1.0.0",
                success: true,
                lastExitCode: 0);
            f.Watcher.TriggerUserPackageObservationFromTest(snap);

            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinPackageStarted);
            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinPackageCompleted);
        }

        [Fact]
        public void Watcher_fires_PackageStarted_even_when_DisplayName_is_missing()
        {
            // Today's RJ does not populate the DisplayName value for most package subkeys
            // (only ArgsHash / Success / LastExitCode / Version / Type are written), so
            // gating the started signal on DisplayName presence would silently drop it.
            // After the trigger decoupling: the first observation of the <packageId> subkey
            // is itself the started signal — DisplayName flows through empty as a useful
            // "RJ didn't advertise a name" indicator on the wire.
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            var snap = new RealmJoinPackageSnapshot(
                packageId: "generic-no-name-pkg",
                displayName: null,
                version: "1.0.0",
                success: null,
                lastExitCode: null);

            f.Watcher.TriggerMachinePackageObservationFromTest(snap);

            var started = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinPackageStarted);
            Assert.Equal("generic-no-name-pkg", started.Payload![DecisionEngine.RealmJoinPayloadKeys.PackageId]);
            // DisplayName payload key MUST stay present even when empty — the empty value is
            // itself the diagnostic signal that RJ did not write a DisplayName.
            Assert.Equal(string.Empty, started.Payload[DecisionEngine.RealmJoinPayloadKeys.DisplayName]);
            Assert.Equal("1.0.0", started.Payload[DecisionEngine.RealmJoinPayloadKeys.Version]);

            // Completed must NOT fire — no Success/LastExitCode in this snapshot.
            Assert.DoesNotContain(f.Ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinPackageCompleted);
        }

        [Fact]
        public void Watcher_fires_both_PackageStarted_and_Completed_when_pre_existing_snapshot_has_completion_markers()
        {
            // Pre-installed / already-completed package observed on first agent boot — the
            // single MaybeFirePackageEvents pass must fire BOTH started AND completed, in that
            // order, so the timeline reflects the full lifecycle.
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            var snap = new RealmJoinPackageSnapshot(
                packageId: "generic-prefab",
                displayName: null,
                version: "2.1.0",
                success: true,
                lastExitCode: 0);

            f.Watcher.TriggerMachinePackageObservationFromTest(snap);

            var started = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinPackageStarted);
            var completed = f.Ingress.Posted.Single(p => p.Kind == DecisionSignalKind.RealmJoinPackageCompleted);
            Assert.Equal("generic-prefab", started.Payload![DecisionEngine.RealmJoinPayloadKeys.PackageId]);
            Assert.Equal("generic-prefab", completed.Payload![DecisionEngine.RealmJoinPayloadKeys.PackageId]);
            Assert.Equal("true", completed.Payload[DecisionEngine.RealmJoinPayloadKeys.Success]);
        }

        [Fact]
        public void Overlong_display_name_is_truncated_to_256_characters()
        {
            using var f = new Fixture();
            using var adapter = new RealmJoinWatcherAdapter(f.Watcher, f.Ingress, f.Clock);

            var longName = new string('x', 1000);
            var snap = new RealmJoinPackageSnapshot(
                packageId: "generic-bigname",
                displayName: longName,
                version: null,
                success: null,
                lastExitCode: null);
            adapter.TriggerPackageStartedFromTest(scope: RealmJoinPackageFact.ScopeUser, snap: snap);

            var info = f.Ingress.Posted.Single(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == SharedEventTypes.RealmJoinPackageStarted);
            // Adapter must clamp DisplayName before posting (PII / payload-size guard mirror of
            // RealmJoinPackageFact.MaxDisplayNameLength = 256). The packageId stays intact.
            Assert.Equal(256, info.Payload!["displayName"].Length);
            Assert.Equal("generic-bigname", info.Payload["packageId"]);
            Assert.Equal("user", info.Payload["scope"]);
        }

        // Fake registry-access bundle used by the regression tests below. Lets the test drive
        // the watcher's enumerate / read / exists outcomes deterministically without touching
        // the live hive — production binds these to RealmJoinInfo + the real Registry probe.
        private sealed class FakeRealmJoinRegistry
        {
            public Dictionary<string, List<string>> PackagesByPath { get; } =
                new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> ExistingKeys { get; } =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public IReadOnlyList<string> Enumerate(RegistryHive hive, string packagesPath)
            {
                return PackagesByPath.TryGetValue(packagesPath, out var ids)
                    ? (IReadOnlyList<string>)ids
                    : Array.Empty<string>();
            }

            public bool TryRead(RegistryHive hive, string packagesPath, string packageId, out RealmJoinPackageSnapshot snapshot)
            {
                snapshot = new RealmJoinPackageSnapshot(
                    packageId: packageId,
                    displayName: $"Pre-existing {packageId}",
                    version: "1.0.0",
                    success: true,
                    lastExitCode: 0);
                return true;
            }

            public bool KeyExists(RegistryHive hive, string subPath)
                => ExistingKeys.Contains(subPath);
        }

        [Fact]
        public void Phase_crossing_after_late_ArmHku_does_not_replay_pre_existing_user_packages()
        {
            // Regression for session ff0cdcbe (V2 2.0.886): three RJ user-scope packages were
            // installed during ESP while DeploymentPhase was still Blank. When phase finally
            // crossed 0 -> 100, the pre-fix NotifyRealmJoinPresence ran a standalone
            // CheckUserPackages BEFORE the seed pass inside AttachUserRealmJoinSubtreeWatcher.
            // The dedup sets were still empty at that point, so every pre-existing sub-key
            // surfaced as a started + completed pair instead of being silently absorbed.
            //
            // Drives the exact production sequence (ArmHku first, then phase crosses 100) with
            // a fake registry that already holds three user-scope packages, and asserts that
            // no realmjoin_package_started / _completed signals leak.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var ingress = new FakeSignalIngressSink();
            var clock = new VirtualClock(Fixed);

            const string testSid = "S-1-5-21-test-fake";
            var userRealmJoinPath = testSid + "\\" + RealmJoinInfo.UserRealmJoinSubPath;
            var userPackagesPath = testSid + "\\" + RealmJoinInfo.UserPackagesRegistrySubPath;

            var registry = new FakeRealmJoinRegistry();
            // Make the HKU\<sid>\SOFTWARE\RealmJoin key "exist" so EnsureUserRealmJoinStage
            // takes the attach-and-seed path. KeyExists returns false for everything else,
            // including the HKLM machine-scope probe — that path arms a (no-op-here) appearance
            // watcher on HKLM\SOFTWARE which is irrelevant to this assertion.
            registry.ExistingKeys.Add(userRealmJoinPath);
            registry.PackagesByPath[userPackagesPath] = new List<string>
            {
                "generic-agilebits-1password",
                "generic-realmjoin-promote-tray-icon",
                "generic-vlc-usersettings",
            };

            using var watcher = new RealmJoinWatcher(
                logger,
                enumeratePackageIds: registry.Enumerate,
                tryReadPackage: registry.TryRead,
                keyExists: registry.KeyExists);
            using var adapter = new RealmJoinWatcherAdapter(watcher, ingress, clock);

            // 1) Desktop arrives, SID is recorded — phase is still Blank, so the watcher just
            //    notes the SID and arms nothing. armNow == false.
            watcher.ArmHku(testSid);
            Assert.False(watcher.PackageWatchersArmedForTest);

            // 2) Phase observation crosses RunningThreshold — this is the path that used to
            //    pre-seed-CheckUserPackages and leak events.
            watcher.TriggerNotifyRealmJoinPresenceFromTest(phase: 100);

            Assert.True(watcher.PackageWatchersArmedForTest);
            Assert.DoesNotContain(ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinPackageStarted);
            Assert.DoesNotContain(ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinPackageCompleted);
        }

        [Fact]
        public void Late_ArmHku_with_phase_already_running_does_not_replay_pre_existing_user_packages()
        {
            // Mirror regression for the OTHER pre-fix call site: when ArmHku arrives AFTER the
            // phase has already crossed RunningThreshold, the pre-fix code ran a standalone
            // CheckUserPackages inside ArmHku BEFORE EnsureUserRealmJoinStage's seed. Same
            // family of bug, opposite arrival order — desktop_arrived after phase==100.
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            var ingress = new FakeSignalIngressSink();
            var clock = new VirtualClock(Fixed);

            const string testSid = "S-1-5-21-test-fake";
            var userRealmJoinPath = testSid + "\\" + RealmJoinInfo.UserRealmJoinSubPath;
            var userPackagesPath = testSid + "\\" + RealmJoinInfo.UserPackagesRegistrySubPath;

            var registry = new FakeRealmJoinRegistry();
            registry.ExistingKeys.Add(userRealmJoinPath);
            registry.PackagesByPath[userPackagesPath] = new List<string>
            {
                "generic-prefab-user-a",
                "generic-prefab-user-b",
            };

            using var watcher = new RealmJoinWatcher(
                logger,
                enumeratePackageIds: registry.Enumerate,
                tryReadPackage: registry.TryRead,
                keyExists: registry.KeyExists);
            using var adapter = new RealmJoinWatcherAdapter(watcher, ingress, clock);

            // 1) Phase crosses RunningThreshold first — package watchers arm but SID isn't
            //    known yet, so the HKU branch is skipped.
            watcher.TriggerNotifyRealmJoinPresenceFromTest(phase: 100);
            Assert.True(watcher.PackageWatchersArmedForTest);

            // 2) Desktop arrives late, SID resolved — armNow == true, used to leak events here.
            watcher.ArmHku(testSid);

            Assert.DoesNotContain(ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinPackageStarted);
            Assert.DoesNotContain(ingress.Posted, p => p.Kind == DecisionSignalKind.RealmJoinPackageCompleted);
        }

    }
}
