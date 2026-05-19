using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Runtime
{
    /// <summary>
    /// Phase 4 of <see cref="Program"/>'s <c>RunAgent</c>: fetch the tenant's
    /// <see cref="AgentConfigResponse"/>, merge it into the runtime
    /// <see cref="AgentConfiguration"/> via <see cref="RemoteConfigMerger"/>, refresh tracker
    /// ceilings + log level, propagate the backend-expected SHA, run the post-config binary-
    /// integrity check, and best-effort delete the persisted bootstrap-config (H-2 mitigation
    /// once the MDM cert proves it can authenticate).
    /// <para>
    /// No early-exit paths — Phase 4 always returns a populated bundle. Failures inside the
    /// integrity check or bootstrap-config-cleanup are surfaced as fire-and-forget distress
    /// reports / log entries; the agent must keep starting on a stale config rather than
    /// abort, matching V1 behaviour.
    /// </para>
    /// </summary>
    internal static class AgentRuntimeConfig
    {
        public static RuntimeConfigBundle Resolve(
            AgentConfiguration agentConfig,
            BackendAuthBundle auth,
            string agentVersion,
            bool consoleMode,
            AgentLogger logger)
        {
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (auth == null) throw new ArgumentNullException(nameof(auth));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            var remoteConfigService = new RemoteConfigService(
                auth.BackendApiClient, agentConfig.TenantId, logger,
                auth.EmergencyReporter, auth.DistressReporter, auth.AuthFailureTracker);
            // retryOnTransientErrors:true is essential for the initial fetch — a Function App
            // cold-start (typically 30-60 s after a deploy) will time out a single-shot call and
            // silently strand the agent on built-in defaults for the entire session. Auth
            // failures still bail immediately (they won't change with a retry).
            var remoteConfig = remoteConfigService
                .FetchConfigAsync(retryOnTransientErrors: true)
                .GetAwaiter().GetResult();

            // Project remote tenant-controlled knobs onto the runtime AgentConfiguration so that
            // downstream consumers (CleanupService, SummaryDialogLauncher, StartupEnvironmentProbes,
            // DiagnosticsPackageService, EnrollmentTerminationHandler, the logger, the watchdog)
            // actually respect the tenant admin settings. V1 parity — remote wins
            // unconditionally for every knob that has a 1:1 mapping. CLI flags seed the initial
            // AgentConfiguration in BuildAgentConfiguration and then yield to tenant policy.
            var configMergeResult = RemoteConfigMerger.Merge(agentConfig, remoteConfig, logger);

            // Refresh tracker ceilings with the tenant-specific values we just merged in.
            auth.AuthFailureTracker.UpdateLimits(agentConfig.MaxAuthFailures, agentConfig.AuthFailureTimeoutMinutes);

            logger.SetLogLevel(agentConfig.LogLevel);

            // Propagate the backend-expected SHA so the runtime hash-mismatch trigger has the
            // up-to-date integrity hash. Also refresh AllowAgentDowngrade.
            if (!string.IsNullOrEmpty(remoteConfig.LatestAgentSha256))
                SelfUpdater.BackendExpectedSha256 = remoteConfig.LatestAgentSha256;

            VerifyBinaryIntegrity(remoteConfig, auth.EmergencyReporter, agentVersion, consoleMode, logger);

            TryCleanupBootstrapConfig(agentConfig, agentVersion, logger);

            return new RuntimeConfigBundle(remoteConfigService, remoteConfig, configMergeResult);
        }

        /// <summary>
        /// Post-config binary-integrity check: verify the running EXE's SHA-256 against the
        /// value advertised by the backend. V1 parity — on mismatch we (a) emit an
        /// IntegrityCheckFailed emergency report and (b) fire the runtime self-update trigger
        /// so the agent auto-heals (SelfUpdater.CheckAndApplyUpdateAsync force-update path).
        /// The trigger is single-shot per process (Interlocked guard inside the verifier).
        /// </summary>
        private static void VerifyBinaryIntegrity(
            AgentConfigResponse remoteConfig,
            EmergencyReporter emergencyReporter,
            string agentVersion,
            bool consoleMode,
            AgentLogger logger)
        {
            var agentDirForTrigger = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? string.Empty;
            Func<string, bool, Task> runtimeSelfUpdateTrigger = (zipHash, downgrade) =>
            {
                if (!string.IsNullOrEmpty(zipHash))
                    SelfUpdater.BackendExpectedSha256 = zipHash;

                return SelfUpdater.CheckAndApplyUpdateAsync(
                    currentVersion: agentVersion,
                    agentDir: agentDirForTrigger,
                    consoleMode: consoleMode,
                    forceUpdate: true,
                    triggerReason: "runtime_hash_mismatch",
                    downloadTimeoutMsOverride: 60000,
                    allowDowngrade: downgrade);
            };

            var integrityResult = BinaryIntegrityVerifier.Check(
                expectedSha256: remoteConfig.LatestAgentExeSha256,
                logger: logger,
                runtimeSelfUpdateTrigger: runtimeSelfUpdateTrigger,
                zipHash: remoteConfig.LatestAgentSha256,
                allowDowngrade: remoteConfig.AllowAgentDowngrade);
            if (integrityResult.IsMismatch)
            {
                _ = emergencyReporter.TrySendAsync(
                    AgentErrorType.IntegrityCheckFailed,
                    $"Running exe SHA-256 differs from backend-advertised hash. actual={integrityResult.ActualSha256}, expected={integrityResult.ExpectedSha256}");
            }
        }

        /// <summary>
        /// H-2 mitigation: delete the persisted bootstrap-config.json once the MDM cert
        /// proves it can authenticate. Non-blocking — any failure leaves the file for retry.
        /// </summary>
        private static void TryCleanupBootstrapConfig(
            AgentConfiguration agentConfig,
            string agentVersion,
            AgentLogger logger)
        {
            try
            {
                BootstrapConfigCleanup
                    .TryDeleteIfCertReadyAsync(agentConfig, logger, agentVersion)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.Debug($"BootstrapConfigCleanup outer exception: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Phase 4 outcome — the live <see cref="RemoteConfigService"/> (used later by the
    /// <c>rotate_config</c> ServerAction callback), the fetched <see cref="AgentConfigResponse"/>
    /// and the <see cref="RemoteConfigMergeResult"/> diff snapshot (used by the
    /// <c>EmitUnrestrictedModeAuditIfChanged</c> lifecycle hook).
    /// </summary>
    internal sealed class RuntimeConfigBundle
    {
        public RemoteConfigService RemoteConfigService { get; }
        public AgentConfigResponse RemoteConfig { get; }
        public RemoteConfigMergeResult MergeResult { get; }

        public RuntimeConfigBundle(
            RemoteConfigService remoteConfigService,
            AgentConfigResponse remoteConfig,
            RemoteConfigMergeResult mergeResult)
        {
            RemoteConfigService = remoteConfigService;
            RemoteConfig = remoteConfig;
            MergeResult = mergeResult;
        }
    }
}
