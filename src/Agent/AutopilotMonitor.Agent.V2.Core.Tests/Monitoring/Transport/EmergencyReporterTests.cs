using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Transport
{
    /// <summary>
    /// Pins the anti-flood semantics of <see cref="EmergencyReporter.TrySendAsync"/> after the
    /// delivery-hardening change (2026-07-23): the once-per-session dedup and the per-session
    /// budget count DELIVERED reports. The previous mark-before-send permanently suppressed an
    /// error key after its first transport failure — fatal for single-shot callers like the 48h
    /// emergency break, whose process exits right after the attempt.
    /// </summary>
    public class EmergencyReporterTests
    {
        private sealed class FakeApiClient : BackendApiClient
        {
            public readonly List<TimeSpan?> Timeouts = new List<TimeSpan?>();
            public readonly List<AgentErrorReport> Reports = new List<AgentErrorReport>();
            public readonly Queue<bool> Results = new Queue<bool>();

            public FakeApiClient() : base(
                httpClient: new HttpClient(),
                baseUrl: "http://localhost",
                manufacturer: string.Empty,
                model: string.Empty,
                serialNumber: string.Empty,
                useBootstrapTokenAuth: false,
                bootstrapToken: null,
                agentVersion: "0.0.0",
                logger: null)
            {
            }

            public int Calls => Timeouts.Count;

            public override Task<bool> ReportAgentErrorAsync(AgentErrorReport report, TimeSpan? timeout = null)
            {
                Timeouts.Add(timeout);
                Reports.Add(report);
                return Task.FromResult(Results.Count > 0 && Results.Dequeue());
            }
        }

        private static EmergencyReporter Reporter(FakeApiClient client)
            => new EmergencyReporter(client, "session", "tenant", "0.0.0", logger: null);

        private static EmergencyReporter Reporter(FakeApiClient client, VirtualClock clock)
            => new EmergencyReporter(client, "session", "tenant", "0.0.0", logger: null, clock: clock);

        private static VirtualClock Clock() => new VirtualClock(new DateTime(2026, 10, 9, 10, 0, 0, DateTimeKind.Utc));

        [Fact]
        public async Task Session_unknown_report_is_not_suppressed_by_the_cooldown_of_an_earlier_report()
        {
            // Terminal report, sent at most once per process right before the termination — a
            // routine report one minute earlier must not cost it. Every other type keeps the cooldown.
            var clock = Clock();
            var client = new FakeApiClient();
            client.Results.Enqueue(true);
            client.Results.Enqueue(true);
            client.Results.Enqueue(true);
            var reporter = Reporter(client, clock);

            await reporter.TrySendAsync(AgentErrorType.IngestFailed, "m", httpStatusCode: 503);
            clock.Advance(TimeSpan.FromMinutes(1));
            await reporter.TrySendAsync(AgentErrorType.TelemetryRejectedSessionUnknown, "m", httpStatusCode: 410);

            Assert.Equal(2, client.Calls);
            Assert.Equal(AgentErrorType.TelemetryRejectedSessionUnknown, client.Reports[1].ErrorType);

            // Control: a third report of another type inside the window is still held back.
            clock.Advance(TimeSpan.FromMinutes(1));
            await reporter.TrySendAsync(AgentErrorType.ConfigFetchFailed, "m");
            Assert.Equal(2, client.Calls);
        }

        [Fact]
        public async Task Second_session_unknown_report_within_the_window_is_still_suppressed()
        {
            var clock = Clock();
            var client = new FakeApiClient();
            client.Results.Enqueue(true);
            client.Results.Enqueue(true);
            var reporter = Reporter(client, clock);

            await reporter.TrySendAsync(AgentErrorType.TelemetryRejectedSessionUnknown, "m", httpStatusCode: 410);
            clock.Advance(TimeSpan.FromMinutes(1));
            await reporter.TrySendAsync(AgentErrorType.TelemetryRejectedSessionUnknown, "m", httpStatusCode: 410);

            Assert.Equal(1, client.Calls);
        }

        [Fact]
        public async Task Session_unknown_report_still_counts_against_the_per_session_maximum()
        {
            // Five delivered reports (ten minutes apart, distinct keys) exhaust the budget; the
            // exemption waives only the cooldown, not the maximum.
            var clock = Clock();
            var client = new FakeApiClient();
            for (var i = 0; i < 6; i++) client.Results.Enqueue(true);
            var reporter = Reporter(client, clock);

            foreach (var status in new[] { 500, 502, 503, 504, 429 })
            {
                await reporter.TrySendAsync(AgentErrorType.IngestFailed, "m", httpStatusCode: status);
                clock.Advance(TimeSpan.FromMinutes(10));
            }
            Assert.Equal(5, client.Calls);

            await reporter.TrySendAsync(AgentErrorType.TelemetryRejectedSessionUnknown, "m", httpStatusCode: 410);

            Assert.Equal(5, client.Calls);
        }

        [Fact]
        public async Task Failed_send_rolls_back_reservation_so_a_later_call_retries()
        {
            var client = new FakeApiClient();
            client.Results.Enqueue(false); // first call fails
            client.Results.Enqueue(true);  // second call succeeds
            var reporter = Reporter(client);

            await reporter.TrySendAsync(AgentErrorType.SessionAgeEmergencyBreak, "m");
            await reporter.TrySendAsync(AgentErrorType.SessionAgeEmergencyBreak, "m");

            // Old behaviour: second call suppressed as "already reported" ⇒ 1 call.
            Assert.Equal(2, client.Calls);
        }

        [Fact]
        public async Task Delivered_send_is_deduplicated_per_key()
        {
            var client = new FakeApiClient();
            client.Results.Enqueue(true);
            var reporter = Reporter(client);

            await reporter.TrySendAsync(AgentErrorType.SessionAgeEmergencyBreak, "m");
            await reporter.TrySendAsync(AgentErrorType.SessionAgeEmergencyBreak, "m");

            Assert.Equal(1, client.Calls);
        }

        [Fact]
        public async Task Retries_stay_inside_one_reservation_and_stop_on_success()
        {
            var client = new FakeApiClient();
            client.Results.Enqueue(false);
            client.Results.Enqueue(true); // second attempt delivers — third must not fire
            var reporter = Reporter(client);

            await reporter.TrySendAsync(
                AgentErrorType.SessionAgeEmergencyBreak, "m",
                attempts: 3, perAttemptTimeout: TimeSpan.FromMilliseconds(50), retryDelay: TimeSpan.Zero);

            Assert.Equal(2, client.Calls);

            // Delivered ⇒ dedup holds for the session.
            await reporter.TrySendAsync(AgentErrorType.SessionAgeEmergencyBreak, "m");
            Assert.Equal(2, client.Calls);
        }

        [Fact]
        public async Task Per_attempt_timeout_is_forwarded_to_the_api_client()
        {
            var client = new FakeApiClient();
            client.Results.Enqueue(true);
            var reporter = Reporter(client);

            var timeout = TimeSpan.FromSeconds(15);
            await reporter.TrySendAsync(
                AgentErrorType.SessionAgeEmergencyBreak, "m",
                attempts: 1, perAttemptTimeout: timeout);

            Assert.Equal(timeout, Assert.Single(client.Timeouts));
        }

        [Fact]
        public async Task All_attempts_failing_rolls_back_after_exhausting_attempts()
        {
            var client = new FakeApiClient(); // queue empty ⇒ every attempt fails
            var reporter = Reporter(client);

            await reporter.TrySendAsync(
                AgentErrorType.SessionAgeEmergencyBreak, "m",
                attempts: 3, retryDelay: TimeSpan.Zero);
            Assert.Equal(3, client.Calls);

            // Rolled back ⇒ a later call may try again (budget + dedup + cooldown restored).
            client.Results.Enqueue(true);
            await reporter.TrySendAsync(AgentErrorType.SessionAgeEmergencyBreak, "m");
            Assert.Equal(4, client.Calls);
        }

        [Fact]
        public async Task Different_keys_do_not_suppress_each_other_after_rollback()
        {
            var client = new FakeApiClient();
            client.Results.Enqueue(false); // break report fails → rollback (incl. cooldown restore)
            client.Results.Enqueue(true);  // unrelated report must still go out
            var reporter = Reporter(client);

            await reporter.TrySendAsync(AgentErrorType.SessionAgeEmergencyBreak, "m");
            await reporter.TrySendAsync(AgentErrorType.ConfigFetchFailed, "m");

            Assert.Equal(2, client.Calls);
        }

        [Fact]
        public async Task Session_unknown_report_carries_error_type_and_http_410()
        {
            var client = new FakeApiClient();
            client.Results.Enqueue(true);
            var reporter = Reporter(client);

            await reporter.TrySendAsync(AgentErrorType.TelemetryRejectedSessionUnknown, "m", httpStatusCode: 410);

            var report = Assert.Single(client.Reports);
            Assert.Equal(AgentErrorType.TelemetryRejectedSessionUnknown, report.ErrorType);
            Assert.Equal(410, report.HttpStatusCode);
        }

        [Fact]
        public async Task Session_age_is_carried_in_the_report_and_absent_by_default()
        {
            // One reporter per report: a delivered report starts the 10-minute cooldown.
            var client = new FakeApiClient();
            client.Results.Enqueue(true);
            client.Results.Enqueue(true);

            await Reporter(client).TrySendAsync(AgentErrorType.SessionAgeEmergencyBreak, "m", sessionAgeHours: 478.2);
            await Reporter(client).TrySendAsync(AgentErrorType.ConfigFetchFailed, "m");

            Assert.Equal(2, client.Reports.Count);
            Assert.Equal(478.2, client.Reports[0].SessionAgeHours);
            Assert.Null(client.Reports[1].SessionAgeHours);
        }

        [Fact]
        public async Task Prior_registration_failure_is_carried_in_the_report_and_absent_by_default()
        {
            var client = new FakeApiClient();
            client.Results.Enqueue(true);
            client.Results.Enqueue(true);
            var record = new RegistrationFailureSummary { FailedRuns = 2, Outcome = "Failed", NetworkLinkUpAtEnd = false };

            await Reporter(client).TrySendAsync(AgentErrorType.SessionAgeEmergencyBreak, "m", priorRegistrationFailure: record);
            await Reporter(client).TrySendAsync(AgentErrorType.ConfigFetchFailed, "m");

            Assert.Equal(2, client.Reports.Count);
            Assert.Same(record, client.Reports[0].PriorRegistrationFailure);
            Assert.Null(client.Reports[1].PriorRegistrationFailure);
        }
    }
}
