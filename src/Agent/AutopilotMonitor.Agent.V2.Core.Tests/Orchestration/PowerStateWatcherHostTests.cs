#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// PowerStateWatcherHost — no-battery/probe-error devices never arm, a low-battery baseline
    /// emits its threshold event onto the single rail, and debounced ticks translate tracker
    /// emissions into <c>power_state_change</c> events. WMI arming itself stays untested
    /// (ConsoleBypassWatcher precedent); <c>StartCore(armWmi: false)</c> exercises everything else.
    /// </summary>
    public sealed class PowerStateWatcherHostTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 8, 21, 9, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly FakeSignalIngressSink _sink = new FakeSignalIngressSink();
        private readonly AgentLogger _logger;
        private PowerStateResult _probeResult = new PowerStateResult { OnAcPower = true, HasBattery = true, BatteryPercent = 80 };

        public PowerStateWatcherHostTests()
        {
            _logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
        }

        public void Dispose() => _tmp.Dispose();

        private PowerStateWatcherHost Build() => new PowerStateWatcherHost(
            sessionId: "sess-power",
            tenantId: "tenant-power",
            ingress: _sink,
            clock: new VirtualClock(At),
            logger: _logger,
            probe: () => _probeResult);

        private IReadOnlyList<FakeSignalIngressSink.PostedSignal> PowerEvents() =>
            _sink.Posted.Where(p =>
                p.Kind == DecisionSignalKind.InformationalEvent
                && p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == Constants.EventTypes.PowerStateChange).ToList();

        [Fact]
        public void No_battery_emits_nothing()
        {
            _probeResult = new PowerStateResult { OnAcPower = true, HasBattery = false };

            Build().StartCore(armWmi: false);

            Assert.Empty(_sink.Posted);
        }

        [Fact]
        public void Probe_error_emits_nothing()
        {
            _probeResult = new PowerStateResult { ProbeError = "GetSystemPowerStatus returned false" };

            Build().StartCore(armWmi: false);

            Assert.Empty(_sink.Posted);
        }

        [Fact]
        public void Baseline_below_threshold_emits_threshold_event_with_immediate_upload()
        {
            _probeResult = new PowerStateResult { OnAcPower = false, HasBattery = true, BatteryPercent = 12, BatteryLifeMinutes = 34 };

            Build().StartCore(armWmi: false);

            var s = Assert.Single(PowerEvents());
            Assert.Equal("Error", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", s.Payload![SignalPayloadKeys.ImmediateUpload]);
            Assert.Contains("15%", s.Payload![SignalPayloadKeys.Message]);

            var data = (IReadOnlyDictionary<string, object>)s.TypedPayload!;
            Assert.Equal("threshold_crossed", data["transition"]);
            Assert.Equal(15, data["thresholdPercent"]);
            Assert.Equal(12, data["batteryPercent"]);
            Assert.Equal(false, data["onAcPower"]);
            Assert.Equal(34, data["batteryLifeMinutes"]);
        }

        [Fact]
        public void Tick_after_unplug_emits_ac_to_battery_warning()
        {
            var host = Build();
            host.StartCore(armWmi: false);
            Assert.Empty(PowerEvents());

            _probeResult = new PowerStateResult { OnAcPower = false, HasBattery = true, BatteryPercent = 79 };
            host.TickForTest();

            var s = Assert.Single(PowerEvents());
            Assert.Equal("Warning", s.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("true", s.Payload![SignalPayloadKeys.ImmediateUpload]);

            var data = (IReadOnlyDictionary<string, object>)s.TypedPayload!;
            Assert.Equal("ac_to_battery", data["transition"]);
            Assert.Equal(79, data["batteryPercent"]);
            Assert.False(data.ContainsKey("thresholdPercent"));

            // Replug: Info recovery event without immediate upload.
            _probeResult = new PowerStateResult { OnAcPower = true, HasBattery = true, BatteryPercent = 79, IsCharging = true };
            host.TickForTest();

            Assert.Equal(2, PowerEvents().Count);
            var back = PowerEvents()[1];
            Assert.Equal("Info", back.Payload![SignalPayloadKeys.Severity]);
            Assert.Equal("false", back.Payload![SignalPayloadKeys.ImmediateUpload]);
            Assert.Equal("battery_to_ac", ((IReadOnlyDictionary<string, object>)back.TypedPayload!)["transition"]);
        }

        [Fact]
        public void Tick_with_unchanged_state_emits_nothing()
        {
            var host = Build();
            host.StartCore(armWmi: false);

            host.TickForTest();
            host.TickForTest();

            Assert.Empty(PowerEvents());
        }

        [Fact]
        public void Stopped_host_ignores_ticks()
        {
            var host = Build();
            host.StartCore(armWmi: false);
            host.Stop();

            _probeResult = new PowerStateResult { OnAcPower = false, HasBattery = true, BatteryPercent = 50 };
            host.TickForTest();

            Assert.Empty(PowerEvents());
        }

        [Fact]
        public void Unknown_battery_percent_serializes_as_unknown()
        {
            var host = Build();
            host.StartCore(armWmi: false);

            _probeResult = new PowerStateResult { OnAcPower = false, HasBattery = true, BatteryPercent = null, BatteryLifeMinutes = null };
            host.TickForTest();

            var data = (IReadOnlyDictionary<string, object>)Assert.Single(PowerEvents()).TypedPayload!;
            Assert.Equal("unknown", data["batteryPercent"]);
            Assert.Equal("unknown", data["batteryLifeMinutes"]);
        }
    }
}
