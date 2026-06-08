#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Office;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Office
{
    /// <summary>
    /// OfficeInstallDetector (Rev 2, event-driven) — the pure decision core driven via its On*
    /// entry points with synthetic snapshots (no real WMI / RegNotify / registry). Asserts: events
    /// only after a worker start, a single started, progress on real change only (no heartbeat),
    /// DO-sample folding (download-%), and completed / failed terminal resolution with version + duration.
    /// </summary>
    public sealed class OfficeInstallDetectorTests
    {
        private static readonly DateTime At = new DateTime(2026, 6, 8, 12, 0, 0, DateTimeKind.Utc);

        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        private sealed class Rig : IDisposable
        {
            public FakeSignalIngressSink Sink { get; }
            public VirtualClock Clock { get; }
            public OfficeInstallDetector Sut { get; }
            public OfficeC2RSnapshot Current { get; set; }
            private readonly TempDirectory _tmp;

            public Rig(OfficeC2RSnapshot initial)
            {
                _tmp = new TempDirectory();
                Sink = new FakeSignalIngressSink();
                Clock = new VirtualClock(At);
                Current = initial;
                var post = new InformationalEventPost(Sink, Clock);
                Sut = new OfficeInstallDetector("S1", "T1", post, NewLogger(_tmp.Path), Clock)
                {
                    SnapshotProvider = () => Current,
                };
            }

            public List<FakeSignalIngressSink.PostedSignal> OfficeEvents() => Sink.Posted
                .Where(p => p.Payload != null
                    && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                    && et != null && et.StartsWith("office_install_", StringComparison.Ordinal))
                .ToList();

            public void Dispose() => _tmp.Dispose();
        }

        private static OfficeC2RSnapshot ActiveStreaming()
        {
            var s = new OfficeC2RSnapshot
            {
                ConfigurationKeyPresent = true,
                Channel = "Current",
                Platform = "x64",
                StreamingFinished = false,
                ActiveScenarioPresent = true,
                ActiveScenarioName = "INSTALL",
                OfficeC2RClientRunning = true,
            };
            s.Products.Add("O365ProPlusRetail");
            return s;
        }

        private static OfficeC2RSnapshot CompletedIdle(string version = "16.0.17628.20144")
        {
            var s = new OfficeC2RSnapshot
            {
                ConfigurationKeyPresent = true,
                Channel = "Current",
                Platform = "x64",
                StreamingFinished = true,
                ActiveScenarioPresent = false,
                ActiveScenarioName = null,
                OfficeC2RClientRunning = false,
                VersionToReport = version,
            };
            s.Products.Add("O365ProPlusRetail");
            return s;
        }

        private static string EventType(FakeSignalIngressSink.PostedSignal p) => p.Payload![SignalPayloadKeys.EventType];
        private static string Severity(FakeSignalIngressSink.PostedSignal p) => p.Payload![SignalPayloadKeys.Severity];
        private static Dictionary<string, object> Data(FakeSignalIngressSink.PostedSignal p)
            => Assert.IsType<Dictionary<string, object>>(p.TypedPayload);

        // ---------------------------------------------------------------- idle guard

        [Fact]
        public void Events_only_after_worker_started()
        {
            using var rig = new Rig(CompletedIdle());

            // No worker-start signal yet → registry/DO/stop are no-ops.
            rig.Sut.OnRegistryChanged();
            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 1, FileSize = 100, TotalBytesDownloaded = 50 });
            rig.Sut.OnWorkerStopped();

            Assert.Empty(rig.OfficeEvents());
        }

        // ---------------------------------------------------------------- started

        [Fact]
        public void Worker_started_emits_exactly_one_started_with_products_and_phase()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted();

            var started = Assert.Single(rig.OfficeEvents());
            Assert.Equal(Constants.EventTypes.OfficeInstallStarted, EventType(started));
            Assert.Equal("Info", Severity(started));

            var data = Data(started);
            Assert.Equal("Streaming", data["phase"]);
            Assert.Equal("INSTALL", data["scenario"]);
            Assert.Contains("O365ProPlusRetail", (List<string>)data["products"]);
            Assert.Equal("Current", data["channel"]);
            Assert.Equal("x64", data["platform"]);
        }

        [Fact]
        public void Second_worker_started_is_ignored_one_lifecycle()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted();
            rig.Sut.OnWorkerStarted(); // already Active — must not emit a second started

            Assert.Single(rig.OfficeEvents());
        }

        // ---------------------------------------------------------------- progress (no heartbeat)

        [Fact]
        public void Registry_change_without_real_delta_emits_no_progress_heartbeat()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted(); // started
            rig.Sut.OnRegistryChanged(); // identical snapshot — must NOT emit progress
            rig.Sut.OnRegistryChanged();

            var events = rig.OfficeEvents();
            Assert.Single(events);
            Assert.Equal(Constants.EventTypes.OfficeInstallStarted, EventType(events[0]));
        }

        [Fact]
        public void Scenario_value_change_emits_one_progress_event()
        {
            var s1 = ActiveStreaming();
            s1.ScenarioValues["INSTALL\\CurrentOperation"] = "Downloading";
            using var rig = new Rig(s1);

            rig.Sut.OnWorkerStarted(); // started

            var s2 = ActiveStreaming();
            s2.ScenarioValues["INSTALL\\CurrentOperation"] = "Applying"; // changed value
            rig.Current = s2;
            rig.Sut.OnRegistryChanged(); // progress

            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count);
            Assert.Equal(Constants.EventTypes.OfficeInstallProgress, EventType(events[1]));
        }

        // ---------------------------------------------------------------- DO folding

        [Fact]
        public void Office_do_sample_folds_download_percent_into_progress()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted(); // started

            rig.Sut.OnOfficeDoSample(new OfficeDoSample
            {
                JobCount = 2,
                FileSize = 1000,
                TotalBytesDownloaded = 250,
                BytesFromPeers = 100,
                BytesFromHttp = 150,
                PercentPeerCaching = 40,
                DownloadMode = 1,
            });

            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count);
            var progress = events[1];
            Assert.Equal(Constants.EventTypes.OfficeInstallProgress, EventType(progress));

            var data = Data(progress);
            Assert.Equal(250L, data["doTotalBytesDownloaded"]);
            Assert.Equal(25, data["downloadPercent"]);
            Assert.Equal(2, data["doJobCount"]);
        }

        [Fact]
        public void Repeated_identical_do_sample_emits_no_progress()
        {
            using var rig = new Rig(ActiveStreaming());
            rig.Sut.OnWorkerStarted();

            var sample = new OfficeDoSample { JobCount = 1, FileSize = 1000, TotalBytesDownloaded = 250 };
            rig.Sut.OnOfficeDoSample(sample);          // progress (bytes advanced)
            rig.Sut.OnOfficeDoSample(new OfficeDoSample { JobCount = 1, FileSize = 1000, TotalBytesDownloaded = 250 }); // same bytes — no progress

            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count); // started + one progress only
        }

        // ---------------------------------------------------------------- completed

        [Fact]
        public void Worker_stopped_with_streaming_finished_emits_completed_with_version_and_duration()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted(); // started
            rig.Clock.Advance(TimeSpan.FromSeconds(412));
            rig.Current = CompletedIdle("16.0.17628.20144");
            rig.Sut.OnWorkerStopped(); // completed

            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count);
            var completed = events[1];
            Assert.Equal(Constants.EventTypes.OfficeInstallCompleted, EventType(completed));
            Assert.Equal("Info", Severity(completed));

            var data = Data(completed);
            Assert.Equal("Completed", data["phase"]);
            Assert.Equal("16.0.17628.20144", data["versionReached"]);
            Assert.Equal(412, data["durationSeconds"]);
        }

        [Fact]
        public void Terminal_state_latches_no_further_events()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted();
            rig.Current = CompletedIdle();
            rig.Sut.OnWorkerStopped(); // completed
            rig.Current = ActiveStreaming();
            rig.Sut.OnWorkerStarted(); // must NOT re-trigger (one lifecycle)
            rig.Sut.OnRegistryChanged();

            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count);
            Assert.Equal(Constants.EventTypes.OfficeInstallCompleted, EventType(events[1]));
        }

        // ---------------------------------------------------------------- failed

        [Fact]
        public void Worker_stopped_without_streaming_finished_emits_failed_warning()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted();

            rig.Current = new OfficeC2RSnapshot
            {
                ConfigurationKeyPresent = true,
                StreamingFinished = false,
                ActiveScenarioPresent = false,
                OfficeC2RClientRunning = false,
            };
            rig.Sut.OnWorkerStopped(); // failed (abandoned)

            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count);
            Assert.Equal(Constants.EventTypes.OfficeInstallFailed, EventType(events[1]));
            Assert.Equal("Warning", Severity(events[1]));
        }

        [Fact]
        public void Observed_error_code_during_progress_emits_failed_error_with_code()
        {
            using var rig = new Rig(ActiveStreaming());

            rig.Sut.OnWorkerStarted();

            var withError = ActiveStreaming();
            withError.ErrorCode = "0x80070005";
            rig.Current = withError;
            rig.Sut.OnRegistryChanged(); // failed (error code)

            var events = rig.OfficeEvents();
            Assert.Equal(2, events.Count);
            var failed = events[1];
            Assert.Equal(Constants.EventTypes.OfficeInstallFailed, EventType(failed));
            Assert.Equal("Error", Severity(failed));
            Assert.Equal("0x80070005", Data(failed)["errorCode"]);
        }

        // ---------------------------------------------------------------- error-code heuristic

        [Theory]
        [InlineData("0x80070005", true)]   // hex HRESULT
        [InlineData("17002", true)]        // decimal C2R exit code
        [InlineData("-2147024891", true)]  // negative decimal HRESULT
        [InlineData("0x0", false)]         // zero hex
        [InlineData("0", false)]           // zero decimal
        [InlineData("Success", false)]     // benign textual value (e.g. Result=Success)
        [InlineData("Completed", false)]
        [InlineData("InProgress", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsNonZeroNumericCode_only_treats_nonzero_numbers_as_errors(string? value, bool expected)
        {
            Assert.Equal(expected, OfficeInstallDetector.IsNonZeroNumericCode(value!));
        }

        // ---------------------------------------------------------------- ctor

        [Fact]
        public void Ctor_rejects_null_dependencies()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var clock = new VirtualClock(At);
            var post = new InformationalEventPost(new FakeSignalIngressSink(), clock);

            Assert.Throws<ArgumentNullException>(() => new OfficeInstallDetector(null!, "T1", post, logger, clock));
            Assert.Throws<ArgumentNullException>(() => new OfficeInstallDetector("S1", "T1", post, logger, null!));
        }
    }
}
