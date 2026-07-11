using System;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Agent.V2.Core.Security;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Phase 1+2 of <see cref="Program"/>'s <c>RunAgent</c>: previous-exit classification,
    /// persisted-config load, TenantId resolution, AgentConfiguration build, SessionId
    /// persistence, completion-marker / emergency-break guards, and (optional)
    /// <c>--await-enrollment</c> MDM-certificate wait.
    /// <para>
    /// Returns a <see cref="BootstrapResult"/> that either signals an early exit (with the
    /// V1-parity exit code: 0 for guard-handled cases, 2 for missing TenantId, 3 for await
    /// timeout) or carries the runtime objects the rest of <c>RunAgent</c> needs.
    /// </para>
    /// </summary>
    internal static class AgentBootstrap
    {
        public static BootstrapResult Run(
            string[] args,
            AgentLogger logger,
            string dataDirectory,
            string logDirectory,
            string stateSubdir,
            bool consoleMode)
        {
            // Previous-exit classification (for the agent_started event + observability).
            var previousExit = Program.DetectPreviousExit(dataDirectory, logDirectory);
            if (previousExit.ExitType != "first_run")
            {
                var crashSuffix = previousExit.CrashExceptionType != null ? $" ({previousExit.CrashExceptionType})" : "";
                logger.Info($"Previous exit: {previousExit.ExitType}{crashSuffix}");
            }

            // Merge persisted bootstrap-config.json + await-enrollment.json into CLI args early.
            var bootstrapConfig = Program.TryReadBootstrapConfig(dataDirectory, logger);
            var awaitConfig = Program.TryReadAwaitEnrollmentConfig(dataDirectory, logger);

            var tenantIdFromRegistry = TenantIdResolver.Resolve(logger);
            var tenantId = !string.IsNullOrEmpty(tenantIdFromRegistry)
                ? tenantIdFromRegistry
                : bootstrapConfig?.TenantId;

            // --install time may have written a bootstrap config that holds a tenantId even when
            // the registry is not yet populated (bootstrap token path). Honour that.
            var agentConfig = Program.BuildAgentConfiguration(args, tenantId, sessionId: null, bootstrapConfig, awaitConfig);

            var sessionPersistence = new SessionIdPersistence(dataDirectory);
            if (args.Contains("--new-session"))
            {
                sessionPersistence.Delete(logger);
                logger.Info("--new-session: cleared persisted SessionId.");
            }
            // Snapshot the WhiteGlove-resume state BEFORE GetOrCreate: on Part-2 resume we want
            // to emit the whiteglove_resumed event AFTER orchestrator.Start. Reading the marker
            // up front avoids racing any downstream clear. V1 parity — the Part-2 detection is
            // marker-based, and the agent announces resume on the session timeline so that
            // dashboards can correlate the session's two boots.
            var isWhiteGloveResume = sessionPersistence.IsWhiteGloveResume();
            agentConfig.SessionId = sessionPersistence.GetOrCreate(logger);

            // Build a cleanup-service factory used by guards: instantiated lazily and with the
            // current agentConfig so command-line overrides (e.g. --no-cleanup) take effect.
            // The closure captures the AgentConfiguration reference; later RemoteConfigMerger
            // mutations to the same instance are visible at factory-invocation time, which is
            // intended (tenant-policy overrides flow through to terminal cleanup).
            Func<CleanupService> cleanupServiceFactory = () => new CleanupService(agentConfig, logger);

            if (Program.CheckEnrollmentCompleteMarker(
                    stateSubdir,
                    agentConfig.SelfDestructOnComplete, cleanupServiceFactory, logger, consoleMode))
            {
                logger.Info("Enrollment-complete marker handled — agent exiting.");
                return BootstrapResult.Exit(0);
            }

            if (Program.CheckSessionAgeEmergencyBreak(
                    dataDirectory, stateSubdir,
                    agentConfig.AbsoluteMaxSessionHours, agentConfig.SelfDestructOnComplete,
                    cleanupServiceFactory, logger, consoleMode,
                    // Best-effort: surface the otherwise-silent 48h break to the backend before cleanup
                    // (tasks/enrollment-status-reclassification.md). Never blocks the exit.
                    onBreakFired: () => EmergencyBreakReporter.TrySend(agentConfig, Program.GetAgentVersion(), logger)))
            {
                logger.Info("Emergency break fired — agent exiting.");
                return BootstrapResult.Exit(0);
            }

            if (agentConfig.AwaitEnrollment)
            {
                logger.Info($"--await-enrollment: polling for MDM certificate (timeout: {agentConfig.AwaitEnrollmentTimeoutMinutes}min).");
                using (var cts = new CancellationTokenSource())
                {
                    var cert = EnrollmentAwaiter
                        .WaitForMdmCertificateAsync(
                            thumbprint: null,
                            timeoutMinutes: agentConfig.AwaitEnrollmentTimeoutMinutes,
                            logger: logger,
                            cancellationToken: cts.Token)
                        .GetAwaiter().GetResult();
                    if (cert == null)
                    {
                        logger.Error("Await-enrollment: timed out waiting for MDM certificate — exiting.");
                        return BootstrapResult.Exit(3);
                    }
                }

                // Re-resolve TenantId — enrollment typically writes the registry key alongside the cert.
                if (string.IsNullOrEmpty(agentConfig.TenantId))
                {
                    agentConfig.TenantId = TenantIdResolver.Resolve(logger);
                    if (!string.IsNullOrEmpty(agentConfig.TenantId))
                        logger.Info($"Await-enrollment: TenantId discovered from registry: {agentConfig.TenantId}");
                }

                // Await-enrollment is one-shot — remove the persisted config so subsequent restarts proceed normally.
                Program.DeleteAwaitEnrollmentConfig(dataDirectory, logger);
            }

            // Event-driven TenantId wait — kicks in only when the user opted-in via
            // --tenant-id-wait <sec> (CLI or persisted bootstrap-config.json). Bridges
            // the OOBE / hybrid-AAD-join race where the agent fires before the registry
            // catches up. No-op when TenantIdWaitSeconds == 0 (legacy fast-fail) or when
            // the await-enrollment branch already resolved a TenantId.
            if (string.IsNullOrEmpty(agentConfig.TenantId) && agentConfig.TenantIdWaitSeconds > 0)
            {
                using (var waitCts = new CancellationTokenSource())
                {
                    agentConfig.TenantId = TenantIdAwaiter.WaitForTenantId(
                        timeoutSeconds: agentConfig.TenantIdWaitSeconds,
                        logger: logger,
                        ct: waitCts.Token);
                }
            }

            if (string.IsNullOrEmpty(agentConfig.TenantId))
            {
                logger.Error("V2 agent cannot start: TenantId not available (registry empty + no bootstrap config).");
                return BootstrapResult.Exit(2);
            }

            return BootstrapResult.Continue(
                agentConfig: agentConfig,
                sessionPersistence: sessionPersistence,
                previousExit: previousExit,
                isWhiteGloveResume: isWhiteGloveResume,
                cleanupServiceFactory: cleanupServiceFactory);
        }
    }

    /// <summary>
    /// Outcome of <see cref="AgentBootstrap.Run"/>. Either an early exit with a V1-parity
    /// exit code or a "continue" payload carrying the runtime objects RunAgent needs for
    /// Phase 3+ (backend-clients, remote-config, session-registration, orchestrator-wiring).
    /// </summary>
    internal sealed class BootstrapResult
    {
        public bool ShouldExit { get; }
        public int ExitCode { get; }

        // Only valid when ShouldExit == false. Exposed as nullable refs so the
        // continuation path in RunAgent reads them with concise null-forgiving deref.
        public AgentConfiguration AgentConfig { get; }
        public SessionIdPersistence SessionPersistence { get; }
        public Program.PreviousExitSummary PreviousExit { get; }
        public bool IsWhiteGloveResume { get; }
        public Func<CleanupService> CleanupServiceFactory { get; }

        private BootstrapResult(
            bool shouldExit,
            int exitCode,
            AgentConfiguration agentConfig,
            SessionIdPersistence sessionPersistence,
            Program.PreviousExitSummary previousExit,
            bool isWhiteGloveResume,
            Func<CleanupService> cleanupServiceFactory)
        {
            ShouldExit = shouldExit;
            ExitCode = exitCode;
            AgentConfig = agentConfig;
            SessionPersistence = sessionPersistence;
            PreviousExit = previousExit;
            IsWhiteGloveResume = isWhiteGloveResume;
            CleanupServiceFactory = cleanupServiceFactory;
        }

        public static BootstrapResult Exit(int code)
            => new BootstrapResult(true, code, null, null, null, false, null);

        public static BootstrapResult Continue(
            AgentConfiguration agentConfig,
            SessionIdPersistence sessionPersistence,
            Program.PreviousExitSummary previousExit,
            bool isWhiteGloveResume,
            Func<CleanupService> cleanupServiceFactory)
            => new BootstrapResult(false, 0, agentConfig, sessionPersistence, previousExit, isWhiteGloveResume, cleanupServiceFactory);
    }
}
