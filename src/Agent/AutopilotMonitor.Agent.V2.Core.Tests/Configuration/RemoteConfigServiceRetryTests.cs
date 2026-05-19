using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Configuration
{
    /// <summary>
    /// Pins the §observability retry + outcome-state contract on
    /// <see cref="RemoteConfigService"/>. Motivating incident: session
    /// 8f2bef72 (2026-05-19) where a Function App cold-start after a deploy
    /// stalled the single-shot GetAgentConfig for 60s, the client gave up,
    /// the agent silently fell back to <c>ConfigVersion=0</c> defaults, and
    /// the entire enrollment ran the wrong policy (Mode=Off, SelfDestruct=true)
    /// with no wire-visible signal of the fallback.
    /// <para>
    /// Tests override the two protected seams (<c>CallBackendAsync</c> +
    /// <c>DelayBetweenAttemptsAsync</c>) so the retry suite runs in
    /// milliseconds instead of the live 10/30/60-second cadence.
    /// </para>
    /// </summary>
    public class RemoteConfigServiceRetryTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly AgentLogger _logger;

        public RemoteConfigServiceRetryTests()
        {
            _logger = new AgentLogger(Path.Combine(_tmp.Path, "logs"), AgentLogLevel.Info);
        }

        public void Dispose() => _tmp.Dispose();

        // ── Retry behaviour ─────────────────────────────────────────────────

        [Fact]
        public async Task FetchConfig_SucceedsOnFirstAttempt_NoRetry()
        {
            var svc = NewService(BackendBehaviour.SuccessImmediately);

            var result = await svc.FetchConfigAsync(retryOnTransientErrors: true);

            Assert.NotNull(result);
            Assert.Equal(42, result.ConfigVersion);
            Assert.Equal(RemoteConfigFetchOutcome.Succeeded, svc.LastFetchOutcome);
            Assert.Equal(1, svc.LastFetchAttempts);
            Assert.Null(svc.LastFetchFailureType);
        }

        [Fact]
        public async Task FetchConfig_TransientFailure_RetriesUpToCap()
        {
            // Always-fail transient → 4 attempts total (1 initial + 3 retries from
            // InitialFetchRetryBackoff.Length=3). Then fall back to defaults.
            var svc = NewService(BackendBehaviour.AlwaysTransientFailure);

            var result = await svc.FetchConfigAsync(retryOnTransientErrors: true);

            Assert.NotNull(result);
            Assert.Equal(0, result.ConfigVersion); // built-in defaults
            Assert.Equal(RemoteConfigFetchOutcome.UsedDefaults, svc.LastFetchOutcome);
            Assert.Equal(1 + RemoteConfigService.InitialFetchRetryBackoff.Length, svc.LastFetchAttempts);
            Assert.Equal(nameof(HttpRequestException), svc.LastFetchFailureType);
            Assert.NotEmpty(svc.LastFetchFailureMessage);
        }

        [Fact]
        public async Task FetchConfig_TransientThenSuccess_Returns_LiveConfig()
        {
            // Reproduces the cold-start pattern: first call times out, second succeeds.
            // The whole reason this retry mechanism exists.
            var svc = NewService(BackendBehaviour.FailTwiceThenSucceed);

            var result = await svc.FetchConfigAsync(retryOnTransientErrors: true);

            Assert.Equal(42, result.ConfigVersion);
            Assert.Equal(RemoteConfigFetchOutcome.Succeeded, svc.LastFetchOutcome);
            Assert.Equal(3, svc.LastFetchAttempts);
        }

        [Fact]
        public async Task FetchConfig_AuthFailure_BailsImmediately_NoRetry()
        {
            // 401/403 won't change with a retry — failing fast is correct. Verifies the
            // retry loop's only short-circuit branch + that the status code is captured.
            var svc = NewService(BackendBehaviour.AlwaysAuthFailure401);

            var result = await svc.FetchConfigAsync(retryOnTransientErrors: true);

            Assert.NotNull(result); // fell back to defaults
            Assert.Equal(RemoteConfigFetchOutcome.UsedDefaults, svc.LastFetchOutcome);
            Assert.Equal(1, svc.LastFetchAttempts);
            Assert.Equal(nameof(BackendAuthException), svc.LastFetchFailureType);
            Assert.Equal(401, svc.LastFetchAuthStatusCode);
        }

        [Fact]
        public async Task FetchConfig_RetryDisabled_OneAttemptOnly()
        {
            // ServerControlPlane's rotate_config path uses single-shot to avoid blocking
            // the action handler for 100 s on a flaky backend.
            var svc = NewService(BackendBehaviour.AlwaysTransientFailure);

            var result = await svc.FetchConfigAsync(retryOnTransientErrors: false);

            Assert.Equal(RemoteConfigFetchOutcome.UsedDefaults, svc.LastFetchOutcome);
            Assert.Equal(1, svc.LastFetchAttempts);
        }

        [Fact]
        public async Task FetchConfig_EmptyResponse_TreatedAsTransient()
        {
            // A backend bug that returns null instead of throwing — fall through to
            // retry so a transient null doesn't strand the agent on defaults.
            var svc = NewService(BackendBehaviour.AlwaysReturnNull);

            var result = await svc.FetchConfigAsync(retryOnTransientErrors: true);

            Assert.Equal(RemoteConfigFetchOutcome.UsedDefaults, svc.LastFetchOutcome);
            // 1 initial + 3 retries = 4 attempts on always-null
            Assert.Equal(4, svc.LastFetchAttempts);
        }

        // ── Outcome state defaults ──────────────────────────────────────────

        [Fact]
        public void NotAttempted_BeforeFirstFetch()
        {
            var svc = NewService(BackendBehaviour.SuccessImmediately);
            Assert.Equal(RemoteConfigFetchOutcome.NotAttempted, svc.LastFetchOutcome);
            Assert.Equal(0, svc.LastFetchAttempts);
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private enum BackendBehaviour
        {
            SuccessImmediately,
            AlwaysTransientFailure,
            FailTwiceThenSucceed,
            AlwaysAuthFailure401,
            AlwaysReturnNull,
        }

        private FakeRemoteConfigService NewService(BackendBehaviour behaviour)
        {
            // Pass a throwaway BackendApiClient — the fake overrides CallBackendAsync
            // before it ever hits the wire.
            var apiClient = new BackendApiClient(
                httpClient: new HttpClient(),
                baseUrl: "http://localhost",
                manufacturer: string.Empty,
                model: string.Empty,
                serialNumber: string.Empty,
                useBootstrapTokenAuth: false,
                bootstrapToken: null,
                agentVersion: "0.0.0",
                logger: _logger);
            return new FakeRemoteConfigService(apiClient, _logger, behaviour);
        }

        private sealed class FakeRemoteConfigService : RemoteConfigService
        {
            private readonly BackendBehaviour _behaviour;
            private int _calls;

            public FakeRemoteConfigService(BackendApiClient apiClient, AgentLogger logger, BackendBehaviour behaviour)
                : base(apiClient, "00000000-0000-0000-0000-000000000001", logger)
            {
                _behaviour = behaviour;
            }

            protected override Task<AgentConfigResponse> CallBackendAsync()
            {
                _calls++;
                switch (_behaviour)
                {
                    case BackendBehaviour.SuccessImmediately:
                        return Task.FromResult(new AgentConfigResponse { ConfigVersion = 42 });
                    case BackendBehaviour.FailTwiceThenSucceed:
                        if (_calls < 3)
                            throw new HttpRequestException("simulated cold-start timeout");
                        return Task.FromResult(new AgentConfigResponse { ConfigVersion = 42 });
                    case BackendBehaviour.AlwaysTransientFailure:
                        throw new HttpRequestException("simulated network failure");
                    case BackendBehaviour.AlwaysAuthFailure401:
                        throw new BackendAuthException("Unauthorized", 401);
                    case BackendBehaviour.AlwaysReturnNull:
                        return Task.FromResult<AgentConfigResponse>(null);
                    default:
                        throw new InvalidOperationException("unknown behaviour");
                }
            }

            // Zero-delay so the suite finishes in ms instead of the live 10/30/60 s schedule.
            protected override Task DelayBetweenAttemptsAsync(TimeSpan delay) => Task.CompletedTask;

            // In-memory + per-instance cache so a prior test's successful fetch can't
            // bleed into a later test that expects UsedDefaults.
            private AgentConfigResponse _cached;
            protected override void CacheConfig(AgentConfigResponse config) { _cached = config; }
            protected override AgentConfigResponse LoadCachedConfig() => _cached;
        }
    }
}
