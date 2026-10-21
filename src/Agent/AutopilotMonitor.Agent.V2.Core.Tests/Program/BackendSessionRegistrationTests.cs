using System;
using System.IO;
using System.Net.Http;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Runtime;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Tests for <see cref="BackendSessionRegistration"/>: Phase 6 extract from
    /// <c>Program.RunAgent</c>. The Register path delegates to
    /// <see cref="SessionRegistrationHelper"/> (covered by SessionRegistrationHelperTests);
    /// here we cover the wrapper's Result-type contract and argument validation.
    /// </summary>
    public sealed class BackendSessionRegistrationTests
    {
        private static AgentLogger NewLogger(string path)
            => new AgentLogger(Path.Combine(path, "logs"), AgentLogLevel.Info);

        [Fact]
        public void Outcome_Exit_with_six_signals_auth_failed()
        {
            var result = SessionRegistrationOutcomeResult.Exit(6);

            Assert.True(result.ShouldExit);
            Assert.Equal(6, result.ExitCode);
            Assert.Null(result.Registration);
        }

        [Fact]
        public void Outcome_Exit_with_seven_signals_non_auth_failed()
        {
            var result = SessionRegistrationOutcomeResult.Exit(7);

            Assert.True(result.ShouldExit);
            Assert.Equal(7, result.ExitCode);
            Assert.Null(result.Registration);
        }

        [Fact]
        public void Outcome_Continue_carries_successful_registration_and_zero_exit_code()
        {
            var registration = SessionRegistrationResult.Succeeded(new RegisterSessionResponse
            {
                Success = true,
                AdminAction = null,
            });

            var result = SessionRegistrationOutcomeResult.Continue(registration);

            Assert.False(result.ShouldExit);
            Assert.Equal(0, result.ExitCode);
            Assert.Same(registration, result.Registration);
        }

        [Fact]
        public void RotateSession_rotates_id_updates_config_drops_spool_and_keeps_whiteglove_marker()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var persistence = new SessionIdPersistence(tmp.Path);
            var original = persistence.GetOrCreate();
            persistence.SaveWhiteGloveComplete();
            var config = new AgentConfiguration { ApiBaseUrl = "https://example.invalid", TenantId = "t", SessionId = original };

            var transportDir = Path.Combine(tmp.Path, "Spool");
            Directory.CreateDirectory(transportDir);
            File.WriteAllText(Path.Combine(transportDir, "spool.jsonl"), "{}");
            File.WriteAllText(Path.Combine(transportDir, "upload-cursor.json"), "{}");
            File.WriteAllText(Path.Combine(transportDir, "unrelated.txt"), "keep");

            var rotated = BackendSessionRegistration.RotateSession(config, persistence, auth: null, transportDir, logger);

            Assert.NotEqual(original, rotated);
            Assert.Equal(rotated, config.SessionId);
            Assert.Equal(rotated, new SessionIdPersistence(tmp.Path).GetOrCreate());
            Assert.True(persistence.IsWhiteGloveResume());
            Assert.False(File.Exists(Path.Combine(transportDir, "spool.jsonl")));
            Assert.False(File.Exists(Path.Combine(transportDir, "upload-cursor.json")));
            Assert.True(File.Exists(Path.Combine(transportDir, "unrelated.txt")));
        }

        [Fact]
        public void RotateSession_tolerates_a_missing_transport_directory()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var persistence = new SessionIdPersistence(tmp.Path);
            var config = new AgentConfiguration { ApiBaseUrl = "https://example.invalid", TenantId = "t", SessionId = persistence.GetOrCreate() };

            var rotated = BackendSessionRegistration.RotateSession(
                config, persistence, auth: null, Path.Combine(tmp.Path, "does-not-exist"), logger);

            Assert.Equal(rotated, config.SessionId);
        }

        [Fact]
        public void Register_throws_on_null_arguments()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var config = new AgentConfiguration { ApiBaseUrl = "https://example.invalid", TenantId = "t", SessionId = "s" };
            var auth = BackendClientFactory.BuildAuthClients(config, agentVersion: "1.0", logger: logger);

            using var http = new HttpClient();

            Assert.Throws<ArgumentNullException>(
                () => BackendSessionRegistration.Register(null, auth, http, "1.0", consoleMode: false, logger: logger));

            Assert.Throws<ArgumentNullException>(
                () => BackendSessionRegistration.Register(config, auth: null, http, "1.0", consoleMode: false, logger: logger));

            Assert.Throws<ArgumentNullException>(
                () => BackendSessionRegistration.Register(config, auth, http, "1.0", consoleMode: false, logger: null));
        }

        // ============================================================ failed-registration record

        [Fact]
        public void RecordFailedRun_records_a_failed_run_with_its_window_and_bounded_error()
        {
            using var tmp = new TempDirectory();
            var persistence = new SessionIdPersistence(tmp.Path);
            var started = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);

            var returned = BackendSessionRegistration.RecordFailedRun(
                persistence, SessionRegistrationResult.Failed(new string('e', 1000)),
                started, started.AddSeconds(1204.46), linkUpAtStart: false, linkUpAtEnd: true,
                configFetchOutcome: "UsedDefaults", networkFingerprint: null, logger: NewLogger(tmp.Path));

            var record = persistence.LoadRegistrationFailure();
            Assert.NotNull(record);
            Assert.Equal(1, record!.FailedRuns);
            Assert.Equal(0, record.InterceptedRuns);
            Assert.Equal("Failed", record.Outcome);
            Assert.Equal(BackendSessionRegistration.MaxRecordedErrorLength, record.LastError!.Length);
            Assert.False(record.NetworkLinkUpAtStart);
            Assert.True(record.NetworkLinkUpAtEnd);
            Assert.Equal(1204.5, record.AttemptWindowSeconds);
            Assert.Equal("UsedDefaults", record.ConfigFetchOutcome);
            Assert.Equal(started.AddSeconds(1204.46), record.LastFailedAtUtc);
            // The caller decides on the record as written, including this run.
            Assert.Equal(1, returned!.FailedRuns);
        }

        [Fact]
        public void RecordFailedRun_records_auth_failures_and_a_success_clears_them()
        {
            using var tmp = new TempDirectory();
            var persistence = new SessionIdPersistence(tmp.Path);
            var at = new DateTime(2026, 8, 20, 9, 0, 0, DateTimeKind.Utc);

            BackendSessionRegistration.RecordFailedRun(
                persistence, SessionRegistrationResult.AuthFailed(401, "unauthorized"),
                at, at.AddSeconds(3), true, true, "Succeeded", null, NewLogger(tmp.Path));
            Assert.Equal("AuthFailed", persistence.LoadRegistrationFailure()!.Outcome);

            BackendSessionRegistration.ClearAfterSuccess(persistence, () => null, NewLogger(tmp.Path));
            Assert.Null(persistence.LoadRegistrationFailure());
        }

        [Fact]
        public void Success_after_a_failed_start_tells_reboot_certificate_and_where_the_failure_began()
        {
            using var tmp = new TempDirectory();
            var persistence = new SessionIdPersistence(tmp.Path);
            var at = new DateTime(2026, 10, 21, 7, 0, 0, DateTimeKind.Utc);
            var failedBoot = at.AddMinutes(-16);

            BackendSessionRegistration.RecordFailedRun(
                persistence, SessionRegistrationResult.Failed("An error occurred while sending the request."),
                at, at.AddSeconds(90), true, true, "Succeeded", "fp-a", NewLogger(tmp.Path),
                new AutopilotMonitor.Agent.V2.Core.Monitoring.Transport.DistressDeviceContext("completed", "account_setup", failedBoot),
                certThumbprint: "6B98620E8BE952FEA7CD9200B899A9A8CCADD1E5");

            // The record carries phase and OOBE flag; boot and thumbprint stay beside it.
            var summary = persistence.LoadRegistrationFailure()!;
            Assert.Equal("account_setup", summary.EspPhase);
            Assert.Equal("completed", summary.OobeState);

            var prior = BackendSessionRegistration.ClearAfterSuccess(
                persistence, () => "fp-a", NewLogger(tmp.Path),
                currentBootUtc: at.AddHours(1), currentCertThumbprint: "6b98620e8be952fea7cd9200b899a9a8ccadd1e5");

            Assert.NotNull(prior);
            Assert.Equal("account_setup", prior!.LastEspPhase);
            Assert.Equal("completed", prior.LastOobeState);
            Assert.True(prior.RebootedSince);
            Assert.False(prior.CertChanged);
            Assert.False(prior.NetworkChanged);
        }

        [Fact]
        public void Same_boot_and_unknown_sides_are_reported_as_such()
        {
            var boot = new DateTime(2026, 10, 21, 6, 40, 0, DateTimeKind.Utc);
            Assert.False(BackendSessionRegistration.RebootedSince(boot, boot.AddSeconds(30)));
            Assert.True(BackendSessionRegistration.RebootedSince(boot, boot.AddMinutes(5)));
            Assert.Null(BackendSessionRegistration.RebootedSince(null, boot));
            Assert.Null(BackendSessionRegistration.RebootedSince(boot, null));
            Assert.True(BackendSessionRegistration.CertificateChanged("AA", "BB"));
            Assert.Null(BackendSessionRegistration.CertificateChanged(null, "BB"));
            Assert.Null(BackendSessionRegistration.CertificateChanged("AA", ""));
        }

        [Fact]
        public void Record_from_an_older_agent_leaves_the_new_comparisons_unknown()
        {
            using var tmp = new TempDirectory();
            var persistence = new SessionIdPersistence(tmp.Path);
            var at = new DateTime(2026, 10, 21, 7, 0, 0, DateTimeKind.Utc);

            // Written without context, boot or thumbprint — the shape agents up to 2.0.1487 leave.
            BackendSessionRegistration.RecordFailedRun(
                persistence, SessionRegistrationResult.Failed("x"), at, at.AddSeconds(5), true, true, null, null, NewLogger(tmp.Path));

            var prior = BackendSessionRegistration.ClearAfterSuccess(
                persistence, () => null, NewLogger(tmp.Path), at.AddHours(1), "AA");

            Assert.NotNull(prior);
            Assert.Null(prior!.LastEspPhase);
            Assert.Null(prior.RebootedSince);
            Assert.Null(prior.CertChanged);
        }

        [Fact]
        public void Recording_without_persistence_is_a_no_op()
        {
            using var tmp = new TempDirectory();
            Assert.Null(BackendSessionRegistration.RecordFailedRun(
                null, SessionRegistrationResult.Failed("x"), DateTime.UtcNow, DateTime.UtcNow,
                null, null, null, null, NewLogger(tmp.Path)));
            Assert.Null(BackendSessionRegistration.ClearAfterSuccess(null, () => null, NewLogger(tmp.Path)));
        }

        // ============================================================ emergency-break message

        [Fact]
        public void Break_message_names_failed_registrations_in_invariant_culture()
        {
            var previous = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
                var record = new RegistrationFailureSummary { FailedRuns = 3, Outcome = "Failed", NetworkLinkUpAtEnd = false };

                var message = EmergencyBreakReporter.BuildMessage(478.2, 48, record);

                Assert.Contains("session age 478.2h (cap 48h)", message);
                Assert.EndsWith(" 3 earlier start(s) failed to register (last: Failed, network link at end: down).", message);
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Fact]
        public void Break_message_without_record_keeps_the_historical_text()
        {
            Assert.Equal(
                "Agent absolute session-age emergency break fired at session age 50.0h (cap 48h) — cleaning up and exiting.",
                EmergencyBreakReporter.BuildMessage(50.0, 48, null));
        }
    }
}
