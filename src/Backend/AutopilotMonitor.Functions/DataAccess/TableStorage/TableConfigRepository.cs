using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
        private readonly ILogger<TableConfigRepository> _logger;

        public TableConfigRepository(TableStorageService storage, ILogger<TableConfigRepository> logger)
        {
            _logger = logger;
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

        public async Task<bool> SaveTenantConfigurationAsync(TenantConfiguration config)
        {
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

                await _previewWhitelistTableClient.UpsertEntityAsync(entity);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding tenant {TenantId} to preview whitelist", tenantId);
                return false;
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

        // --- Tenant Configuration Entity Mapping ---

        private TableEntity ConvertToTenantTableEntity(TenantConfiguration config)
        {
            var entity = new TableEntity(config.TenantId, "config")
            {
                { "DomainName", config.DomainName },
                { "LastUpdated", config.LastUpdated },
                { "UpdatedBy", config.UpdatedBy },
                { "OnboardedBy", config.OnboardedBy },
                { "Disabled", config.Disabled },
                { "DisabledReason", config.DisabledReason },
                { "DisabledUntil", config.DisabledUntil },
                { "RateLimitRequestsPerMinute", config.RateLimitRequestsPerMinute },
                { "CustomRateLimitRequestsPerMinute", config.CustomRateLimitRequestsPerMinute },
                { "ManufacturerWhitelist", config.ManufacturerWhitelist },
                { "ModelWhitelist", config.ModelWhitelist },
                { "ValidateAutopilotDevice", config.ValidateAutopilotDevice },
                { "ValidateCorporateIdentifier", config.ValidateCorporateIdentifier },
                { "ValidateDeviceAssociation", config.ValidateDeviceAssociation },
                { "AllowInsecureAgentRequests", config.AllowInsecureAgentRequests },
                { "DataRetentionDays", config.DataRetentionDays },
                { "SessionTimeoutHours", config.SessionTimeoutHours },
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
                { "PlanTier", config.PlanTier },
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

        private TenantConfiguration ConvertFromTenantTableEntity(TableEntity entity)
        {
            return new TenantConfiguration
            {
                TenantId = entity.PartitionKey,
                DomainName = entity.GetString("DomainName") ?? "",
                LastUpdated = entity.GetDateTime("LastUpdated") ?? DateTime.UtcNow,
                UpdatedBy = entity.GetString("UpdatedBy") ?? "Unknown",
                OnboardedBy = entity.GetString("OnboardedBy"),
                Disabled = entity.GetBoolean("Disabled") ?? false,
                DisabledReason = entity.GetString("DisabledReason"),
                DisabledUntil = entity.GetDateTime("DisabledUntil"),
                RateLimitRequestsPerMinute = entity.GetInt32("RateLimitRequestsPerMinute") ?? 100,
                CustomRateLimitRequestsPerMinute = entity.GetInt32("CustomRateLimitRequestsPerMinute"),
                ManufacturerWhitelist = entity.GetString("ManufacturerWhitelist") ?? "Dell*,HP*,Lenovo*,Microsoft Corporation",
                ModelWhitelist = entity.GetString("ModelWhitelist") ?? "*",
                ValidateAutopilotDevice = entity.GetBoolean("ValidateAutopilotDevice") ?? entity.GetBoolean("ValidateSerialNumber") ?? false,
                ValidateCorporateIdentifier = entity.GetBoolean("ValidateCorporateIdentifier") ?? false,
                ValidateDeviceAssociation = entity.GetBoolean("ValidateDeviceAssociation") ?? false,
                AllowInsecureAgentRequests = entity.GetBoolean("AllowInsecureAgentRequests") ?? false,
                DataRetentionDays = entity.GetInt32("DataRetentionDays") ?? 90,
                SessionTimeoutHours = entity.GetInt32("SessionTimeoutHours") ?? 5,
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
                PlanTier = entity.GetString("PlanTier") ?? "free",
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

        private TableEntity ConvertToAdminTableEntity(AdminConfiguration config)
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
                { "MaxSessionWindowHours", config.MaxSessionWindowHours },
                { "MaintenanceBlockDurationHours", config.MaintenanceBlockDurationHours },
                { "DiagnosticsGlobalLogPathsJson", config.DiagnosticsGlobalLogPathsJson },
                { "ModernDeploymentHarmlessEventIdsJson", config.ModernDeploymentHarmlessEventIdsJson ?? string.Empty },
                { "WhiteGloveSealingPatternIdsJson", config.WhiteGloveSealingPatternIdsJson ?? string.Empty },
                { "NvdApiKey", config.NvdApiKey },
                { "VulnerabilityCorrelationEnabled", config.VulnerabilityCorrelationEnabled },
                { "EnableIndexDualWrite", config.EnableIndexDualWrite },
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
                // Symmetric V1/V2 schema; future V3 = add field set here.
                // Old "LatestAgent*" columns (no V1 suffix) are read in ConvertFrom for migration —
                // never written here so the next Save evicts them implicitly on overwrite.
                { "AllowAgentDowngrade", config.AllowAgentDowngrade },
                { "LatestAgentV1Version", config.LatestAgentV1Version ?? string.Empty },
                { "LatestAgentV1Sha256", config.LatestAgentV1Sha256 ?? string.Empty },
                { "LatestAgentV1ExeSha256", config.LatestAgentV1ExeSha256 ?? string.Empty },
                { "LatestBootstrapV1ScriptVersion", config.LatestBootstrapV1ScriptVersion ?? string.Empty },
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
                { "McpAccessPolicy", config.McpAccessPolicy ?? "WhitelistOnly" }
            };

            return entity;
        }

        private AdminConfiguration ConvertFromAdminTableEntity(TableEntity entity)
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
                MaxSessionWindowHours = entity.GetInt32("MaxSessionWindowHours") ?? 24,
                MaintenanceBlockDurationHours = entity.GetInt32("MaintenanceBlockDurationHours") ?? 12,
                DiagnosticsGlobalLogPathsJson = entity.GetString("DiagnosticsGlobalLogPathsJson"),
                ModernDeploymentHarmlessEventIdsJson = entity.GetString("ModernDeploymentHarmlessEventIdsJson"),
                WhiteGloveSealingPatternIdsJson = entity.GetString("WhiteGloveSealingPatternIdsJson"),
                NvdApiKey = entity.GetString("NvdApiKey"),
                VulnerabilityCorrelationEnabled = entity.GetBoolean("VulnerabilityCorrelationEnabled") ?? true,
                EnableIndexDualWrite = entity.GetBoolean("EnableIndexDualWrite") ?? false,
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
                // Per-line agent binary integrity. Read V1-suffix column first; fall back to the
                // legacy unsuffixed column ("LatestAgentVersion") so existing rows migrate
                // transparently on the next Save.
                AllowAgentDowngrade = entity.GetBoolean("AllowAgentDowngrade") ?? false,
                LatestAgentV1Version = entity.GetString("LatestAgentV1Version")
                    ?? entity.GetString("LatestAgentVersion") ?? string.Empty,
                LatestAgentV1Sha256 = entity.GetString("LatestAgentV1Sha256")
                    ?? entity.GetString("LatestAgentSha256") ?? string.Empty,
                LatestAgentV1ExeSha256 = entity.GetString("LatestAgentV1ExeSha256")
                    ?? entity.GetString("LatestAgentExeSha256") ?? string.Empty,
                LatestBootstrapV1ScriptVersion = entity.GetString("LatestBootstrapV1ScriptVersion")
                    ?? entity.GetString("LatestBootstrapScriptVersion") ?? string.Empty,
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
                McpAccessPolicy = entity.GetString("McpAccessPolicy") ?? "WhitelistOnly"
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
