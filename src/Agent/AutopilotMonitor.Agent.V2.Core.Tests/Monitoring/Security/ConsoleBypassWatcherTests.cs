#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Security
{
    /// <summary>
    /// ConsoleBypassWatcher classification core — driven via the internal <c>HandleStart</c> with an
    /// injected parent-name resolver (no real WMI / processes). Asserts the winlogon-parent
    /// discriminator: a cmd.exe parented to winlogon.exe raises exactly one detection; any other
    /// parent (or an unresolvable one) is ignored, keeping the false-positive rate near zero.
    /// </summary>
    public sealed class ConsoleBypassWatcherTests
    {
        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        private sealed class Rig : IDisposable
        {
            private readonly TempDirectory _tmp = new TempDirectory();
            public List<ConsoleSpawnInfo> Detected { get; } = new List<ConsoleSpawnInfo>();
            public ConsoleBypassWatcher Sut { get; }

            public Rig(Func<int, string?> parentResolver)
            {
                Sut = new ConsoleBypassWatcher(NewLogger(_tmp.Path), parentResolver);
                Sut.BypassConsoleDetected += (_, info) => Detected.Add(info);
            }

            public void Dispose() { Sut.Dispose(); _tmp.Dispose(); }
        }

        [Fact]
        public void Parent_winlogon_raises_one_detection_with_full_signature()
        {
            using var rig = new Rig(parentPid => parentPid == 612 ? "winlogon" : "explorer");

            rig.Sut.HandleStart(pid: 7244, parentPid: 612, sessionId: 1, detectedVia: "process_start_trace");

            var info = Assert.Single(rig.Detected);
            Assert.Equal("cmd.exe", info.ProcessName);
            Assert.Equal(7244, info.ProcessId);
            Assert.Equal("winlogon.exe", info.ParentProcessName);
            Assert.Equal(612, info.ParentProcessId);
            Assert.Equal(1, info.SessionId);
            Assert.Equal("process_start_trace", info.DetectedVia);
        }

        [Fact]
        public void Parent_winlogon_match_is_case_insensitive()
        {
            using var rig = new Rig(_ => "WinLogon");
            rig.Sut.HandleStart(7244, 612, 1, "startup_probe");
            Assert.Single(rig.Detected);
        }

        [Fact]
        public void Non_winlogon_parent_is_ignored()
        {
            using var rig = new Rig(_ => "explorer"); // ordinary install-launched cmd
            rig.Sut.HandleStart(7244, 999, 1, "process_start_trace");
            Assert.Empty(rig.Detected);
        }

        [Fact]
        public void Unresolvable_parent_is_ignored()
        {
            using var rig = new Rig(_ => null);
            rig.Sut.HandleStart(7244, 612, 1, "process_start_trace");
            Assert.Empty(rig.Detected);
        }

        [Fact]
        public void Parent_resolver_throwing_is_swallowed_and_not_flagged()
        {
            using var rig = new Rig(_ => throw new InvalidOperationException("parent gone"));
            rig.Sut.HandleStart(7244, 612, 1, "process_start_trace");
            Assert.Empty(rig.Detected); // conservative: cannot confirm winlogon → no false positive
        }

        [Fact]
        public void Same_pid_seen_twice_raises_only_once()
        {
            // The startup probe and the live trace can both observe the same still-running console.
            using var rig = new Rig(_ => "winlogon");
            rig.Sut.HandleStart(7244, 612, 1, "startup_probe");
            rig.Sut.HandleStart(7244, 612, 1, "process_start_trace");
            Assert.Single(rig.Detected);
        }

        [Fact]
        public void Non_winlogon_start_does_not_suppress_a_later_winlogon_start_with_same_pid()
        {
            // A non-winlogon cmd (pid 7244) exits; the OS reuses pid 7244 for a real Shift+F10
            // console. The first non-match must NOT have marked the pid seen.
            var parentNames = new Dictionary<int, string?> { [999] = "explorer", [612] = "winlogon" };
            using var rig = new Rig(parentPid => parentNames[parentPid]);

            rig.Sut.HandleStart(7244, 999, 1, "process_start_trace"); // ordinary cmd → ignored
            rig.Sut.HandleStart(7244, 612, 1, "process_start_trace"); // reused pid, real bypass

            Assert.Single(rig.Detected);
            Assert.Equal(612, rig.Detected[0].ParentProcessId);
        }

        [Theory]
        [InlineData(0, 612)]
        [InlineData(7244, 0)]
        public void Invalid_pids_are_ignored(int pid, int parentPid)
        {
            using var rig = new Rig(_ => "winlogon");
            rig.Sut.HandleStart(pid, parentPid, 1, "process_start_trace");
            Assert.Empty(rig.Detected);
        }
    }
}
