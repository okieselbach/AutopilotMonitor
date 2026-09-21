using System;
using System.IO;
using AutopilotMonitor.Agent.V2;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Tests for the M4.6.α startup guards in <c>Program.Guards.cs</c>. These helpers are
    /// <c>internal static</c>; the V2 exe exposes its internals to this test assembly via
    /// <c>[InternalsVisibleTo]</c>.
    /// </summary>
    public sealed class ProgramGuardsTests
    {
        private static DateTime ValidUtc => new DateTime(2026, 4, 21, 10, 0, 0, DateTimeKind.Utc);

        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        // ================================================================= DetectPreviousExit

        [Fact]
        public void DetectPreviousExit_reports_first_run_when_nothing_is_present()
        {
            using var tmp = new TempDirectory();
            var summary = AutopilotMonitor.Agent.V2.Program.DetectPreviousExit(tmp.Path, Path.Combine(tmp.Path, "logs"));
            Assert.Equal("first_run", summary.ExitType);
            Assert.Null(summary.CrashExceptionType);
            Assert.Null(summary.LastBootUtc);
        }

        [Fact]
        public void DetectPreviousExit_reports_clean_when_marker_exists_and_deletes_it()
        {
            using var tmp = new TempDirectory();
            var markerPath = Path.Combine(tmp.Path, "clean-exit.marker");
            File.WriteAllText(markerPath, ValidUtc.ToString("O"));

            var summary = AutopilotMonitor.Agent.V2.Program.DetectPreviousExit(tmp.Path, Path.Combine(tmp.Path, "logs"));

            Assert.Equal("clean", summary.ExitType);
            Assert.False(File.Exists(markerPath));
        }

        [Fact]
        public void DetectPreviousExit_reports_exception_crash_and_extracts_exception_type()
        {
            using var tmp = new TempDirectory();
            var logDir = Path.Combine(tmp.Path, "logs");
            Directory.CreateDirectory(logDir);
            var crashPath = Path.Combine(logDir, "crash-20260421-100000.log");
            File.WriteAllText(crashPath, "[2026-04-21T10:00:00Z] FATAL: InvalidOperationException: simulated crash");

            var summary = AutopilotMonitor.Agent.V2.Program.DetectPreviousExit(tmp.Path, logDir);

            Assert.Equal("exception_crash", summary.ExitType);
            Assert.Equal("InvalidOperationException", summary.CrashExceptionType);
            Assert.False(File.Exists(crashPath));
        }

        [Fact]
        public void DetectPreviousExit_reports_hard_kill_when_session_exists_without_marker()
        {
            using var tmp = new TempDirectory();
            new SessionIdPersistence(tmp.Path).GetOrCreate();

            var summary = AutopilotMonitor.Agent.V2.Program.DetectPreviousExit(tmp.Path, Path.Combine(tmp.Path, "logs"));

            Assert.True(summary.ExitType == "hard_kill" || summary.ExitType == "reboot_kill",
                $"Expected hard_kill or reboot_kill, got {summary.ExitType}.");
        }

        // ================================================================= CheckEnrollmentCompleteMarker

        [Theory]
        // (markerPresent, selfDestructOnComplete) → (expectedShouldExit, expectedFactoryCalls)
        [InlineData(false, false, false, 0)] // no marker, no cleanup → proceed normally (coverage gap filled)
        [InlineData(false, true, false, 0)]  // no marker, cleanup armed → proceed (guard stays silent)
        [InlineData(true, false, true, 0)]   // marker + no cleanup → exit, leave ProgramData intact (post-mortem)
        [InlineData(true, true, true, 1)]    // marker + cleanup armed → exit and retry cleanup once
        public void CheckEnrollmentCompleteMarker_gates_exit_and_cleanup_on_marker_and_selfdestruct_flag(
            bool markerPresent, bool selfDestructOnComplete, bool expectedShouldExit, int expectedFactoryCalls)
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            new SessionIdPersistence(tmp.Path).GetOrCreate(logger);
            var stateDir = Path.Combine(tmp.Path, "State");
            if (markerPresent)
            {
                Directory.CreateDirectory(stateDir);
                File.WriteAllText(Path.Combine(stateDir, "enrollment-complete.marker"), "previous completion");
            }
            var factoryCalls = 0;

            var shouldExit = AutopilotMonitor.Agent.V2.Program.CheckEnrollmentCompleteMarker(
                stateDirectory: stateDir,
                selfDestructOnComplete: selfDestructOnComplete,
                cleanupServiceFactory: () =>
                {
                    factoryCalls++;
                    // The real factory would spawn a PowerShell cleanup script; throwing here
                    // forces TryRetryCleanup to swallow + log so we can assert factoryCalls==1
                    // without a real side-effect.
                    throw new InvalidOperationException(
                        "test-harness: cleanup factory must not spawn real PowerShell in unit tests");
                },
                logger: logger,
                consoleMode: false);

            Assert.Equal(expectedShouldExit, shouldExit);
            Assert.Equal(expectedFactoryCalls, factoryCalls);
        }

        // ================================================================= Emergency break

        [Fact]
        public void CheckSessionAgeEmergencyBreak_returns_false_when_session_age_within_limit()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var persistence = new SessionIdPersistence(tmp.Path);
            persistence.GetOrCreate();
            persistence.SaveSessionCreatedAt(DateTime.UtcNow.AddHours(-1));

            var tripped = AutopilotMonitor.Agent.V2.Program.CheckSessionAgeEmergencyBreak(
                dataDirectory: tmp.Path,
                stateDirectory: Path.Combine(tmp.Path, "State"),
                absoluteMaxSessionHours: 48,
                selfDestructOnComplete: false,
                cleanupServiceFactory: null,
                logger: logger,
                consoleMode: false);

            Assert.False(tripped);
        }

        [Fact]
        public void CheckSessionAgeEmergencyBreak_trips_when_session_exceeds_limit()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var persistence = new SessionIdPersistence(tmp.Path);
            persistence.GetOrCreate();
            persistence.SaveSessionCreatedAt(DateTime.UtcNow.AddHours(-100));

            var stateDir = Path.Combine(tmp.Path, "State");

            var tripped = AutopilotMonitor.Agent.V2.Program.CheckSessionAgeEmergencyBreak(
                dataDirectory: tmp.Path,
                stateDirectory: stateDir,
                absoluteMaxSessionHours: 48,
                selfDestructOnComplete: false,
                cleanupServiceFactory: null,
                logger: logger,
                consoleMode: false);

            Assert.True(tripped);
            // Marker must have been written so the next restart exits cleanly.
            Assert.True(File.Exists(Path.Combine(stateDir, "enrollment-complete.marker")));
            // Session must have been cleared.
            Assert.False(persistence.SessionExists());
        }

        [Fact]
        public void CheckSessionAgeEmergencyBreak_skips_whiteglove_resume_sessions()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var persistence = new SessionIdPersistence(tmp.Path);
            persistence.GetOrCreate();
            persistence.SaveSessionCreatedAt(DateTime.UtcNow.AddHours(-999));
            File.WriteAllText(Path.Combine(tmp.Path, "whiteglove.complete"), "1");

            var tripped = AutopilotMonitor.Agent.V2.Program.CheckSessionAgeEmergencyBreak(
                dataDirectory: tmp.Path,
                stateDirectory: Path.Combine(tmp.Path, "State"),
                absoluteMaxSessionHours: 48,
                selfDestructOnComplete: false,
                cleanupServiceFactory: null,
                logger: logger,
                consoleMode: false);

            Assert.False(tripped);
        }

        [Fact]
        public void CheckSessionAgeEmergencyBreak_does_not_trip_after_part2_resume_rebases_the_age_clock()
        {
            // V1-symmetric Part-2 resume contract: AgentRuntimeHost clears the
            // whiteglove.complete marker AND rebases session.created to the Part-2 boot
            // moment. Without the rebase, a crash/reboot during Part-2 AccountSetup
            // would drop us back here with the original Part-1 timestamp and the
            // emergency break would trip immediately. Verifies both halves of the
            // contract by simulating: aged Part-1 session → Part-2 boot rebase →
            // subsequent restart's emergency-break check.
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var persistence = new SessionIdPersistence(tmp.Path);

            // Aged Part-1 session that already burned through most of the lifetime budget.
            persistence.GetOrCreate();
            persistence.SaveSessionCreatedAt(DateTime.UtcNow.AddHours(-47));

            // Simulate AgentRuntimeHost's resume cleanup block (the marker no longer
            // exists at this point — it has been cleared and the timer has been rebased).
            persistence.ClearWhiteGloveComplete(logger);
            persistence.SaveSessionCreatedAt(DateTime.UtcNow);

            // Next restart's emergency-break check sees a fresh Part-2 timestamp.
            var tripped = AutopilotMonitor.Agent.V2.Program.CheckSessionAgeEmergencyBreak(
                dataDirectory: tmp.Path,
                stateDirectory: Path.Combine(tmp.Path, "State"),
                absoluteMaxSessionHours: 48,
                selfDestructOnComplete: false,
                cleanupServiceFactory: null,
                logger: logger,
                consoleMode: false);

            Assert.False(tripped);
            Assert.True(persistence.SessionExists());
        }

        [Fact]
        public void CheckSessionAgeEmergencyBreak_initialises_missing_session_created_on_first_miss()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            // Session exists but session.created does not — simulates older persistence on upgrade.
            File.WriteAllText(Path.Combine(tmp.Path, "session.id"), Guid.NewGuid().ToString());

            var tripped = AutopilotMonitor.Agent.V2.Program.CheckSessionAgeEmergencyBreak(
                dataDirectory: tmp.Path,
                stateDirectory: Path.Combine(tmp.Path, "State"),
                absoluteMaxSessionHours: 48,
                selfDestructOnComplete: false,
                cleanupServiceFactory: null,
                logger: logger,
                consoleMode: false);

            Assert.False(tripped);
            Assert.True(File.Exists(Path.Combine(tmp.Path, "session.created")));
        }

        // ================================================================= Bootstrap config IO

        [Fact]
        public void TryReadBootstrapConfig_returns_null_when_missing()
        {
            using var tmp = new TempDirectory();
            Assert.Null(AutopilotMonitor.Agent.V2.Program.TryReadBootstrapConfig(tmp.Path, NewLogger(tmp.Path)));
        }

        [Fact]
        public void TryReadBootstrapConfig_reads_tenant_and_token()
        {
            using var tmp = new TempDirectory();
            var cfg = new BootstrapConfigFile { BootstrapToken = "tok-1", TenantId = "t-1" };
            File.WriteAllText(Path.Combine(tmp.Path, "bootstrap-config.json"), JsonConvert.SerializeObject(cfg));

            var read = AutopilotMonitor.Agent.V2.Program.TryReadBootstrapConfig(tmp.Path, NewLogger(tmp.Path));

            Assert.NotNull(read);
            Assert.Equal("tok-1", read!.BootstrapToken);
            Assert.Equal("t-1", read.TenantId);
        }

        [Fact]
        public void TryReadBootstrapConfig_returns_null_on_corrupt_json()
        {
            using var tmp = new TempDirectory();
            File.WriteAllText(Path.Combine(tmp.Path, "bootstrap-config.json"), "{ bogus");
            Assert.Null(AutopilotMonitor.Agent.V2.Program.TryReadBootstrapConfig(tmp.Path, NewLogger(tmp.Path)));
        }

        [Fact]
        public void TryReadAwaitEnrollmentConfig_round_trips()
        {
            using var tmp = new TempDirectory();
            var cfg = new AwaitEnrollmentConfigFile { TimeoutMinutes = 120 };
            File.WriteAllText(Path.Combine(tmp.Path, "await-enrollment.json"), JsonConvert.SerializeObject(cfg));

            var read = AutopilotMonitor.Agent.V2.Program.TryReadAwaitEnrollmentConfig(tmp.Path, NewLogger(tmp.Path));

            Assert.NotNull(read);
            Assert.Equal(120, read!.TimeoutMinutes);
        }

        [Fact]
        public void DeleteAwaitEnrollmentConfig_removes_file()
        {
            using var tmp = new TempDirectory();
            var path = Path.Combine(tmp.Path, "await-enrollment.json");
            File.WriteAllText(path, "{}");

            AutopilotMonitor.Agent.V2.Program.DeleteAwaitEnrollmentConfig(tmp.Path, NewLogger(tmp.Path));
            Assert.False(File.Exists(path));
        }

        // ================================================================= Multi-instance guard

        private static AutopilotMonitor.Agent.V2.Program.AgentProcessFact Proc(int pid, int sessionId)
            => new AutopilotMonitor.Agent.V2.Program.AgentProcessFact(pid, sessionId);

        [Fact]
        public void FindSiblingAgentPid_ignores_a_same_named_process_in_a_user_session()
        {
            // The agent (pid 100, session 0) starts while a standard user keeps a process with
            // the agent's image name resident in their own session. It must not block the start.
            var matches = new[] { Proc(100, 0), Proc(200, 1) };

            Assert.Equal(0, AutopilotMonitor.Agent.V2.Program.FindSiblingAgentPid(matches, ownPid: 100));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(7)]
        [InlineData(-1)]
        public void FindSiblingAgentPid_ignores_every_session_but_zero(int sessionId)
        {
            var matches = new[] { Proc(100, 0), Proc(200, sessionId) };

            Assert.Equal(0, AutopilotMonitor.Agent.V2.Program.FindSiblingAgentPid(matches, ownPid: 100));
        }

        [Fact]
        public void FindSiblingAgentPid_reports_a_second_runtime_in_session_zero()
        {
            // Self-update restart while the old runtime is still alive — the single-agent
            // invariant the guard exists for.
            var matches = new[] { Proc(100, 0), Proc(300, 0) };

            Assert.Equal(300, AutopilotMonitor.Agent.V2.Program.FindSiblingAgentPid(matches, ownPid: 100));
        }

        [Fact]
        public void FindSiblingAgentPid_never_reports_the_caller_itself()
        {
            var matches = new[] { Proc(100, 0) };

            Assert.Equal(0, AutopilotMonitor.Agent.V2.Program.FindSiblingAgentPid(matches, ownPid: 100));
        }

        [Fact]
        public void FindSiblingAgentPid_skips_user_session_processes_listed_before_the_runtime()
        {
            // Install-mode launch verification: a user-session process enumerated first must not
            // be reported as the launched runtime.
            var matches = new[] { Proc(200, 1), Proc(201, 2), Proc(100, 0), Proc(300, 0) };

            Assert.Equal(300, AutopilotMonitor.Agent.V2.Program.FindSiblingAgentPid(matches, ownPid: 100));
        }

        [Fact]
        public void FindSiblingAgentPid_blocks_an_interactive_start_next_to_the_session_zero_runtime()
        {
            // --console run from an admin prompt (session 1) while the Scheduled Task runtime is up.
            var matches = new[] { Proc(100, 1), Proc(300, 0) };

            Assert.Equal(300, AutopilotMonitor.Agent.V2.Program.FindSiblingAgentPid(matches, ownPid: 100));
        }

        [Fact]
        public void FindSiblingAgentPid_returns_zero_for_no_matches()
        {
            Assert.Equal(0, AutopilotMonitor.Agent.V2.Program.FindSiblingAgentPid(
                new AutopilotMonitor.Agent.V2.Program.AgentProcessFact[0], ownPid: 100));
            Assert.Equal(0, AutopilotMonitor.Agent.V2.Program.FindSiblingAgentPid(null, ownPid: 100));
        }

        [Fact]
        public void SnapshotProcessesByName_reads_pid_and_session_of_the_running_test_host()
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();

            var facts = AutopilotMonitor.Agent.V2.Program.SnapshotProcessesByName(self.ProcessName);

            var own = Assert.Single(facts, f => f.Pid == self.Id);
            Assert.Equal(self.SessionId, own.SessionId);
        }

        [Fact]
        public void SnapshotProcessesByName_reads_the_session_of_system_owned_processes_without_access_rights()
        {
            // Positive control for the fail-open direction: the session id comes from the process
            // snapshot, not from an opened handle, so even an unprivileged caller reads it for
            // SYSTEM-owned service hosts. A skipped process would let a real sibling go unseen.
            var expected = System.Diagnostics.Process.GetProcessesByName("svchost");
            foreach (var p in expected) p.Dispose();

            var facts = AutopilotMonitor.Agent.V2.Program.SnapshotProcessesByName("svchost");

            Assert.NotEmpty(facts);
            Assert.All(facts, f => Assert.True(f.Pid > 0));
            Assert.Contains(facts, f => f.SessionId == 0);
            // Service hosts come and go; a wholesale access failure would drop (nearly) all of them.
            Assert.True(facts.Count >= expected.Length / 2,
                $"Snapshot returned {facts.Count} of ~{expected.Length} svchost processes.");
        }

        [Fact]
        public void SnapshotProcessesByName_returns_empty_for_an_unknown_name()
        {
            Assert.Empty(AutopilotMonitor.Agent.V2.Program.SnapshotProcessesByName("no-such-process-" + Guid.NewGuid().ToString("N")));
        }

        // ================================================================= Crash log writer

        [Fact]
        public void WriteCrashLog_writes_crash_file_with_fatal_prefix()
        {
            using var tmp = new TempDirectory();
            var logDir = Path.Combine(tmp.Path, "logs");

            AutopilotMonitor.Agent.V2.Program.WriteCrashLog(logDir, new InvalidOperationException("boom"));

            var files = Directory.GetFiles(logDir, "crash-*.log");
            Assert.Single(files);
            var content = File.ReadAllText(files[0]);
            Assert.Contains("FATAL:", content);
            Assert.Contains("InvalidOperationException", content);
        }
    }
}
