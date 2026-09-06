using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Outcome of <see cref="AgentConfigResolver.Build"/>: the response an agent of the tenant
    /// receives, plus the migration candidate that failed validation (null when none) so the
    /// agent channel can log it as delivery evidence.
    /// </summary>
    public sealed class AgentConfigResolution
    {
        public AgentConfigResponse Response { get; }
        public string? RejectedMigrateCandidate { get; }

        public AgentConfigResolution(AgentConfigResponse response, string? rejectedMigrateCandidate)
        {
            Response = response;
            RejectedMigrateCandidate = rejectedMigrateCandidate;
        }
    }

    /// <summary>
    /// The one place that derives an <see cref="AgentConfigResponse"/> from a tenant's stored
    /// configuration, the global admin configuration and the tenant's active rule catalogs.
    /// Serves two callers with the same result: the cert-authenticated agent channel
    /// (GetAgentConfigFunction, which adds the per-device kill verdict on top) and the
    /// Global-Admin report route (GetEffectiveAgentConfigFunction), so the operator sees
    /// exactly what an agent would receive. Every default applied here IS the agent-side
    /// default; the class initializers of <see cref="AgentConfigResponse"/> are the source
    /// of the "custom" highlight in the web report via the shared manifest.
    /// </summary>
    public class AgentConfigResolver
    {
        /// <summary>Bumped when the wire shape gains a field the agent must understand.</summary>
        public const int ConfigVersion = 40; // EnableDoGroupIdAutoSet (Delivery Optimization group ID from network fingerprint)

        /// <summary>
        /// The agent line whose hash oracle is served when no X-Agent-Version is available
        /// (operator report). V2 is the only wired line — see AdminConfiguration.GetAgentLine.
        /// </summary>
        public const int CurrentAgentMajor = 2;

        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly GatherRuleService _gatherRuleService;
        private readonly ImeLogPatternService _imeLogPatternService;

        public AgentConfigResolver(
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            GatherRuleService gatherRuleService,
            ImeLogPatternService imeLogPatternService)
        {
            _configService = configService;
            _adminConfigService = adminConfigService;
            _gatherRuleService = gatherRuleService;
            _imeLogPatternService = imeLogPatternService;
        }

        /// <summary>
        /// Loads the four inputs and builds the response. Rule and pattern reads are independent
        /// and served from per-instance catalog caches — fetched concurrently.
        /// </summary>
        public async Task<AgentConfigResolution> ResolveAsync(string tenantId, int agentMajor)
        {
            var tenantConfig = await _configService.GetConfigurationAsync(tenantId);
            var adminConfig = await _adminConfigService.GetConfigurationAsync();

            var gatherRulesTask = _gatherRuleService.GetActiveRulesForTenantAsync(tenantId);
            var imeLogPatternsTask = _imeLogPatternService.GetActivePatternsForTenantAsync(tenantId);
            await Task.WhenAll(gatherRulesTask, imeLogPatternsTask);

            return Build(tenantConfig, adminConfig, await gatherRulesTask, await imeLogPatternsTask, agentMajor, DateTime.UtcNow);
        }

        /// <summary>
        /// Pure derivation: tenant config + admin config + catalogs → response. Device-specific
        /// fields (DeviceBlocked, DeviceKillSignal, UnblockAt) stay at their defaults; the agent
        /// channel sets them from its kill verdict.
        /// </summary>
        public static AgentConfigResolution Build(
            TenantConfiguration tenantConfig,
            AdminConfiguration adminConfig,
            List<GatherRule> gatherRules,
            List<ImeLogPattern> imeLogPatterns,
            int agentMajor,
            DateTime nowUtc)
        {
            // Collector configuration from tenant settings + global policy
            var collectors = new CollectorConfiguration
            {
                EnablePerformanceCollector = tenantConfig.EnablePerformanceCollector,
                PerformanceIntervalSeconds = tenantConfig.PerformanceCollectorIntervalSeconds,
                CollectorIdleTimeoutMinutes = adminConfig.CollectorIdleTimeoutMinutes,
                DesktopDetectorNoCandidateTimeoutMinutes = adminConfig.DesktopDetectorNoCandidateTimeoutMinutes,
                HelloWaitTimeoutSeconds = tenantConfig.HelloWaitTimeoutSeconds,
                AgentMaxLifetimeMinutes = tenantConfig.AgentMaxLifetimeMinutes ?? 360,
                ModernDeploymentHarmlessEventIds = adminConfig.GetModernDeploymentHarmlessEventIds().ToArray()
            };

            // Merge global + tenant-specific diagnostics log paths (the built-in sections are
            // compiled into the agent — DiagnosticsBuiltInSections — and never travel here)
            var diagLogPaths = MergeDiagnosticsLogPaths(
                adminConfig.GetDiagnosticsGlobalLogPaths(), tenantConfig.GetDiagnosticsLogPaths());

            // Per-line hash oracle (parametric per major). The wire response keeps generic field
            // names (LatestAgentSha256 / LatestAgentExeSha256) so agent code is unchanged across lines.
            var line = adminConfig.GetAgentLine(agentMajor);

            // Endpoint migration on the control channel: the (validated) re-home target, so agents
            // still bound to this backend's compiled-in URL move themselves at their next start.
            var migrateTarget = ResolveMigrateTarget(adminConfig, tenantConfig.TenantId, out var rejectedMigrateCandidate);

            var response = new AgentConfigResponse
            {
                ConfigVersion = ConfigVersion,
                UploadIntervalSeconds = Shared.Constants.DefaultUploadIntervalSeconds,
                SelfDestructOnComplete = tenantConfig.SelfDestructOnComplete ?? true,
                KeepLogFile = tenantConfig.KeepLogFile ?? false,
                EnableGeoLocation = tenantConfig.EnableGeoLocation ?? true,
                EnableImeMatchLog = tenantConfig.EnableImeMatchLog ?? false,
                EnableGatherRuleDebugLog = tenantConfig.EnableGatherRuleDebugLog ?? false,
                EnableEspContinueAnywayObservation = tenantConfig.EnableEspContinueAnywayObservation ?? false,
                MaxAuthFailures = tenantConfig.MaxAuthFailures ?? 5,
                AuthFailureTimeoutMinutes = tenantConfig.AuthFailureTimeoutMinutes ?? 0,
                LogLevel = tenantConfig.LogLevel ?? "Info",
                RebootOnComplete = tenantConfig.RebootOnComplete ?? false,
                RebootDelaySeconds = tenantConfig.RebootDelaySeconds ?? 10,
                ShowEnrollmentSummary = tenantConfig.ShowEnrollmentSummary ?? false,
                EnrollmentSummaryTimeoutSeconds = tenantConfig.EnrollmentSummaryTimeoutSeconds ?? 60,
                EnrollmentSummaryBrandingImageUrl = tenantConfig.EnrollmentSummaryBrandingImageUrl,
                EnrollmentSummaryLaunchRetrySeconds = tenantConfig.EnrollmentSummaryLaunchRetrySeconds ?? 120,
                MaxBatchSize = tenantConfig.MaxBatchSize ?? 100,
                DiagnosticsUploadEnabled = ResolveDiagnosticsUploadEnabled(
                    tenantConfig.DiagnosticsBlobSasUrl, tenantConfig.DiagnosticsUploadDestination),
                DiagnosticsUploadMode = tenantConfig.DiagnosticsUploadMode ?? "Off",
                DiagnosticsLogPaths = diagLogPaths,
                Collectors = collectors,
                Analyzers = new AnalyzerConfiguration
                {
                    EnableLocalAdminAnalyzer = tenantConfig.EnableLocalAdminAnalyzer ?? true,
                    LocalAdminAllowedAccounts = tenantConfig.GetLocalAdminAllowedAccounts(),
                    EnableSoftwareInventoryAnalyzer = tenantConfig.EnableSoftwareInventoryAnalyzer ?? false,
                    EnableIntegrityBypassAnalyzer = tenantConfig.EnableIntegrityBypassAnalyzer ?? true,
                    EnableRealmJoinWatcher = tenantConfig.EnableRealmJoinWatcher ?? false,
                    KeepAwakeDuringUserEsp = tenantConfig.KeepAwakeDuringUserEsp ?? false,
                    EnableConsoleBypassDetection = tenantConfig.EnableConsoleBypassDetection ?? true
                },
                LatestAgentSha256 = line.ZipSha256,
                LatestAgentExeSha256 = line.ExeSha256,
                AllowAgentDowngrade = adminConfig.AllowAgentDowngrade,
                NtpServer = string.IsNullOrEmpty(tenantConfig.NtpServer) ? "time.windows.com" : tenantConfig.NtpServer,
                EnableTimezoneAutoSet = tenantConfig.EnableTimezoneAutoSet ?? false,
                EnableDoGroupIdAutoSet = tenantConfig.EnableDoGroupIdAutoSet ?? false,
                SendTraceEvents = tenantConfig.SendTraceEvents,
                UnrestrictedMode = TenantEntitlementService.IsUnrestrictedModeActive(tenantConfig, nowUtc),
                GatherRules = gatherRules,
                ImeLogPatterns = imeLogPatterns,
                WhiteGloveSealingPatternIds = adminConfig.GetWhiteGloveSealingPatternIds(),
                MigrateToApiBaseUrl = migrateTarget,
            };

            return new AgentConfigResolution(response, rejectedMigrateCandidate);
        }

        /// <summary>
        /// Parses the major-version from an X-Agent-Version header value.
        /// Accepts SemVer-ish strings like "2.0.114" or "2.0.114+abc123".
        /// Missing/unparsable → returns 1 (backward-compat: very old agents may omit the
        /// header). The V1 line is retired, so major 1 resolves to empty hashes via
        /// GetAgentLine's default arm — legacy stragglers just skip their integrity check.
        /// Deliberately NOT defaulting to 2: that would hand V2 hashes to V1 binaries and
        /// could trigger the runtime_hash_mismatch force-update path against the wrong line.
        /// </summary>
        internal static int ParseAgentMajor(string? agentVersion)
        {
            if (string.IsNullOrWhiteSpace(agentVersion))
                return 1;

            var dot = agentVersion.IndexOf('.');
            var majorStr = dot > 0 ? agentVersion.Substring(0, dot) : agentVersion;
            return int.TryParse(majorStr, out var major) ? major : 1;
        }

        /// <summary>
        /// Decides whether the agent should perform diagnostics uploads at all.
        /// The CustomerSas destination is gated on a per-tenant SAS URL being present, but the
        /// Hosted destination has no such URL (the platform owns the storage) — so gating purely
        /// on the SAS URL silently disabled uploads for every Hosted-destination tenant. Enable
        /// when either a customer SAS is configured OR the destination is Hosted.
        /// </summary>
        internal static bool ResolveDiagnosticsUploadEnabled(string? diagnosticsBlobSasUrl, string? destination)
        {
            return !string.IsNullOrEmpty(diagnosticsBlobSasUrl)
                || string.Equals(destination, "Hosted", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Global ∪ tenant diagnostics paths: global first, order preserved, blank paths dropped,
        /// duplicates (trimmed, case-insensitive) collapsed onto the first occurrence. The agent
        /// keys ZIP entries on the path, so a path present in both lists would otherwise produce
        /// duplicate entry names inside the archive.
        /// </summary>
        internal static List<DiagnosticsLogPath> MergeDiagnosticsLogPaths(
            IEnumerable<DiagnosticsLogPath> global,
            IEnumerable<DiagnosticsLogPath> tenant)
        {
            return global.Concat(tenant)
                .Where(p => !string.IsNullOrWhiteSpace(p?.Path))
                .GroupBy(p => p.Path.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>
        /// Resolves the endpoint-migration target for a tenant: per-tenant override wins over
        /// the global value; an override entry with an empty value pins the tenant (no
        /// migration even while the global target is set — staged rollout). The winning value
        /// is validated against <see cref="AutopilotMonitor.Shared.Services.AgentEndpointMigrationRules"/>;
        /// invalid values resolve to null (fail-safe: better to strand an agent on the old
        /// backend than to serve a broken or non-allowlisted URL).
        /// </summary>
        internal static string? ResolveMigrateTarget(
            AdminConfiguration adminConfig,
            string tenantId,
            out string? rejectedCandidate)
        {
            rejectedCandidate = null;

            string? candidate = adminConfig.AgentMigrateApiBaseUrl;
            var overrides = adminConfig.GetAgentMigrateTenantOverrides();
            if (!string.IsNullOrEmpty(tenantId) && overrides.TryGetValue(tenantId, out var tenantTarget))
                candidate = tenantTarget; // empty string = pinned, handled below

            if (string.IsNullOrWhiteSpace(candidate))
                return null;

            if (AutopilotMonitor.Shared.Services.AgentEndpointMigrationRules
                .TryNormalizeTarget(candidate, out var normalized))
            {
                return normalized;
            }

            rejectedCandidate = candidate;
            return null;
        }
    }
}
