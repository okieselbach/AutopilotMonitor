using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Config;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// Plan/edition management: GA-only plan+trial mutation (PATCH plan), tenant-admin self-service
    /// trial (POST trial), and the global usage-plan definitions (SectionUsagePlans). All writes go
    /// through <see cref="TenantConfigurationService"/> so the 5-minute config cache is invalidated,
    /// and every mutation is audited under the target tenant's trail.
    /// </summary>
    public class PlanManagementFunction
    {
        /// <summary>Self-service trial length. GA can grant arbitrary end dates via PATCH.</summary>
        internal const int SelfServiceTrialDays = 30;

        private readonly ILogger<PlanManagementFunction> _logger;
        private readonly TenantConfigurationService _configService;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly TimeProvider _time;

        public PlanManagementFunction(
            ILogger<PlanManagementFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            IMaintenanceRepository maintenanceRepo)
            : this(logger, configService, adminConfigService, maintenanceRepo, TimeProvider.System)
        {
        }

        /// <summary>Test seam — inject a fake <see cref="TimeProvider"/> for deterministic trial math.</summary>
        public PlanManagementFunction(
            ILogger<PlanManagementFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            IMaintenanceRepository maintenanceRepo,
            TimeProvider time)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _maintenanceRepo = maintenanceRepo;
            _time = time;
        }

        /// <summary>
        /// PATCH /api/config/{tenantId}/plan — GlobalAdminOnly (catalog-enforced).
        /// Body: { "planTier"?: "community"|"enterprise", "trialExpiresUtc"?: ISO-8601 | null }.
        /// Setting a trial date grants/extends the trial (TrialConsumed is NOT touched — GA
        /// re-grants stay possible); explicit null ends the trial. Absent properties are unchanged.
        /// </summary>
        [Function("SetTenantPlanTier")]
        public async Task<HttpResponseData> SetPlanTier(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "config/{tenantId}/plan")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                var requestCtx = req.GetRequestContext();
                var caller = requestCtx.UserPrincipalName ?? "Unknown";
                _logger.LogInformation("SetTenantPlanTier: tenantId={TenantId} by {User}", requestCtx.TargetTenantId, caller);

                var body = await req.ReadAsStringAsync() ?? string.Empty;
                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                }
                catch (JsonException)
                {
                    return await BadRequestAsync(req, "Invalid JSON body");
                }

                string? newPlanTier = null;
                bool trialProvided = false;
                DateTime? newTrialExpiresUtc = null;

                using (doc)
                {
                    if (doc.RootElement.ValueKind != JsonValueKind.Object)
                        return await BadRequestAsync(req, "Body must be a JSON object");

                    if (doc.RootElement.TryGetProperty("planTier", out var tierProp))
                    {
                        if (tierProp.ValueKind != JsonValueKind.String)
                            return await BadRequestAsync(req, "planTier must be a string");

                        newPlanTier = tierProp.GetString()!.Trim().ToLowerInvariant();
                        if (newPlanTier != FeatureEntitlementCatalog.CommunityTierName &&
                            newPlanTier != FeatureEntitlementCatalog.EnterpriseTierName)
                        {
                            return await BadRequestAsync(req,
                                $"Invalid planTier. Valid values: {FeatureEntitlementCatalog.CommunityTierName}, {FeatureEntitlementCatalog.EnterpriseTierName}");
                        }
                    }

                    if (doc.RootElement.TryGetProperty("trialExpiresUtc", out var trialProp))
                    {
                        trialProvided = true;
                        if (trialProp.ValueKind == JsonValueKind.Null)
                        {
                            newTrialExpiresUtc = null; // explicit null = end trial
                        }
                        else if (trialProp.ValueKind == JsonValueKind.String &&
                                 trialProp.TryGetDateTime(out var parsed))
                        {
                            newTrialExpiresUtc = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
                        }
                        else
                        {
                            return await BadRequestAsync(req, "trialExpiresUtc must be an ISO-8601 date-time string or null");
                        }
                    }
                }

                if (newPlanTier == null && !trialProvided)
                    return await BadRequestAsync(req, "Provide planTier and/or trialExpiresUtc");

                var config = await _configService.GetConfigurationIfExistsAsync(requestCtx.TargetTenantId);
                if (config == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { error = "Tenant not found" });
                    return notFound;
                }

                var nowUtc = _time.GetUtcNow().UtcDateTime;
                var changes = new Dictionary<string, string>();

                if (newPlanTier != null && !string.Equals(config.PlanTier, newPlanTier, StringComparison.Ordinal))
                {
                    changes["PlanTier"] = $"{config.PlanTier} -> {newPlanTier}";
                    config.PlanTier = newPlanTier;
                }

                if (trialProvided && config.TrialExpiresUtc != newTrialExpiresUtc)
                {
                    changes["TrialExpiresUtc"] = $"{FormatUtc(config.TrialExpiresUtc)} -> {FormatUtc(newTrialExpiresUtc)}";
                    config.TrialExpiresUtc = newTrialExpiresUtc;
                    if (newTrialExpiresUtc.HasValue)
                    {
                        config.TrialStartedUtc ??= nowUtc;
                        config.TrialGrantedBy = caller;
                    }
                }

                if (changes.Count > 0)
                {
                    config.UpdatedBy = caller;
                    await SaveInvalidatingOnFailureAsync(config);

                    await _maintenanceRepo.LogAuditEntryAsync(
                        requestCtx.TargetTenantId,
                        "UPDATE",
                        "TenantPlan",
                        requestCtx.TargetTenantId,
                        caller,
                        changes);
                }

                var effectiveEdition = FeatureEntitlementCatalog.ResolveEdition(config.PlanTier, config.TrialExpiresUtc, nowUtc);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    tenantId = requestCtx.TargetTenantId,
                    planTier = config.PlanTier,
                    trialExpiresUtc = config.TrialExpiresUtc,
                    trialConsumed = config.TrialConsumed,
                    effectiveEdition = effectiveEdition.ToString().ToLowerInvariant()
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting plan tier");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// POST /api/config/{tenantId}/trial — TenantAdminOrGA (catalog-enforced). Self-service
        /// 30-day Enterprise trial, exactly once per tenant. 409 when the trial was already
        /// consumed or the tenant is already effectively Enterprise.
        /// </summary>
        [Function("StartTenantTrial")]
        public async Task<HttpResponseData> StartTrial(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "config/{tenantId}/trial")] HttpRequestData req,
            string tenantId)
        {
            try
            {
                var requestCtx = req.GetRequestContext();
                var caller = requestCtx.UserPrincipalName ?? "Unknown";
                _logger.LogInformation("StartTenantTrial: tenantId={TenantId} by {User}", requestCtx.TargetTenantId, caller);

                var config = await _configService.GetConfigurationIfExistsAsync(requestCtx.TargetTenantId);
                if (config == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { error = "Tenant not found" });
                    return notFound;
                }

                var nowUtc = _time.GetUtcNow().UtcDateTime;

                if (config.TrialConsumed)
                {
                    var conflict = req.CreateResponse(HttpStatusCode.Conflict);
                    await conflict.WriteAsJsonAsync(new
                    {
                        error = "TrialAlreadyConsumed",
                        message = "This tenant has already used its one self-service Enterprise trial. Contact support to extend."
                    });
                    return conflict;
                }

                if (FeatureEntitlementCatalog.ResolveEdition(config.PlanTier, config.TrialExpiresUtc, nowUtc) == TenantEdition.Enterprise)
                {
                    var conflict = req.CreateResponse(HttpStatusCode.Conflict);
                    await conflict.WriteAsJsonAsync(new
                    {
                        error = "AlreadyEnterprise",
                        message = "This tenant is already on the Enterprise edition."
                    });
                    return conflict;
                }

                config.TrialStartedUtc = nowUtc;
                config.TrialExpiresUtc = nowUtc.AddDays(SelfServiceTrialDays);
                config.TrialConsumed = true;
                config.TrialGrantedBy = caller;
                config.UpdatedBy = caller;

                await SaveInvalidatingOnFailureAsync(config);

                await _maintenanceRepo.LogAuditEntryAsync(
                    requestCtx.TargetTenantId,
                    "CREATE",
                    "TenantTrial",
                    requestCtx.TargetTenantId,
                    caller,
                    new Dictionary<string, string>
                    {
                        { "TrialStartedUtc", FormatUtc(config.TrialStartedUtc) },
                        { "TrialExpiresUtc", FormatUtc(config.TrialExpiresUtc) }
                    });

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    tenantId = requestCtx.TargetTenantId,
                    trialStartedUtc = config.TrialStartedUtc,
                    trialExpiresUtc = config.TrialExpiresUtc,
                    effectiveEdition = FeatureEntitlementCatalog.EnterpriseTierName
                });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting trial");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// GET /api/global/config/plan-tiers
        /// Returns plan tier definitions from AdminConfiguration.PlanTierDefinitionsJson.
        /// </summary>
        [Function("GetPlanTierDefinitions")]
        public async Task<HttpResponseData> GetPlanTierDefinitions(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/config/plan-tiers")] HttpRequestData req)
        {
            try
            {
                var config = await _adminConfigService.GetConfigurationAsync();
                var tiers = PlanTierDefinitionParser.Parse(config.PlanTierDefinitionsJson);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { tiers });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting plan tier definitions");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// PUT /api/global/config/plan-tiers
        /// Saves plan tier definitions. Body: { "tiers": [...] }
        /// </summary>
        [Function("SetPlanTierDefinitions")]
        public async Task<HttpResponseData> SetPlanTierDefinitions(
            [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "global/config/plan-tiers")] HttpRequestData req)
        {
            try
            {
                var body = await req.ReadFromJsonAsync<SetPlanTierDefinitionsRequest>();
                if (body?.Tiers == null || body.Tiers.Count == 0)
                {
                    return await BadRequestAsync(req, "At least one tier definition is required");
                }

                // Validate tier names are unique
                var names = body.Tiers.Select(t => t.Name.ToLowerInvariant()).ToList();
                if (names.Distinct().Count() != names.Count)
                {
                    return await BadRequestAsync(req, "Tier names must be unique");
                }

                // Normalize names to lowercase
                foreach (var tier in body.Tiers)
                    tier.Name = tier.Name.ToLowerInvariant();

                var config = await _adminConfigService.GetConfigurationAsync();
                config.PlanTierDefinitionsJson = JsonSerializer.Serialize(body.Tiers);
                await _adminConfigService.SaveConfigurationAsync(config);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { tiers = body.Tiers });
                return response;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving plan tier definitions");
                var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
                await errorResponse.WriteAsJsonAsync(new { error = "Internal server error" });
                return errorResponse;
            }
        }

        /// <summary>
        /// Saves via the service (which invalidates the cache on success). Because the mutated
        /// instance IS the cached instance, a failed save would otherwise leave the cache holding
        /// unsaved mutations for up to 5 minutes — invalidate before rethrowing.
        /// </summary>
        private async Task SaveInvalidatingOnFailureAsync(AutopilotMonitor.Shared.Models.TenantConfiguration config)
        {
            try
            {
                await _configService.SaveConfigurationAsync(config);
            }
            catch
            {
                _configService.InvalidateCache(config.TenantId);
                throw;
            }
        }

        private static string FormatUtc(DateTime? value)
            => value?.ToString("yyyy-MM-ddTHH:mm:ssZ") ?? "(none)";

        private static async Task<HttpResponseData> BadRequestAsync(HttpRequestData req, string message)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = message });
            return badRequest;
        }

        private class SetPlanTierDefinitionsRequest
        {
            public List<PlanTierDefinition> Tiers { get; set; } = new();
        }
    }
}
