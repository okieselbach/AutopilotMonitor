using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Notifications;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Functions.Config
{
    /// <summary>
    /// PATCH global/config — the portal's only write to the platform configuration (D-285). The body
    /// names just the fields the operator changed; the repository re-reads the row, applies them and
    /// writes only the columns whose value actually changed. It replaced a full-model PUT that wrote
    /// every column from the page's copy, so a page loaded before an agent release reverted the
    /// release's hashes on save (2026-09-25).
    /// </summary>
    public class PatchAdminConfigurationFunction
    {
        private const int MaxBodyBytes = 1_048_576;

        private readonly ILogger<PatchAdminConfigurationFunction> _logger;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly IMaintenanceRepository _maintenanceRepo;

        public PatchAdminConfigurationFunction(
            ILogger<PatchAdminConfigurationFunction> logger,
            AdminConfigurationService adminConfigService,
            IMaintenanceRepository maintenanceRepo)
        {
            _logger = logger;
            _adminConfigService = adminConfigService;
            _maintenanceRepo = maintenanceRepo;
        }

        /// <summary>
        /// Fields no patch may write: the server stamps, the columns only the agent release pipeline
        /// writes (build-agent.yml / publish-scripts.yml), and the ones owned by their own flow
        /// (plan-tier endpoint, vulnerability-sync bookkeeping).
        /// </summary>
        internal static readonly HashSet<string> DeniedFields = new(StringComparer.OrdinalIgnoreCase)
        {
            "LastUpdated", "UpdatedBy",
            "LatestAgentV2Version", "LatestAgentV2Sha256", "LatestAgentV2ExeSha256", "LatestBootstrapV2ScriptVersion",
            "PlanTierDefinitionsJson",
            "VulnerabilityDataLastSyncUtc", "MsrcLastSyncUtc", "NvdCacheLastRefreshUtc", "EpssLastSyncUtc",
        };

        /// <summary>
        /// Every persisted, writable AdminConfiguration property except <see cref="DeniedFields"/>.
        /// Derived from the entity converter, so a new column is patchable the moment it is persisted
        /// and a model property that is never stored cannot be "saved" into nothing.
        /// </summary>
        internal static readonly IReadOnlyCollection<string> PatchableFields = BuildPatchableFields();

        private static HashSet<string> BuildPatchableFields()
        {
            var writable = new HashSet<string>(
                typeof(AdminConfiguration)
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
                    .Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);

            return new HashSet<string>(
                TableConfigRepository.ConvertToAdminTableEntity(AdminConfiguration.CreateDefault()).Keys
                    .Where(c => c != "PartitionKey" && c != "RowKey" && writable.Contains(c) && !DeniedFields.Contains(c)),
                StringComparer.OrdinalIgnoreCase);
        }

        [Function("PatchAdminConfiguration")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "patch", Route = "global/config")] HttpRequestData req)
        {
            try
            {
                // Authentication + GlobalAdminOnly authorization enforced by PolicyEnforcementMiddleware
                string userIdentifier = TenantHelper.GetUserIdentifier(req);

                // Newtonsoft on purpose: the field map is applied with JsonConvert.PopulateObject, like the
                // tenant field patch (TypedRequestGuardTests baseline).
                var read = await req.ReadNewtonsoftAsync<PatchAdminConfigurationRequest>(MaxBodyBytes);
                if (read.Error != null) return read.Error;
                var request = read.Value!;
                if (request.Fields == null || request.Fields.Count == 0)
                {
                    return await req.BadRequestAsync(
                        "Body must be { \"fields\": { <fieldName>: <value>, ... } } with at least one field.");
                }

                var fields = JObject.FromObject(request.Fields);
                var gateError = CheckFields(fields);
                if (gateError != null) return await req.BadRequestAsync(gateError);

                var result = await _adminConfigService.UpdateAsync(
                    draft => ApplyFields(draft, fields), userIdentifier, "portal");
                if (result.Error != null) return await req.BadRequestAsync(result.Error);

                _logger.LogInformation(
                    "PatchAdminConfiguration by {User}: {Count} field(s) sent, changed: {Columns}",
                    userIdentifier, fields.Count, string.Join(", ", result.ChangedColumns));

                if (result.ChangedColumns.Count > 0)
                {
                    await _maintenanceRepo.LogAuditEntryAsync(
                        Constants.AuditGlobalTenantId,
                        "UPDATE",
                        "AdminConfiguration",
                        "GlobalConfig",
                        userIdentifier,
                        ConfigDiffHelper.GetChanges(result.Before, result.After));
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new UpdateAdminConfigurationResponse
                {
                    Success = true,
                    Message = result.ChangedColumns.Count > 0
                        ? "Admin configuration updated successfully"
                        : "No changes — nothing was written",
                    Config = result.After,
                });
                return response;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "PatchAdminConfiguration");
            }
        }

        /// <summary>
        /// The field gate, before any storage work: every key must be a patchable field, named in the
        /// error when it is not so the caller can correct itself.
        /// </summary>
        internal static string? CheckFields(JObject fields)
        {
            foreach (var prop in fields.Properties())
            {
                if (DeniedFields.Contains(prop.Name))
                {
                    return $"Field \"{prop.Name}\" is not writable here (server stamp, owned by the agent release " +
                           "pipeline, or written by its own flow).";
                }
                if (!PatchableFields.Contains(prop.Name))
                    return $"Unknown field \"{prop.Name}\".";

                // The ops channel list carries per-channel redacted destinations that are restored below;
                // anywhere else the placeholder would overwrite a real secret with the sentinel.
                if (!prop.Name.Equals(nameof(AdminConfiguration.OpsNotificationChannelsJson), StringComparison.OrdinalIgnoreCase)
                    && prop.Value is JValue { Type: JTokenType.String, Value: string text }
                    && text.Contains(Constants.RedactedSecretPlaceholder, StringComparison.Ordinal))
                {
                    return $"Field \"{prop.Name}\" carries the \"{Constants.RedactedSecretPlaceholder}\" placeholder — " +
                           "a redacted read must never be written back.";
                }
            }
            return null;
        }

        /// <summary>
        /// Applies the gated fields onto the repository's fresh draft and validates the result. JSON
        /// null clears a nullable field; camelCase keys bind case-insensitively to the model.
        /// </summary>
        internal static string? ApplyFields(AdminConfiguration draft, JObject fields)
        {
            var storedChannels = draft.OpsNotificationChannelsJson;
            try
            {
                JsonConvert.PopulateObject(fields.ToString(), draft, new JsonSerializerSettings
                {
                    MissingMemberHandling = MissingMemberHandling.Error,
                });
            }
            catch (JsonException ex)
            {
                // Never ex.Message: Newtonsoft quotes the offending VALUE, which may be a secret.
                var path = (ex as JsonSerializationException)?.Path ?? (ex as JsonReaderException)?.Path;
                return string.IsNullOrEmpty(path)
                    ? "A value has the wrong JSON type for its field."
                    : $"The value for \"{path}\" has the wrong JSON type.";
            }

            // Rate limits must be positive: a zero/negative value would throttle every request
            // (RateLimitService clamps as a last resort, but reject at the edge for a clear error).
            var rateLimitError =
                draft.GlobalRateLimitRequestsPerMinute < 1 ? "Global Device API Rate Limit" :
                draft.UserRateLimitRequestsPerMinute < 1 ? "MCP & Integrations API Rate Limit" :
                draft.PortalUserRateLimitRequestsPerMinute < 1 ? "Portal User API Rate Limit" :
                draft.GlobalAdminRateLimitRequestsPerMinute < 1 ? "Global Admin API Rate Limit" :
                null;
            if (rateLimitError != null)
                return $"{rateLimitError} must be at least 1 request per minute.";

            if (fields.Properties().Any(p => p.Name.Equals(
                    nameof(AdminConfiguration.OpsNotificationChannelsJson), StringComparison.OrdinalIgnoreCase)))
            {
                // A GlobalReader is served destinations as the ***REDACTED*** sentinel; persisting it as a
                // chat ID or webhook URL would silently break every alert, so restore per channel id.
                draft.OpsNotificationChannelsJson = NotificationChannel.RestoreRedactedList(
                    draft.OpsNotificationChannelsJson, storedChannels);

                // Same structural gate the tenant channel list passes. No GA gate on Telegram here:
                // this endpoint is GlobalAdminOnly by policy.
                var channelsError = TenantConfigValidation.ValidateNotificationChannels(draft.OpsNotificationChannelsJson);
                if (channelsError != null)
                    return $"Invalid ops notification channels: {channelsError}";
            }

            return null;
        }
    }
}
