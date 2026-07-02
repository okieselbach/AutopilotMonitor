using System.Linq;
using System.Net;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Returns agent configuration including collector toggles and active gather rules
    /// Called by the agent at startup and periodically to pick up config changes
    /// Uses device authentication (client certificate), not JWT
    /// </summary>
    public class GetAgentConfigFunction
    {
        private readonly ILogger<GetAgentConfigFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly GatherRuleService _gatherRuleService;
        private readonly ImeLogPatternService _imeLogPatternService;
        private readonly RateLimitService _rateLimitService;
        private readonly AutopilotDeviceValidator _autopilotDeviceValidator;
        private readonly CorporateIdentifierValidator _corporateIdentifierValidator;
        private readonly DeviceAssociationValidator _deviceAssociationValidator;
        private readonly BootstrapSessionService _bootstrapSessionService;

        public GetAgentConfigFunction(
            ILogger<GetAgentConfigFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            GatherRuleService gatherRuleService,
            ImeLogPatternService imeLogPatternService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            DeviceAssociationValidator deviceAssociationValidator,
            BootstrapSessionService bootstrapSessionService)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _gatherRuleService = gatherRuleService;
            _imeLogPatternService = imeLogPatternService;
            _rateLimitService = rateLimitService;
            _autopilotDeviceValidator = autopilotDeviceValidator;
            _corporateIdentifierValidator = corporateIdentifierValidator;
            _deviceAssociationValidator = deviceAssociationValidator;
            _bootstrapSessionService = bootstrapSessionService;
        }

        [Function("GetAgentConfig")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "agent/config")] HttpRequestData req)
        {
            try
            {
                // Get tenantId from query parameter
                var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
                var tenantId = query["tenantId"];

                if (string.IsNullOrEmpty(tenantId))
                {
                    var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                    await badRequest.WriteAsJsonAsync(new
                    {
                        error = "tenantId query parameter is required"
                    });
                    return badRequest;
                }

                // Validate request security (certificate, rate limit, hardware whitelist)
                var (validation, errorResponse) = await req.ValidateSecurityAsync(
                    tenantId,
                    _configService,
                    _rateLimitService,
                    _autopilotDeviceValidator,
                    _corporateIdentifierValidator,
                    _logger,
                    bootstrapSessionService: _bootstrapSessionService,
                    deviceAssociationValidator: _deviceAssociationValidator
                );

                if (errorResponse != null)
                {
                    return errorResponse;
                }

                return await ProcessGetConfigAsync(req, tenantId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting agent config");
                var errorResp = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResp.WriteAsJsonAsync(new
                {
                    error = "Internal server error"
                });
                return errorResp;
            }
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
        /// Returns the (ZipSha256, ExeSha256) pair appropriate for the calling agent.
        /// Reads the X-Agent-Version header, parses the major, and dispatches to the
        /// corresponding per-line field set on <see cref="AdminConfiguration"/> via
        /// <see cref="AdminConfiguration.GetAgentLine(int)"/>. Future V3 = no change here;
        /// add a switch arm in GetAgentLine and a field set on AdminConfiguration.
        /// </summary>
        internal static (string ZipSha256, string ExeSha256) SelectAgentHashesForClient(
            HttpRequestData req,
            AutopilotMonitor.Shared.Models.AdminConfiguration adminConfig)
        {
            var agentVersion = req.Headers.Contains("X-Agent-Version")
                ? req.Headers.GetValues("X-Agent-Version").FirstOrDefault()
                : null;

            var line = adminConfig.GetAgentLine(ParseAgentMajor(agentVersion));
            return (line.ZipSha256, line.ExeSha256);
        }

        /// <summary>
        /// Core config logic: fetch tenant + admin config, gather rules, IME patterns.
        /// Called by both the cert-auth Run() method and the bootstrap wrapper.
        /// </summary>
        internal async Task<HttpResponseData> ProcessGetConfigAsync(HttpRequestData req, string tenantId)
        {
            _logger.LogInformation($"GetAgentConfig: Fetching config for tenant {tenantId}");

            // Get tenant configuration
            var tenantConfig = await _configService.GetConfigurationAsync(tenantId);

            // Get global admin config for platform-wide policy settings
            var adminConfig = await _adminConfigService.GetConfigurationAsync();

            // Build collector configuration from tenant settings + global policy
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

            // Get active gather rules for this tenant (user-defined ad-hoc only)
            var gatherRules = await _gatherRuleService.GetActiveRulesForTenantAsync(tenantId);

            // Get active IME log patterns for this tenant (from Table Storage)
            var imeLogPatterns = await _imeLogPatternService.GetActivePatternsForTenantAsync(tenantId);

            // Merge global + tenant-specific diagnostics log paths
            var globalDiagPaths = adminConfig.GetDiagnosticsGlobalLogPaths();
            var tenantDiagPaths = tenantConfig.GetDiagnosticsLogPaths();
            var diagLogPaths = globalDiagPaths.Concat(tenantDiagPaths).ToList();

            // Select per-line hash oracle from the X-Agent-Version header (parametric per major).
            // The wire response keeps generic field names (LatestAgentSha256 / LatestAgentExeSha256)
            // so agent code is unchanged across all major lines.
            var (latestAgentSha256, latestAgentExeSha256) = SelectAgentHashesForClient(req, adminConfig);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new AgentConfigResponse
            {
                ConfigVersion = 32, // V2 OOBE-console / Shift+F10 detection portal toggle (EnableConsoleBypassDetection; tenant-scoped, default ON / opt-out; gates ConsoleBypass host + ConsolePrefetchScanner)
                UploadIntervalSeconds = 10,
                SelfDestructOnComplete = tenantConfig.SelfDestructOnComplete ?? true,
                KeepLogFile = tenantConfig.KeepLogFile ?? false,
                EnableGeoLocation = tenantConfig.EnableGeoLocation ?? true,
                EnableImeMatchLog = tenantConfig.EnableImeMatchLog ?? false,
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
                LatestAgentSha256 = latestAgentSha256,
                LatestAgentExeSha256 = latestAgentExeSha256,
                AllowAgentDowngrade = adminConfig.AllowAgentDowngrade,
                NtpServer = string.IsNullOrEmpty(tenantConfig.NtpServer) ? "time.windows.com" : tenantConfig.NtpServer,
                EnableTimezoneAutoSet = tenantConfig.EnableTimezoneAutoSet ?? false,
                SendTraceEvents = tenantConfig.SendTraceEvents,
                UnrestrictedMode = tenantConfig.UnrestrictedModeEnabled && tenantConfig.UnrestrictedMode,
                GatherRules = gatherRules,
                ImeLogPatterns = imeLogPatterns,
                WhiteGloveSealingPatternIds = adminConfig.GetWhiteGloveSealingPatternIds(),
            });

            return response;
        }
    }
}
