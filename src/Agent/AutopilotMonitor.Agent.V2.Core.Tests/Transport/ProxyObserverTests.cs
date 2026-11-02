using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Transport
{
    /// <summary>Settings and route are played in: no test changes a real proxy setting.</summary>
    public sealed class ProxyObserverTests : IDisposable
    {
        private static readonly ProxySettings AutoDetect = new ProxySettings(true, null, null, null, null);
        private static readonly ProxySettings NoProxy = new ProxySettings(false, null, null, null, null);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly VirtualClock _clock = new VirtualClock(new DateTime(2026, 11, 2, 6, 29, 13, DateTimeKind.Utc));
        private ProxySettings _settings = AutoDetect;
        private string _route = "proxy.example.test:8080";

        private ProxyObserver NewObserver(Func<string>? readRoute = null, TimeSpan? routeBound = null, Func<ProxySettings>? readSettings = null) =>
            new ProxyObserver(
                readSettings ?? (() => _settings),
                readRoute ?? (() => _route),
                new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                _clock,
                routeBound ?? TimeSpan.FromSeconds(30));

        private ProxyObserver StartedObserver()
        {
            var observer = NewObserver();
            observer.CaptureBaseline();
            return observer;
        }

        private void SwitchToNoProxy()
        {
            _settings = NoProxy;
            _route = "direct";
        }

        private List<string> LogLines()
        {
            var lines = new List<string>();
            foreach (var file in Directory.EnumerateFiles(_tmp.Path, "*.log", SearchOption.AllDirectories))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                lines.AddRange(reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
            }
            return lines.Where(l => l.Contains("ProxyObserver:")).ToList();
        }

        public void Dispose() => _tmp.Dispose();

        [Fact]
        public void The_baseline_names_settings_and_route_at_info()
        {
            StartedObserver();

            var line = Assert.Single(LogLines());
            Assert.Contains("[INFO]", line);
            Assert.Contains("start - IE settings of this account: auto-detect; WinHTTP: direct; route to backend: proxy.example.test:8080", line);
        }

        [Fact]
        public void A_rebuild_of_the_clients_does_not_repeat_the_baseline()
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            foreach (var _ in new[] { 1, 2 })
            {
                new ProxyObserver(() => _settings, () => _route, logger, _clock, TimeSpan.FromSeconds(30)).CaptureBaseline();
            }

            Assert.Single(LogLines());
        }

        [Fact]
        public void A_change_warns_once_with_both_sides()
        {
            var observer = StartedObserver();
            SwitchToNoProxy();

            observer.CheckForChange();
            observer.CheckForChange();

            var line = Assert.Single(LogLines().Skip(1));
            Assert.Contains("[WARN]", line);
            Assert.Contains("proxy settings changed - IE settings of this account: auto-detect -> no proxy; route to backend: proxy.example.test:8080 -> direct.", line);
            Assert.Contains("bitsadmin /util /getieproxy localsystem", line);
        }

        [Fact]
        public void Rewriting_the_same_content_says_nothing()
        {
            var observer = StartedObserver();
            _settings = new ProxySettings(true, null, null, null, null);

            observer.CheckForChange();

            Assert.Single(LogLines());
        }

        [Fact]
        public void A_winhttp_change_says_uploads_do_not_use_it()
        {
            var observer = StartedObserver();
            _settings = new ProxySettings(true, null, null, null, "p:8080");

            observer.CheckForChange();

            var line = LogLines().Last();
            Assert.Contains("[WARN]", line);
            Assert.Contains("WinHTTP: direct -> proxy p:8080", line);
            Assert.Contains("do not use WinHTTP", line);
        }

        [Fact]
        public void A_winhttp_change_is_not_blamed_for_failing_uploads()
        {
            var observer = StartedObserver();
            _settings = new ProxySettings(true, null, null, null, "p:8080");
            observer.CheckForChange();

            observer.OnSendFailed(TimeSpan.Zero);

            Assert.Contains("Proxy settings of this account unchanged since start: auto-detect.", LogLines().Last());
        }

        [Fact]
        public void A_change_reaches_the_subscriber_with_settings_and_route()
        {
            var observer = StartedObserver();
            ProxySettings? seen = null;
            string? seenWhat = null;
            string? seenRoute = null;
            observer.SettingsChanged += (settings, what, route) =>
            {
                seen = settings;
                seenWhat = what;
                seenRoute = route;
            };
            SwitchToNoProxy();

            observer.CheckForChange();

            Assert.Equal(NoProxy, seen);
            Assert.Equal("IE settings of this account: auto-detect -> no proxy", seenWhat);
            Assert.Equal("direct", seenRoute);
        }

        [Fact]
        public void A_subscriber_that_throws_does_not_reach_the_caller()
        {
            var observer = StartedObserver();
            observer.SettingsChanged += (_, _, _) => throw new InvalidOperationException("SignalIngress has been stopped.");
            SwitchToNoProxy();

            observer.CheckForChange();

            Assert.Contains(LogLines(), l => l.Contains("proxy settings changed"));
        }

        [Fact]
        public void The_first_failure_ties_the_series_to_the_change()
        {
            var observer = StartedObserver();
            _clock.Advance(TimeSpan.FromMinutes(23));
            observer.OnSendSucceeded();
            _clock.Advance(TimeSpan.FromSeconds(90));
            SwitchToNoProxy();
            observer.CheckForChange();
            _clock.Advance(TimeSpan.FromSeconds(62));

            observer.OnSendFailed(TimeSpan.FromSeconds(21));

            var line = LogLines().Last();
            Assert.Contains("[WARN]", line);
            Assert.Contains("uploads are failing since 06:54:24, 41 s after the proxy settings changed at 06:53:43 (IE settings of this account: auto-detect -> no proxy).", line);
            Assert.Contains("Route to backend: direct. Last successful upload 06:52:13.", line);
        }

        [Fact]
        public void A_failure_notices_a_change_the_watcher_did_not_report()
        {
            var observer = StartedObserver();
            SwitchToNoProxy();

            observer.OnSendFailed(TimeSpan.Zero);

            var lines = LogLines();
            Assert.Contains(lines, l => l.Contains("proxy settings changed - IE settings of this account: auto-detect -> no proxy"));
            Assert.Contains("after the proxy settings changed", lines.Last());
        }

        [Fact]
        public void A_series_writes_its_finding_once()
        {
            var observer = StartedObserver();

            observer.OnSendFailed(TimeSpan.Zero);
            observer.OnSendFailed(TimeSpan.Zero);
            observer.OnSendFailed(TimeSpan.Zero);

            Assert.Single(LogLines(), l => l.Contains("uploads are failing"));
        }

        [Fact]
        public void A_failure_without_a_change_says_the_settings_are_unchanged()
        {
            var observer = StartedObserver();

            observer.OnSendFailed(TimeSpan.Zero);

            var line = LogLines().Last();
            Assert.Contains("Proxy settings of this account unchanged since start: auto-detect.", line);
            Assert.Contains("Route to backend: proxy.example.test:8080. No successful upload yet.", line);
        }

        [Fact]
        public void A_change_before_the_last_success_is_not_blamed()
        {
            var observer = StartedObserver();
            SwitchToNoProxy();
            observer.CheckForChange();
            _clock.Advance(TimeSpan.FromMinutes(1));
            observer.OnSendSucceeded();
            _clock.Advance(TimeSpan.FromMinutes(1));

            observer.OnSendFailed(TimeSpan.Zero);

            Assert.Contains("Proxy settings of this account unchanged since the last successful upload: no proxy.", LogLines().Last());
        }

        [Fact]
        public void Recovery_is_noted_and_a_new_series_gets_its_own_finding()
        {
            var observer = StartedObserver();
            observer.OnSendFailed(TimeSpan.Zero);
            observer.OnSendFailed(TimeSpan.Zero);
            _clock.Advance(TimeSpan.FromMinutes(9));

            observer.OnSendSucceeded();

            var line = LogLines().Last();
            Assert.Contains("[INFO]", line);
            Assert.Contains("uploads work again after 2 failed attempt(s) over 9 min; route to backend: proxy.example.test:8080.", line);

            observer.OnSendFailed(TimeSpan.Zero);
            Assert.Equal(2, LogLines().Count(l => l.Contains("uploads are failing")));
        }

        [Fact]
        public void Recovery_reports_settings_that_came_back()
        {
            var observer = StartedObserver();
            SwitchToNoProxy();
            observer.OnSendFailed(TimeSpan.Zero);
            _settings = AutoDetect;
            _route = "proxy.example.test:8080";

            observer.OnSendSucceeded();

            Assert.Contains(LogLines(), l => l.Contains("no proxy -> auto-detect"));
        }

        [Fact]
        public void A_success_without_a_series_stays_silent()
        {
            var observer = StartedObserver();

            observer.OnSendSucceeded();

            Assert.Single(LogLines());
        }

        [Fact]
        public void Stopping_while_failing_sums_it_up()
        {
            var observer = StartedObserver();
            observer.OnSendSucceeded();
            _clock.Advance(TimeSpan.FromMinutes(1));
            SwitchToNoProxy();
            observer.OnSendFailed(TimeSpan.Zero);
            observer.OnSendFailed(TimeSpan.Zero);

            observer.LogOpenFailures();

            var line = LogLines().Last();
            Assert.Contains("[WARN]", line);
            Assert.Contains("stopping with uploads still failing: 2 failed attempt(s) since 06:30:13, last success 06:29:13, route to backend: direct.", line);
            Assert.Contains("Proxy settings changed at 06:30:13 (IE settings of this account: auto-detect -> no proxy).", line);
        }

        [Fact]
        public void Stopping_without_a_series_stays_silent()
        {
            var observer = StartedObserver();
            observer.OnSendFailed(TimeSpan.Zero);
            observer.OnSendSucceeded();
            var before = LogLines().Count;

            observer.LogOpenFailures();

            Assert.Equal(before, LogLines().Count);
        }

        [Fact]
        public void A_settings_read_that_throws_is_not_a_change_and_never_reaches_the_upload()
        {
            var throwing = false;
            var observer = NewObserver(readSettings: () => throwing ? throw new InvalidOperationException("boom") : _settings);
            observer.CaptureBaseline();
            throwing = true;

            observer.CheckForChange();
            observer.OnSendFailed(TimeSpan.Zero);

            Assert.DoesNotContain(LogLines(), l => l.Contains("proxy settings changed"));
            Assert.Contains("Proxy settings of this account unchanged since start: auto-detect.", LogLines().Last());
        }

        [Fact]
        public void An_unreadable_state_is_not_a_change()
        {
            var observer = StartedObserver();
            _settings = ProxySettings.Unreadable(5);

            observer.CheckForChange();

            Assert.Single(LogLines());
        }

        [Fact]
        public void A_route_read_that_throws_reads_as_unknown()
        {
            var observer = NewObserver(readRoute: () => throw new InvalidOperationException("boom"));

            Assert.Equal("unknown", observer.RouteText());
        }

        [Fact]
        public void A_hanging_route_read_gives_up_and_does_not_pile_up()
        {
            using var release = new ManualResetEventSlim(false);
            var reads = 0;
            var observer = NewObserver(
                readRoute: () =>
                {
                    Interlocked.Increment(ref reads);
                    release.Wait();
                    return "direct";
                },
                routeBound: TimeSpan.FromMilliseconds(50));

            Assert.Equal("unknown", observer.RouteText());
            Assert.Equal("unknown", observer.RouteText());
            Assert.Equal(1, Volatile.Read(ref reads));

            release.Set();
        }
    }
}
