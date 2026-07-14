using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Phase 6 of <see cref="Program"/>'s <c>RunAgent</c>: blocking
    /// <c>POST /api/agent/register-session</c> with 5-retry exponential backoff before the
    /// orchestrator starts. V1 parity (MonitoringService.RegisterSessionAsync) — without
    /// this call the backend's Sessions table never gets a row for this session, so
    /// <c>IncrementSessionEventCountAsync</c> / <c>UpdateSessionStatusAsync</c> silently
    /// no-op and session status / phase / admin-overrides / validator reconcile all break.
    /// On registration failure we follow V1's rule: collectors MUST NOT start and the
    /// agent exits cleanly so the next Scheduled-Task tick can retry.
    /// </summary>
    internal static class BackendSessionRegistration
    {
        public static SessionRegistrationOutcomeResult Register(
            AgentConfiguration agentConfig,
            BackendAuthBundle auth,
            HttpClient mtlsHttpClient,
            string agentVersion,
            bool consoleMode,
            AgentLogger logger)
        {
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            var registrationResult = SessionRegistrationHelper.RegisterWithRetryAsync(
                apiClient: auth.BackendApiClient,
                agentConfig: agentConfig,
                agentVersion: agentVersion,
                logger: logger,
                authFailureTracker: auth.AuthFailureTracker,
                emergencyReporter: auth.EmergencyReporter,
                onTerminalTransportFailure: ex => DiagnoseTerminalTransportFailure(ex, auth, logger),
                // Reuse the single hardened hardware read from the auth bundle so the session row
                // matches the security headers the backend validated against (no second WMI query).
                deviceHardware: (auth.Manufacturer, auth.Model, auth.SerialNumber))
                .GetAwaiter().GetResult();

            if (registrationResult.Outcome != SessionRegistrationOutcome.Succeeded)
            {
                logger.Error(
                    $"=== SESSION REGISTRATION FAILED ({registrationResult.Outcome}: {registrationResult.ErrorMessage}) — " +
                    "collectors will NOT start to prevent orphaned events. ===");
                if (consoleMode)
                    Console.Error.WriteLine($"FATAL: session registration failed ({registrationResult.Outcome}). Agent exiting.");
                try { mtlsHttpClient?.Dispose(); } catch { }
                try { auth.BackendApiClient?.Dispose(); } catch { }
                // Exit code differs so the diag skill can distinguish Auth vs Network in Scheduled-Task history.
                return SessionRegistrationOutcomeResult.Exit(
                    registrationResult.Outcome == SessionRegistrationOutcome.AuthFailed ? 6 : 7);
            }

            return SessionRegistrationOutcomeResult.Continue(registrationResult);
        }

        /// <summary>
        /// Off-hot-path diagnostic invoked once after all session-registration retries fail.
        /// Detects whether the terminal exception was a TLS-layer <c>SecureChannelFailure</c>
        /// and, if so, runs the TPM-PSS capability probe to distinguish "generic Schannel
        /// failure" from "TPM firmware can't do RSA-PSS so Schannel filters the cert out".
        /// On confirmation, emits a <see cref="DistressErrorType.TpmPssUnsupported"/> distress
        /// signal so tenant admins see this device's specific blocker rather than a generic
        /// network failure.
        /// <para>
        /// Never throws — diagnostic-only side effects.
        /// </para>
        /// </summary>
        private static Task DiagnoseTerminalTransportFailure(
            Exception ex,
            BackendAuthBundle auth,
            AgentLogger logger)
        {
            try
            {
                if (!IsSecureChannelFailure(ex))
                    return Task.CompletedTask;

                if (auth?.DistressReporter == null || !auth.HasClientCertificate)
                    return Task.CompletedTask;

                logger.Info(
                    "SessionRegistration: terminal SecureChannelFailure detected — running TPM PSS capability probe to classify the cause.");

                // Re-resolve the cert here rather than holding it on BackendAuthBundle. This
                // path runs at most once per agent run, only on terminal failure, so the extra
                // store lookup is negligible and keeps the bundle's surface clean.
                var cert = new DefaultCertificateResolver().FindClientCertificate(logger);
                var probe = TpmPssCapabilityProbe.Probe(cert, logger);

                logger.Info(
                    $"TpmPssCapabilityProbe: provider={probe.ProviderName} keySize={probe.KeySizeBits} " +
                    $"pkcs1Sha256={probe.Pkcs1Sha256Works} pssSha256={probe.PssSha256Works} pssSha384={probe.PssSha384Works}");

                if (probe.IsTpmPssBroken && probe.IsTpmBacked)
                {
                    logger.Error(
                        "TpmPssCapabilityProbe: device's TPM-backed key cannot perform RSA-PSS — " +
                        "modern Schannel filters the cert out of TLS client-auth, blocking mTLS to the backend. " +
                        "Likely fix: update TPM firmware (common on 2015-era Infineon TPM 2.0).");

                    _ = auth.DistressReporter.TrySendAsync(
                        DistressErrorType.TpmPssUnsupported,
                        probe.ToDistressMessage());
                }
                else if (probe.IsTpmPssBroken)
                {
                    // PSS broken but key isn't TPM-backed — different bug, don't mis-classify.
                    logger.Warning(
                        $"TpmPssCapabilityProbe: PSS signing failed on a non-TPM provider ({probe.ProviderName}). " +
                        "Not emitting TpmPssUnsupported distress. Investigate as a separate crypto issue.");
                }
            }
            catch (Exception probeEx)
            {
                logger?.Warning($"DiagnoseTerminalTransportFailure: unexpected error: {probeEx.Message}");
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Walks the inner-exception chain looking for <c>WebException.Status == SecureChannelFailure</c>,
        /// which is the .NET surface of a TLS handshake/credential rejection. Other failure modes
        /// (timeout, DNS, connect-refused) surface as different statuses and are not classified
        /// here.
        /// </summary>
        private static bool IsSecureChannelFailure(Exception ex)
        {
            var current = ex;
            while (current != null)
            {
                if (current is WebException we && we.Status == WebExceptionStatus.SecureChannelFailure)
                    return true;
                current = current.InnerException;
            }
            return false;
        }
    }

    /// <summary>
    /// Phase 6 outcome — either an early exit (V1 parity: 6 = AuthFailed, 7 = anything else)
    /// or a Continue payload carrying the successful <see cref="SessionRegistrationResult"/>
    /// for downstream consumers (initial-signal posting, admin-preemption handling,
    /// session-started anchor, AdminAction surface in the wired telemetry response).
    /// </summary>
    internal sealed class SessionRegistrationOutcomeResult
    {
        public bool ShouldExit { get; }
        public int ExitCode { get; }
        public SessionRegistrationResult Registration { get; }

        private SessionRegistrationOutcomeResult(bool shouldExit, int exitCode, SessionRegistrationResult registration)
        {
            ShouldExit = shouldExit;
            ExitCode = exitCode;
            Registration = registration;
        }

        public static SessionRegistrationOutcomeResult Exit(int code)
            => new SessionRegistrationOutcomeResult(true, code, null);

        public static SessionRegistrationOutcomeResult Continue(SessionRegistrationResult registration)
            => new SessionRegistrationOutcomeResult(false, 0, registration);
    }
}
