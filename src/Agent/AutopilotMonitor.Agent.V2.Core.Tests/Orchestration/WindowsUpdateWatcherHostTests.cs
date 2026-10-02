#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.DecisionCore.State;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// WindowsUpdateWatcherHost (D-310) — routes every configured WU EventID to the channel the
    /// provider manifest writes it to, reports counted activity once, and feeds Windows/.NET update
    /// steps to the decision engine unless <c>OobeUpdatePhaseMode</c> is Off. The event-log
    /// watchers themselves are not armed here; the trackers' ProcessEvent seams drive the host.
    /// </summary>
    public sealed class WindowsUpdateWatcherHostTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 10, 1, 9, 30, 0, DateTimeKind.Utc);
        private const string Lcu = "2026-09 Cumulative Update for Windows 11, version 25H2 for x64-based Systems (KB5099999)";

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private long _nextRecordId = 1;

        public void Dispose() => _tmp.Dispose();

        private WindowsUpdateWatcherHost Build(bool osUpdateSignalsEnabled, bool servicingWatcherEnabled = true) =>
            new WindowsUpdateWatcherHost(
                sessionId: "sess-wuhost",
                tenantId: "tenant-wuhost",
                logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                ingress: _sink,
                clock: new VirtualClock(At),
                targetedEventIds: new[] { 19, 20, 43, 44 },
                backfillLookbackMinutes: 0,
                stateDirectory: _tmp.Path,
                operationalEventIds: new[] { 25, 26, 31, 41 },
                servicingWatcherEnabled: servicingWatcherEnabled,
                osUpdateSignalsEnabled: osUpdateSignalsEnabled);

        private void Wu(WindowsUpdateTracker tracker, int eventId, string title, DateTime at) =>
            tracker.ProcessEvent(eventId, 4, _nextRecordId++, at, title, updateGuid: null, updateRevisionNumber: null,
                errorCode: null, formattedDescription: null, isBackfill: false);

        private void Cbs(ServicingTracker tracker, int eventId, string client, DateTime at) =>
            tracker.ProcessEvent(eventId, _nextRecordId++, at, "KB5099999", initialState: null, targetState: "Installed",
                errorCode: "0x0", client: client, isBackfill: false);

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> OsUpdateActivity() =>
            _sink.Posted.Where(p => p.Kind == DecisionSignalKind.OsUpdateActivity).ToList();

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> ByType(string eventType) =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == eventType).ToList();

        // ---------------------------------------------------------------- routing ----

        [Fact]
        public void RouteEventIds_Defaults_SplitByManifest()
        {
            var (system, operational) = WindowsUpdateWatcherHost.RouteEventIds(new[] { 19, 20, 43, 44 }, new[] { 25, 26, 31, 41 });

            Assert.Equal(new[] { 19, 20, 43, 44 }, system);
            Assert.Equal(new[] { 25, 26, 31, 41 }, operational);
        }

        [Fact]
        public void RouteEventIds_MisplacedIds_EndUpOnTheirRealChannel()
        {
            // An old config that lists 41 under the targeted IDs, or 20 under the operational
            // ones, must still watch the channel that carries them.
            var (system, operational) = WindowsUpdateWatcherHost.RouteEventIds(new[] { 41, 19 }, new[] { 20, 41 });

            Assert.Equal(new[] { 19, 20 }, system);
            Assert.Equal(new[] { 41 }, operational);
        }

        [Fact]
        public void RouteEventIds_NothingConfigured_WatchesNothing()
        {
            var (system, operational) = WindowsUpdateWatcherHost.RouteEventIds(null, Array.Empty<int>());

            Assert.Empty(system);
            Assert.Empty(operational);
        }

        [Fact]
        public void Host_BuildsOneTrackerPerChannel_AndTheServicingTracker()
        {
            var host = Build(osUpdateSignalsEnabled: false);

            Assert.Equal(WindowsUpdateTracker.SystemChannel, host.SystemTrackerForTest!.WatchedChannel);
            Assert.Equal(new[] { 19, 20, 43, 44 }, host.SystemTrackerForTest.TargetedEventIds.OrderBy(id => id).ToArray());
            Assert.Equal(WindowsUpdateTracker.Channel, host.OperationalTrackerForTest!.WatchedChannel);
            Assert.Equal(new[] { 25, 26, 31, 41 }, host.OperationalTrackerForTest.TargetedEventIds.OrderBy(id => id).ToArray());
            Assert.NotNull(host.ServicingTrackerForTest);
        }

        [Fact]
        public void ServicingWatcherDisabled_BuildsNoServicingTracker()
        {
            Assert.Null(Build(osUpdateSignalsEnabled: false, servicingWatcherEnabled: false).ServicingTrackerForTest);
        }

        // ------------------------------------------------- engine signal behind flag ----

        [Fact]
        public void OsUpdateSignalsDisabled_ListsTheUpdate_ButPostsNoEngineSignal()
        {
            var host = Build(osUpdateSignalsEnabled: false);

            Wu(host.SystemTrackerForTest!, WindowsUpdateTracker.EventId_InstallStarted, Lcu, At);
            Cbs(host.ServicingTrackerForTest!, ServicingTracker.EventId_RebootRequired, "UpdateAgentLCU", At.AddMinutes(20));

            Assert.Single(ByType(Constants.EventTypes.WindowsUpdateStarted));
            Assert.Single(ByType(Constants.EventTypes.WindowsUpdateServicing));
            Assert.Empty(OsUpdateActivity());
        }

        [Fact]
        public void OsUpdateSignalsEnabled_PostsOsUpdateActivity_FromBothSources()
        {
            var host = Build(osUpdateSignalsEnabled: true);
            var wuAt = DateTime.SpecifyKind(At, DateTimeKind.Unspecified);

            Wu(host.SystemTrackerForTest!, WindowsUpdateTracker.EventId_InstallStarted, Lcu, wuAt);
            Cbs(host.ServicingTrackerForTest!, ServicingTracker.EventId_RebootRequired, "UpdateAgentLCU", At.AddMinutes(20));

            var signals = OsUpdateActivity();
            Assert.Equal(2, signals.Count);

            var wu = signals[0];
            Assert.Equal(OsUpdateSteps.InstallStarted, wu.Payload![OsUpdatePayloadKeys.Step]);
            Assert.Equal("wu", wu.Payload![OsUpdatePayloadKeys.Source]);
            Assert.Equal(Lcu, wu.Payload![OsUpdatePayloadKeys.Update]);
            Assert.Equal("WindowsUpdateWatcher", wu.SourceOrigin);
            Assert.Equal(EvidenceKind.Raw, wu.Evidence.Kind);
            Assert.Equal("os-update-activity:wu", wu.Evidence.Identifier);
            Assert.Equal(At, wu.OccurredAtUtc);
            Assert.Equal(DateTimeKind.Utc, wu.OccurredAtUtc.Kind);
            Assert.Equal(1, wu.KindSchemaVersion);

            var cbs = signals[1];
            Assert.Equal(OsUpdateSteps.RebootRequired, cbs.Payload![OsUpdatePayloadKeys.Step]);
            Assert.Equal("servicing", cbs.Payload![OsUpdatePayloadKeys.Source]);
            Assert.Equal("KB5099999", cbs.Payload![OsUpdatePayloadKeys.Update]);
            Assert.Equal("ServicingWatcher", cbs.SourceOrigin);
            Assert.Equal("os-update-activity:servicing", cbs.Evidence.Identifier);
            Assert.Equal(At.AddMinutes(20), cbs.OccurredAtUtc);
        }

        [Fact]
        public void OsUpdateSignalsEnabled_NonOsActivity_NeverReachesTheEngine()
        {
            var host = Build(osUpdateSignalsEnabled: true);

            Wu(host.SystemTrackerForTest!, WindowsUpdateTracker.EventId_InstallSuccess, "9NSTH9KHZDLQ-Microsoft.UI.Xaml.2.8", At);
            Wu(host.OperationalTrackerForTest!, WindowsUpdateTracker.EventId_Downloaded,
                "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.459.505.0) - Current Channel (Broad)", At);
            Cbs(host.ServicingTrackerForTest!, ServicingTracker.EventId_StateReached, "CbsTask", At);

            Assert.Empty(OsUpdateActivity());
            Assert.Empty(_sink.Posted);
        }

        // ------------------------------------------------------- activity summary ----

        [Fact]
        public void ActivitySummary_IsEmittedOnce_WhenTheHostStops()
        {
            var host = Build(osUpdateSignalsEnabled: false);
            Wu(host.SystemTrackerForTest!, WindowsUpdateTracker.EventId_InstallSuccess, "9NSTH9KHZDLQ-Microsoft.UI.Xaml.2.8", At);
            Wu(host.SystemTrackerForTest!, WindowsUpdateTracker.EventId_InstallSuccess, "9NBLGGH4NNS1-Microsoft.DesktopAppInstaller", At);
            Cbs(host.ServicingTrackerForTest!, ServicingTracker.EventId_InitiatingChanges, "CbsTask", At);

            // The termination path stops the host explicitly and disposes it afterwards.
            host.Stop();
            host.Dispose();

            var summary = Assert.Single(ByType(Constants.EventTypes.WindowsUpdateActivitySummary));
            Assert.Equal("Debug", summary.Payload![SignalPayloadKeys.Severity]);
            Assert.Contains("3 event(s)", summary.Payload![SignalPayloadKeys.Message]);
            var data = (IReadOnlyDictionary<string, object>)summary.TypedPayload!;
            Assert.Equal(2, data["store_19"]);
            Assert.Equal(1, data["servicing_other_1"]);
        }

        [Fact]
        public void ActivitySummary_NothingCounted_EmitsNothing()
        {
            var host = Build(osUpdateSignalsEnabled: false);
            Wu(host.SystemTrackerForTest!, WindowsUpdateTracker.EventId_InstallSuccess, Lcu, At);

            host.Stop();

            Assert.Empty(ByType(Constants.EventTypes.WindowsUpdateActivitySummary));
        }
    }
}
