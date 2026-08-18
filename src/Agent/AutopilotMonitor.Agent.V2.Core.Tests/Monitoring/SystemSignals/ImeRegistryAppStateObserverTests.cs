#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.SystemSignals
{
    /// <summary>
    /// Registry second pillar (audit 2026-08-17): pure parsing/diff logic plus the
    /// tick flow (baseline silence, state-change emission, reconciliation dwell)
    /// driven through the snapshot override — no live registry.
    /// </summary>
    public sealed class ImeRegistryAppStateObserverTests
    {
        private const string App1 = "11111111-1111-1111-1111-111111111111";
        private const string App2 = "22222222-2222-2222-2222-222222222222";
        private static readonly DateTime T0 = new DateTime(2026, 8, 18, 8, 0, 0, DateTimeKind.Utc);

        // ── pure helpers ────────────────────────────────────────────────────────

        [Theory]
        [InlineData("11111111-1111-1111-1111-111111111111_1", "11111111-1111-1111-1111-111111111111")]
        [InlineData("11111111-1111-1111-1111-111111111111", "11111111-1111-1111-1111-111111111111")]
        [InlineData("GRS", null)]
        [InlineData("", null)]
        [InlineData("not-a-guid_1", null)]
        public void ExtractAppId_parses_app_keys_and_rejects_non_app_keys(string keyName, string? expected)
            => Assert.Equal(expected, ImeRegistryAppStateObserver.ExtractAppId(keyName));

        [Fact]
        public void ParseEnforcementStateMessage_reads_state_and_error()
        {
            var (state, error) = ImeRegistryAppStateObserver.ParseEnforcementStateMessage(
                "{\"EnforcementState\":1000,\"ErrorCode\":0}");
            Assert.Equal(1000, state);
            Assert.Equal(0L, error);

            Assert.Equal((null, null), ImeRegistryAppStateObserver.ParseEnforcementStateMessage("not json"));
            Assert.Equal((null, null), ImeRegistryAppStateObserver.ParseEnforcementStateMessage("[]"));
        }

        [Theory]
        [InlineData(1000, "success")]
        [InlineData(1004, "success")]
        [InlineData(2009, "inProgress")]
        [InlineData(3000, "requirementsNotMet")]
        [InlineData(5003, "error")]
        [InlineData(6001, "notAttempted")]
        [InlineData(42, "unknown")]
        public void ClassifyEnforcementState_bands(int state, string expected)
            => Assert.Equal(expected, ImeRegistryAppStateObserver.ClassifyEnforcementState(state));

        [Theory]
        [InlineData(1000, "installed")]
        [InlineData(1999, "installed")]
        [InlineData(3000, "failed")]
        [InlineData(2000, "notApplicable")]
        [InlineData(2999, "notApplicable")]
        [InlineData(0, null)]
        [InlineData(999, null)]
        [InlineData(3001, null)]
        [InlineData(4000, null)]
        public void ClassifyStatusServiceStatus_bands(int status, string? expected)
            => Assert.Equal(expected, ImeRegistryAppStateObserver.ClassifyStatusServiceStatus(status));

        [Fact]
        public void TerminalOutcome_prefers_enforcement_state_then_status_service()
        {
            // Enforcement state alone decides when terminal.
            Assert.Equal("success", ImeRegistryAppStateObserver.TerminalOutcome(Entry(enforcementState: 1000)));
            Assert.Equal("error", ImeRegistryAppStateObserver.TerminalOutcome(Entry(enforcementState: 5003)));
            // Non-terminal enforcement bands defer to the StatusService pillar.
            Assert.Null(ImeRegistryAppStateObserver.TerminalOutcome(Entry(enforcementState: 2009)));
            Assert.Equal("error", ImeRegistryAppStateObserver.TerminalOutcome(
                Entry(enforcementState: 2009, statusServiceStatus: 3000)));
            Assert.Equal("success", ImeRegistryAppStateObserver.TerminalOutcome(Entry(statusServiceStatus: 1000)));
            Assert.Equal("error", ImeRegistryAppStateObserver.TerminalOutcome(Entry(statusServiceStatus: 3000)));
            // requirementsNotMet / notApplicable / empty entries are not judgeable.
            Assert.Null(ImeRegistryAppStateObserver.TerminalOutcome(Entry(enforcementState: 3000)));
            Assert.Null(ImeRegistryAppStateObserver.TerminalOutcome(Entry(statusServiceStatus: 2500)));
            Assert.Null(ImeRegistryAppStateObserver.TerminalOutcome(Entry()));
        }

        [Fact]
        public void ChangedFields_reports_each_field_and_ignores_espPhase_casing()
        {
            var prev = Entry(enforcementState: 2009, errorCode: null, exitCode: null,
                statusServiceStatus: null, espTracked: false, espPhase: "DevicePreparation");
            var next = Entry(enforcementState: 1000, errorCode: 0, exitCode: 0,
                statusServiceStatus: 1000, espTracked: true, espPhase: "devicepreparation");

            var changed = ImeRegistryAppStateObserver.ChangedFields(prev, next);

            Assert.Equal(
                new[] { "enforcementState", "errorCode", "exitCode", "statusServiceStatus", "espTracked" },
                changed);
            // Case-only espPhase difference is not a change; identity diffs to empty.
            Assert.DoesNotContain("espPhase", changed);
            Assert.Empty(ImeRegistryAppStateObserver.ChangedFields(next, next));
        }

        [Fact]
        public void ChangedFields_null_previous_reports_populated_fields_of_new_entry()
        {
            var fresh = Entry(enforcementState: 2000, espTracked: true, espPhase: "AccountSetup");

            var changed = ImeRegistryAppStateObserver.ChangedFields(null, fresh);

            Assert.Contains("enforcementState", changed);
            Assert.Contains("espTracked", changed);
            Assert.Contains("espPhase", changed);
            // Fields that are null on both sides are not "changes" for a brand-new entry.
            Assert.DoesNotContain("errorCode", changed);
            Assert.DoesNotContain("exitCode", changed);
            Assert.DoesNotContain("statusServiceStatus", changed);
        }

        [Fact]
        public void DiffSnapshots_reports_only_changed_fields()
        {
            var prev = Snapshot((App1, 2009, null));
            var next = Snapshot((App1, 1000, 0), (App2, 2000, null));

            var changes = ImeRegistryAppStateObserver.DiffSnapshots(prev, next);

            Assert.Equal(2, changes.Count);
            var app1Change = changes.Single(c => c.Entry.AppId == App1);
            Assert.Contains("enforcementState", app1Change.ChangedFields);
            Assert.Contains("errorCode", app1Change.ChangedFields);
            Assert.False(app1Change.IsNew);
            Assert.True(changes.Single(c => c.Entry.AppId == App2).IsNew);

            Assert.Empty(ImeRegistryAppStateObserver.DiffSnapshots(next, next));
        }

        [Fact]
        public void IsDivergent_rules()
        {
            var installed = PackageState(App1, AppInstallationState.Installed);
            var errored = PackageState(App1, AppInstallationState.Error);

            Assert.True(ImeRegistryAppStateObserver.IsDivergent("error", installed, true, out var r1));
            Assert.Equal("registry_error_log_installed", r1);
            Assert.True(ImeRegistryAppStateObserver.IsDivergent("success", errored, true, out var r2));
            Assert.Equal("registry_success_log_error", r2);
            Assert.True(ImeRegistryAppStateObserver.IsDivergent("success", null, true, out var r3));
            Assert.Equal("app_unknown_to_log_tracking", r3);
            // Tracker idle (no apps at all) — not judgeable, no false alarm.
            Assert.False(ImeRegistryAppStateObserver.IsDivergent("success", null, false, out _));
            // Agreement.
            Assert.False(ImeRegistryAppStateObserver.IsDivergent("success", installed, true, out _));
            Assert.False(ImeRegistryAppStateObserver.IsDivergent("error", errored, true, out _));
        }

        // ── tick flow ───────────────────────────────────────────────────────────

        [Fact]
        public void Baseline_is_silent_then_changes_emit_state_events()
        {
            var (sink, clock, observer) = CreateObserver(trackerApps: null);
            var current = Snapshot((App1, 2000, null));

            using (new ImeRegistryAppStateObserver.ScopedSnapshotOverride(() => current))
            {
                observer.Tick("baseline");
                Assert.Empty(Events(sink, SharedEventTypes.RegistryAppState));

                current = Snapshot((App1, 1000, 0));
                observer.Tick("registry_change");
            }

            var evt = Assert.Single(Events(sink, SharedEventTypes.RegistryAppState));
            Assert.Equal(App1, evt.Payload!["appId"]);
            Assert.Equal("1000", evt.Payload["enforcementState"]);
            Assert.Equal("success", evt.Payload["enforcementClass"]);
        }

        [Fact]
        public void NonEmpty_baseline_emits_one_summary_with_counts()
        {
            var (sink, clock, observer) = CreateObserver(trackerApps: null);
            var current = Snapshot((App1, 1000, 0), (App2, 5003, 101));

            using (new ImeRegistryAppStateObserver.ScopedSnapshotOverride(() => current))
            {
                observer.Tick("baseline");
                observer.Tick("periodic"); // summary is one-shot, not per tick
            }

            var summary = Assert.Single(Events(sink, SharedEventTypes.RegistryAppBaseline));
            Assert.Equal("2", summary.Payload!["totalApps"]);
            Assert.Equal("1", summary.Payload["successCount"]);
            Assert.Equal("1", summary.Payload["errorCount"]);
            // The baseline stays silent on the diff rail.
            Assert.Empty(Events(sink, SharedEventTypes.RegistryAppState));
        }

        [Fact]
        public void Empty_baseline_emits_no_summary()
        {
            var (sink, clock, observer) = CreateObserver(trackerApps: null);
            var current = new ImeRegistrySnapshot();

            using (new ImeRegistryAppStateObserver.ScopedSnapshotOverride(() => current))
            {
                observer.Tick("baseline");
            }

            Assert.Empty(Events(sink, SharedEventTypes.RegistryAppBaseline));
        }

        [Fact]
        public void Unchanged_snapshot_emits_nothing()
        {
            var (sink, clock, observer) = CreateObserver(trackerApps: null);
            var current = Snapshot((App1, 2000, null));

            using (new ImeRegistryAppStateObserver.ScopedSnapshotOverride(() => current))
            {
                observer.Tick("baseline");
                observer.Tick("periodic");
                observer.Tick("periodic");
            }

            Assert.Empty(Events(sink, SharedEventTypes.RegistryAppState));
        }

        [Fact]
        public void Reconciliation_fires_once_after_settle_delay_on_divergence()
        {
            var tracker = new List<AppPackageState> { PackageState(App1, AppInstallationState.Installed) };
            var (sink, clock, observer) = CreateObserver(trackerApps: tracker);
            var current = Snapshot((App1, 2000, null));

            using (new ImeRegistryAppStateObserver.ScopedSnapshotOverride(() => current))
            {
                observer.Tick("baseline");
                current = Snapshot((App1, 5003, 101)); // registry error, log says Installed
                observer.Tick("registry_change");

                // Before the settle delay: no reconciliation yet.
                Assert.Empty(Events(sink, SharedEventTypes.AppStateReconciliation));

                clock.Advance(ImeRegistryAppStateObserver.ReconcileSettleDelay + TimeSpan.FromSeconds(1));
                observer.Tick("periodic");
                observer.Tick("periodic"); // second pass must not re-emit
            }

            var rec = Assert.Single(Events(sink, SharedEventTypes.AppStateReconciliation));
            Assert.Equal("error", rec.Payload!["registryOutcome"]);
            Assert.Equal("registry_error_log_installed", rec.Payload["reason"]);
            Assert.Equal("Installed", rec.Payload["logState"]);
        }

        [Fact]
        public void Reconciliation_skips_agreeing_and_baseline_only_apps()
        {
            var tracker = new List<AppPackageState> { PackageState(App1, AppInstallationState.Installed) };
            var (sink, clock, observer) = CreateObserver(trackerApps: tracker);

            // App2 is terminal in the BASELINE (pre-existing from an earlier enrollment) and
            // never changes — it must never be judged. App1 changes and agrees with the log.
            var current = Snapshot((App1, 2000, null), (App2, 5000, 1));
            using (new ImeRegistryAppStateObserver.ScopedSnapshotOverride(() => current))
            {
                observer.Tick("baseline");
                current = Snapshot((App1, 1000, 0), (App2, 5000, 1));
                observer.Tick("registry_change");

                clock.Advance(ImeRegistryAppStateObserver.ReconcileSettleDelay + TimeSpan.FromSeconds(1));
                observer.Tick("periodic");
            }

            Assert.Empty(Events(sink, SharedEventTypes.AppStateReconciliation));
        }

        [Fact]
        public void State_event_cap_emits_single_warning_then_suppresses()
        {
            var (sink, clock, observer) = CreateObserver(trackerApps: null);

            var baseline = new ImeRegistrySnapshot();
            var flooded = new ImeRegistrySnapshot();
            for (var i = 0; i < ImeRegistryAppStateObserver.MaxStateEventsPerSession + 25; i++)
            {
                var appId = Guid.NewGuid().ToString("D");
                baseline.GetOrAdd(ImeRegistrySnapshot.DeviceContext, appId).EnforcementState = 2000;
                flooded.GetOrAdd(ImeRegistrySnapshot.DeviceContext, appId).EnforcementState = 1000;
            }

            var current = baseline;
            using (new ImeRegistryAppStateObserver.ScopedSnapshotOverride(() => current))
            {
                observer.Tick("baseline");
                current = flooded;
                observer.Tick("registry_change");
            }

            var events = Events(sink, SharedEventTypes.RegistryAppState);
            // Cap payload-carrying events + exactly one cap-notice Warning.
            Assert.Equal(ImeRegistryAppStateObserver.MaxStateEventsPerSession + 1, events.Count);
            Assert.Single(events, e => e.Payload != null
                && e.Payload.TryGetValue(SignalPayloadKeys.Message, out var m)
                && m.Contains("cap reached"));
        }

        // ── harness ─────────────────────────────────────────────────────────────

        private static (FakeSignalIngressSink sink, VirtualClock clock, ImeRegistryAppStateObserver observer)
            CreateObserver(IReadOnlyList<AppPackageState>? trackerApps)
        {
            var sink = new FakeSignalIngressSink();
            var clock = new VirtualClock(T0);
            var post = new InformationalEventPost(sink, clock);
            var observer = new ImeRegistryAppStateObserver(
                post,
                logger: null,
                clock: clock,
                trackerStateProbe: trackerApps == null ? null : () => trackerApps);
            return (sink, clock, observer);
        }

        private static AppRegistryEntry Entry(
            int? enforcementState = null, long? errorCode = null, int? exitCode = null,
            int? statusServiceStatus = null, bool espTracked = false, string? espPhase = null)
        {
            var entry = new AppRegistryEntry($"{ImeRegistrySnapshot.DeviceContext}|{App1}", ImeRegistrySnapshot.DeviceContext, App1);
            entry.EnforcementState = enforcementState;
            entry.ErrorCode = errorCode;
            entry.ExitCode = exitCode;
            entry.StatusServiceStatus = statusServiceStatus;
            entry.EspTracked = espTracked;
            entry.EspPhase = espPhase;
            return entry;
        }

        private static ImeRegistrySnapshot Snapshot(params (string appId, int enforcementState, long? errorCode)[] apps)
        {
            var snapshot = new ImeRegistrySnapshot();
            foreach (var (appId, state, error) in apps)
            {
                var entry = snapshot.GetOrAdd(ImeRegistrySnapshot.DeviceContext, appId);
                entry.EnforcementState = state;
                entry.ErrorCode = error;
            }
            return snapshot;
        }

        private static AppPackageState PackageState(string id, AppInstallationState state)
        {
            var pkg = new AppPackageState(id, listPos: 0);
            if (state == AppInstallationState.Installed)
            {
                // Route through Installing first — a bare Installed would trip the
                // inverse-detection auto-downgrade heuristic (Installed w/o activity -> Skipped).
                pkg.UpdateState(AppInstallationState.Installing);
            }
            pkg.UpdateState(state);
            return pkg;
        }

        private static IReadOnlyList<FakeSignalIngressSink.PostedSignal> Events(FakeSignalIngressSink sink, string eventType) =>
            sink.Posted.Where(p =>
                p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == eventType).ToList();
    }
}
