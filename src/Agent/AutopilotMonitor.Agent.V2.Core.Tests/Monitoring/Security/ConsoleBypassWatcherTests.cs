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
    /// ConsoleBypassWatcher classification — driven via the internal <c>HandleStart</c>/<c>Classify</c>
    /// with an injected process probe (no real WMI / processes). Asserts the parent-free discriminator:
    /// an interactive-session cmd with a bare command line is flagged (high confidence); a scripted
    /// <c>cmd /c</c> and a session-0 (service) cmd are ignored; an interactive-session cmd whose command
    /// line cannot be read (instant-close race) is still surfaced at low confidence.
    /// </summary>
    public sealed class ConsoleBypassWatcherTests
    {
        private const string BareCmd = @"C:\WINDOWS\system32\cmd.exe";

        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        private sealed class Rig : IDisposable
        {
            private readonly TempDirectory _tmp = new TempDirectory();
            public List<ConsoleSpawnInfo> Detected { get; } = new List<ConsoleSpawnInfo>();
            public ConsoleBypassWatcher Sut { get; }

            public Rig(Func<int, ProcessProbe?> probe)
            {
                Sut = new ConsoleBypassWatcher(NewLogger(_tmp.Path), probe);
                Sut.BypassConsoleDetected += (_, info) => Detected.Add(info);
            }

            public void Dispose() { Sut.Dispose(); _tmp.Dispose(); }
        }

        private static Func<int, ProcessProbe?> Probe(string? commandLine, string? owner = @"PC\defaultuser0")
            => _ => new ProcessProbe(commandLine, owner);

        private static readonly Func<int, ProcessProbe?> GoneProbe = _ => null;

        // ---------------------------------------------------------------- HandleStart end-to-end

        [Fact]
        public void Interactive_session_bare_cmd_raises_high_confidence_with_signature()
        {
            using var rig = new Rig(Probe(BareCmd, owner: @"PC\defaultuser0"));

            rig.Sut.HandleStart(pid: 9636, parentPid: 9016, sessionId: 1, ownerSidHint: null, detectedVia: "process_start_trace");

            var info = Assert.Single(rig.Detected);
            Assert.Equal("cmd.exe", info.ProcessName);
            Assert.Equal(9636, info.ProcessId);
            Assert.Equal(9016, info.ParentProcessId);
            Assert.Equal(1, info.SessionId);
            Assert.Equal("high", info.Confidence);
            Assert.Equal("interactive_console", info.Classification);
            Assert.Equal(@"PC\defaultuser0", info.Owner);
            Assert.Equal(BareCmd, info.CommandLine);
        }

        [Fact]
        public void Session_zero_cmd_is_ignored_even_when_bare()
        {
            // IME / Intune install cmd run as SYSTEM in session 0 — never an interactive console.
            using var rig = new Rig(Probe(BareCmd));
            rig.Sut.HandleStart(1234, 1, sessionId: 0, ownerSidHint: null, detectedVia: "process_start_trace");
            Assert.Empty(rig.Detected);
        }

        [Theory]
        [InlineData(@"C:\WINDOWS\system32\cmd.exe /c ""install.bat""")]
        [InlineData(@"cmd.exe /C ping")]
        public void Scripted_cmd_is_ignored(string commandLine)
        {
            using var rig = new Rig(Probe(commandLine));
            rig.Sut.HandleStart(2222, 1, sessionId: 1, ownerSidHint: null, detectedVia: "process_start_trace");
            Assert.Empty(rig.Detected);
        }

        [Fact]
        public void Run_and_stay_cmd_k_raises_low_confidence()
        {
            // L12: /k runs its command and then leaves a fully interactive shell — a deliberate
            // false-negative seam if ignored (e.g. a technician-planted `cmd /k whoami`).
            using var rig = new Rig(Probe(@"cmd.exe /k whoami"));

            rig.Sut.HandleStart(2222, 1, sessionId: 1, ownerSidHint: null, detectedVia: "process_start_trace");

            var info = Assert.Single(rig.Detected);
            Assert.Equal("low", info.Confidence);
            Assert.Equal("interactive_console_with_command", info.Classification);
        }

        [Fact]
        public void Instant_close_unreadable_commandline_raises_low_confidence()
        {
            // Win32_Process query loses the race (process already exited). SessionID from the trace is
            // still known, so the console is surfaced — low confidence, owner from the trace SID hint.
            using var rig = new Rig(GoneProbe);

            rig.Sut.HandleStart(9636, 9016, sessionId: 1, ownerSidHint: "S-1-5-18", detectedVia: "process_start_trace");

            var info = Assert.Single(rig.Detected);
            Assert.Equal("low", info.Confidence);
            Assert.Equal("interactive_session_unclassified", info.Classification);
            Assert.Equal("S-1-5-18", info.Owner);
            Assert.Null(info.CommandLine);
        }

        [Fact]
        public void Owner_prefers_probe_over_trace_hint()
        {
            using var rig = new Rig(Probe(BareCmd, owner: @"PC\defaultuser0"));
            rig.Sut.HandleStart(9636, 9016, sessionId: 1, ownerSidHint: "S-1-5-18", detectedVia: "process_start_trace");
            Assert.Equal(@"PC\defaultuser0", Assert.Single(rig.Detected).Owner);
        }

        [Fact]
        public void Same_pid_seen_twice_raises_only_once()
        {
            // The startup probe and the live trace can both observe the same still-open console.
            using var rig = new Rig(Probe(BareCmd));
            rig.Sut.HandleStart(9636, 9016, 1, null, "startup_probe");
            rig.Sut.HandleStart(9636, 9016, 1, null, "process_start_trace");
            Assert.Single(rig.Detected);
        }

        [Fact]
        public void Invalid_pid_is_ignored()
        {
            using var rig = new Rig(Probe(BareCmd));
            rig.Sut.HandleStart(0, 9016, 1, null, "process_start_trace");
            Assert.Empty(rig.Detected);
        }

        // ---------------------------------------------------------------- Classify (pure)

        [Theory]
        [InlineData(0, BareCmd, "Ignore")]                             // service session
        [InlineData(1, null, "UnclassifiedInteractive")]               // instant-close
        [InlineData(1, BareCmd, "InteractiveConsole")]                 // bare shell
        [InlineData(1, @"cmd.exe /c foo", "Ignore")]                   // scripted, run-and-exit
        [InlineData(2, @"C:\x\cmd.exe /K bar", "InteractiveWithCommand")] // L12: /k stays interactive
        [InlineData(1, @"cmd.exe /k whoami", "InteractiveWithCommand")]   // technician-planted shell
        [InlineData(1, @"cmd.exe /d/q/c exit 9", "Ignore")]            // combined run-and-exit wins
        public void Classify_maps_session_and_commandline(int sessionId, string? commandLine, string expected)
        {
            Assert.Equal(expected, ConsoleBypassWatcher.Classify(sessionId, commandLine).ToString());
        }

        // ---------------------------------------------------------------- HasScriptArgument (pure)

        [Theory]
        [InlineData(@"C:\WINDOWS\system32\cmd.exe", false)]
        [InlineData(@"""C:\WINDOWS\system32\cmd.exe""", false)]
        [InlineData(@"cmd.exe /d", false)]                       // /d skips AutoRun — still interactive
        [InlineData(@"C:\cmd\cmd.exe", false)]                   // folder named 'cmd', not a switch
        [InlineData(@"""C:/k/cmd.exe""", false)]                  // forward-slash path stripped before scan
        [InlineData("", false)]
        [InlineData(@"cmd.exe /c whoami", true)]
        [InlineData(@"cmd.exe /C whoami", true)]
        [InlineData(@"cmd.exe /k", false)]                       // L12: /k leaves an interactive shell
        [InlineData(@"cmd.exe /d/q/c exit 9", true)]             // combined switches — the field case
        [InlineData(@"cmd.exe /D/Q/K stay", false)]              // L12: run-and-stay is not scripted
        [InlineData(@"""C:\WINDOWS\system32\cmd.exe"" /c ""x.bat""", true)]
        public void HasScriptArgument_detects_run_command_switch(string commandLine, bool expected)
        {
            Assert.Equal(expected, ConsoleBypassWatcher.HasScriptArgument(commandLine));
        }

        [Theory]
        [InlineData(@"C:\WINDOWS\system32\cmd.exe", "")]
        [InlineData(@"cmd.exe /d/q/c exit 9", "/d/q/c exit 9")]
        [InlineData(@"""C:\Program Files\x\cmd.exe"" /c y", @" /c y")]
        public void StripExecutable_removes_leading_exe_token(string commandLine, string expected)
        {
            Assert.Equal(expected, ConsoleBypassWatcher.StripExecutable(commandLine));
        }
    }
}
