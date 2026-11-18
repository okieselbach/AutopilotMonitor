#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared.Models;
using Xunit;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// D-444: the one sender of download_progress — DO and IME samples in, one stream per download
    /// out, held samples flushed at the download's end, one notice per slower stage.
    /// </summary>
    public sealed class DownloadProgressPublisherTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
        private const long MiB = 1_048_576;

        private sealed class Harness
        {
            public FakeSignalIngressSink Ingress { get; } = new FakeSignalIngressSink();
            public VirtualClock Clock { get; } = new VirtualClock(T0);
            public DownloadProgressBudget Budget { get; } = new DownloadProgressBudget();
            public DownloadProgressPublisher Publisher { get; }

            public Harness(int eventsSent = 0)
            {
                if (eventsSent > 0)
                    Budget.Restore(new DownloadProgressBudgetState { EventsSent = eventsSent, Stage = DownloadProgressGate.StageFor(eventsSent) });
                Publisher = new DownloadProgressPublisher(new InformationalEventPost(Ingress, Clock), Clock, Budget);
            }

            public List<FakeSignalIngressSink.PostedSignal> Events(string eventType) =>
                Ingress.Posted
                    .Where(p => p.Kind == DecisionSignalKind.InformationalEvent
                                && p.Payload != null
                                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var t)
                                && t == eventType)
                    .ToList();

            public List<FakeSignalIngressSink.PostedSignal> All() =>
                Ingress.Posted.Where(p => p.Kind == DecisionSignalKind.InformationalEvent).ToList();
        }

        private static AppPackageState MakeApp(string id = "app-1", AppInstallationState state = AppInstallationState.Downloading,
            long bytes = 0, long total = 0) =>
            AppPackageState.Restore(
                id: id, listPos: 0, name: "Contoso Tool",
                runAs: AppRunAs.System, intent: AppIntent.Install, targeted: AppTargeted.Device,
                dependsOn: new HashSet<string>(),
                installationState: state, downloadingOrInstallingSeen: true,
                progressPercent: 0, bytesDownloaded: bytes, bytesTotal: total,
                appType: "Win32");

        private static DoProgressSample Do(long bytes, long total, int mode = 1, string? cacheHost = null) =>
            new DoProgressSample(bytes, total, 0, bytes, 0, mode, 0, 0, 0, 0, 0, cacheHost);

        private static IReadOnlyDictionary<string, object> Typed(FakeSignalIngressSink.PostedSignal s) =>
            Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(s.TypedPayload);

        private static long BytesOf(FakeSignalIngressSink.PostedSignal s) =>
            s.TypedPayload is IReadOnlyDictionary<string, object> typed
                ? Convert.ToInt64(typed["bytesDownloaded"])
                : long.Parse(s.Payload!["bytesDownloaded"]);

        [Fact]
        public void A_DO_sample_becomes_one_event_with_the_DO_values_and_the_app_state()
        {
            var h = new Harness();
            var app = MakeApp();

            h.Publisher.OfferDoSample(app, Do(5 * MiB, 100 * MiB));

            var ev = Assert.Single(h.Events(SharedEventTypes.DownloadProgress));
            Assert.Equal("DeliveryOptimizationCollector", ev.Payload![SignalPayloadKeys.Source]);
            Assert.Equal(EventSeverity.Debug.ToString(), ev.Payload[SignalPayloadKeys.Severity]);
            Assert.Equal("true", ev.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("Contoso Tool: 5% (5242880/104857600) peers=0% mode=1", ev.Payload[SignalPayloadKeys.Message]);
            Assert.Equal(T0, ev.OccurredAtUtc);

            var data = Typed(ev);
            Assert.Equal(5 * MiB, Convert.ToInt64(data["bytesDownloaded"]));
            Assert.Equal(100 * MiB, Convert.ToInt64(data["bytesTotal"]));
            Assert.Equal(1, Convert.ToInt32(data["doDownloadMode"]));
            Assert.Equal("os_cmdlet", data["doSource"]);
            Assert.Equal("Downloading", data["state"]);
            Assert.Equal("Install", data["intent"]);
            Assert.Equal("Device", data["targeted"]);
            Assert.Equal("Win32", data["appType"]);
            Assert.False(data.ContainsKey("doCacheHost"));
        }

        [Fact]
        public void An_IME_line_is_dropped_while_DO_reports_and_takes_over_with_the_last_DO_values_when_DO_is_quiet()
        {
            var h = new Harness();
            var app = MakeApp(total: 100 * MiB);
            h.Publisher.OfferDoSample(app, Do(5 * MiB, 100 * MiB, cacheHost: "http://10.0.0.5/"));

            h.Clock.Advance(TimeSpan.FromSeconds(5));
            app.UpdateState(AppInstallationState.Downloading, 6, bytesDownloaded: 6 * MiB, bytesTotal: 100 * MiB);
            h.Publisher.OfferImeSample(app, ImePayload(app), "Contoso Tool: 6%", h.Clock.UtcNow);
            Assert.Single(h.Events(SharedEventTypes.DownloadProgress));

            h.Clock.Advance(TimeSpan.FromSeconds(11));
            app.UpdateState(AppInstallationState.Downloading, 9, bytesDownloaded: 9 * MiB, bytesTotal: 100 * MiB);
            h.Publisher.OfferImeSample(app, ImePayload(app), "Contoso Tool: 9%", h.Clock.UtcNow);

            var ime = h.Events(SharedEventTypes.DownloadProgress).Last();
            Assert.Equal("ImeLogTracker", ime.Payload![SignalPayloadKeys.Source]);
            Assert.Equal((9 * MiB).ToString(), ime.Payload["bytesDownloaded"]);
            Assert.Equal("1", ime.Payload["doDownloadMode"]);
            Assert.Equal("http://10.0.0.5/", ime.Payload["doCacheHost"]);
        }

        [Fact]
        public void A_sample_held_by_the_interval_is_sent_when_the_download_ends()
        {
            var h = new Harness(eventsSent: 760);
            var app = MakeApp();
            h.Publisher.OfferDoSample(app, Do(1 * MiB, 100 * MiB));
            h.Clock.Advance(TimeSpan.FromSeconds(61));
            h.Publisher.OfferDoSample(app, Do(2 * MiB, 100 * MiB));
            h.Clock.Advance(TimeSpan.FromSeconds(3));
            h.Publisher.OfferDoSample(app, Do(3 * MiB, 100 * MiB));
            Assert.Equal(2, h.Events(SharedEventTypes.DownloadProgress).Count);

            h.Publisher.EndDownload(app.Id);

            var sent = h.Events(SharedEventTypes.DownloadProgress);
            Assert.Equal(3, sent.Count);
            Assert.Equal(3 * MiB, BytesOf(sent.Last()));
        }

        [Fact]
        public void Held_samples_are_sent_when_the_DO_collector_goes_idle()
        {
            var h = new Harness(eventsSent: 760);
            var a = MakeApp("app-a");
            var b = MakeApp("app-b");
            foreach (var app in new[] { a, b })
            {
                h.Publisher.OfferDoSample(app, Do(1 * MiB, 100 * MiB));
            }
            h.Clock.Advance(TimeSpan.FromSeconds(61));
            foreach (var app in new[] { a, b })
            {
                h.Publisher.OfferDoSample(app, Do(2 * MiB, 100 * MiB));
            }
            h.Clock.Advance(TimeSpan.FromSeconds(3));
            foreach (var app in new[] { a, b })
            {
                h.Publisher.OfferDoSample(app, Do(3 * MiB, 100 * MiB));
            }
            Assert.Equal(4, h.Events(SharedEventTypes.DownloadProgress).Count);

            h.Publisher.FlushPending();
            h.Publisher.FlushPending(); // idempotent

            Assert.Equal(6, h.Events(SharedEventTypes.DownloadProgress).Count);
        }

        [Fact]
        public void Crossing_a_stage_sends_one_throttled_notice_after_the_event_that_crossed_it()
        {
            var h = new Harness(eventsSent: 399);
            var app = MakeApp();

            h.Publisher.OfferDoSample(app, Do(1 * MiB, 100 * MiB));
            h.Clock.Advance(TimeSpan.FromSeconds(3));
            h.Publisher.OfferDoSample(app, Do(2 * MiB, 100 * MiB));

            var all = h.All();
            Assert.Equal(SharedEventTypes.DownloadProgress, all[0].Payload![SignalPayloadKeys.EventType]);
            var notice = Assert.Single(h.Events(SharedEventTypes.DownloadProgressThrottled));
            Assert.Same(all[1], notice);
            Assert.Equal("DownloadProgressPublisher", notice.Payload![SignalPayloadKeys.Source]);
            Assert.Equal(EventSeverity.Info.ToString(), notice.Payload[SignalPayloadKeys.Severity]);
            Assert.Equal("true", notice.Payload[SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("1", notice.Payload["stage"]);
            Assert.Equal("10", notice.Payload["intervalSeconds"]);
            Assert.Equal("400", notice.Payload["progressEventsSent"]);
            Assert.Equal("400", notice.Payload["stageThreshold"]);
            Assert.Equal("60", notice.Payload["liveStartSeconds"]);
            Assert.Equal(401, h.Budget.EventsSent);
        }

        [Fact]
        public void The_terminal_tick_is_always_sent_and_counts_toward_the_session()
        {
            var h = new Harness(eventsSent: 759);
            var app = MakeApp(state: AppInstallationState.Installed, bytes: 100, total: 100);
            var payload = ImePayload(app).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            payload["status"] = "completed";

            h.Publisher.EmitTerminalTick(app, payload, "Contoso Tool: completed", T0);

            var tick = Assert.Single(h.Events(SharedEventTypes.DownloadProgress));
            Assert.Equal("completed", tick.Payload!["status"]);
            Assert.Equal("ImeLogTracker", tick.Payload[SignalPayloadKeys.Source]);
            Assert.Equal("3", Assert.Single(h.Events(SharedEventTypes.DownloadProgressThrottled)).Payload!["stage"]);
            Assert.Equal(760, h.Budget.EventsSent);
        }

        [Fact]
        public void A_synthetic_hour_long_download_stays_far_below_the_event_limit()
        {
            // 1,100 MiB at about 350 KB/s: DO every 3 s (1 MiB steps) and the IME line every 5 s,
            // like the field session that hit the limit. Today: one event per DO poll plus one per
            // IME line (about 1,760).
            var h = new Harness();
            var app = MakeApp(total: 1_100 * MiB);
            var start = h.Clock.UtcNow;
            for (var second = 0; second <= 3_300; second++)
            {
                h.Clock.SetUtcNow(start.AddSeconds(second));
                if (second % 3 == 0)
                {
                    var bytes = Math.Min(1_100 * MiB, (second / 3 + 1) * MiB);
                    h.Publisher.OfferDoSample(app, Do(bytes, 1_100 * MiB));
                }
                if (second % 5 == 0)
                {
                    var bytes = Math.Min(1_100 * MiB, (second / 3) * MiB);
                    app.UpdateState(AppInstallationState.Downloading, (int)(bytes * 100 / (1_100 * MiB)), bytesDownloaded: bytes, bytesTotal: 1_100 * MiB);
                    h.Publisher.OfferImeSample(app, ImePayload(app), "Contoso Tool", h.Clock.UtcNow);
                }
            }

            var sent = h.Events(SharedEventTypes.DownloadProgress);
            Assert.DoesNotContain(sent, e => e.Payload![SignalPayloadKeys.Source] == "ImeLogTracker");
            Assert.InRange(sent.Count, 450, 700);
            Assert.Equal(1_100 * MiB, BytesOf(sent.Last()));                       // completion always sent

            var notice = Assert.Single(h.Events(SharedEventTypes.DownloadProgressThrottled));
            Assert.Equal("1", notice.Payload!["stage"]);
            var noticeIndex = h.All().IndexOf(notice);
            Assert.Equal(400, h.All().Take(noticeIndex).Count(e => e.Payload![SignalPayloadKeys.EventType] == SharedEventTypes.DownloadProgress));

            // After the notice: at least 9.5 s between two progress events, except the completion.
            var afterNotice = h.All().Skip(noticeIndex + 1).ToList();
            for (var i = 1; i < afterNotice.Count - 1; i++)
                Assert.True((afterNotice[i].OccurredAtUtc - afterNotice[i - 1].OccurredAtUtc) >= TimeSpan.FromSeconds(9.5));
        }

        [Fact]
        public void Adapter_sends_the_held_progress_before_the_app_leaves_Downloading()
        {
            using var f = new ImeLogTrackerAdapterFixture(T0);
            f.Tracker.DownloadProgressBudget.Restore(new DownloadProgressBudgetState { EventsSent = 760, Stage = 3 });
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            var app = MakeApp(total: 100 * MiB);

            adapter.TriggerAppStateFromTest(app, AppInstallationState.Unknown, AppInstallationState.Downloading);
            f.Clock.Advance(TimeSpan.FromSeconds(1));
            app.UpdateState(AppInstallationState.Downloading, 1, bytesDownloaded: 1 * MiB, bytesTotal: 100 * MiB);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Downloading, AppInstallationState.Downloading);
            f.Clock.Advance(TimeSpan.FromSeconds(61));
            app.UpdateState(AppInstallationState.Downloading, 2, bytesDownloaded: 2 * MiB, bytesTotal: 100 * MiB);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Downloading, AppInstallationState.Downloading);
            f.Clock.Advance(TimeSpan.FromSeconds(3));
            app.UpdateState(AppInstallationState.Downloading, 3, bytesDownloaded: 3 * MiB, bytesTotal: 100 * MiB);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Downloading, AppInstallationState.Downloading); // held
            f.Clock.Advance(TimeSpan.FromSeconds(2));
            app.UpdateState(AppInstallationState.Installing);
            adapter.TriggerAppStateFromTest(app, AppInstallationState.Downloading, AppInstallationState.Installing);

            var types = f.AllInfoEvents()
                .Select(e => e.Payload![SignalPayloadKeys.EventType])
                .Where(t => t == SharedEventTypes.DownloadProgress || t == SharedEventTypes.AppDownloadStarted || t == SharedEventTypes.AppInstallStart)
                .ToList();
            Assert.Equal(new[]
            {
                SharedEventTypes.AppDownloadStarted,
                SharedEventTypes.DownloadProgress,
                SharedEventTypes.DownloadProgress,
                SharedEventTypes.DownloadProgress,     // the held 3 MiB line, flushed first
                SharedEventTypes.AppInstallStart,
            }, types);
            Assert.Equal((3 * MiB).ToString(), f.InfoEvents(SharedEventTypes.DownloadProgress).Last().Payload!["bytesDownloaded"]);
        }

        private static IReadOnlyDictionary<string, string> ImePayload(AppPackageState app) =>
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["appId"] = app.Id,
                ["appName"] = app.Name ?? string.Empty,
                ["state"] = app.InstallationState.ToString(),
                ["bytesDownloaded"] = app.BytesDownloaded.ToString(),
                ["bytesTotal"] = app.BytesTotal.ToString(),
            };
    }
}
