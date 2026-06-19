#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Analyzers
{
    /// <summary>
    /// ConsolePrefetchScanner — startup forensic over a synthetic prefetch directory with an injected
    /// boot time (no WMI / real %WINDIR%). Asserts the post-boot-cmd-artifact heuristic: a CMD.EXE-*.pf
    /// whose last-run is after boot emits a Warning console_prefetch_detected; a stale (pre-boot)
    /// artifact, a missing artifact, or a missing directory stay silent. Restart dedup via the gate.
    /// </summary>
    public sealed class ConsolePrefetchScannerTests
    {
        private static readonly DateTime BootUtc = new DateTime(2026, 6, 19, 1, 9, 0, DateTimeKind.Utc);

        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        private sealed class Rig : IDisposable
        {
            private readonly TempDirectory _tmp = new TempDirectory();
            public FakeSignalIngressSink Sink { get; } = new FakeSignalIngressSink();
            public string PrefetchDir { get; }
            public string StateDir { get; }

            public Rig()
            {
                PrefetchDir = Path.Combine(_tmp.Path, "Prefetch");
                StateDir = Path.Combine(_tmp.Path, "state");
                Directory.CreateDirectory(StateDir);
            }

            public void CreatePrefetch() => Directory.CreateDirectory(PrefetchDir);

            public void WritePf(string name, DateTime lastWriteUtc)
            {
                Directory.CreateDirectory(PrefetchDir);
                var path = Path.Combine(PrefetchDir, name);
                File.WriteAllText(path, "pf");
                File.SetLastWriteTimeUtc(path, lastWriteUtc);
            }

            public ConsolePrefetchScanner NewScanner(DateTime? bootTime = null, StartupEventGate? gate = null)
            {
                var post = new InformationalEventPost(Sink, new VirtualClock(BootUtc));
                return new ConsolePrefetchScanner(
                    "S1", "T1", post, NewLogger(StateDir), gate,
                    prefetchDirectory: PrefetchDir,
                    bootTimeProvider: () => bootTime ?? BootUtc);
            }

            public StartupEventGate NewGate() => new StartupEventGate(StateDir, NewLogger(StateDir));

            public FakeSignalIngressSink.PostedSignal? PrefetchEvent() => Sink.Posted.FirstOrDefault(p =>
                p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "console_prefetch_detected");

            public void Dispose() => _tmp.Dispose();
        }

        [Fact]
        public void Cmd_prefetch_after_boot_emits_warning_with_signature()
        {
            using var rig = new Rig();
            rig.WritePf("CMD.EXE-0BD30981.pf", BootUtc.AddMinutes(4)); // ran after boot

            rig.NewScanner().AnalyzeAtStartup();

            var evt = rig.PrefetchEvent();
            Assert.NotNull(evt);
            Assert.Equal("Warning", evt!.Payload![SignalPayloadKeys.Severity]);

            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(evt.TypedPayload!);
            Assert.Equal("console_prefetch_after_boot", data["decision"]);
            Assert.Equal("CMD.EXE-0BD30981.pf", data["artifact"]);
            Assert.Equal(true, data["ranAfterBoot"]);
            Assert.Equal(false, data["coverageComplete"]);
        }

        [Fact]
        public void Conhost_artifact_after_boot_is_listed_as_corroborating()
        {
            using var rig = new Rig();
            rig.WritePf("CMD.EXE-0BD30981.pf", BootUtc.AddMinutes(4));
            rig.WritePf("CONHOST.EXE-0C6456FB.pf", BootUtc.AddMinutes(4));

            rig.NewScanner().AnalyzeAtStartup();

            var data = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object>>(rig.PrefetchEvent()!.TypedPayload!);
            var corroborating = Assert.IsAssignableFrom<IEnumerable<string>>(data["corroboratingArtifacts"]);
            Assert.Contains("CONHOST.EXE-0C6456FB.pf", corroborating);
        }

        [Fact]
        public void Stale_pre_boot_cmd_artifact_is_not_flagged()
        {
            using var rig = new Rig();
            rig.WritePf("CMD.EXE-0BD30981.pf", BootUtc.AddMinutes(-30)); // image-build artifact

            rig.NewScanner().AnalyzeAtStartup();

            Assert.Null(rig.PrefetchEvent());
        }

        [Fact]
        public void No_cmd_artifact_stays_silent()
        {
            using var rig = new Rig();
            rig.CreatePrefetch(); // dir exists, but no CMD.EXE-*.pf

            rig.NewScanner().AnalyzeAtStartup();

            Assert.Null(rig.PrefetchEvent());
        }

        [Fact]
        public void Missing_prefetch_directory_stays_silent()
        {
            using var rig = new Rig(); // never creates the Prefetch dir (prefetch disabled)

            rig.NewScanner().AnalyzeAtStartup();

            Assert.Null(rig.PrefetchEvent());
        }

        [Fact]
        public void Gate_suppresses_a_second_unchanged_run()
        {
            using var rig = new Rig();
            rig.WritePf("CMD.EXE-0BD30981.pf", BootUtc.AddMinutes(4));
            var gate = rig.NewGate();

            rig.NewScanner(gate: gate).AnalyzeAtStartup();
            rig.NewScanner(gate: gate).AnalyzeAtStartup(); // restart, same artifact

            var emitted = rig.Sink.Posted.Count(p =>
                p.Payload != null
                && p.Payload.TryGetValue(SignalPayloadKeys.EventType, out var et)
                && et == "console_prefetch_detected");
            Assert.Equal(1, emitted);
        }

        [Fact]
        public void Shutdown_is_a_noop()
        {
            using var rig = new Rig();
            rig.WritePf("CMD.EXE-0BD30981.pf", BootUtc.AddMinutes(4));

            rig.NewScanner().AnalyzeAtShutdown();

            Assert.Null(rig.PrefetchEvent());
        }
    }
}
