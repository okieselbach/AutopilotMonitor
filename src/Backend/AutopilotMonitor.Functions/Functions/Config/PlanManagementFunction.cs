using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
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
        private readonly OpsEventService _opsEvents;
        private readonly ProConferralService _proConferral;
        private readonly TimeProvider _time;

        public PlanManagementFunction(
            ILogger<PlanManagementFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            IMaintenanceRepository maintenanceRepo,
            OpsEventService opsEvents,
            ProConferralService proConferral)
            : this(logger, configService, adminConfigService, maintenanceRepo, opsEvents, proConferral, TimeProvider.System)
        {
        }

        /// <summary>Test seam — inject a fake <see cref="TimeProvider"/> for deterministic trial math.</summary>
        public PlanManagementFunction(
            ILogger<PlanManagementFunction> logger,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            IMaintenanceRepository maintenanceRepo,
            OpsEventService opsEvents,
            ProConferralService proConferral,
            TimeProvider time)
        {
            _logger = logger;
            _configService = configService;
            _adminConfigService = adminConfigService;
            _maintenanceRepo = maintenanceRepo;
            _opsEvents = opsEvents;
            _proConferral = proConferral;
            _time = time;
        }

        /// <summary>
        /// PATCH /api/config/{tenantId}/plan — GlobalAdminOnly (catalog-enforced).
        /// Body: { "planTier"?: "community"|"pro", "trialExpiresUtc"?: ISO-8601 | null, "maxDelegatedTenants"?: int | null,
        ///         "mcpUsagePlan"?: string | null, "payingCustomer"?: bool }.
        /// payingCustomer is sales bookkeeping only (no entitlement effect). A permanent-Pro tenant that drops to
        /// Community here takes conferred Pro away from every tenant it manages — their retention grace anchor
        /// is stamped in the same request (ProConferralService).
        /// maxDelegatedTenants sets the delegated (MSP) slot override (null clears it — the plan entitlement applies).
        /// mcpUsagePlan sets the tenant-wide MCP usage-plan override — the name of a SectionUsagePlans plan, validated
        /// against the current definitions (400 for an unknown name, so a typo can never silently drop the tenant
        /// to the Community fallback); null clears it (the edition default applies). It does NOT change the edition.
        /// The legacy stored value "enterprise" stays readable (resolves as Pro) but is no longer
        /// accepted on writes.
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
                bool slotsProvided = false;
                int? newMaxDelegatedTenants = null;
                bool mcpPlanProvided = false;
                string? newMcpUsagePlan = null;
                bool payingProvided = false;
                bool newPayingCustomer = false;

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
                            newPlanTier != FeatureEntitlementCatalog.ProTierName)
                        {
                            return await BadRequestAsync(req,
                                $"Invalid planTier. Valid values: {FeatureEntitlementCatalog.CommunityTierName}, {FeatureEntitlementCatalog.ProTierName}");
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

                    if (doc.RootElement.TryGetProperty("maxDelegatedTenants", out var slotsProp))
                    {
                        slotsProvided = true;
                        if (slotsProp.ValueKind == JsonValueKind.Null)
                        {
                            newMaxDelegatedTenants = null; // explicit null = clear the override
                        }
                        else if (slotsProp.ValueKind == JsonValueKind.Number && slotsProp.TryGetInt32(out var slots) && slots >= 0)
                        {
                            newMaxDelegatedTenants = slots;
                        }
                        else
                        {
                            return await BadRequestAsync(req, "maxDelegatedTenants must be a non-negative integer or null");
                        }
                    }

                    if (doc.RootElement.TryGetProperty("mcpUsagePlan", out var mcpPlanProp))
                    {
                        mcpPlanProvided = true;
                        if (mcpPlanProp.ValueKind == JsonValueKind.Null)
                        {
                            newMcpUsagePlan = null; // explicit null = clear the override
                        }
                        else if (mcpPlanProp.ValueKind == JsonValueKind.String)
                        {
                            // Blank = clear as well (the select's "edition default" option); a name is validated below.
                            newMcpUsagePlan = TenantEntitlementService.NormalizePlanName(mcpPlanProp.GetString());
                        }
                        else
                        {
                            return await BadRequestAsync(req, "mcpUsagePlan must be a plan name or null");
                        }
                    }

                    if (doc.RootElement.TryGetProperty("payingCustomer", out var payingProp))
                    {
                        if (payingProp.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            return await BadRequestAsync(req, "payingCustomer must be a boolean");
                        payingProvided = true;
                        newPayingCustomer = payingProp.GetBoolean();
                    }
                }

                if (newPlanTier == null && !trialProvided && !slotsProvided && !mcpPlanProvided && !payingProvided)
                    return await BadRequestAsync(req, "Provide planTier, trialExpiresUtc, maxDelegatedTenants, mcpUsagePlan and/or payingCustomer");

                if (newMcpUsagePlan != null)
                {
                    // Fail-loud on SET: an override naming a plan that exists nowhere would resolve fail-closed to
                    // the Community fallback at read time — silently shrinking the tenant instead of raising it.
                    var adminConfig = await _adminConfigService.GetConfigurationAsync();
                    var definitions = PlanTierDefinitionParser.Parse(adminConfig.PlanTierDefinitionsJson);
                    if (!IsKnownUsagePlan(newMcpUsagePlan, definitions))
                    {
                        var known = string.Join(", ", KnownUsagePlanNames(definitions));
                        return await BadRequestAsync(req, $"Unknown mcpUsagePlan '{newMcpUsagePlan}'. Known plans: {known}");
                    }
                }

                var config = await _configService.GetConfigurationIfExistsAsync(requestCtx.TargetTenantId);
                if (config == null)
                {
                    var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFound.WriteAsJsonAsync(new { error = "Tenant not found" });
                    return notFound;
                }

                var nowUtc = _time.GetUtcNow().UtcDateTime;
                var changes = new Dictionary<string, string>();

                // Conferred Pro follows the PERMANENT tier of the managing tenant (a trial confers nothing):
                // remember the standing before the mutation so a permanent-Pro → non-permanent drop can
                // stamp the managed tenants' grace anchors below.
                var wasPermanentPro = FeatureEntitlementCatalog.IsPermanentProTier(config.PlanTier);
                var (editionBefore, editionAfter) =
                    ApplyPlanChanges(config, newPlanTier, trialProvided, newTrialExpiresUtc, caller, nowUtc, changes);
                ApplyDelegatedSlotChange(config, slotsProvided, newMaxDelegatedTenants, changes);
                ApplyMcpUsagePlanChange(config, mcpPlanProvided, newMcpUsagePlan, changes);
                ApplyPayingCustomerChange(config, payingProvided, newPayingCustomer, changes);

                if (changes.Count > 0)
                {
                    config.UpdatedBy = caller;
                    // Fail-loud: SaveConfigurationAsync throws when the write did not persist (and
                    // invalidates the cached — possibly mutated — instance in its finally), so a
                    // failed save can never be audited or returned as 200.
                    await _configService.SaveConfigurationAsync(config, "plan", "plan tier change");

                    await _maintenanceRepo.LogAuditEntryAsync(
                        requestCtx.TargetTenantId,
                        "UPDATE",
                        "TenantPlan",
                        requestCtx.TargetTenantId,
                        caller,
                        changes);

                    if (editionBefore == TenantEdition.Pro && editionAfter == TenantEdition.Community)
                    {
                        await _opsEvents.RecordTenantPlanDowngradedAsync(
                            requestCtx.TargetTenantId,
                            config.DomainName,
                            caller,
                            TenantEntitlementService.GetRetentionGraceEndUtc(config, nowUtc),
                            config.DataRetentionDays);
                    }

                    if (wasPermanentPro && !FeatureEntitlementCatalog.IsPermanentProTier(config.PlanTier))
                    {
                        // After the save, so the managed tenants' index rebuild already sees the new tier.
                        var stamped = await _proConferral.RecordLossForOwnedGroupAsync(
                            requestCtx.TargetTenantId, "manager-lost-permanent-pro");
                        if (stamped > 0)
                            changes["ConferredProEndedForManagedTenants"] = stamped.ToString();
                    }

                    // The mirror image of the downgrade event: a GA granting or extending a trial
                    // is the same conversion moment as the self-service start, and sales/support
                    // channels bind to one event type, not two. Keyed off the recorded change so
                    // an unrelated planTier-only edit stays silent; ending a trial (explicit null)
                    // is a downgrade, already covered above.
                    if (changes.ContainsKey("TrialExpiresUtc") && config.TrialExpiresUtc.HasValue)
                    {
                        await _opsEvents.RecordTenantTrialStartedAsync(
                            requestCtx.TargetTenantId,
                            config.DomainName,
                            config.ContactEmail,
                            config.CompanyName,
                            config.TrialStartedUtc,
                            config.TrialExpiresUtc,
                            caller,
                            selfService: false);
                    }
                }

                var resolution = FeatureEntitlementCatalog.Resolve(config, nowUtc);
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new SetTenantPlanTierResponse
                {
                    TenantId = requestCtx.TargetTenantId,
                    PlanTier = config.PlanTier,
                    TrialExpiresUtc = config.TrialExpiresUtc,
                    TrialConsumed = config.TrialConsumed,
                    EffectiveEdition = resolution.EditionName,
                    EditionSource = resolution.SourceName,
                    RetentionGraceEndsUtc = TenantEntitlementService.GetRetentionGraceEndUtc(config, nowUtc),
                    MaxDelegatedTenants = TenantEntitlementService.GetMaxDelegatedTenants(config, nowUtc),
                    MaxDelegatedTenantsOverride = config.MaxDelegatedTenantsOverride,
                    McpUsagePlan = TenantEntitlementService.GetMcpUsagePlanName(config, nowUtc),
                    McpUsagePlanOverride = config.McpUsagePlanOverride,
                    PayingCustomer = config.PayingCustomer,
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
        /// Pure mutation core of the PATCH plan endpoint — no I/O, unit-testable. Applies the
        /// plan/trial changes to <paramref name="config"/>, records human-readable entries in
        /// <paramref name="changes"/>, and returns the EFFECTIVE edition before/after (same
        /// <paramref name="nowUtc"/> for both, so only the mutation itself can flip it).
        ///
        /// Maintains the retention downgrade grace anchor: an effective Pro→Community
        /// transition stamps <see cref="TenantConfiguration.ProDowngradedUtc"/> (covers both a
        /// planTier downgrade and an explicitly ended trial); any state that is effectively Pro
        /// afterwards clears the anchor, so a re-upgrade also resets the grace clock.
        /// </summary>
        internal static (TenantEdition Before, TenantEdition After) ApplyPlanChanges(
            TenantConfiguration config,
            string? newPlanTier,
            bool trialProvided,
            DateTime? newTrialExpiresUtc,
            string caller,
            DateTime nowUtc,
            Dictionary<string, string> changes)
        {
            var before = FeatureEntitlementCatalog.ResolveEdition(config, nowUtc);

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

            var after = FeatureEntitlementCatalog.ResolveEdition(config, nowUtc);

            if (before == TenantEdition.Pro && after == TenantEdition.Community)
            {
                changes["ProDowngradedUtc"] = $"{FormatUtc(config.ProDowngradedUtc)} -> {FormatUtc(nowUtc)}";
                config.ProDowngradedUtc = nowUtc;
            }
            else if (after == TenantEdition.Pro && config.ProDowngradedUtc.HasValue)
            {
                changes["ProDowngradedUtc"] = $"{FormatUtc(config.ProDowngradedUtc)} -> (none)";
                config.ProDowngradedUtc = null;
            }

            return (before, after);
        }

        /// <summary>
        /// Pure mutation core for the delegated (MSP) slot override: records and applies the change only
        /// when a value was provided AND differs. The audit entry names "(catalog)" for the absent override.
        /// </summary>
        internal static void ApplyDelegatedSlotChange(
            TenantConfiguration config, bool provided, int? maxDelegatedTenants, Dictionary<string, string> changes)
        {
            if (!provided || config.MaxDelegatedTenantsOverride == maxDelegatedTenants)
                return;
            changes["MaxDelegatedTenantsOverride"] =
                $"{config.MaxDelegatedTenantsOverride?.ToString() ?? "(catalog)"} -> {maxDelegatedTenants?.ToString() ?? "(catalog)"}";
            config.MaxDelegatedTenantsOverride = maxDelegatedTenants;
        }

        /// <summary>
        /// Pure mutation core for the tenant-wide MCP usage-plan override: records and applies the change only
        /// when a value was provided AND differs (case-insensitive — the stored value is normalized on write).
        /// The audit entry names "(edition default)" for the absent override. Validation of the plan name against
        /// the definitions happens in the endpoint (I/O), not here.
        /// </summary>
        internal static void ApplyMcpUsagePlanChange(
            TenantConfiguration config, bool provided, string? mcpUsagePlan, Dictionary<string, string> changes)
        {
            var normalized = TenantEntitlementService.NormalizePlanName(mcpUsagePlan);
            if (!provided || string.Equals(config.McpUsagePlanOverride, normalized, StringComparison.OrdinalIgnoreCase))
                return;
            changes["McpUsagePlanOverride"] =
                $"{config.McpUsagePlanOverride ?? "(edition default)"} -> {normalized ?? "(edition default)"}";
            config.McpUsagePlanOverride = normalized;
        }

        /// <summary>
        /// Pure mutation core for the sales bookkeeping flag: records and applies the change only when a value
        /// was provided AND differs. No entitlement effect — see <see cref="TenantConfiguration.PayingCustomer"/>.
        /// </summary>
        internal static void ApplyPayingCustomerChange(
            TenantConfiguration config, bool provided, bool payingCustomer, Dictionary<string, string> changes)
        {
            if (!provided || config.PayingCustomer == payingCustomer)
                return;
            changes["PayingCustomer"] = $"{config.PayingCustomer} -> {payingCustomer}";
            config.PayingCustomer = payingCustomer;
        }

        /// <summary>Pure: whether <paramref name="planName"/> (normalized) names a SectionUsagePlans definition.</summary>
        internal static bool IsKnownUsagePlan(string planName, IEnumerable<PlanTierDefinition> definitions)
            => definitions.Any(d => string.Equals(TenantEntitlementService.NormalizePlanName(d.Name), planName, StringComparison.OrdinalIgnoreCase));

        private static IEnumerable<string> KnownUsagePlanNames(IEnumerable<PlanTierDefinition> definitions)
            => definitions.Select(d => TenantEntitlementService.NormalizePlanName(d.Name)).Where(n => n != null).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal);

        /// <summary>
        /// POST /api/config/{tenantId}/trial — TenantAdminOrGA (catalog-enforced). Self-service
        /// 30-day Pro trial, exactly once per tenant. 409 when the trial was already
        /// consumed or the tenant is already effectively Pro.
        /// </summary>
        /// <summary>
        /// Pure verdict for the self-service trial start; null = allowed. Order matters and is
        /// test-pinned: the terminal conditions (trial consumed, already Pro) win over the
        /// contact-profile prompt — asking for an address would be pointless there. The contact
        /// profile (address + company name) is enforced ONLY at this plan entry point, never as
        /// a runtime gate on Pro features (existing Pro tenants get the dashboard banner instead
        /// of a lockout); GA plan assignment (PATCH plan) deliberately has no such block — the
        /// admin UI warns. One verdict names everything that is missing, so a caller never has
        /// to fix the profile in two round trips.
        /// </summary>
        internal static (string Error, string Message)? EvaluateTrialStart(TenantConfiguration config, DateTime nowUtc)
        {
            if (config.TrialConsumed)
            {
                return ("TrialAlreadyConsumed",
                    "This tenant has already used its one self-service Pro trial. Contact support to extend.");
            }

            // Effective edition, conferred Pro included: a tenant managed by a Pro tenant needs no trial.
            if (FeatureEntitlementCatalog.ResolveEdition(config, nowUtc) == TenantEdition.Pro)
            {
                return ("AlreadyPro", "This tenant is already on the Pro plan.");
            }

            var missing = MissingContactProfileParts(config);
            if (missing.Count > 0)
            {
                return ("ContactProfileRequired",
                    $"Pro requires a tenant contact profile so we can reach and identify you for service or security matters. Missing: {string.Join(" and ", missing)}. Set it under Settings → Tenant → Contact, then start the trial.");
            }

            return null;
        }

        /// <summary>
        /// The parts of the contact profile the Pro entry point requires and this config lacks,
        /// in display order. Empty = complete.
        /// </summary>
        internal static IReadOnlyList<string> MissingContactProfileParts(TenantConfiguration config)
        {
            var missing = new List<string>(2);
            if (string.IsNullOrWhiteSpace(config.ContactEmail)) missing.Add("contact address");
            if (string.IsNullOrWhiteSpace(config.CompanyName)) missing.Add("company name");
            return missing;
        }

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

                if (EvaluateTrialStart(config, nowUtc) is { } deny)
                {
                    var conflict = req.CreateResponse(HttpStatusCode.Conflict);
                    await conflict.WriteAsJsonAsync(new { error = deny.Error, message = deny.Message });
                    return conflict;
                }

                config.TrialStartedUtc = nowUtc;
                config.TrialExpiresUtc = nowUtc.AddDays(SelfServiceTrialDays);
                config.TrialConsumed = true;
                config.TrialGrantedBy = caller;
                // Effectively Pro again — a leftover retention grace anchor is obsolete.
                config.ProDowngradedUtc = null;
                config.UpdatedBy = caller;

                // Fail-loud: throws when the write did not persist (cache invalidated in finally).
                await _configService.SaveConfigurationAsync(config, "plan", "self-service trial start");

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

                // The conversion moment. Fired after the write persisted (SaveConfigurationAsync
                // is fail-loud), so an alert never announces a trial that was not stored.
                await _opsEvents.RecordTenantTrialStartedAsync(
                    requestCtx.TargetTenantId,
                    config.DomainName,
                    config.ContactEmail,
                    config.CompanyName,
                    config.TrialStartedUtc,
                    config.TrialExpiresUtc,
                    caller,
                    selfService: true);

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new StartTenantTrialResponse
                {
                    TenantId = requestCtx.TargetTenantId,
                    TrialStartedUtc = config.TrialStartedUtc,
                    TrialExpiresUtc = config.TrialExpiresUtc,
                    EffectiveEdition = FeatureEntitlementCatalog.ProTierName
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
                await response.WriteAsJsonAsync(new PlanTierDefinitionsResponse { Tiers = tiers });
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
                await response.WriteAsJsonAsync(new PlanTierDefinitionsResponse { Tiers = body.Tiers });
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
