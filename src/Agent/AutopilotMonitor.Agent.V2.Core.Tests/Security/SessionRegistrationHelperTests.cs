using System;
using System.IO;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Security
{
    /// <summary>
    /// V1 parity guard for <see cref="SessionRegistrationHelper"/>. The helper's retry + short-
    /// circuit behaviour is the single replacement for V1 <c>MonitoringService.RegisterSessionAsync</c>.
    /// If these tests break, production enrollment registration is at risk.
    /// </summary>
    public sealed class SessionRegistrationHelperTests
    {
        private static AgentLogger NewLogger(TempDirectory tmp)
            => new AgentLogger(Path.Combine(tmp.Path, "logs"), AgentLogLevel.Debug);

        private static AgentConfiguration NewConfig() => new AgentConfiguration
        {
            ApiBaseUrl = "https://example.test",
            SessionId = Guid.NewGuid().ToString(),
            TenantId = Guid.NewGuid().ToString(),
        };

        [Fact]
        public async Task Succeeds_on_first_response_success()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);
            var config = NewConfig();

            var apiClient = new FakeApiClient(attempts => new RegisterSessionResponse
            {
                Success = true,
                SessionId = config.SessionId,
                ValidatedBy = ValidatorType.AutopilotV1,
            });

            var result = await SessionRegistrationHelper.RegisterWithRetryAsync(
                apiClient, config, agentVersion: "0.0.0", logger: logger,
                backoffDelay: _ => Task.CompletedTask);

            Assert.Equal(SessionRegistrationOutcome.Succeeded, result.Outcome);
            Assert.Equal(1, apiClient.CallCount);
            Assert.Equal(ValidatorType.AutopilotV1, result.ValidatedBy);
            Assert.Null(result.AdminAction);
        }

        [Fact]
        public async Task Retries_until_success_on_success_false()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);
            var config = NewConfig();

            var apiClient = new FakeApiClient(attempt =>
            {
                if (attempt < 3) return new RegisterSessionResponse { Success = false, Message = "backend 500" };
                return new RegisterSessionResponse { Success = true, SessionId = config.SessionId };
            });

            var result = await SessionRegistrationHelper.RegisterWithRetryAsync(
                apiClient, config, agentVersion: "0.0.0", logger: logger,
                backoffDelay: _ => Task.CompletedTask);

            Assert.Equal(SessionRegistrationOutcome.Succeeded, result.Outcome);
            Assert.Equal(3, apiClient.CallCount);
        }

        [Fact]
        public async Task Auth_failure_short_circuits_without_retry()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);
            var config = NewConfig();

            var apiClient = new FakeApiClient(attempt =>
                throw new BackendAuthException("cert rejected", statusCode: 401));

            var tracker = new AuthFailureTracker(maxFailures: 5, timeoutMinutes: 0,
                clock: SystemClock.Instance, logger: logger);

            var result = await SessionRegistrationHelper.RegisterWithRetryAsync(
                apiClient, config, agentVersion: "0.0.0", logger: logger, authFailureTracker: tracker);

            Assert.Equal(SessionRegistrationOutcome.AuthFailed, result.Outcome);
            Assert.Equal(401, result.HttpStatusCode);
            Assert.Equal(1, apiClient.CallCount);
            Assert.Equal(1, tracker.ConsecutiveFailures);
        }

        [Fact]
        public async Task Fails_after_five_exceptions_without_auth_error()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);
            var config = NewConfig();

            var apiClient = new FakeApiClient(attempt =>
                throw new InvalidOperationException($"transient attempt {attempt}"));

            var result = await SessionRegistrationHelper.RegisterWithRetryAsync(
                apiClient, config, agentVersion: "0.0.0", logger: logger,
                backoffDelay: _ => Task.CompletedTask);

            Assert.Equal(SessionRegistrationOutcome.Failed, result.Outcome);
            Assert.Equal(5, apiClient.CallCount);
            Assert.Contains("transient attempt 5", result.ErrorMessage);
        }

        [Fact]
        public async Task Populates_registration_with_device_and_version_details()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);
            var config = NewConfig();

            SessionRegistration captured = null!;
            var apiClient = new FakeApiClient(
                _ => new RegisterSessionResponse { Success = true, SessionId = config.SessionId },
                onRegister: reg => captured = reg);

            await SessionRegistrationHelper.RegisterWithRetryAsync(
                apiClient, config, agentVersion: "9.9.9", logger: logger);

            Assert.NotNull(captured);
            Assert.Equal(config.SessionId, captured.SessionId);
            Assert.Equal(config.TenantId, captured.TenantId);
            Assert.Equal("9.9.9", captured.AgentVersion);
            Assert.Equal(Environment.MachineName, captured.DeviceName);
            Assert.False(string.IsNullOrEmpty(captured.EnrollmentType));
            Assert.True(captured.IsUserDriven);
        }

        [Fact]
        public async Task Uses_supplied_device_hardware_instead_of_querying_wmi()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp);
            var config = NewConfig();

            SessionRegistration captured = null!;
            var apiClient = new FakeApiClient(
                _ => new RegisterSessionResponse { Success = true, SessionId = config.SessionId },
                onRegister: reg => captured = reg);

            // Simulates the auth bundle's already-hardened hardware read being passed through.
            // The body must carry these verbatim rather than re-reading WMI on the test host.
            await SessionRegistrationHelper.RegisterWithRetryAsync(
                apiClient, config, agentVersion: "9.9.9", logger: logger,
                deviceHardware: ("Contoso Devices", "Contoso Book 3", "SN-TEST-12345"));

            Assert.NotNull(captured);
            Assert.Equal("Contoso Devices", captured.Manufacturer);
            Assert.Equal("Contoso Book 3", captured.Model);
            Assert.Equal("SN-TEST-12345", captured.SerialNumber);
        }

        // Subclasses the real BackendApiClient so we inherit its public API but intercept the
        // single virtual entry point that matters. BackendApiClient's ctor is protected-less in
        // V2 (`public BackendApiClient(string baseUrl, ...)` + `protected BackendApiClient()`);
        // we use the protected ctor to avoid HttpClient construction side effects in unit tests.
        private sealed class FakeApiClient : BackendApiClient
        {
            private readonly Func<int, RegisterSessionResponse> _respond;
            private readonly Action<SessionRegistration>? _onRegister;
            public int CallCount;

            public FakeApiClient(Func<int, RegisterSessionResponse> respond, Action<SessionRegistration>? onRegister = null)
            {
                _respond = respond;
                _onRegister = onRegister;
            }

            public override Task<RegisterSessionResponse> RegisterSessionAsync(SessionRegistration registration)
            {
                CallCount++;
                _onRegister?.Invoke(registration);
                try { return Task.FromResult(_respond(CallCount)); }
                catch (Exception ex) { return Task.FromException<RegisterSessionResponse>(ex); }
            }
        }
    }
}
