using System;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Security
{
    /// <summary>
    /// V1-parity wrapper around <see cref="BackendApiClient.RegisterSessionAsync"/>. Plan §3.9 /
    /// PR #51. The backend's <c>/api/agent/register-session</c> is the authoritative point where
    /// a session row is created in the <c>Sessions</c> table; without it, every subsequent
    /// <c>IncrementSessionEventCountAsync</c> / <c>UpdateSessionStatusAsync</c> silently no-ops
    /// (the server logs a warning and returns) so events still land but session status,
    /// phase, admin-overrides and validator reconciliation break.
    /// <para>
    /// <b>Retry contract (V1 <c>MonitoringService.RegisterSessionAsync</c>):</b>
    /// </para>
    /// <list type="bullet">
    ///   <item>5 attempts total; between attempts <c>2^attempt</c> seconds (2s, 4s, 8s, 16s).</item>
    ///   <item>On <c>response.Success == true</c> → stop immediately.</item>
    ///   <item>On <see cref="BackendAuthException"/> (401/403) → report to the
    ///     <see cref="AuthFailureTracker"/> (which fires the first-failure distress + may trip
    ///     the shutdown threshold) and return <see cref="SessionRegistrationOutcome.AuthFailed"/>
    ///     without retrying — the backend has definitively rejected the device cert.</item>
    ///   <item>Any other exception on the last attempt → <see cref="EmergencyReporter.TrySendAsync"/>
    ///     with <c>AgentErrorType.RegisterSessionFailed</c> so operators see the final cause.</item>
    /// </list>
    /// <para>
    /// The caller (<c>Program.RunAgent</c>) must treat a non-<see cref="SessionRegistrationOutcome.Succeeded"/>
    /// outcome as fatal — V1 parity: <c>"=== SESSION REGISTRATION FAILED — collectors will NOT start
    /// to prevent orphaned events ==="</c>. The agent exits cleanly instead of spinning up
    /// the orchestrator and flooding the Events table for an unregistered session.
    /// </para>
    /// </summary>
    public static class SessionRegistrationHelper
    {
        private const int MaxAttempts = 5;

        /// <summary>
        /// Executes the 5-retry register-session handshake against the backend. Returns the
        /// response + classified outcome. Never throws.
        /// </summary>
        /// <param name="backoffDelay">
        /// Optional delay-provider used by tests to avoid real-time waits. Production callers
        /// pass <c>null</c> → V1 parity <c>Task.Delay(2^attempt * 1000)</c> between attempts.
        /// </param>
        public static async Task<SessionRegistrationResult> RegisterWithRetryAsync(
            BackendApiClient apiClient,
            AgentConfiguration agentConfig,
            string agentVersion,
            AgentLogger logger,
            AuthFailureTracker authFailureTracker = null,
            EmergencyReporter emergencyReporter = null,
            Func<int, Task> backoffDelay = null,
            Func<Exception, Task> onTerminalTransportFailure = null)
        {
            if (apiClient == null) throw new ArgumentNullException(nameof(apiClient));
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            backoffDelay ??= DefaultBackoffDelay;
            var registration = BuildRegistration(agentConfig, agentVersion);
            string lastError = null;
            Exception lastException = null;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    logger.Info($"Registering session with backend (attempt {attempt}/{MaxAttempts})");
                    var response = await apiClient.RegisterSessionAsync(registration).ConfigureAwait(false);

                    if (response != null && response.Success)
                    {
                        logger.Info($"Session registered successfully: {response.SessionId} (validatedBy={response.ValidatedBy}, adminAction={response.AdminAction ?? "(none)"})");
                        return SessionRegistrationResult.Succeeded(response);
                    }

                    lastError = response?.Message ?? "(null response)";
                    logger.Warning($"Session registration failed: {lastError}");
                }
                catch (BackendAuthException ex)
                {
                    logger.Error($"Session registration authentication failed ({ex.StatusCode}): {ex.Message}");
                    // Feed the central auth-failure tracker so the first-failure distress dispatches
                    // and the shutdown threshold advances. V1 parity: no retry on auth-failure —
                    // backend has definitively rejected the cert/token.
                    authFailureTracker?.RecordFailure(ex.StatusCode, "agent/register-session");
                    return SessionRegistrationResult.AuthFailed(ex.StatusCode, ex.Message);
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    lastException = ex;
                    logger.Error($"Failed to register session (attempt {attempt}/{MaxAttempts}): {ex.Message}", ex);

                    if (attempt == MaxAttempts)
                    {
                        try
                        {
                            if (emergencyReporter != null)
                                _ = emergencyReporter.TrySendAsync(
                                    AgentErrorType.RegisterSessionFailed,
                                    ex.Message);
                        }
                        catch { /* emergency channel is best-effort */ }
                    }
                }

                if (attempt < MaxAttempts)
                {
                    var delaySeconds = (int)Math.Pow(2, attempt); // 2, 4, 8, 16
                    logger.Info($"Retrying session registration in {delaySeconds}s");
                    await backoffDelay(attempt).ConfigureAwait(false);
                }
            }

            // Terminal failure path. The caller can hook diagnostic side-effects here (e.g. the
            // TPM-PSS capability probe that distinguishes a generic SecureChannelFailure from
            // the specific case where Schannel filtered the cert out because the TPM firmware
            // can't sign with RSA-PSS). Kept off the hot path on purpose — a healthy device
            // never reaches this code.
            if (lastException != null && onTerminalTransportFailure != null)
            {
                try
                {
                    await onTerminalTransportFailure(lastException).ConfigureAwait(false);
                }
                catch (Exception probeEx)
                {
                    logger.Warning($"onTerminalTransportFailure callback threw: {probeEx.Message}");
                }
            }

            return SessionRegistrationResult.Failed(lastError ?? "max retries exceeded");
        }

        /// <summary>V1 parity exponential backoff: 2^attempt seconds.</summary>
        private static Task DefaultBackoffDelay(int attempt)
            => Task.Delay(((int)Math.Pow(2, attempt)) * 1000);

        private static SessionRegistration BuildRegistration(AgentConfiguration agentConfig, string agentVersion)
        {
            return new SessionRegistration
            {
                SessionId = agentConfig.SessionId,
                TenantId = agentConfig.TenantId,
                SerialNumber = DeviceInfoProvider.GetSerialNumber() ?? string.Empty,
                Manufacturer = DeviceInfoProvider.GetManufacturer() ?? string.Empty,
                Model = DeviceInfoProvider.GetModel() ?? string.Empty,
                DeviceName = Environment.MachineName,
                OsName = DeviceInfoProvider.GetOsName() ?? string.Empty,
                OsBuild = DeviceInfoProvider.GetOsBuild() ?? string.Empty,
                OsDisplayVersion = DeviceInfoProvider.GetOsDisplayVersion() ?? string.Empty,
                OsEdition = DeviceInfoProvider.GetOsEdition() ?? string.Empty,
                OsLanguage = System.Globalization.CultureInfo.CurrentCulture.Name ?? string.Empty,
                StartedAt = DateTime.UtcNow,
                AgentVersion = agentVersion ?? string.Empty,
                EnrollmentType = EnrollmentRegistryDetector.DetectEnrollmentType(),
                IsHybridJoin = EnrollmentRegistryDetector.DetectHybridJoin(),
                IsSelfDeployingProfile = EnrollmentRegistryDetector.DetectSelfDeployingProfile(),
                // Deliberately stays true even for self-deploying profiles: the WhiteGlove
                // Part1/Part2 merge paths and existing dashboards key on IsUserDriven;
                // IsSelfDeployingProfile is the additive classification signal.
                IsUserDriven = true,
            };
        }
    }

    /// <summary>Classification of <see cref="SessionRegistrationHelper.RegisterWithRetryAsync"/>.</summary>
    public enum SessionRegistrationOutcome
    {
        /// <summary>Backend responded with <c>Success=true</c>. The session row is live.</summary>
        Succeeded = 0,

        /// <summary>401/403 from the backend — cert/token rejected. No retry was attempted.</summary>
        AuthFailed = 1,

        /// <summary>All 5 attempts failed with non-auth errors (network, 5xx, malformed response).</summary>
        Failed = 2,
    }

    /// <summary>Result of <see cref="SessionRegistrationHelper.RegisterWithRetryAsync"/>.</summary>
    public sealed class SessionRegistrationResult
    {
        public SessionRegistrationOutcome Outcome { get; }
        public RegisterSessionResponse Response { get; }
        public int HttpStatusCode { get; }
        public string ErrorMessage { get; }

        public string AdminAction => Response?.AdminAction;
        public ValidatorType ValidatedBy => Response?.ValidatedBy ?? ValidatorType.Unknown;

        private SessionRegistrationResult(
            SessionRegistrationOutcome outcome,
            RegisterSessionResponse response,
            int httpStatusCode,
            string errorMessage)
        {
            Outcome = outcome;
            Response = response;
            HttpStatusCode = httpStatusCode;
            ErrorMessage = errorMessage;
        }

        public static SessionRegistrationResult Succeeded(RegisterSessionResponse response)
            => new SessionRegistrationResult(SessionRegistrationOutcome.Succeeded, response, 200, null);

        public static SessionRegistrationResult AuthFailed(int statusCode, string message)
            => new SessionRegistrationResult(SessionRegistrationOutcome.AuthFailed, null, statusCode, message);

        public static SessionRegistrationResult Failed(string message)
            => new SessionRegistrationResult(SessionRegistrationOutcome.Failed, null, 0, message);
    }
}
