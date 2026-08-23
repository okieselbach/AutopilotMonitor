using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Configuration
{
    /// <summary>
    /// Projects the remote <see cref="AgentConfigResponse"/> onto the runtime
    /// <see cref="AgentConfiguration"/>. Before this merger existed the agent fetched the remote
    /// config and stored it internally in <see cref="RemoteConfigService.CurrentConfig"/>, but
    /// only <c>Collectors</c>, <c>ImeLogPatterns</c>, <c>GatherRules</c>,
    /// <c>WhiteGloveSealingPatternIds</c> and <c>LatestAgentSha256</c> were consumed downstream.
    /// All other tenant-controlled knobs — <c>SelfDestructOnComplete</c>, <c>KeepLogFile</c>,
    /// <c>NtpServer</c>, <c>DiagnosticsUploadMode</c>, <c>ShowEnrollmentSummary</c>, the reboot
    /// policy, the log level, the lifetime watchdog and the guardrail-relax switch — were bound
    /// to <see cref="AgentConfiguration"/> defaults set from CLI / bootstrap-config, so tenant
    /// admin settings were effectively ignored.
    /// <para>
    /// <b>Merge semantics (V1 parity):</b> remote values OVERWRITE the runtime configuration
    /// unconditionally (V1 <c>ApplyRuntimeSettingsFromRemoteConfig</c>). CLI flags seed the
    /// initial <see cref="AgentConfiguration"/> before Merge runs and then yield to tenant
    /// policy — if an operator deploys with <c>--reboot-on-complete</c> but the tenant config
    /// disables reboots, the tenant wins. This matches the V1 behaviour and keeps tenants as
    /// the source of truth for the knobs that have a remote equivalent.
    /// </para>
    /// </summary>
    public static class RemoteConfigMerger
    {
        /// <summary>
        /// Overwrites the relevant <paramref name="agentConfig"/> fields with values from
        /// <paramref name="remoteConfig"/>. Remote wins unconditionally for every field that has
        /// a direct 1:1 mapping. Returns a <see cref="RemoteConfigMergeResult"/> describing which
        /// observable knobs changed so the caller can emit audit events (e.g.
        /// <c>agent_unrestricted_mode_changed</c>) after the orchestrator is running.
        /// </summary>
        public static RemoteConfigMergeResult Merge(
            AgentConfiguration agentConfig,
            AgentConfigResponse remoteConfig,
            AgentLogger logger = null)
        {
            if (agentConfig == null) throw new ArgumentNullException(nameof(agentConfig));
            if (remoteConfig == null) return RemoteConfigMergeResult.NoChange();

            var result = new RemoteConfigMergeResult();

            // PR5: snapshot the operator-relevant flags BEFORE overwrite so we can log what
            // actually flipped. Log spam is bounded — Merge runs at most once per remote
            // config fetch (every ~5 min by default).
            var prevSelfDestruct = agentConfig.SelfDestructOnComplete;
            var prevKeepLog = agentConfig.KeepLogFile;
            var prevReboot = agentConfig.RebootOnComplete;
            var prevDiagMode = agentConfig.DiagnosticsUploadMode;
            var prevDiagEnabled = agentConfig.DiagnosticsUploadEnabled;
            var prevShowSummary = agentConfig.ShowEnrollmentSummary;
            var prevMaxLifetime = agentConfig.AgentMaxLifetimeMinutes;

            // ---------------- Simple scalar flags — remote wins.
            agentConfig.SelfDestructOnComplete = remoteConfig.SelfDestructOnComplete;
            agentConfig.KeepLogFile = remoteConfig.KeepLogFile;
            agentConfig.RebootOnComplete = remoteConfig.RebootOnComplete;
            agentConfig.EnableGeoLocation = remoteConfig.EnableGeoLocation;

            // ---------------- Log level — remote wins, but we parse defensively so an invalid
            //                  remote value keeps the current level (and logs a warning).
            result.OldLogLevel = agentConfig.LogLevel;
            if (!string.IsNullOrWhiteSpace(remoteConfig.LogLevel))
            {
                if (Enum.TryParse<AgentLogLevel>(remoteConfig.LogLevel, ignoreCase: true, out var parsedLevel))
                {
                    if (parsedLevel != agentConfig.LogLevel)
                    {
                        logger?.Info($"Remote config: LogLevel {agentConfig.LogLevel} → {parsedLevel}");
                        agentConfig.LogLevel = parsedLevel;
                    }
                }
                else
                {
                    logger?.Warning($"Remote config: LogLevel '{remoteConfig.LogLevel}' is not a valid AgentLogLevel — keeping {agentConfig.LogLevel}.");
                }
            }
            result.NewLogLevel = agentConfig.LogLevel;
            result.LogLevelChanged = result.OldLogLevel != result.NewLogLevel;

            // ---------------- Reboot / summary-dialog knobs — direct assignment.
            agentConfig.RebootDelaySeconds = remoteConfig.RebootDelaySeconds;
            agentConfig.ShowEnrollmentSummary = remoteConfig.ShowEnrollmentSummary;
            agentConfig.EnrollmentSummaryTimeoutSeconds = remoteConfig.EnrollmentSummaryTimeoutSeconds;
            agentConfig.EnrollmentSummaryBrandingImageUrl = remoteConfig.EnrollmentSummaryBrandingImageUrl;
            agentConfig.EnrollmentSummaryLaunchRetrySeconds = remoteConfig.EnrollmentSummaryLaunchRetrySeconds;

            // ---------------- NTP / Timezone — remote wins even when the remote sends null so
            //                  a tenant admin can clear a previously-applied custom NTP server.
            agentConfig.NtpServer = remoteConfig.NtpServer;
            agentConfig.EnableTimezoneAutoSet = remoteConfig.EnableTimezoneAutoSet;
            agentConfig.EnableDoGroupIdAutoSet = remoteConfig.EnableDoGroupIdAutoSet;

            // ---------------- Diagnostics — remote wins (including DiagnosticsUploadMode="Off"
            //                  which must clear any CLI-initial default).
            agentConfig.DiagnosticsUploadEnabled = remoteConfig.DiagnosticsUploadEnabled;
            agentConfig.DiagnosticsUploadMode = remoteConfig.DiagnosticsUploadMode;
            agentConfig.DiagnosticsLogPaths = remoteConfig.DiagnosticsLogPaths ?? new List<DiagnosticsLogPath>();
            // The diagnostics package gates its RealmJoin sections on the tenant's watcher
            // toggle but never sees the remote response — mirror the one Analyzers knob here.
            // The remaining Analyzers flags flow untouched to DefaultComponentFactory.
            agentConfig.EnableRealmJoinWatcher = remoteConfig.Analyzers?.EnableRealmJoinWatcher ?? false;

            agentConfig.SendTraceEvents = remoteConfig.SendTraceEvents;

            // ---------------- Continue-Anyway observation — remote wins (operator-set tenant
            //                  opt-in; false must clear a stale cached true).
            agentConfig.EnableEspContinueAnywayObservation = remoteConfig.EnableEspContinueAnywayObservation;

            // ---------------- UnrestrictedMode audit — track old/new for the caller so the
            //                  agent_unrestricted_mode_changed event can fire AFTER the
            //                  orchestrator emitter is alive (V1 parity with
            //                  AuditUnrestrictedModeChange in MonitoringService.cs).
            result.OldUnrestrictedMode = agentConfig.UnrestrictedMode;
            agentConfig.UnrestrictedMode = remoteConfig.UnrestrictedMode;
            result.NewUnrestrictedMode = agentConfig.UnrestrictedMode;
            result.UnrestrictedModeChanged = result.OldUnrestrictedMode != result.NewUnrestrictedMode;

            // ---------------- ImeMatchLog: remote ships a bool, V2 runtime uses a file path.
            //                  V1 parity — `true` expands Constants.ImeMatchLogPath, `false`
            //                  nulls the runtime path so the collector disables itself.
            //                  A CLI override (--ime-match-log <path>) already populated
            //                  ImeMatchLogPath in BuildAgentConfiguration; we only clobber it
            //                  when the remote bool flips off, mirroring V1's unconditional
            //                  assignment with tolerance for the explicit dev override.
            if (remoteConfig.EnableImeMatchLog)
            {
                agentConfig.ImeMatchLogPath = Environment.ExpandEnvironmentVariables(Constants.ImeMatchLogPath);
            }
            else
            {
                // Only null the path when the user did NOT supply an explicit CLI override.
                // A non-null value that does not match the default is a dev-time override.
                var defaultPath = Environment.ExpandEnvironmentVariables(Constants.ImeMatchLogPath);
                if (string.IsNullOrEmpty(agentConfig.ImeMatchLogPath)
                    || string.Equals(agentConfig.ImeMatchLogPath, defaultPath, StringComparison.OrdinalIgnoreCase))
                {
                    agentConfig.ImeMatchLogPath = null;
                }
            }

            // ---------------- GatherRuleDebugLog: same bool→path projection as ImeMatchLog
            //                  above. `true` expands Constants.GatherRuleDebugLogPath, `false`
            //                  nulls the runtime path unless a --gather-debug-log CLI override
            //                  supplied a non-default path.
            if (remoteConfig.EnableGatherRuleDebugLog)
            {
                agentConfig.GatherRuleDebugLogPath = Environment.ExpandEnvironmentVariables(Constants.GatherRuleDebugLogPath);
            }
            else
            {
                var defaultGatherDebugPath = Environment.ExpandEnvironmentVariables(Constants.GatherRuleDebugLogPath);
                if (string.IsNullOrEmpty(agentConfig.GatherRuleDebugLogPath)
                    || string.Equals(agentConfig.GatherRuleDebugLogPath, defaultGatherDebugPath, StringComparison.OrdinalIgnoreCase))
                {
                    agentConfig.GatherRuleDebugLogPath = null;
                }
            }

            // ---------------- Upload interval / batch — direct, V1 parity (no > 0 gate so a
            //                  misconfigured tenant can in theory break uploads, but this
            //                  surfaces the config bug instead of silently masking it).
            agentConfig.UploadIntervalSeconds = remoteConfig.UploadIntervalSeconds;
            if (remoteConfig.MaxBatchSize > 0)
                agentConfig.MaxBatchSize = remoteConfig.MaxBatchSize;

            // ---------------- AuthFailure ceilings — tracker.UpdateLimits() called by caller
            //                  so the refresh lands on the live watchdog.
            agentConfig.MaxAuthFailures = remoteConfig.MaxAuthFailures;
            agentConfig.AuthFailureTimeoutMinutes = remoteConfig.AuthFailureTimeoutMinutes;

            // ---------------- Nested CollectorConfiguration — only the two knobs that have a
            //                  V2 consumer on AgentConfiguration itself. The rest flow through
            //                  the untouched remoteConfig.Collectors to DefaultComponentFactory.
            var collectors = remoteConfig.Collectors;
            if (collectors != null)
            {
                // AgentMaxLifetimeMinutes: 0 = disabled (valid); passed to the orchestrator as a TimeSpan?.
                agentConfig.AgentMaxLifetimeMinutes = collectors.AgentMaxLifetimeMinutes;

                // HelloWaitTimeoutSeconds: V1 parity — only apply when > 0 so an accidentally
                // zero/negative remote value keeps the current non-zero default. V1:
                // MonitoringService.ApplyRuntimeSettingsFromRemoteConfig preserves the current
                // setting instead of disabling Hello-wait altogether.
                if (collectors.HelloWaitTimeoutSeconds > 0)
                    agentConfig.HelloWaitTimeoutSeconds = collectors.HelloWaitTimeoutSeconds;
            }

            // PR5: per-flip Info lines for operator-relevant knobs that were silent before.
            // LogLevel + UnrestrictedMode already log via existing branches above.
            if (prevSelfDestruct != agentConfig.SelfDestructOnComplete)
                logger?.Info($"Remote config: SelfDestructOnComplete {prevSelfDestruct} -> {agentConfig.SelfDestructOnComplete}");
            if (prevKeepLog != agentConfig.KeepLogFile)
                logger?.Info($"Remote config: KeepLogFile {prevKeepLog} -> {agentConfig.KeepLogFile}");
            if (prevReboot != agentConfig.RebootOnComplete)
                logger?.Info($"Remote config: RebootOnComplete {prevReboot} -> {agentConfig.RebootOnComplete}");
            if (!string.Equals(prevDiagMode, agentConfig.DiagnosticsUploadMode, StringComparison.Ordinal))
                logger?.Info($"Remote config: DiagnosticsUploadMode '{prevDiagMode ?? "(null)"}' -> '{agentConfig.DiagnosticsUploadMode ?? "(null)"}'");
            if (prevDiagEnabled != agentConfig.DiagnosticsUploadEnabled)
                logger?.Info($"Remote config: DiagnosticsUploadEnabled {prevDiagEnabled} -> {agentConfig.DiagnosticsUploadEnabled}");
            if (prevShowSummary != agentConfig.ShowEnrollmentSummary)
                logger?.Info($"Remote config: ShowEnrollmentSummary {prevShowSummary} -> {agentConfig.ShowEnrollmentSummary}");
            if (prevMaxLifetime != agentConfig.AgentMaxLifetimeMinutes)
                logger?.Info($"Remote config: AgentMaxLifetimeMinutes {prevMaxLifetime} -> {agentConfig.AgentMaxLifetimeMinutes}");

            return result;
        }
    }

    /// <summary>
    /// Observable side-effects of <see cref="RemoteConfigMerger.Merge"/>. The caller uses this
    /// to decide whether to emit audit events / reconfigure live components (logger, watchdog)
    /// after the orchestrator is alive.
    /// </summary>
    public sealed class RemoteConfigMergeResult
    {
        public bool UnrestrictedModeChanged { get; set; }
        public bool OldUnrestrictedMode { get; set; }
        public bool NewUnrestrictedMode { get; set; }

        public bool LogLevelChanged { get; set; }
        public AgentLogLevel OldLogLevel { get; set; }
        public AgentLogLevel NewLogLevel { get; set; }

        public static RemoteConfigMergeResult NoChange() => new RemoteConfigMergeResult();
    }
}
