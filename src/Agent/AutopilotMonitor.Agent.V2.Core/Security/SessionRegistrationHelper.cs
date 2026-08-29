using System;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Shared;
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
    ///   <item>Boot-time NIC grace before the first attempt: when no network link is up yet
    ///     (BootTrigger relaunch after a mid-enrollment reboot, Wi-Fi still associating) wait
    ///     up to <see cref="NetworkLinkWaitMax"/> for the link-level signal. Best-effort and
    ///     free on the normal path — a live link returns immediately.</item>
    ///   <item>6 attempts total; between attempts <c>2^attempt</c> seconds (2s, 4s, 8s, 16s, 32s —
    ///     ~62 s of backoff). The original V1 budget of 5 attempts / ~30 s was too short for the
    ///     reboot-relaunch on Wi-Fi kiosks: the relaunched agent gave up and exited 7 before
    ///     the network came back, leaving the session silent forever (tenant aebdce78,
    ///     2026-08-23 audit). A relaunch that misses here is lost — nothing retries later.</item>
    ///   <item>On <c>response.Success == true</c> → stop immediately.</item>
    ///   <item>On <see cref="BackendAuthException"/> (401/403) → report to the
    ///     <see cref="AuthFailureTracker"/> (which fires the first-failure distress + may trip
    ///     the shutdown threshold) and return <see cref="SessionRegistrationOutcome.AuthFailed"/>
    ///     without retrying — the backend has definitively rejected the device cert.</item>
    ///   <item>Exception to the above — a 403 carrying error code
    ///     <c>session_owner_mismatch</c> (SESSION-OWNER-BINDING): the persisted SessionId names a
    ///     session bound to another device identity (typically an Intune re-enrollment without a
    ///     wipe, where <c>session.id</c> survived but the certificate identity changed). That is
    ///     not an auth failure: the session is rotated through <c>rotateSession</c> and
    ///     registration retried exactly once. The tracker is NOT fed — five of these would
    ///     otherwise soft-shutdown a perfectly authorized agent.</item>
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
        private const int MaxAttempts = 6;

        /// <summary>Upper bound for the pre-registration link wait (see class remarks).</summary>
        internal static readonly TimeSpan NetworkLinkWaitMax = TimeSpan.FromSeconds(15);

        /// <summary>
        /// Executes the retrying register-session handshake against the backend. Returns the
        /// response + classified outcome. Never throws.
        /// </summary>
        /// <param name="backoffDelay">
        /// Optional delay-provider used by tests to avoid real-time waits. Production callers
        /// pass <c>null</c> → V1 parity <c>Task.Delay(2^attempt * 1000)</c> between attempts.
        /// </param>
        /// <param name="networkLinkWait">
        /// Optional pre-registration link wait used by tests. Production callers pass
        /// <c>null</c> → <see cref="WaitForNetworkLinkAsync"/> (bounded by <see cref="NetworkLinkWaitMax"/>).
        /// </param>
        public static async Task<SessionRegistrationResult> RegisterWithRetryAsync(
            BackendApiClient apiClient,
            AgentConfiguration agentConfig,
            string agentVersion,
            AgentLogger logger,
            AuthFailureTracker authFailureTracker = null,
            EmergencyReporter emergencyReporter = null,
            Func<int, Task> backoffDelay = null,
            Func<Exception, Task> onTerminalTransportFailure = null,
            (string Manufacturer, string Model, string SerialNumber)? deviceHardware = null,
            Func<AgentLogger, Task> networkLinkWait = null,
            Func<string> rotateSession = null)
        {
            if (apiClient == null) throw new ArgumentNullException(nameof(apiClient));
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            backoffDelay ??= DefaultBackoffDelay;
            networkLinkWait ??= WaitForNetworkLinkAsync;
            var registration = BuildRegistration(agentConfig, agentVersion, deviceHardware);

            try { await networkLinkWait(logger).ConfigureAwait(false); }
            catch (Exception ex) { logger.Debug($"Pre-registration network wait failed ({ex.Message}) — registering anyway."); }
            string lastError = null;
            Exception lastException = null;
            string rotatedFromSessionId = null;

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    logger.Info($"Registering session with backend (attempt {attempt}/{MaxAttempts})");
                    var response = await apiClient.RegisterSessionAsync(registration).ConfigureAwait(false);

                    if (response != null && response.Success)
                    {
                        logger.Info($"Session registered successfully: {response.SessionId} (validatedBy={response.ValidatedBy}, adminAction={response.AdminAction ?? "(none)"})");
                        return SessionRegistrationResult.Succeeded(response, rotatedFromSessionId);
                    }

                    lastError = response?.Message ?? "(null response)";
                    logger.Warning($"Session registration failed: {lastError}");
                }
                catch (BackendAuthException ex) when (
                    ex.ErrorCode == Constants.AgentErrorCodes.SessionOwnerMismatch
                    && rotateSession != null
                    && rotatedFromSessionId == null)
                {
                    // SESSION-OWNER-BINDING: the id on disk belongs to a session this device
                    // identity does not own. Become a new session and register once more —
                    // immediately, no backoff, no auth-failure bookkeeping.
                    rotatedFromSessionId = registration.SessionId;
                    string newSessionId;
                    try
                    {
                        newSessionId = rotateSession();
                    }
                    catch (Exception rotateEx)
                    {
                        logger.Error($"Session owner mismatch reported by backend but session rotation failed: {rotateEx.Message}", rotateEx);
                        authFailureTracker?.RecordFailure(ex.StatusCode, "agent/register-session", ex.EndpointUnavailable);
                        return SessionRegistrationResult.AuthFailed(ex.StatusCode, ex.Message);
                    }
                    logger.Warning(
                        $"Backend refused registration with {Constants.AgentErrorCodes.SessionOwnerMismatch}: session {rotatedFromSessionId} " +
                        $"is bound to another device identity (re-enrollment without wipe?). Rotated to {newSessionId}; re-registering.");
                    registration = BuildRegistration(agentConfig, agentVersion, deviceHardware);
                    continue;
                }
                catch (BackendAuthException ex)
                {
                    logger.Error($"Session registration authentication failed ({ex.StatusCode}): {ex.Message}");
                    // Feed the central auth-failure tracker so the first-failure distress dispatches
                    // and the shutdown threshold advances. V1 parity: no retry on auth-failure —
                    // backend has definitively rejected the cert/token.
                    authFailureTracker?.RecordFailure(ex.StatusCode, "agent/register-session", ex.EndpointUnavailable);
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
                    var delaySeconds = (int)Math.Pow(2, attempt); // 2, 4, 8, 16, 32
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

        /// <summary>
        /// Boot-time NIC grace before the first registration attempt — shared polling loop in
        /// <see cref="NetworkLinkWait"/>, bounded by <see cref="NetworkLinkWaitMax"/>. Probe
        /// errors end the wait, never the registration.
        /// </summary>
        internal static Task WaitForNetworkLinkAsync(AgentLogger logger)
            => NetworkLinkWait.WaitAsync(logger, NetworkLinkWaitMax, "Session registration");

        private static SessionRegistration BuildRegistration(
            AgentConfiguration agentConfig,
            string agentVersion,
            (string Manufacturer, string Model, string SerialNumber)? deviceHardware = null)
        {
            // Reuse the single hardened hardware read (HardwareInfo.GetHardwareInfo) that already
            // populated the security headers instead of re-querying WMI here. This saves a WMI
            // round-trip AND — because that read retries on transient WMI unavailability during
            // OOBE — keeps the session row's Manufacturer/Model/SerialNumber consistent with the
            // headers the backend validated against. Falls back to a fresh DeviceInfoProvider read
            // only when no hardware was supplied (e.g. unit tests exercising the helper directly).
            string manufacturer = deviceHardware?.Manufacturer ?? DeviceInfoProvider.GetManufacturer() ?? string.Empty;
            string model = deviceHardware?.Model ?? DeviceInfoProvider.GetModel() ?? string.Empty;
            string serialNumber = deviceHardware?.SerialNumber ?? DeviceInfoProvider.GetSerialNumber() ?? string.Empty;

            return new SessionRegistration
            {
                SessionId = agentConfig.SessionId,
                TenantId = agentConfig.TenantId,
                SerialNumber = serialNumber,
                Manufacturer = manufacturer,
                Model = model,
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
                IsCloudPc = CloudPcDetector.DetectIsCloudPc(),
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

        /// <summary>All attempts failed with non-auth errors (network, 5xx, malformed response).</summary>
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

        /// <summary>
        /// SESSION-OWNER-BINDING: the SessionId this run started with when the backend refused it
        /// with <c>session_owner_mismatch</c> and the session was rotated before the successful
        /// registration. Null on the normal path.
        /// </summary>
        public string RotatedFromSessionId { get; }

        private SessionRegistrationResult(
            SessionRegistrationOutcome outcome,
            RegisterSessionResponse response,
            int httpStatusCode,
            string errorMessage,
            string rotatedFromSessionId = null)
        {
            Outcome = outcome;
            Response = response;
            HttpStatusCode = httpStatusCode;
            ErrorMessage = errorMessage;
            RotatedFromSessionId = rotatedFromSessionId;
        }

        public static SessionRegistrationResult Succeeded(RegisterSessionResponse response, string rotatedFromSessionId = null)
            => new SessionRegistrationResult(SessionRegistrationOutcome.Succeeded, response, 200, null, rotatedFromSessionId);

        public static SessionRegistrationResult AuthFailed(int statusCode, string message)
            => new SessionRegistrationResult(SessionRegistrationOutcome.AuthFailed, null, statusCode, message);

        public static SessionRegistrationResult Failed(string message)
            => new SessionRegistrationResult(SessionRegistrationOutcome.Failed, null, 0, message);
    }
}
