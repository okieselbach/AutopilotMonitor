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
            var crashPath = Path.Combine(logDir, "crash_20260421_100000.log");
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

        // ================================================================= Crash log writer

        [Fact]
        public void WriteCrashLog_writes_crash_file_with_fatal_prefix()
        {
            using var tmp = new TempDirectory();
            var logDir = Path.Combine(tmp.Path, "logs");

            AutopilotMonitor.Agent.V2.Program.WriteCrashLog(logDir, new InvalidOperationException("boom"));

            var files = Directory.GetFiles(logDir, "crash_*.log");
            Assert.Single(files);
            var content = File.ReadAllText(files[0]);
            Assert.Contains("FATAL:", content);
            Assert.Contains("InvalidOperationException", content);
        }
    }
}
