#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Security
{
    /// <summary>
    /// ConsoleBypassWatcher classification — driven via the internal <c>HandleStart</c>/<c>Classify</c>
    /// with injected probes (no real WMI / processes). A console that is still running when probed is
    /// reported: bare command line (high confidence), <c>/k</c> or unreadable (low). A scripted
    /// <c>cmd /c</c> and a session-0 cmd are ignored. A start whose process is gone, or whose id now
    /// belongs to another process, is counted as script activity and never reported as a console.
    /// </summary>
    public sealed class ConsoleBypassWatcherTests
    {
        private const string Cmd = "cmd.exe";
        private const string BareCmd = @"C:\WINDOWS\system32\cmd.exe";
        private const string OobeAccount = "S-1-5-21-1-2-3-1000";
        private const string Created = "20261111143453.119277+060";
        private const int Pid = 9636;
        private const int Parent = 9016;
        private static readonly DateTime At = new DateTime(2026, 11, 11, 14, 34, 53, DateTimeKind.Utc);

        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        private sealed class Rig : IDisposable
        {
            private readonly TempDirectory _tmp = new TempDirectory();
            public List<ConsoleSpawnInfo> Detected { get; } = new List<ConsoleSpawnInfo>();
            public List<int> ParentLookups { get; } = new List<int>();
            public int WindowsOpened { get; private set; }
            public ConsoleBypassWatcher Sut { get; }

            public Rig(Func<int, string, ProcessProbe?> probe, bool alive = true, Func<int, string?>? parentName = null)
            {
                Sut = new ConsoleBypassWatcher(
                    NewLogger(_tmp.Path),
                    probe,
                    isAlive: _ => alive,
                    parentName: pid => { ParentLookups.Add(pid); return parentName?.Invoke(pid); },
                    utcNow: () => At);
                Sut.BypassConsoleDetected += (_, info) => Detected.Add(info);
                Sut.ScriptActivityStarted += (_, __) => WindowsOpened++;
            }

            public void Trace(int pid = Pid, int parentPid = Parent, int sessionId = 1, string? ownerSid = OobeAccount) =>
                Sut.HandleStart(pid, parentPid, sessionId, ownerSid, "process_start_trace", Cmd);

            public void Dispose() { Sut.Dispose(); _tmp.Dispose(); }
        }

        /// <summary>A running process that is the traced one, with the given command line.</summary>
        private static Func<int, string, ProcessProbe?> Running(
            string? commandLine, string name = Cmd, int sessionId = 1, int parentPid = Parent, string creationDate = Created)
            => (_, __) => new ProcessProbe(name, sessionId, parentPid, commandLine, creationDate);

        /// <summary>The process has exited before it could be read.</summary>
        private static readonly Func<int, string, ProcessProbe?> Gone = (_, __) => null;

        // ---------------------------------------------------------------- consoles that are reported

        [Fact]
        public void Interactive_session_bare_cmd_raises_high_confidence_with_signature()
        {
            using var rig = new Rig(Running(BareCmd), parentName: _ => "explorer.exe");

            rig.Trace();

            var info = Assert.Single(rig.Detected);
            Assert.Equal(Cmd, info.ProcessName);
            Assert.Equal(Pid, info.ProcessId);
            Assert.Equal(Parent, info.ParentProcessId);
            Assert.Equal("explorer.exe", info.ParentProcessName);
            Assert.Equal(1, info.SessionId);
            Assert.Equal("high", info.Confidence);
            Assert.Equal("interactive_console", info.Classification);
            Assert.Equal(OobeAccount, info.Owner);
            Assert.Equal(BareCmd, info.CommandLine);
            Assert.Null(rig.Sut.TakeScriptActivity());
        }

        [Fact]
        public void A_console_whose_launcher_is_gone_has_no_parent_name()
        {
            // The launcher behind Shift+F10 exits within milliseconds.
            using var rig = new Rig(Running(BareCmd));

            rig.Trace();

            Assert.Null(Assert.Single(rig.Detected).ParentProcessName);
        }

        [Fact]
        public void Run_and_stay_cmd_k_raises_low_confidence()
        {
            // L12: /k runs its command and then leaves a fully interactive shell — a deliberate
            // false-negative seam if ignored (e.g. a technician-planted `cmd /k whoami`).
            using var rig = new Rig(Running(@"cmd.exe /k whoami"));

            rig.Trace();

            var info = Assert.Single(rig.Detected);
            Assert.Equal("low", info.Confidence);
            Assert.Equal("interactive_console_with_command", info.Classification);
        }

        [Theory]
        [InlineData(@"cmd /k rem /c")]
        [InlineData(@"cmd.exe /k ""title /c""")]
        public void Cmd_k_with_slash_c_in_payload_is_not_demoted_to_scripted(string commandLine)
        {
            // cmd.exe honors only the FIRST /c-or-/k switch; a '/c' inside the /k payload is inert, so
            // the console is a persistent human-usable shell and must still be surfaced (low confidence).
            using var rig = new Rig(Running(commandLine));

            rig.Trace();

            var info = Assert.Single(rig.Detected);
            Assert.Equal("low", info.Confidence);
            Assert.Equal("interactive_console_with_command", info.Classification);
        }

        [Fact]
        public void A_running_process_without_a_readable_command_line_raises_low_confidence()
        {
            using var rig = new Rig(Running(commandLine: null), alive: true);

            rig.Trace();

            var info = Assert.Single(rig.Detected);
            Assert.Equal("low", info.Confidence);
            Assert.Equal("interactive_session_unclassified", info.Classification);
            Assert.Null(info.CommandLine);
            Assert.Null(rig.Sut.TakeScriptActivity());
        }

        [Fact]
        public void A_running_process_that_could_not_be_read_at_all_raises_low_confidence()
        {
            // The query failed. That says nothing about the process: it is reported, never counted away.
            using var rig = new Rig((_, __) => ProcessProbe.Unreadable, alive: true);

            rig.Trace();

            var info = Assert.Single(rig.Detected);
            Assert.Equal("low", info.Confidence);
            Assert.Equal("interactive_session_unclassified", info.Classification);
            Assert.Equal(OobeAccount, info.Owner);
            Assert.Null(rig.Sut.TakeScriptActivity());
        }

        // ---------------------------------------------------------------- starts that are ignored

        [Fact]
        public void Session_zero_cmd_is_ignored_even_when_bare()
        {
            // IME / Intune install cmd run as SYSTEM in session 0 — never an interactive console.
            using var rig = new Rig(Running(BareCmd));

            rig.Trace(sessionId: 0);

            Assert.Empty(rig.Detected);
            Assert.Null(rig.Sut.TakeScriptActivity());
        }

        [Theory]
        [InlineData(@"C:\WINDOWS\system32\cmd.exe /c ""install.bat""")]
        [InlineData(@"cmd.exe /C ping")]
        public void A_running_scripted_cmd_is_neither_reported_nor_counted(string commandLine)
        {
            using var rig = new Rig(Running(commandLine));

            rig.Trace();

            Assert.Empty(rig.Detected);
            Assert.Null(rig.Sut.TakeScriptActivity());
        }

        [Fact]
        public void Invalid_pid_is_ignored()
        {
            using var rig = new Rig(Running(BareCmd));

            rig.Trace(pid: 0);

            Assert.Empty(rig.Detected);
            Assert.Null(rig.Sut.TakeScriptActivity());
        }

        // ---------------------------------------------------------------- starts that are counted

        [Fact]
        public void A_process_that_has_exited_is_counted_and_not_reported()
        {
            // WMI delivers the start about a second late; a scripted cmd /c is gone by then. That is
            // script activity, not a console someone types into.
            using var rig = new Rig(Gone, parentName: _ => "powershell.exe");

            rig.Trace();

            Assert.Empty(rig.Detected);
            var activity = rig.Sut.TakeScriptActivity()!;
            Assert.Equal(1, activity.Count);
            Assert.Equal(Cmd, activity.ProcessName);
            Assert.Equal(At, activity.FirstSeenUtc);
            Assert.Equal(new[] { 1 }, activity.WindowsSessions);
            Assert.Equal(new[] { OobeAccount }, activity.Owners);
            var parent = Assert.Single(activity.Parents);
            Assert.Equal(Parent, parent.ProcessId);
            Assert.Equal("powershell.exe", parent.Name);
        }

        [Theory]
        [InlineData("SearchFilterHost.exe", 1, Parent)] // another program was given the id
        [InlineData(Cmd, 0, Parent)]                    // a service-session cmd was given the id
        [InlineData(Cmd, 1, 4242)]                      // a cmd of another parent was given the id
        public void An_id_that_now_belongs_to_another_process_is_counted_and_not_reported(string name, int sessionId, int parentPid)
        {
            // Its command line has no /c, which used to read as a bare interactive console.
            using var rig = new Rig(Running(@"C:\Windows\System32\other.exe -Embedding", name, sessionId, parentPid));

            rig.Trace();

            Assert.Empty(rig.Detected);
            Assert.Equal(1, rig.Sut.TakeScriptActivity()!.Count);
        }

        [Fact]
        public void A_row_without_a_command_line_whose_process_has_ended_is_counted()
        {
            using var rig = new Rig(Running(commandLine: null), alive: false);

            rig.Trace();

            Assert.Empty(rig.Detected);
            Assert.Equal(1, rig.Sut.TakeScriptActivity()!.Count);
        }

        [Fact]
        public void A_process_that_could_not_be_read_and_is_gone_at_the_second_look_is_counted()
        {
            using var rig = new Rig((_, __) => ProcessProbe.Unreadable, alive: false);

            rig.Trace();

            Assert.Empty(rig.Detected);
            Assert.Equal(1, rig.Sut.TakeScriptActivity()!.Count);
        }

        [Fact]
        public void A_burst_opens_one_window_and_asks_for_each_parent_once()
        {
            using var rig = new Rig(Gone, parentName: _ => "python.exe");

            for (var i = 0; i < 25; i++) rig.Trace(pid: 5000 + i, parentPid: 2688);
            for (var i = 0; i < 21; i++) rig.Trace(pid: 6000 + i, parentPid: 9296);

            Assert.Empty(rig.Detected);
            Assert.Equal(1, rig.WindowsOpened);
            Assert.Equal(new[] { 2688, 9296 }, rig.ParentLookups);
            var activity = rig.Sut.TakeScriptActivity()!;
            Assert.Equal(46, activity.Count);
            Assert.Equal(new[] { 25, 21 }, activity.Parents.Select(p => p.Children));
        }

        [Fact]
        public void The_next_start_after_the_content_was_taken_opens_a_new_window()
        {
            using var rig = new Rig(Gone);

            rig.Trace(pid: 1);
            rig.Trace(pid: 2);
            rig.Sut.TakeScriptActivity();
            rig.Trace(pid: 3);

            Assert.Equal(2, rig.WindowsOpened);
            Assert.Equal(1, rig.Sut.TakeScriptActivity()!.Count);
        }

        [Fact]
        public void Nothing_is_reported_or_counted_after_dispose()
        {
            var rig = new Rig(Gone);
            rig.Sut.Dispose();

            rig.Trace();

            Assert.Empty(rig.Detected);
            Assert.Null(rig.Sut.TakeScriptActivity());
            rig.Dispose();
        }

        // ---------------------------------------------------------------- de-duplication

        [Fact]
        public void The_same_process_seen_by_startup_probe_and_trace_raises_only_once()
        {
            // The startup probe and the live trace can both observe the same still-open console.
            using var rig = new Rig(Running(BareCmd));

            rig.Sut.HandleProbed(Pid, Parent, 1, OobeAccount, "startup_probe", Cmd, new ProcessProbe(Cmd, 1, Parent, BareCmd, Created));
            rig.Trace();

            Assert.Equal("startup_probe", Assert.Single(rig.Detected).DetectedVia);
        }

        [Fact]
        public void A_later_console_that_was_given_an_id_seen_before_is_reported_again()
        {
            var created = Created;
            using var rig = new Rig((_, __) => new ProcessProbe(Cmd, 1, Parent, BareCmd, created));

            rig.Trace();
            created = "20261111150324.504661+060";
            rig.Trace();

            Assert.Equal(2, rig.Detected.Count);
        }

        // ---------------------------------------------------------------- the probe's decision

        private static readonly ProcessProbe Row = new ProcessProbe(Cmd, 1, Parent, BareCmd, Created);

        [Theory]
        [InlineData("NotRunning")]
        [InlineData("OtherImage")]
        public void The_probe_says_gone_only_when_the_process_list_is_certain(string listed)
        {
            var asked = false;
            var state = (ConsoleBypassWatcher.ListedAs)Enum.Parse(typeof(ConsoleBypassWatcher.ListedAs), listed);

            var probe = ConsoleBypassWatcher.Probe(Pid, state, _ => { asked = true; return Row; });

            Assert.Null(probe);
            Assert.False(asked, "a process that is certainly gone costs no query");
        }

        [Theory]
        [InlineData("Expected")]
        [InlineData("Unknown")]
        public void The_probe_returns_the_row_of_a_process_that_may_still_run(string listed)
        {
            var state = (ConsoleBypassWatcher.ListedAs)Enum.Parse(typeof(ConsoleBypassWatcher.ListedAs), listed);

            Assert.Same(Row, ConsoleBypassWatcher.Probe(Pid, state, _ => Row));
        }

        [Fact]
        public void A_failed_query_is_unreadable_and_never_gone()
        {
            var probe = ConsoleBypassWatcher.Probe(
                Pid, ConsoleBypassWatcher.ListedAs.Expected, _ => throw new InvalidOperationException("WinMgmt did not answer"));

            Assert.Same(ProcessProbe.Unreadable, probe);
        }

        [Fact]
        public void A_query_without_a_row_is_unreadable_and_never_gone()
        {
            var probe = ConsoleBypassWatcher.Probe(Pid, ConsoleBypassWatcher.ListedAs.Expected, _ => null);

            Assert.Same(ProcessProbe.Unreadable, probe);
        }

        [Fact]
        public void The_process_list_tells_running_other_image_and_not_running_apart()
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            const int neverAProcessId = int.MaxValue;

            Assert.Equal(ConsoleBypassWatcher.ListedAs.Expected, ConsoleBypassWatcher.ProcessListState(me.Id, me.ProcessName + ".exe"));
            Assert.Equal(ConsoleBypassWatcher.ListedAs.OtherImage, ConsoleBypassWatcher.ProcessListState(me.Id, "not-" + me.ProcessName + ".exe"));
            Assert.Equal(ConsoleBypassWatcher.ListedAs.NotRunning, ConsoleBypassWatcher.ProcessListState(neverAProcessId, Cmd));
            Assert.True(ConsoleBypassWatcher.IsRunning(me.Id));
            Assert.False(ConsoleBypassWatcher.IsRunning(neverAProcessId));
        }

        // ---------------------------------------------------------------- IsTracedProcess (pure)

        [Theory]
        [InlineData("cmd.exe", 1, Parent, true)]
        [InlineData("CMD.EXE", 2, Parent, true)]     // image name compares without case
        [InlineData("cmd.exe", 1, 0, true)]          // the row does not know its parent
        // A fact that could not be read contradicts nothing — the process stays the traced one.
        [InlineData(null, null, null, true)]
        [InlineData(null, 1, Parent, true)]
        [InlineData("cmd.exe", null, Parent, true)]
        [InlineData("cmd.exe", 1, null, true)]
        // A fact that was read and differs makes it another process.
        [InlineData("conhost.exe", 1, Parent, false)]
        [InlineData("cmd.exe", 0, Parent, false)]
        [InlineData("cmd.exe", 1, 77, false)]
        public void IsTracedProcess_compares_name_session_and_parent(string? name, int? sessionId, int? rowParent, bool expected)
        {
            var probe = new ProcessProbe(name, sessionId, rowParent, BareCmd, Created);
            Assert.Equal(expected, ConsoleBypassWatcher.IsTracedProcess(probe, Cmd, Parent));
        }

        [Fact]
        public void IsTracedProcess_accepts_any_parent_when_the_trace_has_none()
        {
            var probe = new ProcessProbe(Cmd, 1, 77, BareCmd, Created);
            Assert.True(ConsoleBypassWatcher.IsTracedProcess(probe, Cmd, parentPid: 0));
        }

        // ---------------------------------------------------------------- Classify (pure)

        [Theory]
        [InlineData(0, BareCmd, "Ignore")]                             // service session
        [InlineData(1, null, "UnclassifiedInteractive")]               // running, command line not readable
        [InlineData(1, BareCmd, "InteractiveConsole")]                 // bare shell
        [InlineData(1, @"""C:\WINDOWS\system32\cmd.exe""", "InteractiveConsole")]
        [InlineData(1, "", "InteractiveConsole")]
        [InlineData(1, @"cmd.exe /d", "InteractiveConsole")]           // /d skips AutoRun — still interactive
        [InlineData(1, @"C:\cmd\cmd.exe", "InteractiveConsole")]       // folder named 'cmd', not a switch
        [InlineData(1, @"""C:/k/cmd.exe""", "InteractiveConsole")]     // forward-slash path stripped before scan
        [InlineData(1, @"cmd.exe /c foo", "Ignore")]                   // scripted, run-and-exit
        [InlineData(1, @"cmd.exe /C whoami", "Ignore")]
        [InlineData(1, @"""C:\WINDOWS\system32\cmd.exe"" /c ""x.bat""", "Ignore")]
        [InlineData(2, @"C:\x\cmd.exe /K bar", "InteractiveWithCommand")] // L12: /k stays interactive
        [InlineData(1, @"cmd.exe /k whoami", "InteractiveWithCommand")]   // technician-planted shell
        [InlineData(1, @"cmd.exe /k", "InteractiveWithCommand")]
        [InlineData(1, @"cmd.exe /D/Q/K stay", "InteractiveWithCommand")] // combined run-and-stay
        [InlineData(1, @"cmd.exe /d/q/c exit 9", "Ignore")]            // combined run-and-exit wins
        [InlineData(1, @"cmd /k rem /c", "InteractiveWithCommand")]     // cmd.exe honors the FIRST switch: /k stays interactive
        [InlineData(1, @"cmd.exe /k ""title /c""", "InteractiveWithCommand")] // /c inside the /k payload is not a switch
        [InlineData(1, @"cmd /k /c", "InteractiveWithCommand")]         // /c after /k is just the /k payload
        [InlineData(1, @"cmd.exe /c cmd /k", "Ignore")]                 // first switch /c wins the other way round
        [InlineData(1, @"cmd.exe /c rem /k", "Ignore")]
        public void Classify_maps_session_and_commandline(int sessionId, string? commandLine, string expected)
        {
            Assert.Equal(expected, ConsoleBypassWatcher.Classify(sessionId, commandLine).ToString());
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
