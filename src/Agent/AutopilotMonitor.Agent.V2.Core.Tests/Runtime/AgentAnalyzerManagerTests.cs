#nullable enable
using System;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Runtime
{
    /// <summary>
    /// M4.6.δ — AgentAnalyzerManager gating + lifecycle behaviour. The three real analyzers
    /// are out-of-scope here (covered by their own tests in Agent.Core.Tests + V2 port-parity
    /// sharing the same implementation files) — we verify the manager's own contract.
    /// </summary>
    public sealed class AgentAnalyzerManagerTests
    {
        private static AgentConfiguration Config() =>
            new AgentConfiguration { SessionId = "S1", TenantId = "T1" };

        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        // Throwaway InformationalEventPost for tests that exercise manager lifecycle, not
        // emission surface. Tracker events flow into a discarded FakeSignalIngressSink so
        // tests that inject a FakeAnalyzer see no cross-talk from real analyzer emissions.
        private static InformationalEventPost NewPost() =>
            new InformationalEventPost(new FakeSignalIngressSink(), new VirtualClock(new DateTime(2026, 4, 23, 10, 0, 0, DateTimeKind.Utc)));

        [Fact]
        public void Initialize_with_all_toggles_off_still_registers_always_on_AutoLogon()
        {
            using var tmp = new TempDirectory();
            var sut = new AgentAnalyzerManager(
                Config(), NewLogger(tmp.Path), NewPost(),
                new AnalyzerConfiguration
                {
                    EnableLocalAdminAnalyzer = false,
                    EnableSoftwareInventoryAnalyzer = false,
                    EnableIntegrityBypassAnalyzer = false,
                    EnableConsoleBypassDetection = false, // default-on opt-out; off for this isolation test
                });

            sut.Initialize();
            // AutoLogonAnalyzer has no toggle — it is always registered.
            var names = sut.Analyzers.Select(a => a.Name).ToList();
            Assert.Equal(new[] { "AutoLogonAnalyzer" }, names);
        }

        [Fact]
        public void Initialize_registers_only_enabled_analyzers()
        {
            using var tmp = new TempDirectory();
            var sut = new AgentAnalyzerManager(
                Config(), NewLogger(tmp.Path), NewPost(),
                new AnalyzerConfiguration
                {
                    EnableLocalAdminAnalyzer = true,
                    EnableSoftwareInventoryAnalyzer = false,
                    EnableIntegrityBypassAnalyzer = true,
                    EnableConsoleBypassDetection = false, // default-on opt-out; off so the count stays exact
                });

            sut.Initialize();
            var names = sut.Analyzers.Select(a => a.Name).ToList();
            Assert.Contains("LocalAdminAnalyzer", names);
            Assert.DoesNotContain("SoftwareInventoryAnalyzer", names);
            Assert.Contains("IntegrityBypassAnalyzer", names);
            // AutoLogonAnalyzer is always-on, in addition to the two enabled toggled analyzers.
            Assert.Contains("AutoLogonAnalyzer", names);
            Assert.Equal(3, sut.Analyzers.Count);
        }

        [Fact]
        public void Initialize_registers_ConsolePrefetchScanner_when_console_bypass_detection_on()
        {
            using var tmp = new TempDirectory();
            var sut = new AgentAnalyzerManager(
                Config(), NewLogger(tmp.Path), NewPost(),
                new AnalyzerConfiguration
                {
                    EnableLocalAdminAnalyzer = false,
                    EnableSoftwareInventoryAnalyzer = false,
                    EnableIntegrityBypassAnalyzer = false,
                    EnableConsoleBypassDetection = true,
                });

            sut.Initialize();
            Assert.Contains("ConsolePrefetchScanner", sut.Analyzers.Select(a => a.Name));
        }

        [Fact]
        public void Initialize_omits_ConsolePrefetchScanner_when_console_bypass_detection_off()
        {
            using var tmp = new TempDirectory();
            var sut = new AgentAnalyzerManager(
                Config(), NewLogger(tmp.Path), NewPost(),
                new AnalyzerConfiguration
                {
                    EnableLocalAdminAnalyzer = false,
                    EnableSoftwareInventoryAnalyzer = false,
                    EnableIntegrityBypassAnalyzer = false,
                    EnableConsoleBypassDetection = false,
                });

            sut.Initialize();
            Assert.DoesNotContain("ConsolePrefetchScanner", sut.Analyzers.Select(a => a.Name));
        }

        [Fact]
        public void Initialize_tolerates_null_analyzer_config()
        {
            using var tmp = new TempDirectory();
            var sut = new AgentAnalyzerManager(Config(), NewLogger(tmp.Path), NewPost(), analyzerConfig: null);

            sut.Initialize();
            // Default AnalyzerConfiguration enables LocalAdmin + IntegrityBypass + ConsoleBypassDetection
            // (opt-out), disables SoftwareInventory.
            var names = sut.Analyzers.Select(a => a.Name).ToList();
            Assert.Contains("LocalAdminAnalyzer", names);
            Assert.Contains("IntegrityBypassAnalyzer", names);
            Assert.Contains("ConsolePrefetchScanner", names);
            Assert.Contains("AutoLogonAnalyzer", names);
            Assert.DoesNotContain("SoftwareInventoryAnalyzer", names);
        }

        [Fact]
        public void Initialize_is_idempotent_across_repeated_calls()
        {
            using var tmp = new TempDirectory();
            var sut = new AgentAnalyzerManager(
                Config(), NewLogger(tmp.Path), NewPost(),
                new AnalyzerConfiguration { EnableLocalAdminAnalyzer = true });

            sut.Initialize();
            var first = sut.Analyzers.Count;
            sut.Initialize();
            Assert.Equal(first, sut.Analyzers.Count);
        }

        [Fact]
        public void Ctor_rejects_null_required_dependencies()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            Assert.Throws<ArgumentNullException>(() =>
                new AgentAnalyzerManager(null!, logger, NewPost(), null));
            Assert.Throws<ArgumentNullException>(() =>
                new AgentAnalyzerManager(Config(), null!, NewPost(), null));
            Assert.Throws<ArgumentNullException>(() =>
                new AgentAnalyzerManager(Config(), logger, null!, null));
        }

        // ---------------------------------------------------------------- Fake-analyzer lifecycle

#pragma warning disable CS0649  // ThrowOnStartup is settable by tests; keep the symmetric pair.
        private sealed class FakeAnalyzer : IAgentAnalyzer
        {
            public string Name => "FakeAnalyzer";
            public int StartupCalls;
            public int ShutdownCalls;
            public Exception? ThrowOnStartup;
            public Exception? ThrowOnShutdown;

            public void AnalyzeAtStartup()
            {
                Interlocked.Increment(ref StartupCalls);
                if (ThrowOnStartup != null) throw ThrowOnStartup;
            }

            public void AnalyzeAtShutdown()
            {
                Interlocked.Increment(ref ShutdownCalls);
                if (ThrowOnShutdown != null) throw ThrowOnShutdown;
            }
        }
#pragma warning restore CS0649

        /// <summary>
        /// Test-only harness: injects a fake analyzer via reflection so we do not have to run
        /// the real ones (which touch WMI / registry / file system).
        /// </summary>
        private static void InjectAnalyzer(AgentAnalyzerManager sut, IAgentAnalyzer fake)
        {
            var field = typeof(AgentAnalyzerManager).GetField("_analyzers",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            var list = (System.Collections.Generic.List<IAgentAnalyzer>)field!.GetValue(sut)!;
            list.Clear();
            list.Add(fake);
            // Mark as initialised so the manager does not reset the list on RunStartup/RunShutdown.
            var initField = typeof(AgentAnalyzerManager).GetField("_initialised",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            initField!.SetValue(sut, true);
        }

        [Fact]
        public void RunShutdown_invokes_AnalyzeAtShutdown_on_every_analyzer()
        {
            using var tmp = new TempDirectory();
            var sut = new AgentAnalyzerManager(Config(), NewLogger(tmp.Path), NewPost(), new AnalyzerConfiguration());
            var fake = new FakeAnalyzer();
            InjectAnalyzer(sut, fake);

            sut.RunShutdown();
            Assert.Equal(1, fake.ShutdownCalls);
        }

        [Fact]
        public void RunShutdown_swallows_analyzer_exceptions_and_continues()
        {
            using var tmp = new TempDirectory();
            var sut = new AgentAnalyzerManager(Config(), NewLogger(tmp.Path), NewPost(), new AnalyzerConfiguration());
            var fake = new FakeAnalyzer { ThrowOnShutdown = new InvalidOperationException("boom") };
            InjectAnalyzer(sut, fake);

            // Must not throw.
            var ex = Record.Exception(() => sut.RunShutdown());
            Assert.Null(ex);
            Assert.Equal(1, fake.ShutdownCalls);
        }

        [Fact]
        public void RunShutdown_is_safe_with_only_the_always_on_AutoLogon_analyzer()
        {
            using var tmp = new TempDirectory();
            var sut = new AgentAnalyzerManager(Config(), NewLogger(tmp.Path), NewPost(),
                new AnalyzerConfiguration
                {
                    EnableLocalAdminAnalyzer = false,
                    EnableSoftwareInventoryAnalyzer = false,
                    EnableIntegrityBypassAnalyzer = false,
                    EnableConsoleBypassDetection = false,
                });
            sut.Initialize();
            var ex = Record.Exception(() => sut.RunShutdown());
            Assert.Null(ex);
        }

        [Fact]
        public void RunDeviceSetupCompleteAutoLogonCheck_emits_one_device_setup_autologon_event()
        {
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink,
                new VirtualClock(new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc)));
            var sut = new AgentAnalyzerManager(Config(), NewLogger(tmp.Path), post,
                new AnalyzerConfiguration
                {
                    EnableLocalAdminAnalyzer = false,
                    EnableSoftwareInventoryAnalyzer = false,
                    EnableIntegrityBypassAnalyzer = false,
                    EnableConsoleBypassDetection = false,
                });

            sut.RunDeviceSetupCompleteAutoLogonCheck();

            var events = sink.Posted.Where(p => p.Payload != null
                && p.Payload.TryGetValue("eventType", out var et)
                && et == AutopilotMonitor.Shared.Constants.EventTypes.AutoLogonAnalysis).ToList();
            Assert.Single(events);
            var data = Assert.IsType<System.Collections.Generic.Dictionary<string, object>>(events[0].TypedPayload);
            Assert.Equal("device_setup_complete", data["triggered_at"]);
        }
    }
}
