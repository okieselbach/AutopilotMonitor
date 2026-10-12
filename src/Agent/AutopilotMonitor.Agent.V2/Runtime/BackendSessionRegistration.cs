using System;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Phase 6 of <see cref="Program"/>'s <c>RunAgent</c>: blocking
    /// <c>POST /api/agent/register-session</c> with link-wait + 6-retry exponential backoff before the
    /// orchestrator starts. V1 parity (MonitoringService.RegisterSessionAsync) — without
    /// this call the backend's Sessions table never gets a row for this session, so
    /// <c>IncrementSessionEventCountAsync</c> / <c>UpdateSessionStatusAsync</c> silently
    /// no-op and session status / phase / admin-overrides / validator reconcile all break.
    /// On registration failure we follow V1's rule: collectors MUST NOT start and the
    /// agent exits cleanly; the Scheduled Task has only a boot trigger, so the next attempt is
    /// the next boot. The failed run is recorded for the emergency break report. A registration
    /// that something on the network path answered gets <c>InterceptedRegistrationRetryBoots</c>
    /// further boots, then the agent cleans up like the emergency break (<see cref="ConcludeFailedRun"/>).
    /// A successful registration after failed starts reports them on <c>agent_started</c>
    /// (<see cref="ClearAfterSuccess"/>).
    /// </summary>
    internal static class BackendSessionRegistration
    {
        /// <summary>
        /// How long a failed start waits for the pending ClientCertificateNotReceived distress before
        /// it exits or cleans up — the distress reporter's own HTTP timeout.
        /// </summary>
        internal static readonly TimeSpan PendingDistressBound = TimeSpan.FromSeconds(5);

        /// <param name="stateDirectory">State folder for <c>enrollment-complete.marker</c> on the intercepted-registration give-up.</param>
        /// <param name="cleanupServiceFactory">Self-destruct for the give-up (run when <c>SelfDestructOnComplete</c>).</param>
        public static SessionRegistrationOutcomeResult Register(
            AgentConfiguration agentConfig,
            BackendAuthBundle auth,
            HttpClient mtlsHttpClient,
            string agentVersion,
            bool consoleMode,
            AgentLogger logger,
            Func<string> rotateSession = null,
            SessionIdPersistence sessionPersistence = null,
            string configFetchOutcome = null,
            string stateDirectory = null,
            Func<CleanupService> cleanupServiceFactory = null)
        {
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            // Wall clock on purpose: a window far above the retry schedule is the evidence that
            // the device slept or was suspended while this run tried to register.
            var startedUtc = DateTime.UtcNow;
            var linkUpAtStart = TryIsNetworkLinkUp();

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
                deviceHardware: (auth.Manufacturer, auth.Model, auth.SerialNumber),
                rotateSession: rotateSession)
                .GetAwaiter().GetResult();

            if (registrationResult.Outcome == SessionRegistrationOutcome.Succeeded)
            {
                return SessionRegistrationOutcomeResult.Continue(
                    registrationResult, ClearAfterSuccess(sessionPersistence, NetworkFingerprint.TryCapture, logger));
            }

            var record = RecordFailedRun(
                sessionPersistence, registrationResult, startedUtc, DateTime.UtcNow,
                linkUpAtStart, TryIsNetworkLinkUp(), configFetchOutcome, NetworkFingerprint.TryCapture(), logger);

            logger.Error(
                $"=== SESSION REGISTRATION FAILED ({registrationResult.Outcome}: {registrationResult.ErrorMessage}) — " +
                "collectors will NOT start to prevent orphaned events. ===");
            if (consoleMode)
                Console.Error.WriteLine($"FATAL: session registration failed ({registrationResult.Outcome}). Agent exiting.");
            try { mtlsHttpClient?.Dispose(); } catch { }
            try { auth.BackendApiClient?.Dispose(); } catch { }

            return SessionRegistrationOutcomeResult.Exit(ConcludeFailedRun(
                registrationResult, record, agentConfig, auth.AuthFailureTracker,
                sessionPersistence, stateDirectory, cleanupServiceFactory, logger));
        }

        /// <summary>Longest <see cref="RegistrationFailureSummary.LastError"/> kept on disk and on the wire.</summary>
        internal const int MaxRecordedErrorLength = 256;

        /// <summary>
        /// Folds a failed run into <c>registration-failure.json</c> (the emergency break later reports
        /// it — for a session that never registered, the only account of why) and returns the record
        /// including this run; null without persistence or when it could not be written. An
        /// intercepted run also counts toward <see cref="RegistrationFailureSummary.InterceptedRuns"/>,
        /// which only a successful registration resets. <paramref name="networkFingerprint"/> is this
        /// run's network; it is kept beside the record and never leaves the device. Never throws.
        /// </summary>
        internal static RegistrationFailureSummary RecordFailedRun(
            SessionIdPersistence sessionPersistence,
            SessionRegistrationResult registrationResult,
            DateTime startedUtc,
            DateTime endedUtc,
            bool? linkUpAtStart,
            bool? linkUpAtEnd,
            string configFetchOutcome,
            string networkFingerprint,
            AgentLogger logger)
        {
            if (sessionPersistence == null || registrationResult == null) return null;
            try
            {
                var error = registrationResult.ErrorMessage;
                if (error != null && error.Length > MaxRecordedErrorLength)
                    error = error.Substring(0, MaxRecordedErrorLength);

                return sessionPersistence.RecordRegistrationFailure(new RegistrationFailureSummary
                {
                    FailedRuns = 1,
                    InterceptedRuns = registrationResult.IsIntercepted ? 1 : 0,
                    FirstFailedAtUtc = endedUtc,
                    LastFailedAtUtc = endedUtc,
                    Outcome = registrationResult.Outcome.ToString(),
                    LastError = error,
                    NetworkLinkUpAtStart = linkUpAtStart,
                    NetworkLinkUpAtEnd = linkUpAtEnd,
                    AttemptWindowSeconds = Math.Round(Math.Max(0, (endedUtc - startedUtc).TotalSeconds), 1),
                    ConfigFetchOutcome = configFetchOutcome,
                }, logger, networkFingerprint);
            }
            catch (Exception ex)
            {
                logger?.Warning($"Registration run record failed (best-effort): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// A successful registration clears <c>registration-failure.json</c>. When earlier starts of
        /// this session had failed, the cleared record comes back for this run's <c>agent_started</c>
        /// — until now the success erased it silently and late successes were uncountable. The
        /// network fingerprints are compared here (<paramref name="captureNetworkFingerprint"/> runs
        /// only when there is a record) and never leave the device. Null when there was no record.
        /// Never throws.
        /// </summary>
        internal static PriorRegistrationFailures ClearAfterSuccess(
            SessionIdPersistence sessionPersistence,
            Func<string> captureNetworkFingerprint,
            AgentLogger logger)
        {
            if (sessionPersistence == null) return null;
            try
            {
                var cleared = sessionPersistence.LoadRegistrationFailureRecord();
                sessionPersistence.ClearRegistrationFailure(logger);
                if (cleared == null) return null;

                var prior = new PriorRegistrationFailures(
                    cleared.Summary.FailedRuns,
                    cleared.Summary.InterceptedRuns,
                    cleared.Summary.Outcome,
                    NetworkFingerprint.Differs(cleared.NetworkFingerprint, captureNetworkFingerprint?.Invoke()));
                logger?.Info(
                    $"Session registered after {prior.FailedRuns} failed start(s) ({prior.InterceptedRuns} intercepted, " +
                    $"last: {prior.LastOutcome}, network changed: {prior.NetworkChanged?.ToString() ?? "unknown"}).");
                return prior;
            }
            catch (Exception ex)
            {
                logger?.Warning($"Registration run record failed (best-effort): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The end of a start whose registration failed. First the ClientCertificateNotReceived
        /// distress, which the tracker dispatched fire-and-forget, gets up to
        /// <see cref="PendingDistressBound"/> to leave the machine: this process is about to exit or
        /// clean up, and nothing else waits for it. Then the intercepted-registration buffer: when
        /// this start was intercepted and the intercepted starts since the last success (this one
        /// included) exceed <c>InterceptedRegistrationRetryBoots</c>, monitoring ends exactly like
        /// the 48-hour emergency break (<see cref="Program.EndMonitoringAtStart"/>). Every other
        /// failure — a backend 401/403, a network failure, an interception within the buffer, a
        /// record that could not be written — leaves everything in place for the next boot. Returns
        /// the exit code: 6 for an auth failure (the give-up included: it is still one), 7 otherwise.
        /// </summary>
        internal static int ConcludeFailedRun(
            SessionRegistrationResult registrationResult,
            RegistrationFailureSummary record,
            AgentConfiguration agentConfig,
            AuthFailureTracker authFailureTracker,
            SessionIdPersistence sessionPersistence,
            string stateDirectory,
            Func<CleanupService> cleanupServiceFactory,
            AgentLogger logger)
        {
            if (authFailureTracker != null && !authFailureTracker.WaitForPendingForeignDistress(PendingDistressBound))
                logger.Warning($"ClientCertificateNotReceived distress still in flight after {PendingDistressBound.TotalSeconds:F0}s — continuing without it.");

            var buffer = agentConfig.InterceptedRegistrationRetryBoots;
            if (registrationResult.IsIntercepted && record != null && record.InterceptedRuns > buffer)
            {
                logger.Warning(
                    $"=== REGISTRATION INTERCEPTED ON {record.InterceptedRuns} START(S) SINCE THE LAST SUCCESS " +
                    $"(buffer: {buffer} further boot(s)) — cleaning up like the emergency break. ===");
                Program.EndMonitoringAtStart(
                    stateDirectory, sessionPersistence, agentConfig.SelfDestructOnComplete, cleanupServiceFactory, logger,
                    markerText: $"Intercepted registration at {DateTime.UtcNow:O} ({record.InterceptedRuns} intercepted start(s), buffer {buffer})",
                    cleanupReason: "intercepted_registration");
            }
            else if (registrationResult.IsIntercepted)
            {
                logger.Info(
                    $"Registration intercepted on {record?.InterceptedRuns.ToString() ?? "an unrecorded number of"} start(s) " +
                    $"since the last success (buffer: {buffer} further boot(s)) — the next boot retries.");
            }

            // Exit code differs so the diag skill can distinguish Auth vs Network in Scheduled-Task history.
            return registrationResult.Outcome == SessionRegistrationOutcome.AuthFailed ? 6 : 7;
        }

        private static bool? TryIsNetworkLinkUp()
        {
            try { return NetworkInterface.GetIsNetworkAvailable(); }
            catch { return null; }
        }

        /// <summary>
        /// SESSION-OWNER-BINDING: the one place that turns "backend says this session id is not
        /// ours" into a new session. Rotates the persisted id, points the runtime config at it,
        /// drops the transport spool (its lines carry the refused session's partition key and
        /// would otherwise be uploaded against it after the rotation) and re-targets the
        /// emergency reporter, which snapshotted the id before registration. Everything else that
        /// holds a session id (orchestrator, emitters, spool) is built in Phase 7 from the rotated
        /// <c>agentConfig.SessionId</c> — keep it that way: a component built before Phase 6 needs
        /// its own re-target here.
        /// </summary>
        internal static string RotateSession(
            AgentConfiguration agentConfig,
            SessionIdPersistence sessionPersistence,
            BackendAuthBundle auth,
            string transportDir,
            AgentLogger logger)
        {
            var newSessionId = sessionPersistence.Rotate(logger);
            agentConfig.SessionId = newSessionId;
            auth?.EmergencyReporter?.UpdateSessionId(newSessionId);
            DropTransportSpool(transportDir, logger);
            return newSessionId;
        }

        private static void DropTransportSpool(string transportDir, AgentLogger logger)
        {
            if (string.IsNullOrWhiteSpace(transportDir)) return;
            foreach (var name in new[] { "spool.jsonl", "upload-cursor.json" })
            {
                var path = System.IO.Path.Combine(transportDir, name);
                try
                {
                    if (!System.IO.File.Exists(path)) continue;
                    var size = new System.IO.FileInfo(path).Length;
                    System.IO.File.Delete(path);
                    logger.Info($"Session rotation: dropped {name} ({size} bytes) — its items belonged to the refused session.");
                }
                catch (Exception ex)
                {
                    logger.Warning($"Session rotation: could not drop {name}: {ex.Message}");
                }
            }
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
    /// session-started anchor, AdminAction surface in the wired telemetry response) and the
    /// failed starts this registration followed (<see cref="PriorFailures"/>, for <c>agent_started</c>).
    /// </summary>
    internal sealed class SessionRegistrationOutcomeResult
    {
        public bool ShouldExit { get; }
        public int ExitCode { get; }
        public SessionRegistrationResult Registration { get; }

        /// <summary>The failed-registration record this success cleared; null when there was none.</summary>
        public PriorRegistrationFailures PriorFailures { get; }

        private SessionRegistrationOutcomeResult(
            bool shouldExit, int exitCode, SessionRegistrationResult registration, PriorRegistrationFailures priorFailures)
        {
            ShouldExit = shouldExit;
            ExitCode = exitCode;
            Registration = registration;
            PriorFailures = priorFailures;
        }

        public static SessionRegistrationOutcomeResult Exit(int code)
            => new SessionRegistrationOutcomeResult(true, code, null, null);

        public static SessionRegistrationOutcomeResult Continue(
            SessionRegistrationResult registration, PriorRegistrationFailures priorFailures = null)
            => new SessionRegistrationOutcomeResult(false, 0, registration, priorFailures);
    }

    /// <summary>
    /// What the failed starts before a successful registration left in the record it cleared,
    /// reported once on that run's <c>agent_started</c> (<c>priorRegistration*</c> keys): whether a
    /// further boot rescues intercepted or failed starts becomes countable. Only the fingerprint
    /// comparison is carried, never a fingerprint.
    /// </summary>
    internal sealed class PriorRegistrationFailures
    {
        public PriorRegistrationFailures(int failedRuns, int interceptedRuns, string lastOutcome, bool? networkChanged)
        {
            FailedRuns = failedRuns;
            InterceptedRuns = interceptedRuns;
            LastOutcome = lastOutcome;
            NetworkChanged = networkChanged;
        }

        public int FailedRuns { get; }

        public int InterceptedRuns { get; }

        /// <summary>Registration outcome of the latest failed start (e.g. <c>AuthFailed</c>, <c>Failed</c>).</summary>
        public string LastOutcome { get; }

        /// <summary>The latest failed start ran on a different network than this one; null when either side is unknown.</summary>
        public bool? NetworkChanged { get; }
    }
}
