using System.Text.Json;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage
{
    /// <summary>
    /// Table Storage implementation of <see cref="IHardwareRejectionNotificationTracker"/>.
    /// PartitionKey = tenantId (lowercased). RowKey encodes the dedup subject:
    ///   - hardware rejection: "{manufacturer-lower}|{model-lower}" (trimmed)
    ///   - TPM PSS unsupported: "tpmpss|{serial-lower}" (trimmed; the literal "tpmpss" prefix
    ///     cannot collide with a hardware key because no real manufacturer is named "tpmpss")
    ///   - F3 rule regression: "ruleregression|{ruleId-lower}" (trimmed; payload-carrying rows
    ///     that are refreshed while the episode is active and deleted on re-arm)
    ///   - app-version duration regression: "appversionregression|{app-lower}|{version-lower}"
    ///     (trimmed + table-key-sanitized; same payload-carrying episode semantics — raw
    ///     AppName/CurrentVersion live in columns, the mapper never parses the RowKey)
    /// Race-safe via AddEntityAsync: Azure Table Storage returns 409 Conflict if the entity already exists.
    /// </summary>
    public class TableHardwareRejectionNotificationTracker : IHardwareRejectionNotificationTracker
    {
        private readonly TableClient _table;
        private readonly ILogger<TableHardwareRejectionNotificationTracker> _logger;

        public TableHardwareRejectionNotificationTracker(
            TableStorageService storage,
            ILogger<TableHardwareRejectionNotificationTracker> logger)
        {
            _logger = logger;
            _table = storage.GetTableClient(Constants.TableNames.HardwareRejectionNotificationTracker);
        }

        public async Task<bool> TryRegisterFirstNotificationAsync(string tenantId, string manufacturer, string model)
        {
            if (string.IsNullOrWhiteSpace(tenantId)
                || string.IsNullOrWhiteSpace(manufacturer)
                || string.IsNullOrWhiteSpace(model))
            {
                return false;
            }

            var partitionKey = tenantId.ToLowerInvariant();
            var rowKey = BuildRowKey(manufacturer, model);

            var entity = new TableEntity(partitionKey, rowKey)
            {
                ["TenantId"] = tenantId,
                ["Manufacturer"] = manufacturer,
                ["Model"] = model,
                ["FirstNotifiedAt"] = DateTime.UtcNow
            };

            try
            {
                await _table.AddEntityAsync(entity);
                _logger.LogInformation(
                    "HardwareRejection tracker registered: tenant={TenantId} mfr={Manufacturer} model={Model}",
                    tenantId, manufacturer, model);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Already notified for this (tenant, manufacturer, model) — no second bell.
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "HardwareRejection tracker failed: tenant={TenantId} mfr={Manufacturer} model={Model}",
                    tenantId, manufacturer, model);
                // On unexpected failure, return false so we do not double-fire if the row was actually written.
                return false;
            }
        }

        public async Task<bool> TryRegisterFirstTpmPssNotificationAsync(string tenantId, string serialNumber)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(serialNumber))
                return false;

            var partitionKey = tenantId.ToLowerInvariant();
            var rowKey = BuildTpmPssRowKey(serialNumber);

            var entity = new TableEntity(partitionKey, rowKey)
            {
                ["TenantId"] = tenantId,
                ["SerialNumber"] = serialNumber,
                ["FirstNotifiedAt"] = DateTime.UtcNow
            };

            try
            {
                await _table.AddEntityAsync(entity);
                _logger.LogInformation(
                    "TpmPssUnsupported tracker registered: tenant={TenantId} sn={SerialNumber}",
                    tenantId, serialNumber);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Already notified for this (tenant, serial) — no second bell.
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "TpmPssUnsupported tracker failed: tenant={TenantId} sn={SerialNumber}",
                    tenantId, serialNumber);
                // On unexpected failure, return false so we do not double-fire if the row was actually written.
                return false;
            }
        }

        public async Task<bool> TryRegisterRuleRegressionAsync(string tenantId, RuleRegressionAlert alert)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(alert?.RuleId))
                return false;

            try
            {
                await _table.AddEntityAsync(BuildRuleRegressionEntity(tenantId, alert!));
                _logger.LogInformation(
                    "RuleRegression tracker registered: tenant={TenantId} rule={RuleId}", tenantId, alert!.RuleId);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Episode already active — one bell per episode.
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RuleRegression tracker failed: tenant={TenantId} rule={RuleId}", tenantId, alert!.RuleId);
                // Fail closed so we do not double-fire if the row was actually written.
                return false;
            }
        }

        public async Task RefreshRuleRegressionAsync(string tenantId, RuleRegressionAlert alert)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(alert?.RuleId))
                return;
            try
            {
                // Replace-mode upsert with the FULL payload incl. the caller-carried
                // FirstNotifiedAt — the retention sweep re-arms on the ORIGINAL age, so a
                // refresh must never rejuvenate the row.
                await _table.UpsertEntityAsync(BuildRuleRegressionEntity(tenantId, alert!), TableUpdateMode.Replace);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RuleRegression tracker refresh failed: tenant={TenantId} rule={RuleId} (stale numbers remain)",
                    tenantId, alert!.RuleId);
            }
        }

        public async Task DeleteRuleRegressionAsync(string tenantId, string ruleId)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(ruleId))
                return;
            try
            {
                await _table.DeleteEntityAsync(tenantId.ToLowerInvariant(), BuildRuleRegressionRowKey(ruleId));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already gone (retention sweep or concurrent pass) — idempotent.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "RuleRegression tracker delete failed: tenant={TenantId} rule={RuleId} (episode stays until retention)",
                    tenantId, ruleId);
            }
        }

        public async Task<List<RuleRegressionAlert>> GetRuleRegressionsAsync(string tenantId)
        {
            var results = new List<RuleRegressionAlert>();
            if (string.IsNullOrWhiteSpace(tenantId)) return results;
            try
            {
                var partitionKey = tenantId.ToLowerInvariant();
                // '}' (0x7D) sorts directly after '|' (0x7C) — prefix range over "ruleregression|…".
                var filter = $"PartitionKey eq '{partitionKey}' and RowKey ge '{RuleRegressionRowKeyPrefix}' and RowKey lt 'ruleregression}}'";
                await foreach (var entity in _table.QueryAsync<TableEntity>(filter: filter))
                {
                    results.Add(MapToRuleRegressionAlert(entity));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "RuleRegression tracker list failed for tenant {TenantId}", tenantId);
            }
            return results;
        }

        public async Task<int> DeleteOlderThanAsync(DateTime cutoffUtc)
        {
            try
            {
                // FirstNotifiedAt == the row's first registration (rule-regression refreshes carry
                // it forward unchanged). Pruning here resets the dedup for EVERY key space in this
                // table: a hardware model rejected again after the cutoff rings once more, so does
                // a TPM-PSS device that reports again ("tpmpss|{serial}" rows), and a rule
                // regression still active after the window fires a fresh bell — a month-old
                // still-burning regression is worth a reminder (spec §F3 retention re-arm).
                var filter = $"FirstNotifiedAt lt datetime'{cutoffUtc:yyyy-MM-ddTHH:mm:ss}Z'";
                var query = _table.QueryAsync<TableEntity>(filter: filter, select: new[] { "PartitionKey", "RowKey" });

                int deleted = 0;
                await foreach (var entity in query)
                {
                    try
                    {
                        await _table.DeleteEntityAsync(entity.PartitionKey, entity.RowKey);
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete hardware-rejection tracker row {PK}/{RK}", entity.PartitionKey, entity.RowKey);
                    }
                }

                if (deleted > 0)
                    _logger.LogInformation("Deleted {Count} hardware-rejection tracker rows older than {Cutoff:yyyy-MM-dd}", deleted, cutoffUtc);

                return deleted;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete old hardware-rejection tracker rows");
                return 0;
            }
        }

        internal static string BuildRowKey(string manufacturer, string model)
        {
            var mfr = (manufacturer ?? string.Empty).Trim().ToLowerInvariant();
            var mdl = (model ?? string.Empty).Trim().ToLowerInvariant();
            return $"{mfr}|{mdl}";
        }

        internal static string BuildTpmPssRowKey(string serialNumber)
        {
            var sn = (serialNumber ?? string.Empty).Trim().ToLowerInvariant();
            return $"tpmpss|{sn}";
        }

        public async Task<bool> TryRegisterAppVersionRegressionAsync(string tenantId, AppVersionRegressionAlert alert)
        {
            if (string.IsNullOrWhiteSpace(tenantId)
                || string.IsNullOrWhiteSpace(alert?.AppName)
                || string.IsNullOrWhiteSpace(alert?.CurrentVersion))
            {
                return false;
            }

            try
            {
                await _table.AddEntityAsync(BuildAppVersionRegressionEntity(tenantId, alert!));
                _logger.LogInformation(
                    "AppVersionRegression tracker registered: tenant={TenantId} app={AppName} version={Version}",
                    tenantId, alert!.AppName, alert!.CurrentVersion);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Episode already active — one bell per (app, version) episode.
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AppVersionRegression tracker failed: tenant={TenantId} app={AppName} version={Version}",
                    tenantId, alert!.AppName, alert!.CurrentVersion);
                // Fail closed so we do not double-fire if the row was actually written.
                return false;
            }
        }

        public async Task RefreshAppVersionRegressionAsync(string tenantId, AppVersionRegressionAlert alert)
        {
            if (string.IsNullOrWhiteSpace(tenantId)
                || string.IsNullOrWhiteSpace(alert?.AppName)
                || string.IsNullOrWhiteSpace(alert?.CurrentVersion))
            {
                return;
            }
            try
            {
                // Replace-mode upsert with the FULL payload incl. the caller-carried
                // FirstNotifiedAt — the retention sweep re-arms on the ORIGINAL age, so a
                // refresh must never rejuvenate the row.
                await _table.UpsertEntityAsync(BuildAppVersionRegressionEntity(tenantId, alert!), TableUpdateMode.Replace);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AppVersionRegression tracker refresh failed: tenant={TenantId} app={AppName} version={Version} (stale numbers remain)",
                    tenantId, alert!.AppName, alert!.CurrentVersion);
            }
        }

        public async Task DeleteAppVersionRegressionAsync(string tenantId, string appName, string currentVersion)
        {
            if (string.IsNullOrWhiteSpace(tenantId)
                || string.IsNullOrWhiteSpace(appName)
                || string.IsNullOrWhiteSpace(currentVersion))
            {
                return;
            }
            try
            {
                await _table.DeleteEntityAsync(
                    tenantId.ToLowerInvariant(), BuildAppVersionRegressionRowKey(appName, currentVersion));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already gone (retention sweep or concurrent pass) — idempotent.
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AppVersionRegression tracker delete failed: tenant={TenantId} app={AppName} version={Version} (episode stays until retention)",
                    tenantId, appName, currentVersion);
            }
        }

        public async Task<List<AppVersionRegressionAlert>> GetAppVersionRegressionsAsync(string tenantId)
        {
            var results = new List<AppVersionRegressionAlert>();
            if (string.IsNullOrWhiteSpace(tenantId)) return results;
            try
            {
                var partitionKey = tenantId.ToLowerInvariant();
                // '}' (0x7D) sorts directly after '|' (0x7C) — prefix range over "appversionregression|…".
                var filter = $"PartitionKey eq '{partitionKey}' and RowKey ge '{AppVersionRegressionRowKeyPrefix}' and RowKey lt 'appversionregression}}'";
                await foreach (var entity in _table.QueryAsync<TableEntity>(filter: filter))
                {
                    results.Add(MapToAppVersionRegressionAlert(entity));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "AppVersionRegression tracker list failed for tenant {TenantId}", tenantId);
            }
            return results;
        }

        internal const string RuleRegressionRowKeyPrefix = "ruleregression|";

        internal static string BuildRuleRegressionRowKey(string ruleId)
        {
            var id = (ruleId ?? string.Empty).Trim().ToLowerInvariant();
            return $"{RuleRegressionRowKeyPrefix}{id}";
        }

        // internal static: entity builder + mapper are pinned by round-trip unit tests
        // (table-storage-serialization rule — every property must survive Store→Map).
        internal static TableEntity BuildRuleRegressionEntity(string tenantId, RuleRegressionAlert alert)
        {
            var entity = new TableEntity(tenantId.ToLowerInvariant(), BuildRuleRegressionRowKey(alert.RuleId))
            {
                ["TenantId"] = tenantId,
                ["RuleId"] = alert.RuleId,
                ["RuleTitle"] = alert.RuleTitle ?? string.Empty,
                ["WindowFireCount"] = alert.WindowFireCount,
                ["WindowSessionCount"] = alert.WindowSessionCount,
                ["BaselineFireCount"] = alert.BaselineFireCount,
                ["BaselineSessionCount"] = alert.BaselineSessionCount,
                ["WindowRatePct"] = alert.WindowRatePct,
                ["BaselineRatePct"] = alert.BaselineRatePct,
                ["WindowStartDate"] = alert.WindowStartDate ?? string.Empty,
                ["WindowEndDate"] = alert.WindowEndDate ?? string.Empty,
                ["FirstNotifiedAt"] = alert.FirstNotifiedAt,
                ["LastEvaluatedAt"] = alert.LastEvaluatedAt,
            };
            // Tri-states: absent column = "no claim" — a zero-baseline alert has no finite lift,
            // and a missing dimension means "no clear concentration", never a guessed one.
            if (alert.Lift.HasValue)
                entity["Lift"] = alert.Lift.Value;
            if (alert.Dimension != null)
                entity["DimensionJson"] = JsonSerializer.Serialize(alert.Dimension);
            return entity;
        }

        internal static RuleRegressionAlert MapToRuleRegressionAlert(TableEntity entity)
        {
            RuleRegressionDimension? dimension = null;
            var dimensionJson = entity.GetString("DimensionJson");
            if (!string.IsNullOrEmpty(dimensionJson))
            {
                try { dimension = JsonSerializer.Deserialize<RuleRegressionDimension>(dimensionJson!); }
                catch (JsonException) { dimension = null; } // corrupt column — degrade to "no claim"
            }

            return new RuleRegressionAlert
            {
                TenantId = entity.GetString("TenantId") ?? entity.PartitionKey,
                RuleId = entity.GetString("RuleId") ?? entity.RowKey.Substring(RuleRegressionRowKeyPrefix.Length),
                RuleTitle = entity.GetString("RuleTitle") ?? string.Empty,
                WindowFireCount = entity.GetInt32("WindowFireCount") ?? 0,
                WindowSessionCount = entity.GetInt32("WindowSessionCount") ?? 0,
                BaselineFireCount = entity.GetInt32("BaselineFireCount") ?? 0,
                BaselineSessionCount = entity.GetInt32("BaselineSessionCount") ?? 0,
                WindowRatePct = entity.GetDouble("WindowRatePct") ?? 0,
                BaselineRatePct = entity.GetDouble("BaselineRatePct") ?? 0,
                Lift = entity.GetDouble("Lift"),
                WindowStartDate = entity.GetString("WindowStartDate") ?? string.Empty,
                WindowEndDate = entity.GetString("WindowEndDate") ?? string.Empty,
                Dimension = dimension,
                FirstNotifiedAt = entity.GetDateTimeOffset("FirstNotifiedAt")?.UtcDateTime ?? DateTime.MinValue,
                LastEvaluatedAt = entity.GetDateTimeOffset("LastEvaluatedAt")?.UtcDateTime ?? DateTime.MinValue,
            };
        }

        // ===== VERDICT CALIBRATION DRIFT (docs/backend/verdict-calibration.md) =====

        internal const string VerdictCalibrationRowKeyPrefix = "verdictcalibration|";

        internal static string BuildVerdictCalibrationRowKey(string kind, string verdictPath, string status)
        {
            // Paths are a closed vocabulary plus rule ids (ANALYZE-...), statuses are enum names and
            // the group marker "*" - none contain table-key-hostile characters, but sanitize anyway.
            var k = SanitizeTableKey((kind ?? string.Empty).Trim().ToLowerInvariant());
            var p = SanitizeTableKey((verdictPath ?? string.Empty).Trim().ToLowerInvariant());
            var s = SanitizeTableKey((status ?? string.Empty).Trim().ToLowerInvariant());
            return $"{VerdictCalibrationRowKeyPrefix}{k}|{p}|{s}";
        }

        public async Task<bool> TryRegisterVerdictCalibrationAlertAsync(string tenantId, VerdictCalibrationAlert alert)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(alert?.Kind) || string.IsNullOrWhiteSpace(alert?.VerdictPath))
                return false;
            try
            {
                await _table.AddEntityAsync(BuildVerdictCalibrationEntity(tenantId, alert!));
                _logger.LogInformation(
                    "VerdictCalibration tracker registered: tenant={TenantId} kind={Kind} path={Path}", tenantId, alert!.Kind, alert.VerdictPath);
                return true;
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                return false; // episode already active - one ops event per episode
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VerdictCalibration tracker failed: tenant={TenantId} kind={Kind} path={Path}", tenantId, alert!.Kind, alert.VerdictPath);
                return false; // fail closed - never double-fire
            }
        }

        public async Task RefreshVerdictCalibrationAlertAsync(string tenantId, VerdictCalibrationAlert alert)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(alert?.Kind) || string.IsNullOrWhiteSpace(alert?.VerdictPath))
                return;
            try
            {
                await _table.UpsertEntityAsync(BuildVerdictCalibrationEntity(tenantId, alert!), TableUpdateMode.Replace);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VerdictCalibration tracker refresh failed: tenant={TenantId} kind={Kind} path={Path}", tenantId, alert!.Kind, alert.VerdictPath);
            }
        }

        public async Task DeleteVerdictCalibrationAlertAsync(string tenantId, string kind, string verdictPath, string status)
        {
            if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(verdictPath))
                return;
            try
            {
                await _table.DeleteEntityAsync(tenantId.ToLowerInvariant(), BuildVerdictCalibrationRowKey(kind, verdictPath, status));
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // already gone - idempotent
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VerdictCalibration tracker delete failed: tenant={TenantId} kind={Kind} path={Path}", tenantId, kind, verdictPath);
            }
        }

        public async Task<List<VerdictCalibrationAlert>> GetVerdictCalibrationAlertsAsync(string tenantId)
        {
            var results = new List<VerdictCalibrationAlert>();
            if (string.IsNullOrWhiteSpace(tenantId)) return results;
            try
            {
                var partitionKey = tenantId.ToLowerInvariant();
                // '}' sorts directly after '|' - prefix range over "verdictcalibration|...".
                var filter = $"PartitionKey eq '{partitionKey}' and RowKey ge '{VerdictCalibrationRowKeyPrefix}' and RowKey lt 'verdictcalibration}}'";
                await foreach (var entity in _table.QueryAsync<TableEntity>(filter: filter))
                {
                    results.Add(MapToVerdictCalibrationAlert(entity));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "VerdictCalibration tracker list failed for tenant {TenantId}", tenantId);
            }
            return results;
        }

        // internal static: pinned by round-trip unit tests (table-storage-serialization rule).
        internal static TableEntity BuildVerdictCalibrationEntity(string tenantId, VerdictCalibrationAlert alert)
        {
            var entity = new TableEntity(tenantId.ToLowerInvariant(), BuildVerdictCalibrationRowKey(alert.Kind, alert.VerdictPath, alert.Status))
            {
                ["TenantId"] = tenantId,
                ["Kind"] = alert.Kind,
                ["VerdictPath"] = alert.VerdictPath,
                ["Status"] = alert.Status ?? string.Empty,
                ["WindowHitCount"] = alert.WindowHitCount,
                ["WindowSessionCount"] = alert.WindowSessionCount,
                ["BaselineHitCount"] = alert.BaselineHitCount,
                ["BaselineSessionCount"] = alert.BaselineSessionCount,
                ["WindowRatePct"] = alert.WindowRatePct,
                ["BaselineRatePct"] = alert.BaselineRatePct,
                ["WindowStartDate"] = alert.WindowStartDate ?? string.Empty,
                ["WindowEndDate"] = alert.WindowEndDate ?? string.Empty,
                ["FirstNotifiedAt"] = alert.FirstNotifiedAt,
                ["LastEvaluatedAt"] = alert.LastEvaluatedAt,
            };
            if (alert.Lift.HasValue)
                entity["Lift"] = alert.Lift.Value;
            if (alert.Dimension != null)
                entity["DimensionJson"] = JsonSerializer.Serialize(alert.Dimension);
            return entity;
        }

        internal static VerdictCalibrationAlert MapToVerdictCalibrationAlert(TableEntity entity)
        {
            RuleRegressionDimension? dimension = null;
            var dimensionJson = entity.GetString("DimensionJson");
            if (!string.IsNullOrEmpty(dimensionJson))
            {
                try { dimension = JsonSerializer.Deserialize<RuleRegressionDimension>(dimensionJson!); }
                catch (JsonException) { dimension = null; }
            }
            return new VerdictCalibrationAlert
            {
                TenantId = entity.GetString("TenantId") ?? entity.PartitionKey,
                Kind = entity.GetString("Kind") ?? string.Empty,
                VerdictPath = entity.GetString("VerdictPath") ?? string.Empty,
                Status = entity.GetString("Status") ?? string.Empty,
                WindowHitCount = entity.GetInt32("WindowHitCount") ?? 0,
                WindowSessionCount = entity.GetInt32("WindowSessionCount") ?? 0,
                BaselineHitCount = entity.GetInt32("BaselineHitCount") ?? 0,
                BaselineSessionCount = entity.GetInt32("BaselineSessionCount") ?? 0,
                WindowRatePct = entity.GetDouble("WindowRatePct") ?? 0,
                BaselineRatePct = entity.GetDouble("BaselineRatePct") ?? 0,
                Lift = entity.GetDouble("Lift"),
                WindowStartDate = entity.GetString("WindowStartDate") ?? string.Empty,
                WindowEndDate = entity.GetString("WindowEndDate") ?? string.Empty,
                Dimension = dimension,
                FirstNotifiedAt = entity.GetDateTimeOffset("FirstNotifiedAt")?.UtcDateTime ?? DateTime.MinValue,
                LastEvaluatedAt = entity.GetDateTimeOffset("LastEvaluatedAt")?.UtcDateTime ?? DateTime.MinValue,
            };
        }

        internal const string AppVersionRegressionRowKeyPrefix = "appversionregression|";

        /// <summary>
        /// App names and versions are free-form agent payloads — they can contain characters
        /// Azure Table keys reject ('/', '\', '#', '?'). Sanitized for the KEY only (worst case
        /// two apps share an episode row — tolerable); the raw values live in columns and the
        /// mapper reads them, never the RowKey.
        /// </summary>
        internal static string BuildAppVersionRegressionRowKey(string appName, string version)
        {
            var app = SanitizeTableKey((appName ?? string.Empty).Trim().ToLowerInvariant());
            var ver = SanitizeTableKey((version ?? string.Empty).Trim().ToLowerInvariant());
            return $"{AppVersionRegressionRowKeyPrefix}{app}|{ver}";
        }

        /// <summary>Replace characters not allowed in Azure Table Storage keys (/, \, #, ?).</summary>
        private static string SanitizeTableKey(string key)
        {
            return key
                .Replace("/", "_")
                .Replace("\\", "_")
                .Replace("#", "_")
                .Replace("?", "_");
        }

        // internal static: entity builder + mapper are pinned by round-trip unit tests
        // (table-storage-serialization rule — every property must survive Store→Map).
        internal static TableEntity BuildAppVersionRegressionEntity(string tenantId, AppVersionRegressionAlert alert)
        {
            return new TableEntity(
                tenantId.ToLowerInvariant(), BuildAppVersionRegressionRowKey(alert.AppName, alert.CurrentVersion))
            {
                ["TenantId"] = tenantId,
                ["AppName"] = alert.AppName,
                ["CurrentVersion"] = alert.CurrentVersion,
                ["PreviousVersion"] = alert.PreviousVersion ?? string.Empty,
                ["CurrentMedianSeconds"] = alert.CurrentMedianSeconds,
                ["PreviousMedianSeconds"] = alert.PreviousMedianSeconds,
                ["CurrentMeasuredCount"] = alert.CurrentMeasuredCount,
                ["PreviousMeasuredCount"] = alert.PreviousMeasuredCount,
                ["Lift"] = alert.Lift,
                ["FirstNotifiedAt"] = alert.FirstNotifiedAt,
                ["LastEvaluatedAt"] = alert.LastEvaluatedAt,
            };
        }

        internal static AppVersionRegressionAlert MapToAppVersionRegressionAlert(TableEntity entity)
        {
            return new AppVersionRegressionAlert
            {
                TenantId = entity.GetString("TenantId") ?? entity.PartitionKey,
                AppName = entity.GetString("AppName") ?? string.Empty,
                CurrentVersion = entity.GetString("CurrentVersion") ?? string.Empty,
                PreviousVersion = entity.GetString("PreviousVersion") ?? string.Empty,
                CurrentMedianSeconds = entity.GetInt32("CurrentMedianSeconds") ?? 0,
                PreviousMedianSeconds = entity.GetInt32("PreviousMedianSeconds") ?? 0,
                CurrentMeasuredCount = entity.GetInt32("CurrentMeasuredCount") ?? 0,
                PreviousMeasuredCount = entity.GetInt32("PreviousMeasuredCount") ?? 0,
                Lift = entity.GetDouble("Lift") ?? 0,
                FirstNotifiedAt = entity.GetDateTimeOffset("FirstNotifiedAt")?.UtcDateTime ?? DateTime.MinValue,
                LastEvaluatedAt = entity.GetDateTimeOffset("LastEvaluatedAt")?.UtcDateTime ?? DateTime.MinValue,
            };
        }
    }
}
