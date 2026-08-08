using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of IConfigRepository.
    /// Handles entity mapping and storage operations for tenant configuration,
    /// admin configuration, preview whitelist, and preview config tables.
    /// </summary>
    public class TableConfigRepository : IConfigRepository
    {
        private readonly TableClient _tenantConfigTableClient;
        private readonly TableClient _adminConfigTableClient;
        private readonly TableClient _previewWhitelistTableClient;
        private readonly TableClient _previewConfigTableClient;
        private readonly IConfigBackupRepository _backupRepo;
        private readonly ILogger<TableConfigRepository> _logger;

        /// <summary>
        /// Changes to ONLY these properties do not snapshot the tenant-config row: the
        /// LastAuthClientId pair flips as an auth-flow side effect and would flood the
        /// two backup slots with states nobody ever wants to revert to.
        /// </summary>
        private static readonly HashSet<string> TenantBackupNoiseProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "LastUpdated", "UpdatedBy", "LastAuthClientId", "LastAuthClientIdSince",
        };

        private static readonly HashSet<string> AdminBackupNoiseProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "LastUpdated", "UpdatedBy",
        };

        public TableConfigRepository(
            TableStorageService storage,
            IConfigBackupRepository backupRepo,
            ILogger<TableConfigRepository> logger)
        {
            _logger = logger;
            _backupRepo = backupRepo;
            _tenantConfigTableClient = storage.GetTableClient(Constants.TableNames.TenantConfiguration);
            _adminConfigTableClient = storage.GetTableClient(Constants.TableNames.AdminConfiguration);
            _previewWhitelistTableClient = storage.GetTableClient(Constants.TableNames.PreviewWhitelist);
            _previewConfigTableClient = storage.GetTableClient(Constants.TableNames.PreviewConfig);
        }

        // --- Tenant Configuration ---

        public async Task<TenantConfiguration?> GetTenantConfigurationAsync(string tenantId)
        {
            try
            {
                var entity = await _tenantConfigTableClient.GetEntityAsync<TableEntity>(tenantId, "config");
                return ConvertFromTenantTableEntity(entity.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading tenant configuration for {TenantId}", tenantId);
                throw;
            }
        }

        public Task<bool> SaveTenantConfigurationAsync(TenantConfiguration config)
            => SaveTenantConfigurationAsync(config, backupSource: null, backupReason: null);

        public async Task<bool> SaveTenantConfigurationAsync(
            TenantConfiguration config, string? backupSource, string? backupReason)
        {
            await TrySnapshotBeforeSaveAsync(
                _tenantConfigTableClient, config.TenantId, "config",
                ConvertFromTenantTableEntity, config, TenantBackupNoiseProperties,
                config.UpdatedBy, backupSource, backupReason);

            try
            {
                var entity = ConvertToTenantTableEntity(config);
                await _tenantConfigTableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving tenant configuration for {TenantId}", config.TenantId);
                return false;
            }
        }

        public async Task<(TenantConfiguration Config, string ETag)?> GetTenantConfigurationWithEtagAsync(string tenantId)
        {
            try
            {
                var entity = await _tenantConfigTableClient.GetEntityAsync<TableEntity>(tenantId, "config");
                return (ConvertFromTenantTableEntity(entity.Value), entity.Value.ETag.ToString());
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            // Everything else throws (fail-loud): the transactional caller must never treat a
            // storage outage as "no row".
        }

        public async Task<bool> TryReplaceTenantConfigurationAsync(TenantConfiguration config, string etag)
        {
            var entity = ConvertToTenantTableEntity(config);
            try
            {
                await _tenantConfigTableClient.UpdateEntityAsync(entity, new ETag(etag), TableUpdateMode.Replace);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status is 412 or 404)
            {
                // 412: lost the CAS race — caller re-reads and retries (bounded).
                // 404: the row was deleted since the read (offboarding) — the retry's
                //      re-read surfaces that as "no configuration row".
                return false;
            }
        }

        /// <summary>
        /// Pre-write snapshot hook shared by the tenant- and admin-config save paths.
        /// Fail-SOFT by design: this is a safety net around long-standing writers
        /// (portal, plan, auth side effects) — a backup-storage hiccup must never turn
        /// into a config-save outage. The transactional patch/revert flow does its own
        /// fail-CLOSED snapshot before calling the conditional write and never relies
        /// on this hook.
        /// </summary>
        private async Task TrySnapshotBeforeSaveAsync<TModel>(
            TableClient tableClient,
            string partitionKey,
            string rowKey,
            Func<TableEntity, TModel> convertFromEntity,
            TModel incoming,
            HashSet<string> noiseProperties,
            string? changedBy,
            string? source,
            string? reason) where TModel : class
        {
            try
            {
                TableEntity existing;
                try
                {
                    existing = (await tableClient.GetEntityAsync<TableEntity>(partitionKey, rowKey)).Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return; // Row creation — nothing to snapshot.
                }

                // Compare at MODEL level, not raw-entity level: retrieved entities carry
                // DateTimeOffset/typing quirks that would false-positive against a freshly
                // built entity. The model round-trip puts both sides on identical CLR types.
                var before = convertFromEntity(existing);
                var changed = ConfigPropertyComparer.GetChangedPropertyNames(before, incoming);
                changed.ExceptWith(noiseProperties);
                if (changed.Count == 0)
                    return; // Noise-only or no-op save — don't burn a backup slot.

                await _backupRepo.UpsertAsync(BuildBackupEntry(
                    existing, partitionKey, changedBy,
                    writeSource: source ?? "unknown", reason: reason,
                    diffJson: JsonSerializer.Serialize(ConfigDiffHelper.GetChanges(before, incoming))));

                await _backupRepo.PruneAsync(partitionKey, Constants.ConfigBackupKeepCount);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Config backup snapshot failed for {PartitionKey} — proceeding with save (fail-soft)",
                    partitionKey);
            }
        }

        /// <summary>
        /// Snapshots the raw stored row (same fidelity approach as the offboarding
        /// customs archive): every table property except the Azure pseudo-properties,
        /// serialized as JSON. Restore therefore survives model refactors.
        /// </summary>
        internal static ConfigBackupEntry BuildBackupEntry(
            TableEntity sourceRow, string partitionKey, string? changedBy,
            string writeSource, string? reason, string? diffJson)
        {
            var snapshot = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var kv in sourceRow)
            {
                if (kv.Key is "odata.etag" or "Timestamp") continue;
                snapshot[kv.Key] = kv.Value;
            }

            return new ConfigBackupEntry
            {
                PartitionKey = partitionKey,
                RowKey = TableConfigBackupRepository.BuildRowKey(DateTime.UtcNow),
                TenantId = partitionKey,
                EntityJson = JsonSerializer.Serialize(snapshot),
                ChangedBy = string.IsNullOrWhiteSpace(changedBy) ? "system" : changedBy,
                Source = writeSource,
                Reason = reason,
                DiffJson = diffJson,
                BackupTakenAt = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// Conditional, single-property seed of ContactEmail (see <see cref="IConfigRepository"/>).
        /// Merge mode leaves every other stored property untouched, and the ETag precondition turns
        /// a concurrent write into a 412 rather than a silent overwrite. Both matter: the caller is
        /// a background/side-effect path that must never beat a tenant admin's own edit.
        /// </summary>
        public async Task<bool> TrySeedTenantContactEmailAsync(string tenantId, string email)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(email))
                return false;

            try
            {
                var existing = await _tenantConfigTableClient.GetEntityAsync<TableEntity>(tenantId, "config");

                // The tenant already owns an address — the seed is one-way and never re-syncs.
                if (!string.IsNullOrWhiteSpace(existing.Value.GetString("ContactEmail")))
                    return false;

                var patch = new TableEntity(tenantId, "config")
                {
                    ["ContactEmail"] = email.Trim()
                };

                await _tenantConfigTableClient.UpdateEntityAsync(
                    patch, existing.Value.ETag, TableUpdateMode.Merge);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // No configuration row yet — nothing to seed onto.
                return false;
            }
            catch (RequestFailedException ex) when (ex.Status == 412)
            {
                // Someone wrote the row between the read and the write. They win by construction:
                // a lost seed is recoverable (the backfill retries), a lost admin edit is not.
                _logger.LogInformation(
                    "Contact address seed for {TenantId} skipped — the configuration changed concurrently", tenantId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error seeding contact address for tenant {TenantId}", tenantId);
                return false;
            }
        }

        public async Task<List<TenantConfiguration>> GetAllTenantConfigurationsAsync()
        {
            try
            {
                var configurations = new List<TenantConfiguration>();

                await foreach (var entity in _tenantConfigTableClient.QueryAsync<TableEntity>(filter: "RowKey eq 'config'"))
                {
                    var config = ConvertFromTenantTableEntity(entity);
                    configurations.Add(config);
                }

                return configurations.OrderBy(c => c.TenantId).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading all tenant configurations");
                throw;
            }
        }

        public async Task<RawPage<TenantConfiguration>> GetTenantConfigurationsPageAsync(int pageSize, string? continuation)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

            // Cross-partition scan over the single 'config' row per tenant. Azure returns
            // (PartitionKey asc, RowKey asc); PartitionKey == TenantId, so pages are already
            // TenantId-ordered — a stable cursor without an in-memory re-sort (which would
            // break pagination by only ordering the current page).
            var (entities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                client: _tenantConfigTableClient,
                filter: "RowKey eq 'config'",
                pageSize: pageSize,
                continuation: continuation);

            var configurations = new List<TenantConfiguration>(entities.Count);
            foreach (var entity in entities) configurations.Add(ConvertFromTenantTableEntity(entity));
            return new RawPage<TenantConfiguration>(configurations, nextRawToken);
        }

        // --- Admin Configuration ---

        public async Task<AdminConfiguration?> GetAdminConfigurationAsync()
        {
            try
            {
                var entity = await _adminConfigTableClient.GetEntityAsync<TableEntity>("GlobalConfig", "config");
                return ConvertFromAdminTableEntity(entity.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading admin configuration");
                throw;
            }
        }

        public async Task<bool> SaveAdminConfigurationAsync(AdminConfiguration config)
        {
            await TrySnapshotBeforeSaveAsync(
                _adminConfigTableClient, "GlobalConfig", "config",
                ConvertFromAdminTableEntity, config, AdminBackupNoiseProperties,
                config.UpdatedBy, "admin-config", reason: null);

            try
            {
                var entity = ConvertToAdminTableEntity(config);
                await _adminConfigTableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving admin configuration");
                return false;
            }
        }

        // --- Preview Whitelist ---

        public async Task<bool> IsInPreviewWhitelistAsync(string tenantId)
        {
            try
            {
                var entity = await _previewWhitelistTableClient.GetEntityAsync<PreviewWhitelistEntity>(tenantId, "approved");
                return entity?.Value != null;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking preview whitelist for tenant {TenantId}", tenantId);
                throw;
            }
        }

        public async Task<bool> AddToPreviewWhitelistAsync(string tenantId, string addedBy)
        {
            try
            {
                var entity = new PreviewWhitelistEntity
                {
                    PartitionKey = tenantId,
                    RowKey = "approved",
                    ApprovedAt = DateTime.UtcNow,
                    ApprovedBy = addedBy
                };

                // Conditional INSERT, not upsert: the storage layer arbitrates concurrent
                // activations (double signup → two auto-approve envelopes), so exactly one
                // caller sees "newly added" and runs the side effects (welcome mail, ops event).
                await _previewWhitelistTableClient.AddEntityAsync(entity);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                return false;
            }
            catch (Exception ex)
            {
                // Throw, don't swallow: activation is the one step that must fail loud —
                // a false-y return here used to let callers send the welcome mail for a
                // tenant that was never actually activated.
                _logger.LogError(ex, "Error adding tenant {TenantId} to preview whitelist", tenantId);
                throw;
            }
        }

        public async Task<bool> RemoveFromPreviewWhitelistAsync(string tenantId)
        {
            try
            {
                await _previewWhitelistTableClient.DeleteEntityAsync(tenantId, "approved");
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing tenant {TenantId} from preview whitelist", tenantId);
                return false;
            }
        }

        public async Task<List<string>> GetPreviewWhitelistAsync()
        {
            try
            {
                var results = new List<string>();

                await foreach (var entity in _previewWhitelistTableClient.QueryAsync<PreviewWhitelistEntity>(
                    filter: "RowKey eq 'approved'"))
                {
                    results.Add(entity.PartitionKey);
                }

                return results.OrderBy(t => t).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading preview whitelist");
                throw;
            }
        }

        // --- Preview Config ---

        public async Task<Dictionary<string, string>> GetPreviewConfigAsync()
        {
            try
            {
                var config = new Dictionary<string, string>();
                var entity = await _previewConfigTableClient.GetEntityAsync<TableEntity>("TelegramBot", "config");

                foreach (var kvp in entity.Value)
                {
                    if (kvp.Key == "odata.etag" || kvp.Key == "PartitionKey" || kvp.Key == "RowKey" || kvp.Key == "Timestamp")
                        continue;
                    config[kvp.Key] = kvp.Value?.ToString() ?? string.Empty;
                }

                return config;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return new Dictionary<string, string>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading preview config");
                throw;
            }
        }

        public async Task<bool> SavePreviewConfigAsync(string key, string value)
        {
            try
            {
                // Get existing entity or create new one
                TableEntity entity;
                try
                {
                    var existing = await _previewConfigTableClient.GetEntityAsync<TableEntity>("TelegramBot", "config");
                    entity = existing.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    entity = new TableEntity("TelegramBot", "config");
                }

                entity[key] = value;
                await _previewConfigTableClient.UpsertEntityAsync(entity);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving preview config key {Key}", key);
                return false;
            }
        }

        // --- Preview Notification Email ---

        public async Task<string?> GetNotificationEmailAsync(string tenantId)
        {
            try
            {
                var entity = await _previewWhitelistTableClient.GetEntityAsync<PreviewNotificationEntity>(tenantId, "notification-email");
                return entity?.Value?.Email;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read notification email for tenant {TenantId}", tenantId);
                return null;
            }
        }

        public async Task SaveNotificationEmailAsync(string tenantId, string? email)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                // Clear: delete the row if it exists
                try { await _previewWhitelistTableClient.DeleteEntityAsync(tenantId, "notification-email"); }
                catch (RequestFailedException ex) when (ex.Status == 404) { /* already gone */ }
                return;
            }

            var entity = new PreviewNotificationEntity
            {
                PartitionKey = tenantId,
                RowKey = "notification-email",
                Email = email.Trim()
            };
            await _previewWhitelistTableClient.UpsertEntityAsync(entity);
        }

        // --- Welcome Email Sent Marker ---

        public async Task<bool> TryMarkWelcomeEmailSentAsync(string tenantId)
        {
            var entity = new TableEntity(tenantId, "welcome-email-sent")
            {
                { "SentAt", DateTime.UtcNow }
            };

            try
            {
                // Conditional INSERT: arbitrates the send race between the approval path and
                // the notification-email save path — exactly one caller wins and sends.
                await _previewWhitelistTableClient.AddEntityAsync(entity);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error marking welcome email sent for tenant {TenantId}", tenantId);
                throw;
            }
        }

        public async Task ClearWelcomeEmailSentMarkerAsync(string tenantId)
        {
            try
            {
                await _previewWhitelistTableClient.DeleteEntityAsync(tenantId, "welcome-email-sent");
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // already gone
            }
            catch (Exception ex)
            {
                // Fail-soft like the revoke path itself: a stale marker only suppresses a
                // courtesy mail on a later re-approve.
                _logger.LogWarning(ex, "Error clearing welcome email marker for tenant {TenantId}", tenantId);
            }
        }

        // --- Tenant Configuration Entity Mapping ---

        // Internal static (not private): the Store↔Map pair is a serialization CONTRACT — every
        // model field must appear in BOTH methods (project rule "table-serialization"). The
        // roundtrip tests (TenantConfigTableSerializationTests) pin that contract, incl. legacy
        // rows lacking the newer columns.
        internal static TableEntity ConvertToTenantTableEntity(TenantConfiguration config)
        {
            var entity = new TableEntity(config.TenantId, "config")
            {
                { "DomainName", config.DomainName },
                { "LastUpdated", config.LastUpdated },
                { "UpdatedBy", config.UpdatedBy },
                { "OnboardedBy", config.OnboardedBy },
                { "ContactEmail", config.ContactEmail },
                { "Disabled", config.Disabled },
                { "DisabledReason", config.DisabledReason },
                { "DisabledUntil", config.DisabledUntil },
                { "CustomRateLimitRequestsPerMinute", config.CustomRateLimitRequestsPerMinute },
                { "CustomUserRateLimitRequestsPerMinute", config.CustomUserRateLimitRequestsPerMinute },
                { "ManufacturerWhitelist", config.ManufacturerWhitelist },
                { "ModelWhitelist", config.ModelWhitelist },
                { "ValidateAutopilotDevice", config.ValidateAutopilotDevice },
                { "ValidateCorporateIdentifier", config.ValidateCorporateIdentifier },
                { "ValidateDeviceAssociation", config.ValidateDeviceAssociation },
                { "ValidateCloudPcDevice", config.ValidateCloudPcDevice },
                { "AllowInsecureAgentRequests", config.AllowInsecureAgentRequests },
                { "DataRetentionDays", config.DataRetentionDays },
                { "SessionTimeoutHours", config.SessionTimeoutHours },
                { "SessionGraceHours", config.SessionGraceHours },
                { "AbsoluteMaxSessionHours", config.AbsoluteMaxSessionHours },
                { "MaxNdjsonPayloadSizeMB", config.MaxNdjsonPayloadSizeMB },
                { "EnablePerformanceCollector", config.EnablePerformanceCollector },
                { "PerformanceCollectorIntervalSeconds", config.PerformanceCollectorIntervalSeconds },
                { "MaxAuthFailures", config.MaxAuthFailures },
                { "AuthFailureTimeoutMinutes", config.AuthFailureTimeoutMinutes },
                { "SelfDestructOnComplete", config.SelfDestructOnComplete },
                { "KeepLogFile", config.KeepLogFile },
                { "RebootOnComplete", config.RebootOnComplete },
                { "RebootDelaySeconds", config.RebootDelaySeconds },
                { "EnableGeoLocation", config.EnableGeoLocation },
                { "EnableTimezoneAutoSet", config.EnableTimezoneAutoSet },
                { "NtpServer", config.NtpServer },
                { "EnableImeMatchLog", config.EnableImeMatchLog },
                { "EnableGatherRuleDebugLog", config.EnableGatherRuleDebugLog },
                { "EnableEspContinueAnywayObservation", config.EnableEspContinueAnywayObservation },
                { "LogLevel", config.LogLevel },
                { "MaxBatchSize", config.MaxBatchSize },
                { "DiagnosticsBlobSasUrl", config.DiagnosticsBlobSasUrl },
                { "DiagnosticsUploadMode", config.DiagnosticsUploadMode },
                { "DiagnosticsUploadDestination", config.DiagnosticsUploadDestination },
                { "DiagnosticsLogPathsJson", config.DiagnosticsLogPathsJson },
                { "TeamsWebhookUrl", config.TeamsWebhookUrl },
                { "TeamsNotifyOnSuccess", config.TeamsNotifyOnSuccess },
                { "TeamsNotifyOnFailure", config.TeamsNotifyOnFailure },
                { "TeamsNotifyOnStart", config.TeamsNotifyOnStart },
                { "WebhookProviderType", config.WebhookProviderType },
                { "WebhookUrl", config.WebhookUrl },
                { "WebhookNotifyOnSuccess", config.WebhookNotifyOnSuccess },
                { "WebhookNotifyOnFailure", config.WebhookNotifyOnFailure },
                { "WebhookNotifyOnHardwareRejection", config.WebhookNotifyOnHardwareRejection },
                { "WebhookNotifyOnStart", config.WebhookNotifyOnStart },
                { "WebhookCustomHeadersJson", config.WebhookCustomHeadersJson },
                { "NotificationChannelsJson", config.NotificationChannelsJson },
                { "ShowScriptOutput", config.ShowScriptOutput },
                { "ShowEnrollmentSummary", config.ShowEnrollmentSummary },
                { "EnrollmentSummaryTimeoutSeconds", config.EnrollmentSummaryTimeoutSeconds },
                { "EnrollmentSummaryBrandingImageUrl", config.EnrollmentSummaryBrandingImageUrl },
                { "EnrollmentSummaryLaunchRetrySeconds", config.EnrollmentSummaryLaunchRetrySeconds },
                { "HelloWaitTimeoutSeconds", config.HelloWaitTimeoutSeconds },
                { "AgentMaxLifetimeMinutes", config.AgentMaxLifetimeMinutes },
                { "SendTraceEvents", config.SendTraceEvents },
                { "EnableLocalAdminAnalyzer", config.EnableLocalAdminAnalyzer },
                { "EnableSoftwareInventoryAnalyzer", config.EnableSoftwareInventoryAnalyzer },
                { "EnableIntegrityBypassAnalyzer", config.EnableIntegrityBypassAnalyzer },
                { "EnableRealmJoinWatcher", config.EnableRealmJoinWatcher },
                { "KeepAwakeDuringUserEsp", config.KeepAwakeDuringUserEsp },
                { "EnableConsoleBypassDetection", config.EnableConsoleBypassDetection },
                { "LocalAdminAllowedAccountsJson", config.LocalAdminAllowedAccountsJson },
                { "BootstrapTokenEnabled", config.BootstrapTokenEnabled },
                { "UnrestrictedModeEnabled", config.UnrestrictedModeEnabled },
                { "UnrestrictedMode", config.UnrestrictedMode },
                { "EntraAppRolesEnabled", config.EntraAppRolesEnabled },
                { "OnboardedAt", config.OnboardedAt },
                { "HomedAppClientId", config.HomedAppClientId },
                { "LastAuthClientId", config.LastAuthClientId },
                { "LastAuthClientIdSince", config.LastAuthClientIdSince },
                { "PlanTier", config.PlanTier },
                { "TrialExpiresUtc", config.TrialExpiresUtc },
                { "TrialStartedUtc", config.TrialStartedUtc },
                { "TrialConsumed", config.TrialConsumed },
                { "TrialGrantedBy", config.TrialGrantedBy },
                // SLA targets
                { "SlaTargetSuccessRate", config.SlaTargetSuccessRate.HasValue ? (double)config.SlaTargetSuccessRate.Value : (double?)null },
                { "SlaTargetMaxDurationMinutes", config.SlaTargetMaxDurationMinutes },
                { "SlaTargetAppInstallSuccessRate", config.SlaTargetAppInstallSuccessRate.HasValue ? (double)config.SlaTargetAppInstallSuccessRate.Value : (double?)null },
                // SLA notification subscriptions
                { "SlaNotifyOnSuccessRateBreach", config.SlaNotifyOnSuccessRateBreach },
                { "SlaSuccessRateNotifyThreshold", config.SlaSuccessRateNotifyThreshold.HasValue ? (double)config.SlaSuccessRateNotifyThreshold.Value : (double?)null },
                { "SlaNotifyOnDurationBreach", config.SlaNotifyOnDurationBreach },
                { "SlaNotifyOnAppInstallBreach", config.SlaNotifyOnAppInstallBreach },
                { "SlaNotifyOnConsecutiveFailures", config.SlaNotifyOnConsecutiveFailures },
                { "SlaConsecutiveFailureThreshold", config.SlaConsecutiveFailureThreshold }
            };

            return entity;
        }

        internal static TenantConfiguration ConvertFromTenantTableEntity(TableEntity entity)
        {
            return new TenantConfiguration
            {
                TenantId = entity.PartitionKey,
                DomainName = entity.GetString("DomainName") ?? "",
                LastUpdated = entity.GetDateTime("LastUpdated") ?? DateTime.UtcNow,
                UpdatedBy = entity.GetString("UpdatedBy") ?? "Unknown",
                OnboardedBy = entity.GetString("OnboardedBy"),
                ContactEmail = entity.GetString("ContactEmail"),
                Disabled = entity.GetBoolean("Disabled") ?? false,
                DisabledReason = entity.GetString("DisabledReason"),
                DisabledUntil = entity.GetDateTime("DisabledUntil"),
                CustomRateLimitRequestsPerMinute = entity.GetInt32("CustomRateLimitRequestsPerMinute"),
                CustomUserRateLimitRequestsPerMinute = entity.GetInt32("CustomUserRateLimitRequestsPerMinute"),
                ManufacturerWhitelist = entity.GetString("ManufacturerWhitelist") ?? "Dell*,HP*,Lenovo*,Microsoft Corporation",
                ModelWhitelist = entity.GetString("ModelWhitelist") ?? "*",
                ValidateAutopilotDevice = entity.GetBoolean("ValidateAutopilotDevice") ?? entity.GetBoolean("ValidateSerialNumber") ?? false,
                ValidateCorporateIdentifier = entity.GetBoolean("ValidateCorporateIdentifier") ?? false,
                ValidateDeviceAssociation = entity.GetBoolean("ValidateDeviceAssociation") ?? false,
                ValidateCloudPcDevice = entity.GetBoolean("ValidateCloudPcDevice") ?? false,
                AllowInsecureAgentRequests = entity.GetBoolean("AllowInsecureAgentRequests") ?? false,
                DataRetentionDays = entity.GetInt32("DataRetentionDays") ?? 90,
                SessionTimeoutHours = entity.GetInt32("SessionTimeoutHours") ?? 5,
                // 0 (default) = auto-derive grace from the agent's absolute cap; legacy rows lacking
                // the column read back as 0, preserving the auto-derive behaviour.
                SessionGraceHours = entity.GetInt32("SessionGraceHours") ?? 0,
                // null = agent default (48); nullable so an unset override never masquerades as an
                // explicit value in EnrollmentTimeoutClassifier.ResolveGraceHours.
                AbsoluteMaxSessionHours = entity.GetInt32("AbsoluteMaxSessionHours"),
                MaxNdjsonPayloadSizeMB = entity.GetInt32("MaxNdjsonPayloadSizeMB") ?? 5,
                EnablePerformanceCollector = entity.GetBoolean("EnablePerformanceCollector") ?? false,
                PerformanceCollectorIntervalSeconds = entity.GetInt32("PerformanceCollectorIntervalSeconds") ?? 30,
                MaxAuthFailures = entity.GetInt32("MaxAuthFailures"),
                AuthFailureTimeoutMinutes = entity.GetInt32("AuthFailureTimeoutMinutes"),
                SelfDestructOnComplete = entity.GetBoolean("SelfDestructOnComplete"),
                KeepLogFile = entity.GetBoolean("KeepLogFile"),
                RebootOnComplete = entity.GetBoolean("RebootOnComplete"),
                RebootDelaySeconds = entity.GetInt32("RebootDelaySeconds"),
                EnableGeoLocation = entity.GetBoolean("EnableGeoLocation"),
                EnableTimezoneAutoSet = entity.GetBoolean("EnableTimezoneAutoSet"),
                NtpServer = string.IsNullOrWhiteSpace(entity.GetString("NtpServer")) ? "time.windows.com" : entity.GetString("NtpServer"),
                EnableImeMatchLog = entity.GetBoolean("EnableImeMatchLog"),
                EnableGatherRuleDebugLog = entity.GetBoolean("EnableGatherRuleDebugLog"),
                EnableEspContinueAnywayObservation = entity.GetBoolean("EnableEspContinueAnywayObservation"),
                LogLevel = entity.GetString("LogLevel"),
                MaxBatchSize = entity.GetInt32("MaxBatchSize"),
                DiagnosticsBlobSasUrl = entity.GetString("DiagnosticsBlobSasUrl"),
                DiagnosticsUploadMode = entity.GetString("DiagnosticsUploadMode") ?? "Off",
                // Default for legacy rows without the field: "CustomerSas" — preserves existing
                // behaviour and ensures hosted mode is never silently enabled.
                DiagnosticsUploadDestination = entity.GetString("DiagnosticsUploadDestination") ?? "CustomerSas",
                DiagnosticsLogPathsJson = entity.GetString("DiagnosticsLogPathsJson"),
                TeamsWebhookUrl = entity.GetString("TeamsWebhookUrl"),
                TeamsNotifyOnSuccess = entity.GetBoolean("TeamsNotifyOnSuccess") ?? true,
                TeamsNotifyOnFailure = entity.GetBoolean("TeamsNotifyOnFailure") ?? true,
                TeamsNotifyOnStart = entity.GetBoolean("TeamsNotifyOnStart") ?? false,
                WebhookProviderType = entity.GetInt32("WebhookProviderType") ?? 0,
                WebhookUrl = entity.GetString("WebhookUrl"),
                WebhookNotifyOnSuccess = entity.GetBoolean("WebhookNotifyOnSuccess") ?? true,
                WebhookNotifyOnFailure = entity.GetBoolean("WebhookNotifyOnFailure") ?? true,
                WebhookNotifyOnHardwareRejection = entity.GetBoolean("WebhookNotifyOnHardwareRejection") ?? false,
                WebhookNotifyOnStart = entity.GetBoolean("WebhookNotifyOnStart") ?? false,
                WebhookCustomHeadersJson = entity.GetString("WebhookCustomHeadersJson"),
                // null for legacy rows → GetNotificationChannels() synthesizes from the single-webhook fields
                NotificationChannelsJson = entity.GetString("NotificationChannelsJson"),
                ShowScriptOutput = entity.GetBoolean("ShowScriptOutput") ?? true,
                ShowEnrollmentSummary = entity.GetBoolean("ShowEnrollmentSummary"),
                EnrollmentSummaryTimeoutSeconds = entity.GetInt32("EnrollmentSummaryTimeoutSeconds"),
                EnrollmentSummaryBrandingImageUrl = entity.GetString("EnrollmentSummaryBrandingImageUrl"),
                EnrollmentSummaryLaunchRetrySeconds = entity.GetInt32("EnrollmentSummaryLaunchRetrySeconds"),
                HelloWaitTimeoutSeconds = entity.GetInt32("HelloWaitTimeoutSeconds") ?? 30,
                AgentMaxLifetimeMinutes = entity.GetInt32("AgentMaxLifetimeMinutes"),
                SendTraceEvents = entity.GetBoolean("SendTraceEvents") ?? true,
                EnableLocalAdminAnalyzer = entity.GetBoolean("EnableLocalAdminAnalyzer"),
                EnableSoftwareInventoryAnalyzer = entity.GetBoolean("EnableSoftwareInventoryAnalyzer"),
                EnableIntegrityBypassAnalyzer = entity.GetBoolean("EnableIntegrityBypassAnalyzer"),
                EnableRealmJoinWatcher = entity.GetBoolean("EnableRealmJoinWatcher"),
                KeepAwakeDuringUserEsp = entity.GetBoolean("KeepAwakeDuringUserEsp"),
                EnableConsoleBypassDetection = entity.GetBoolean("EnableConsoleBypassDetection"),
                LocalAdminAllowedAccountsJson = entity.GetString("LocalAdminAllowedAccountsJson"),
                BootstrapTokenEnabled = entity.GetBoolean("BootstrapTokenEnabled") ?? false,
                UnrestrictedModeEnabled = entity.GetBoolean("UnrestrictedModeEnabled") ?? false,
                UnrestrictedMode = entity.GetBoolean("UnrestrictedMode") ?? false,
                EntraAppRolesEnabled = entity.GetBoolean("EntraAppRolesEnabled") ?? false,
                OnboardedAt = entity.GetDateTime("OnboardedAt"),
                // Null = legacy app registration (rows pre-dating the C4A8 app-reg move).
                HomedAppClientId = entity.GetString("HomedAppClientId"),
                LastAuthClientId = entity.GetString("LastAuthClientId"),
                LastAuthClientIdSince = entity.GetDateTime("LastAuthClientIdSince"),
                PlanTier = entity.GetString("PlanTier") ?? "free",
                TrialExpiresUtc = entity.GetDateTime("TrialExpiresUtc"),
                TrialStartedUtc = entity.GetDateTime("TrialStartedUtc"),
                TrialConsumed = entity.GetBoolean("TrialConsumed") ?? false,
                TrialGrantedBy = entity.GetString("TrialGrantedBy"),
                // SLA targets
                SlaTargetSuccessRate = entity.GetDouble("SlaTargetSuccessRate") != null ? (decimal)entity.GetDouble("SlaTargetSuccessRate")! : null,
                SlaTargetMaxDurationMinutes = entity.GetInt32("SlaTargetMaxDurationMinutes"),
                SlaTargetAppInstallSuccessRate = entity.GetDouble("SlaTargetAppInstallSuccessRate") != null ? (decimal)entity.GetDouble("SlaTargetAppInstallSuccessRate")! : null,
                // SLA notification subscriptions
                SlaNotifyOnSuccessRateBreach = entity.GetBoolean("SlaNotifyOnSuccessRateBreach") ?? false,
                SlaSuccessRateNotifyThreshold = entity.GetDouble("SlaSuccessRateNotifyThreshold") != null ? (decimal)entity.GetDouble("SlaSuccessRateNotifyThreshold")! : null,
                SlaNotifyOnDurationBreach = entity.GetBoolean("SlaNotifyOnDurationBreach") ?? false,
                SlaNotifyOnAppInstallBreach = entity.GetBoolean("SlaNotifyOnAppInstallBreach") ?? false,
                SlaNotifyOnConsecutiveFailures = entity.GetBoolean("SlaNotifyOnConsecutiveFailures") ?? false,
                SlaConsecutiveFailureThreshold = entity.GetInt32("SlaConsecutiveFailureThreshold") ?? 5
            };
        }

        // --- Admin Configuration Entity Mapping ---

        internal static TableEntity ConvertToAdminTableEntity(AdminConfiguration config)
        {
            var entity = new TableEntity("GlobalConfig", "config")
            {
                { "LastUpdated", config.LastUpdated },
                { "UpdatedBy", config.UpdatedBy },
                { "GlobalRateLimitRequestsPerMinute", config.GlobalRateLimitRequestsPerMinute },
                { "PlatformStatsBlobSasUrl", config.PlatformStatsBlobSasUrl ?? string.Empty },
                { "CollectorIdleTimeoutMinutes", config.CollectorIdleTimeoutMinutes },
                { "DesktopDetectorNoCandidateTimeoutMinutes", config.DesktopDetectorNoCandidateTimeoutMinutes },
                { "ExcessiveEventCountThreshold", config.ExcessiveEventCountThreshold },
                { "ExcessiveEventAutoActionMode", config.ExcessiveEventAutoActionMode ?? "Off" },
                { "ExcessiveEventAutoActionThreshold", config.ExcessiveEventAutoActionThreshold },
                { "ExcessiveEventAutoActionDurationHours", config.ExcessiveEventAutoActionDurationHours },
                { "DiagnosticsGlobalLogPathsJson", config.DiagnosticsGlobalLogPathsJson },
                { "ModernDeploymentHarmlessEventIdsJson", config.ModernDeploymentHarmlessEventIdsJson ?? string.Empty },
                { "WhiteGloveSealingPatternIdsJson", config.WhiteGloveSealingPatternIdsJson ?? string.Empty },
                { "NvdApiKey", config.NvdApiKey },
                { "VulnerabilityCorrelationEnabled", config.VulnerabilityCorrelationEnabled },
                { "EnableIndexDualWrite", config.EnableIndexDualWrite },
                { "AutoApproveNewTenants", config.AutoApproveNewTenants },
                { "SelfServiceAppHomingEnabled", config.SelfServiceAppHomingEnabled },
                { "SessionDeletionKillSwitch", config.SessionDeletionKillSwitch },
                { "VulnerabilityDataLastSyncUtc", config.VulnerabilityDataLastSyncUtc },
                { "MsrcLastSyncUtc", config.MsrcLastSyncUtc },
                { "MaxDiagnosticsDownloadSizeMB", config.MaxDiagnosticsDownloadSizeMB },
                { "DiagnosticsDownloadTimeoutSeconds", config.DiagnosticsDownloadTimeoutSeconds },
                { "OpsEventRetentionDays", config.OpsEventRetentionDays },
                { "SlaNotificationCooldownHours", config.SlaNotificationCooldownHours },
                // Ops Alert settings
                { "OpsAlertRulesJson", config.OpsAlertRulesJson ?? string.Empty },
                { "OpsAlertTelegramEnabled", config.OpsAlertTelegramEnabled },
                { "OpsAlertTelegramChatId", config.OpsAlertTelegramChatId ?? string.Empty },
                { "OpsAlertTeamsEnabled", config.OpsAlertTeamsEnabled },
                { "OpsAlertTeamsWebhookUrl", config.OpsAlertTeamsWebhookUrl ?? string.Empty },
                { "OpsAlertSlackEnabled", config.OpsAlertSlackEnabled },
                { "OpsAlertSlackWebhookUrl", config.OpsAlertSlackWebhookUrl ?? string.Empty },
                // Per-line agent binary integrity (written by build scripts via Merge).
                // V2 is the only wired line; future V3 = add field set here. Retired columns
                // (V1-suffix and the even older unsuffixed "LatestAgent*") are never written,
                // so the next Save evicts them implicitly on overwrite.
                { "AllowAgentDowngrade", config.AllowAgentDowngrade },
                { "LatestAgentV2Version", config.LatestAgentV2Version ?? string.Empty },
                { "LatestAgentV2Sha256", config.LatestAgentV2Sha256 ?? string.Empty },
                { "LatestAgentV2ExeSha256", config.LatestAgentV2ExeSha256 ?? string.Empty },
                { "LatestBootstrapV2ScriptVersion", config.LatestBootstrapV2ScriptVersion ?? string.Empty },
                // Rate limiting per-role settings
                { "UserRateLimitRequestsPerMinute", config.UserRateLimitRequestsPerMinute },
                { "GlobalAdminRateLimitRequestsPerMinute", config.GlobalAdminRateLimitRequestsPerMinute },
                // Plan tier definitions
                { "PlanTierDefinitionsJson", config.PlanTierDefinitionsJson ?? string.Empty },
                // Feedback settings
                { "FeedbackEnabled", config.FeedbackEnabled },
                { "FeedbackMinTenantAgeDays", config.FeedbackMinTenantAgeDays },
                { "FeedbackCooldownDays", config.FeedbackCooldownDays },
                // MCP access control
                { "McpAccessPolicy", config.McpAccessPolicy ?? "WhitelistOnly" },
                // Agent endpoint migration (config-channel re-home)
                { "AgentMigrateApiBaseUrl", config.AgentMigrateApiBaseUrl ?? string.Empty },
                { "AgentMigrateTenantOverridesJson", config.AgentMigrateTenantOverridesJson ?? string.Empty }
            };

            return entity;
        }

        internal static AdminConfiguration ConvertFromAdminTableEntity(TableEntity entity)
        {
            return new AdminConfiguration
            {
                PartitionKey = entity.PartitionKey,
                RowKey = entity.RowKey,
                LastUpdated = entity.GetDateTime("LastUpdated") ?? DateTime.UtcNow,
                UpdatedBy = entity.GetString("UpdatedBy") ?? "Unknown",
                GlobalRateLimitRequestsPerMinute = entity.GetInt32("GlobalRateLimitRequestsPerMinute") ?? 100,
                PlatformStatsBlobSasUrl = entity.GetString("PlatformStatsBlobSasUrl") ?? string.Empty,
                CollectorIdleTimeoutMinutes = entity.GetInt32("CollectorIdleTimeoutMinutes") ?? 15,
                DesktopDetectorNoCandidateTimeoutMinutes = entity.GetInt32("DesktopDetectorNoCandidateTimeoutMinutes") ?? 10,
                ExcessiveEventCountThreshold = entity.GetInt32("ExcessiveEventCountThreshold") ?? 2000,
                ExcessiveEventAutoActionMode = entity.GetString("ExcessiveEventAutoActionMode") ?? "Off",
                ExcessiveEventAutoActionThreshold = entity.GetInt32("ExcessiveEventAutoActionThreshold") ?? 2500,
                ExcessiveEventAutoActionDurationHours = entity.GetInt32("ExcessiveEventAutoActionDurationHours") ?? 24,
                DiagnosticsGlobalLogPathsJson = entity.GetString("DiagnosticsGlobalLogPathsJson"),
                ModernDeploymentHarmlessEventIdsJson = entity.GetString("ModernDeploymentHarmlessEventIdsJson"),
                WhiteGloveSealingPatternIdsJson = entity.GetString("WhiteGloveSealingPatternIdsJson"),
                NvdApiKey = entity.GetString("NvdApiKey"),
                VulnerabilityCorrelationEnabled = entity.GetBoolean("VulnerabilityCorrelationEnabled") ?? true,
                EnableIndexDualWrite = entity.GetBoolean("EnableIndexDualWrite") ?? false,
                AutoApproveNewTenants = entity.GetBoolean("AutoApproveNewTenants") ?? false,
                SelfServiceAppHomingEnabled = entity.GetBoolean("SelfServiceAppHomingEnabled") ?? false,
                SessionDeletionKillSwitch = entity.GetBoolean("SessionDeletionKillSwitch") ?? false,
                VulnerabilityDataLastSyncUtc = entity.GetString("VulnerabilityDataLastSyncUtc"),
                MsrcLastSyncUtc = entity.GetString("MsrcLastSyncUtc"),
                MaxDiagnosticsDownloadSizeMB = entity.GetInt32("MaxDiagnosticsDownloadSizeMB") ?? 500,
                DiagnosticsDownloadTimeoutSeconds = entity.GetInt32("DiagnosticsDownloadTimeoutSeconds") ?? 120,
                OpsEventRetentionDays = entity.GetInt32("OpsEventRetentionDays") ?? 90,
                SlaNotificationCooldownHours = entity.GetInt32("SlaNotificationCooldownHours") ?? 24,
                // Ops Alert settings
                OpsAlertRulesJson = entity.GetString("OpsAlertRulesJson"),
                OpsAlertTelegramEnabled = entity.GetBoolean("OpsAlertTelegramEnabled") ?? false,
                OpsAlertTelegramChatId = entity.GetString("OpsAlertTelegramChatId"),
                OpsAlertTeamsEnabled = entity.GetBoolean("OpsAlertTeamsEnabled") ?? false,
                OpsAlertTeamsWebhookUrl = entity.GetString("OpsAlertTeamsWebhookUrl"),
                OpsAlertSlackEnabled = entity.GetBoolean("OpsAlertSlackEnabled") ?? false,
                OpsAlertSlackWebhookUrl = entity.GetString("OpsAlertSlackWebhookUrl"),
                // Per-line agent binary integrity. V2 is the only wired line (V1 retired).
                AllowAgentDowngrade = entity.GetBoolean("AllowAgentDowngrade") ?? false,
                LatestAgentV2Version = entity.GetString("LatestAgentV2Version") ?? string.Empty,
                LatestAgentV2Sha256 = entity.GetString("LatestAgentV2Sha256") ?? string.Empty,
                LatestAgentV2ExeSha256 = entity.GetString("LatestAgentV2ExeSha256") ?? string.Empty,
                LatestBootstrapV2ScriptVersion = entity.GetString("LatestBootstrapV2ScriptVersion") ?? string.Empty,
                // Rate limiting per-role settings
                UserRateLimitRequestsPerMinute = entity.GetInt32("UserRateLimitRequestsPerMinute") ?? 120,
                GlobalAdminRateLimitRequestsPerMinute = entity.GetInt32("GlobalAdminRateLimitRequestsPerMinute") ?? 600,
                // Plan tier definitions
                PlanTierDefinitionsJson = entity.GetString("PlanTierDefinitionsJson"),
                // Feedback settings
                FeedbackEnabled = entity.GetBoolean("FeedbackEnabled") ?? true,
                FeedbackMinTenantAgeDays = entity.GetInt32("FeedbackMinTenantAgeDays") ?? 14,
                FeedbackCooldownDays = entity.GetInt32("FeedbackCooldownDays") ?? 60,
                // MCP access control
                McpAccessPolicy = entity.GetString("McpAccessPolicy") ?? "WhitelistOnly",
                // Agent endpoint migration (config-channel re-home)
                AgentMigrateApiBaseUrl = entity.GetString("AgentMigrateApiBaseUrl") ?? string.Empty,
                AgentMigrateTenantOverridesJson = entity.GetString("AgentMigrateTenantOverridesJson") ?? string.Empty
            };
        }

    }

    /// <summary>
    /// Entity representing an approved tenant in the PreviewWhitelist table.
    /// </summary>
    public class PreviewWhitelistEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = string.Empty; // TenantId
        public string RowKey { get; set; } = "approved";
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public DateTime ApprovedAt { get; set; }
        public string ApprovedBy { get; set; } = string.Empty;
    }

    /// <summary>
    /// Entity storing the notification email for a tenant in the PreviewWhitelist table.
    /// PartitionKey = TenantId, RowKey = "notification-email".
    /// Temporary — remove after GA.
    /// </summary>
    public class PreviewNotificationEntity : ITableEntity
    {
        public string PartitionKey { get; set; } = string.Empty; // TenantId
        public string RowKey { get; set; } = "notification-email";
        public DateTimeOffset? Timestamp { get; set; }
        public ETag ETag { get; set; }

        public string Email { get; set; } = string.Empty;
    }
}
