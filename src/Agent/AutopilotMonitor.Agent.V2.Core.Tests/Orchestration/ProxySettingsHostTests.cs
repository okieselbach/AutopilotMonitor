using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Transport;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>The watchers only listen on the real keys; settings are played in, none is changed.</summary>
    public sealed class ProxySettingsHostTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _ingress = new FakeSignalIngressSink();
        private readonly VirtualClock _clock = new VirtualClock(new DateTime(2026, 11, 2, 6, 53, 43, DateTimeKind.Utc));
        private readonly AgentLogger _logger;
        private readonly ProxyObserver _observer;
        private ProxySettings _settings = new ProxySettings(true, null, null, null, null);
        private string _route = "proxy.example.test:8080";

        public ProxySettingsHostTests()
        {
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _observer = new ProxyObserver(() => _settings, () => _route, _logger, _clock, TimeSpan.FromSeconds(30));
            _observer.CaptureBaseline();
        }

        private ProxySettingsHost NewHost() => new ProxySettingsHost(_observer, _ingress, _clock, _logger);

        private void SwitchToNoProxy()
        {
            _settings = new ProxySettings(false, null, null, null, null);
            _route = "direct";
        }

        private List<FakeSignalIngressSink.PostedSignal> ProxyEvents() =>
            _ingress.Posted
                .Where(p => p.Kind == DecisionSignalKind.InformationalEvent
                            && p.Payload != null
                            && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var eventType)
                            && eventType == Constants.EventTypes.ProxyConfiguration)
                .ToList();

        private string Log()
        {
            var text = string.Empty;
            foreach (var file in Directory.EnumerateFiles(_tmp.Path, "*.log", SearchOption.AllDirectories))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                text += reader.ReadToEnd();
            }
            return text;
        }

        public void Dispose() => _tmp.Dispose();

        [Fact]
        public void A_change_goes_on_the_timeline_as_a_warning_with_what_moved()
        {
            using var host = NewHost();
            host.Start();
            SwitchToNoProxy();

            _observer.CheckForChange();

            var signal = Assert.Single(ProxyEvents());
            Assert.Equal(nameof(EventSeverity.Warning), signal.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("Proxy settings changed: IE settings of this account: auto-detect -> no proxy", signal.Payload[SignalPayloadKeys.Message]);

            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(signal.TypedPayload);
            Assert.Equal("changed", data["trigger"]);
            Assert.Equal("IE settings of this account: auto-detect -> no proxy", data["change"]);
            Assert.Equal("direct", data["route"]);
            Assert.Equal("Direct", data["proxyType"]);
        }

        [Fact]
        public void A_change_between_the_baseline_and_the_start_is_noticed_at_the_start()
        {
            SwitchToNoProxy();

            using var host = NewHost();
            host.Start();

            Assert.Single(ProxyEvents());
        }

        [Fact]
        public void No_change_means_no_event()
        {
            using var host = NewHost();
            host.Start();

            _observer.CheckForChange();

            Assert.Empty(ProxyEvents());
        }

        [Fact]
        public void A_stopped_ingress_does_not_reach_the_upload_path()
        {
            using var host = NewHost();
            host.Start();
            _ingress.ThrowOnPost = new InvalidOperationException("SignalIngress has been stopped.");
            SwitchToNoProxy();

            _observer.OnSendFailed(TimeSpan.Zero);

            Assert.Contains("uploads are failing", Log());
        }

        [Fact]
        public void Stopping_while_uploads_fail_says_so_once()
        {
            var host = NewHost();
            host.Start();
            _observer.OnSendFailed(TimeSpan.Zero);

            host.Stop();
            host.Stop();
            host.Dispose();

            var log = Log();
            var first = log.IndexOf("stopping with uploads still failing", StringComparison.Ordinal);
            Assert.True(first >= 0);
            Assert.Equal(-1, log.IndexOf("stopping with uploads still failing", first + 1, StringComparison.Ordinal));
        }

        [Fact]
        public void A_change_after_the_stop_no_longer_posts()
        {
            var host = NewHost();
            host.Start();
            host.Stop();
            SwitchToNoProxy();

            _observer.CheckForChange();

            Assert.Empty(ProxyEvents());
            host.Dispose();
        }

        [Fact]
        public void A_host_that_never_started_stays_silent_on_stop()
        {
            var host = NewHost();
            _observer.OnSendFailed(TimeSpan.Zero);

            host.Dispose();

            Assert.DoesNotContain("stopping with uploads still failing", Log());
        }
    }
}
