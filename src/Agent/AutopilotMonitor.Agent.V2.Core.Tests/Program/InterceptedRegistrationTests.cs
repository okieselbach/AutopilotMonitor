using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Transport;
using AutopilotMonitor.Agent.V2.Runtime;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;
using ProgramAlias = AutopilotMonitor.Agent.V2.Program;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// A registration that something on the network path answered at process start (client
    /// certificate interception plan, K4/K5): a buffer of further boots, then the emergency break's
    /// cleanup; the pending ClientCertificateNotReceived distress leaves the machine first; a later
    /// successful registration reports the failed starts on <c>agent_started</c>. Each start is
    /// ended the way <see cref="BackendSessionRegistration.Register"/> ends it: record the failed
    /// run, then conclude it.
    /// </summary>
    public sealed class InterceptedRegistrationTests : IDisposable
    {
        private static readonly DateTime At = new DateTime(2026, 10, 12, 9, 0, 0, DateTimeKind.Utc);

        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly AgentLogger _logger;
        private readonly SessionIdPersistence _persistence;
        private readonly string _stateDir;
        private int _cleanupCalls;

        public InterceptedRegistrationTests()
        {
            _logger = new AgentLogger(Path.Combine(_tmp.Path, "logs"), AgentLogLevel.Info);
            _persistence = new SessionIdPersistence(_tmp.Path);
            _persistence.GetOrCreate(_logger);
            _stateDir = Path.Combine(_tmp.Path, "State");
        }

        public void Dispose() => _tmp.Dispose();

        // ============================================================ K4: buffer, then clean up

        [Fact]
        public void Agent_default_buffer_allows_one_further_boot_then_cleans_up()
        {
            var config = new AgentConfiguration();
            Assert.Equal(1, config.InterceptedRegistrationRetryBoots);

            Assert.Equal(6, Boot(Intercepted(), config));

            Assert.False(GaveUp);
            Assert.True(_persistence.SessionExists());
            Assert.Equal(0, _cleanupCalls);
            var record = _persistence.LoadRegistrationFailure()!;
            Assert.Equal(1, record.FailedRuns);
            Assert.Equal(1, record.InterceptedRuns);
            Assert.Equal("AuthFailed", record.Outcome);

            Assert.Equal(6, Boot(Intercepted(), config)); // the give-up keeps exit 6

            AssertCleanedUpLikeTheEmergencyBreak(expectedCleanupCalls: 1);
        }

        [Theory]
        [InlineData(0)] // clean up at the first intercepted start
        [InlineData(2)]
        [InlineData(5)]
        public void Gives_up_on_the_first_intercepted_start_beyond_the_buffer(int buffer)
        {
            var config = new AgentConfiguration { InterceptedRegistrationRetryBoots = buffer };

            for (var start = 1; start <= buffer; start++)
            {
                Assert.Equal(6, Boot(Intercepted(), config));
                Assert.False(GaveUp, $"gave up on start {start} with a buffer of {buffer}");
            }

            Assert.Equal(6, Boot(Intercepted(), config));

            AssertCleanedUpLikeTheEmergencyBreak(expectedCleanupCalls: 1);
        }

        [Theory]
        [InlineData(true, 1)]
        [InlineData(false, 0)]
        public void Give_up_runs_the_cleanup_only_with_SelfDestructOnComplete_like_the_emergency_break(
            bool selfDestructOnComplete, int expectedCleanupCalls)
        {
            var config = new AgentConfiguration
            {
                InterceptedRegistrationRetryBoots = 0,
                SelfDestructOnComplete = selfDestructOnComplete,
            };

            Assert.Equal(6, Boot(Intercepted(), config));

            AssertCleanedUpLikeTheEmergencyBreak(expectedCleanupCalls);
        }

        [Fact]
        public async Task An_intercepted_start_takes_the_buffer_from_the_cached_config()
        {
            // The config fetch of an intercepted start is intercepted too: it falls back to the
            // cache, and the registration decides on the cached buffer, not the agent default.
            var config = new AgentConfiguration();
            var service = new InterceptedConfigService(_logger, cached: new AgentConfigResponse
            {
                InterceptedRegistrationRetryBoots = 0,
                SelfDestructOnComplete = true,
            });

            var fetched = await service.FetchConfigAsync(retryOnTransientErrors: true);
            Assert.Equal(RemoteConfigFetchOutcome.FromCache, service.LastFetchOutcome);
            RemoteConfigMerger.Merge(config, fetched, _logger);
            Assert.Equal(0, config.InterceptedRegistrationRetryBoots);

            Assert.Equal(6, Boot(Intercepted(), config));

            AssertCleanedUpLikeTheEmergencyBreak(expectedCleanupCalls: 1);
        }

        [Fact]
        public void Backend_rejection_and_network_failure_keep_their_exit_codes_and_never_give_up()
        {
            var config = new AgentConfiguration { InterceptedRegistrationRetryBoots = 0 };

            Assert.Equal(6, Boot(BackendRejected(), config));
            Assert.Equal(7, Boot(NetworkFailure(), config));

            Assert.False(GaveUp);
            Assert.True(_persistence.SessionExists());
            Assert.Equal(0, _cleanupCalls);
            var record = _persistence.LoadRegistrationFailure()!;
            Assert.Equal(2, record.FailedRuns);
            Assert.Equal(0, record.InterceptedRuns);
        }

        [Fact]
        public void Only_an_intercepted_start_gives_up_even_when_the_count_already_exceeds_the_buffer()
        {
            var config = new AgentConfiguration { InterceptedRegistrationRetryBoots = 5 };
            Boot(Intercepted(), config);
            Boot(Intercepted(), config);
            config.InterceptedRegistrationRetryBoots = 0; // e.g. a later cached config lowered it

            Assert.Equal(6, Boot(BackendRejected(), config));
            Assert.Equal(7, Boot(NetworkFailure(), config));

            Assert.False(GaveUp);
            Assert.Equal(0, _cleanupCalls);
            var record = _persistence.LoadRegistrationFailure()!;
            Assert.Equal(4, record.FailedRuns);
            Assert.Equal(2, record.InterceptedRuns); // unchanged by the other failures
            Assert.Equal("Failed", record.Outcome);
        }

        [Fact]
        public void Failures_of_another_kind_between_interceptions_do_not_reset_the_count()
        {
            var config = new AgentConfiguration(); // buffer 1

            Assert.Equal(6, Boot(Intercepted(), config));
            Assert.Equal(7, Boot(NetworkFailure(), config));
            Assert.Equal(6, Boot(BackendRejected(), config));
            Assert.False(GaveUp);

            Assert.Equal(6, Boot(Intercepted(), config)); // second interception since the last success

            AssertCleanedUpLikeTheEmergencyBreak(expectedCleanupCalls: 1);
        }

        [Fact]
        public void Only_a_successful_registration_resets_the_count()
        {
            var config = new AgentConfiguration(); // buffer 1
            Boot(Intercepted(), config);
            BackendSessionRegistration.ClearAfterSuccess(_persistence, () => "fp-office", _logger);

            Assert.Equal(6, Boot(Intercepted(), config));

            Assert.False(GaveUp);
            Assert.Equal(1, _persistence.LoadRegistrationFailure()!.InterceptedRuns);
        }

        [Fact]
        public void Without_a_written_record_the_buffer_cannot_be_enforced_and_the_next_boot_retries()
        {
            var config = new AgentConfiguration { InterceptedRegistrationRetryBoots = 0 };

            var exit = BackendSessionRegistration.ConcludeFailedRun(
                Intercepted(), record: null, config, authFailureTracker: null,
                _persistence, _stateDir, CleanupFactory, _logger);

            Assert.Equal(6, exit);
            Assert.False(GaveUp);
            Assert.True(_persistence.SessionExists());
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task The_registration_answer_decides_whether_the_start_counts_as_intercepted(bool foreign)
        {
            // Through the real client: no correlation echo + the platform's "Client Certificate
            // Required" page is an interception; a JSON 403 that echoes the id is the backend's verdict.
            using var handler = new RecordingHttpMessageHandler();
            if (foreign)
                handler.QueueForeign(HttpStatusCode.Forbidden, InterceptionFixtures.CertificateRequiredPage);
            else
                handler.QueueStatus(HttpStatusCode.Forbidden, "{\"error\":\"Device validation failed\"}");
            using var http = new HttpClient(handler);
            var apiClient = new BackendApiClient(
                http, "https://backend.test", manufacturer: "m", model: "d", serialNumber: "s",
                useBootstrapTokenAuth: false, bootstrapToken: null, agentVersion: "0.0.0", logger: _logger);
            var distress = new FakeDistressReporter();
            var config = new AgentConfiguration
            {
                ApiBaseUrl = "https://backend.test",
                TenantId = "00000000-0000-0000-0000-000000000001",
                SessionId = _persistence.GetOrCreate(_logger),
                InterceptedRegistrationRetryBoots = 0,
            };

            var result = await SessionRegistrationHelper.RegisterWithRetryAsync(
                apiClient, config, agentVersion: "0.0.0", logger: _logger,
                authFailureTracker: NewTracker(distress),
                deviceHardware: ("m", "d", "s"),
                networkLinkWait: _ => Task.CompletedTask);

            Assert.Equal(SessionRegistrationOutcome.AuthFailed, result.Outcome);
            Assert.Equal(foreign, result.IsIntercepted);
            Assert.Equal(
                foreign ? DistressErrorType.ClientCertificateNotReceived : DistressErrorType.DeviceNotRegistered,
                Assert.Single(distress.Calls).ErrorType);

            Assert.Equal(6, Boot(result, config));
            Assert.Equal(foreign, GaveUp);
        }

        // ============================================================ the distress leaves first

        [Fact]
        public void The_exit_waits_for_the_pending_ClientCertificateNotReceived_send()
        {
            var record = RecordFailed(Intercepted(), "fp-office");
            var (tracker, inFlight) = DispatchSlowCertificateDistress();

            var exit = BackendSessionRegistration.ConcludeFailedRun(
                Intercepted(), record, new AgentConfiguration(), tracker,
                _persistence, _stateDir, CleanupFactory, _logger);

            Assert.Equal(6, exit);
            Assert.True(inFlight.IsCompleted);
            Assert.False(GaveUp);
        }

        [Fact]
        public void The_give_up_waits_for_the_pending_send_before_it_cleans_up()
        {
            var record = RecordFailed(Intercepted(), "fp-office");
            var (tracker, inFlight) = DispatchSlowCertificateDistress();
            bool? sentBeforeCleanup = null;

            var exit = BackendSessionRegistration.ConcludeFailedRun(
                Intercepted(), record, new AgentConfiguration { InterceptedRegistrationRetryBoots = 0 }, tracker,
                _persistence, _stateDir,
                () =>
                {
                    sentBeforeCleanup = inFlight.IsCompleted;
                    throw new InvalidOperationException("test-harness: cleanup factory must not spawn real PowerShell in unit tests");
                },
                _logger);

            Assert.Equal(6, exit);
            Assert.True(sentBeforeCleanup);
            Assert.True(GaveUp);
        }

        [Fact]
        public void The_wait_for_the_distress_is_bounded_by_its_own_http_timeout()
        {
            Assert.True(BackendSessionRegistration.PendingDistressBound <= TimeSpan.FromSeconds(5));
        }

        // ============================================================ K5: measurement point

        [Theory]
        [InlineData("fp-office", "fp-office", false)]
        [InlineData("fp-office", "fp-home", true)]
        public void A_success_after_failed_starts_reports_the_record_it_clears(
            string failedOn, string registeredOn, bool networkChanged)
        {
            var config = new AgentConfiguration { InterceptedRegistrationRetryBoots = 5 };
            Boot(NetworkFailure(), config, fingerprint: "fp-elsewhere");
            Boot(Intercepted(), config, fingerprint: failedOn); // the latest failed start's network counts

            var prior = BackendSessionRegistration.ClearAfterSuccess(_persistence, () => registeredOn, _logger)!;

            Assert.Equal(2, prior.FailedRuns);
            Assert.Equal(1, prior.InterceptedRuns);
            Assert.Equal("AuthFailed", prior.LastOutcome);
            Assert.Equal(networkChanged, prior.NetworkChanged);
            Assert.Null(_persistence.LoadRegistrationFailureRecord());

            var data = EmitAgentStarted(prior);
            Assert.Equal(2, data["priorRegistrationFailedRuns"]);
            Assert.Equal(1, data["priorRegistrationInterceptedRuns"]);
            Assert.Equal("AuthFailed", data["priorRegistrationLastOutcome"]);
            Assert.Equal(networkChanged, data["priorRegistrationNetworkChanged"]);
        }

        [Theory]
        [InlineData(null, "fp-home")]   // the failed start's network was unknown
        [InlineData("fp-office", null)] // this start's network is unknown
        public void Network_change_is_omitted_when_either_fingerprint_is_unknown(string? failedOn, string? registeredOn)
        {
            Boot(Intercepted(), new AgentConfiguration(), fingerprint: failedOn);

            var prior = BackendSessionRegistration.ClearAfterSuccess(_persistence, () => registeredOn!, _logger)!;

            Assert.Null(prior.NetworkChanged);
            var data = EmitAgentStarted(prior);
            Assert.Equal(1, data["priorRegistrationFailedRuns"]);
            Assert.False(data.ContainsKey("priorRegistrationNetworkChanged"));
        }

        [Fact]
        public void A_success_without_failed_starts_reports_nothing_and_never_reads_the_network()
        {
            var captures = 0;

            var prior = BackendSessionRegistration.ClearAfterSuccess(
                _persistence, () => { captures++; return "fp-office"; }, _logger);

            Assert.Null(prior);
            Assert.Equal(0, captures);
            Assert.DoesNotContain(EmitAgentStarted(null).Keys, k => k.StartsWith("priorRegistration", StringComparison.Ordinal));
        }

        [Fact]
        public void The_network_fingerprint_stays_on_the_device()
        {
            Boot(Intercepted(), new AgentConfiguration(), fingerprint: "0123456789abcdef");

            // Kept locally beside the record ...
            Assert.Contains("0123456789abcdef", File.ReadAllText(Path.Combine(_tmp.Path, "registration-failure.json")));
            Assert.Equal("0123456789abcdef", _persistence.LoadRegistrationFailureRecord()!.NetworkFingerprint);

            // ... while the summary the emergency break sends cannot carry it.
            var wire = JsonConvert.SerializeObject(new AgentErrorReport
            {
                PriorRegistrationFailure = _persistence.LoadRegistrationFailure(),
            });
            Assert.DoesNotContain("0123456789abcdef", wire);
            Assert.DoesNotContain("Fingerprint", wire);
            Assert.Contains("\"InterceptedRuns\":1", wire);
        }

        // ============================================================ helpers

        private static SessionRegistrationResult Intercepted() =>
            SessionRegistrationResult.AuthFailed(403, "The device certificate did not reach the backend.", intercepted: true);

        private static SessionRegistrationResult BackendRejected() =>
            SessionRegistrationResult.AuthFailed(403, "Backend returned 403 Forbidden. The device is not authorized.");

        private static SessionRegistrationResult NetworkFailure() =>
            SessionRegistrationResult.Failed("No such host is known.");

        private bool GaveUp => File.Exists(Path.Combine(_stateDir, "enrollment-complete.marker"));

        private AuthFailureTracker NewTracker(FakeDistressReporter distress) =>
            new AuthFailureTracker(maxFailures: 5, timeoutMinutes: 0, SystemClock.Instance, _logger, distress);

        /// <summary>
        /// What the registration helper does when the answer did not come from the backend: the
        /// tracker dispatches ClientCertificateNotReceived — here a send that stays in flight for a
        /// second, started right before the conclusion so nothing else can use the time up.
        /// </summary>
        private (AuthFailureTracker Tracker, Task InFlight) DispatchSlowCertificateDistress()
        {
            Task? inFlight = null;
            var distress = new FakeDistressReporter { Send = () => inFlight = Task.Delay(TimeSpan.FromSeconds(1)) };
            var tracker = NewTracker(distress);
            tracker.RecordFailure(403, "agent/register-session", InterceptionFixtures.CertificateRequired());

            Assert.Equal(DistressErrorType.ClientCertificateNotReceived, Assert.Single(distress.Calls).ErrorType);
            Assert.False(inFlight!.IsCompleted);
            return (tracker, inFlight);
        }

        private RegistrationFailureSummary? RecordFailed(SessionRegistrationResult result, string? fingerprint) =>
            BackendSessionRegistration.RecordFailedRun(
                _persistence, result, At, At.AddSeconds(2), linkUpAtStart: true, linkUpAtEnd: true,
                configFetchOutcome: "FromCache", networkFingerprint: fingerprint, logger: _logger);

        /// <summary>One start whose registration failed with <paramref name="result"/>, ended the way Register ends it.</summary>
        private int Boot(SessionRegistrationResult result, AgentConfiguration config, string? fingerprint = "fp-office")
        {
            var record = RecordFailed(result, fingerprint);
            return BackendSessionRegistration.ConcludeFailedRun(
                result, record, config, authFailureTracker: null, _persistence, _stateDir, CleanupFactory, _logger);
        }

        private CleanupService CleanupFactory()
        {
            _cleanupCalls++;
            // The real factory spawns a PowerShell cleanup script; throwing makes the cleanup retry
            // swallow and log it, so the call is counted without a side effect (as in ProgramGuardsTests).
            throw new InvalidOperationException("test-harness: cleanup factory must not spawn real PowerShell in unit tests");
        }

        /// <summary>The emergency break's end state (ProgramGuardsTests): marker, session files gone, cleanup per SelfDestructOnComplete.</summary>
        private void AssertCleanedUpLikeTheEmergencyBreak(int expectedCleanupCalls)
        {
            Assert.True(GaveUp);
            Assert.False(_persistence.SessionExists());
            Assert.Null(_persistence.LoadRegistrationFailure());
            Assert.Equal(expectedCleanupCalls, _cleanupCalls);
            // The next start ends at the bootstrap guard, exactly as after the break.
            Assert.True(ProgramAlias.CheckEnrollmentCompleteMarker(
                _stateDir, selfDestructOnComplete: false, cleanupServiceFactory: null, _logger, consoleMode: false));
        }

        private Dictionary<string, object> EmitAgentStarted(PriorRegistrationFailures? prior)
        {
            var sink = new FakeSignalIngressSink();
            LifecycleEmitters.EmitAgentStarted(
                new InformationalEventPost(sink, SystemClock.Instance, _logger),
                new AgentConfiguration { SessionId = "S1", TenantId = "00000000-0000-0000-0000-000000000001", CommandLineArgs = string.Empty },
                new ProgramAlias.PreviousExitSummary { ExitType = "reboot_kill" },
                "2.0.test",
                remoteConfigService: null,
                _logger,
                priorRegistrationFailures: prior);
            return (Dictionary<string, object>)Assert.Single(sink.Posted).TypedPayload!;
        }

        /// <summary>A start whose config fetch is answered by the interception: it falls back to the cache.</summary>
        private sealed class InterceptedConfigService : RemoteConfigService
        {
            private readonly AgentConfigResponse _cached;

            public InterceptedConfigService(AgentLogger logger, AgentConfigResponse cached)
                : base(
                    new BackendApiClient(
                        httpClient: new HttpClient(), baseUrl: "http://localhost", manufacturer: string.Empty,
                        model: string.Empty, serialNumber: string.Empty, useBootstrapTokenAuth: false,
                        bootstrapToken: null, agentVersion: "0.0.0", logger: logger),
                    "00000000-0000-0000-0000-000000000001",
                    logger)
            {
                _cached = cached;
            }

            protected override Task<AgentConfigResponse> CallBackendAsync() =>
                throw new BackendAuthException("The device certificate did not reach the backend.", 403,
                    classification: InterceptionFixtures.CertificateRequired());

            protected override AgentConfigResponse LoadCachedConfig() => _cached;

            protected override Task WaitForNetworkLinkAsync() => Task.CompletedTask;
        }
    }
}
