using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    public partial class TableStorageService
    {
        // ===== SESSION INDEX HELPERS =====

        /// <summary>
        /// Computes the inverted-tick RowKey for the SessionsIndex table.
        /// Newest sessions have the smallest RowKey, so Azure Table Storage returns them first.
        /// Format: "{invertedTicks:D19}_{sessionId}" to guarantee uniqueness.
        /// </summary>
        private static string ComputeIndexRowKey(DateTime startedAt, string sessionId)
            => $"{(DateTime.MaxValue.Ticks - startedAt.Ticks):D19}_{sessionId}";

        /// <summary>
        /// Computes the SessionsIndex RowKey upper bound (D19 inverted-tick prefix) for a
        /// "last N days" window. Use with `RowKey lt '{prefix}'` — sessions newer than the
        /// cutoff have smaller inverted-tick values than this prefix.
        /// </summary>
        internal static string ComputeCutoffRowKeyPrefix(int days)
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-days);
            return $"{(DateTime.MaxValue.Ticks - cutoffDate.Ticks):D19}";
        }

        /// <summary>
        /// Extracts the SessionId from an index RowKey ("{invertedTicks}_{sessionId}").
        /// </summary>
        private static string ExtractSessionIdFromIndexRowKey(string indexRowKey)
        {
            var underscoreIndex = indexRowKey.IndexOf('_');
            return underscoreIndex >= 0 ? indexRowKey.Substring(underscoreIndex + 1) : indexRowKey;
        }

        /// <summary>
        /// Upserts a session entry in the SessionsIndex table and stores the IndexRowKey
        /// back in the Sessions entity. Copies all SessionSummary-relevant fields so that
        /// listing queries only need to hit the index table.
        /// </summary>
        private async Task UpsertSessionIndexAsync(TableEntity sessionEntity, DateTime startedAt)
        {
            try
            {
                var tenantId = sessionEntity.PartitionKey;
                var sessionId = sessionEntity.RowKey;
                var indexRowKey = ComputeIndexRowKey(startedAt, sessionId);

                var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);

                // Check if there's an existing index entry with a different RowKey (StartedAt changed)
                var existingIndexRowKey = sessionEntity.GetString("IndexRowKey");
                if (!string.IsNullOrEmpty(existingIndexRowKey) && existingIndexRowKey != indexRowKey)
                {
                    // StartedAt shifted — delete old index entry
                    try
                    {
                        await indexTableClient.DeleteEntityAsync(tenantId, existingIndexRowKey);
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404)
                    {
                        // Old index entry already gone — fine
                    }
                }

                // Build index entity with all SessionSummary fields
                var indexEntity = new TableEntity(tenantId, indexRowKey)
                {
                    ["SessionId"] = sessionId,
                    ["SerialNumber"] = sessionEntity.GetString("SerialNumber") ?? string.Empty,
                    ["DeviceName"] = sessionEntity.GetString("DeviceName") ?? string.Empty,
                    ["Manufacturer"] = sessionEntity.GetString("Manufacturer") ?? string.Empty,
                    ["Model"] = sessionEntity.GetString("Model") ?? string.Empty,
                    ["StartedAt"] = EnsureUtc(startedAt),
                    ["Status"] = sessionEntity.GetString("Status") ?? "InProgress",
                    ["CurrentPhase"] = sessionEntity.GetInt32("CurrentPhase") ?? 0,
                    ["CurrentPhaseDetail"] = sessionEntity.GetString("CurrentPhaseDetail") ?? string.Empty,
                    ["EventCount"] = sessionEntity.GetInt32("EventCount") ?? 0,
                    ["EnrollmentType"] = sessionEntity.GetString("EnrollmentType") ?? "v1",
                    ["IsPreProvisioned"] = sessionEntity.GetBoolean("IsPreProvisioned") ?? false,
                    ["IsHybridJoin"] = sessionEntity.GetBoolean("IsHybridJoin") ?? false,
                    ["IsUserDriven"] = sessionEntity.GetBoolean("IsUserDriven") ?? false,
                    ["AgentVersion"] = sessionEntity.GetString("AgentVersion") ?? string.Empty,
                    ["OsName"] = sessionEntity.GetString("OsName") ?? string.Empty,
                    ["OsBuild"] = sessionEntity.GetString("OsBuild") ?? string.Empty,
                    ["OsDisplayVersion"] = sessionEntity.GetString("OsDisplayVersion") ?? string.Empty,
                    ["OsEdition"] = sessionEntity.GetString("OsEdition") ?? string.Empty,
                    ["OsLanguage"] = sessionEntity.GetString("OsLanguage") ?? string.Empty,
                    ["GeoCountry"] = sessionEntity.GetString("GeoCountry") ?? string.Empty,
                    ["GeoRegion"] = sessionEntity.GetString("GeoRegion") ?? string.Empty,
                    ["GeoCity"] = sessionEntity.GetString("GeoCity") ?? string.Empty,
                    ["GeoLoc"] = sessionEntity.GetString("GeoLoc") ?? string.Empty
                };

                // Copy nullable fields
                var completedAt = sessionEntity.GetDateTimeOffset("CompletedAt")?.UtcDateTime;
                if (completedAt.HasValue)
                    indexEntity["CompletedAt"] = EnsureUtc(completedAt.Value);

                var failureReason = sessionEntity.GetString("FailureReason");
                if (!string.IsNullOrEmpty(failureReason))
                    indexEntity["FailureReason"] = failureReason;

                var durationSeconds = sessionEntity.GetInt32("DurationSeconds");
                if (durationSeconds.HasValue)
                    indexEntity["DurationSeconds"] = durationSeconds.Value;

                var diagnosticsBlobName = sessionEntity.GetString("DiagnosticsBlobName");
                if (!string.IsNullOrEmpty(diagnosticsBlobName))
                    indexEntity["DiagnosticsBlobName"] = diagnosticsBlobName;

                var lastEventAt = sessionEntity.GetDateTimeOffset("LastEventAt")?.UtcDateTime;
                if (lastEventAt.HasValue)
                    indexEntity["LastEventAt"] = EnsureUtc(lastEventAt.Value);

                var resumedAt = sessionEntity.GetDateTimeOffset("ResumedAt")?.UtcDateTime;
                if (resumedAt.HasValue)
                    indexEntity["ResumedAt"] = EnsureUtc(resumedAt.Value);

                await indexTableClient.UpsertEntityAsync(indexEntity);

                // Store IndexRowKey back in the Sessions entity so Merge-mode updates can find it
                var sessionsTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var indexRefUpdate = new TableEntity(tenantId, sessionId)
                {
                    ["IndexRowKey"] = indexRowKey
                };
                await sessionsTableClient.UpdateEntityAsync(indexRefUpdate, ETag.All, TableUpdateMode.Merge);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upsert session index for {SessionId}", sessionEntity.RowKey);
            }
        }

        /// <summary>
        /// Merges specific changed fields into the SessionsIndex entry.
        /// Used by Merge-mode update methods to keep the index in sync without full entity rewrite.
        /// </summary>
        private async Task MergeSessionIndexAsync(string tenantId, string indexRowKey, TableEntity fieldsToMerge)
        {
            if (string.IsNullOrEmpty(indexRowKey))
                return;

            try
            {
                var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
                var indexUpdate = new TableEntity(tenantId, indexRowKey);

                foreach (var kvp in fieldsToMerge)
                {
                    if (kvp.Key == "odata.etag" || kvp.Key == "PartitionKey" || kvp.Key == "RowKey" || kvp.Key == "Timestamp")
                        continue;
                    indexUpdate[kvp.Key] = kvp.Value;
                }

                await indexTableClient.UpdateEntityAsync(indexUpdate, ETag.All, TableUpdateMode.Merge);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to merge session index for {TenantId}/{IndexRowKey}", tenantId, indexRowKey);
            }
        }

        /// <summary>
        /// Deletes a session entry from the SessionsIndex table.
        /// </summary>
        private async Task DeleteSessionIndexAsync(string tenantId, string? indexRowKey)
        {
            if (string.IsNullOrEmpty(indexRowKey))
                return;

            try
            {
                var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
                await indexTableClient.DeleteEntityAsync(tenantId, indexRowKey);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Already gone — fine
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete session index for {TenantId}/{IndexRowKey}", tenantId, indexRowKey);
            }
        }

        /// <summary>
        /// Maps an index entity (from SessionsIndex table) to SessionSummary.
        /// The key difference from MapToSessionSummary: SessionId comes from a stored property
        /// instead of RowKey (which contains the inverted-tick key in the index).
        /// </summary>
        private SessionSummary MapIndexEntityToSessionSummary(TableEntity entity)
        {
            var startedAt = SafeGetDateTime(entity, "StartedAt") ?? DateTime.UtcNow;
            var completedAt = SafeGetDateTime(entity, "CompletedAt");

            var statusString = entity.GetString("Status") ?? "InProgress";
            if (!Enum.TryParse<SessionStatus>(statusString, ignoreCase: true, out var status))
            {
                status = SessionStatus.Unknown;
            }

            return new SessionSummary
            {
                SessionId = entity.GetString("SessionId") ?? ExtractSessionIdFromIndexRowKey(entity.RowKey),
                TenantId = entity.PartitionKey,
                SerialNumber = entity.GetString("SerialNumber") ?? string.Empty,
                DeviceName = entity.GetString("DeviceName") ?? string.Empty,
                Manufacturer = entity.GetString("Manufacturer") ?? string.Empty,
                Model = entity.GetString("Model") ?? string.Empty,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                CurrentPhase = SafeGetInt32(entity, "CurrentPhase") ?? 0,
                CurrentPhaseDetail = entity.GetString("CurrentPhaseDetail") ?? string.Empty,
                Status = status,
                FailureReason = entity.GetString("FailureReason") ?? string.Empty,
                FailureSource = entity.GetString("FailureSource") ?? string.Empty,
                AdminMarkedAction = entity.GetString("AdminMarkedAction"),
                PendingActionsJson = entity.GetString("PendingActionsJson") ?? string.Empty,
                PendingActionsQueuedAt = SafeGetDateTime(entity, "PendingActionsQueuedAt"),
                EventCount = SafeGetInt32(entity, "EventCount") ?? 0,
                DurationSeconds = ComputeEffectiveDuration(entity, status, startedAt, completedAt),
                EnrollmentType = entity.GetString("EnrollmentType") ?? "v1",
                DiagnosticsBlobName = entity.GetString("DiagnosticsBlobName"),
                LastEventAt = SafeGetDateTime(entity, "LastEventAt"),
                IsPreProvisioned = entity.GetBoolean("IsPreProvisioned") ?? false,
                IsHybridJoin = entity.GetBoolean("IsHybridJoin") ?? false,
                ResumedAt = SafeGetDateTime(entity, "ResumedAt"),
                StalledAt = SafeGetDateTime(entity, "StalledAt"),
                OsName = entity.GetString("OsName") ?? string.Empty,
                OsBuild = entity.GetString("OsBuild") ?? string.Empty,
                OsDisplayVersion = entity.GetString("OsDisplayVersion") ?? string.Empty,
                OsEdition = entity.GetString("OsEdition") ?? string.Empty,
                OsLanguage = entity.GetString("OsLanguage") ?? string.Empty,
                IsUserDriven = entity.GetBoolean("IsUserDriven") ?? false,
                AgentVersion = entity.GetString("AgentVersion") ?? string.Empty,
                ImeAgentVersion = entity.GetString("ImeAgentVersion") ?? string.Empty,
                GeoCountry = entity.GetString("GeoCountry") ?? string.Empty,
                GeoRegion = entity.GetString("GeoRegion") ?? string.Empty,
                GeoCity = entity.GetString("GeoCity") ?? string.Empty,
                GeoLoc = entity.GetString("GeoLoc") ?? string.Empty,
                PlatformScriptCount = SafeGetInt32(entity, "PlatformScriptCount") ?? 0,
                RemediationScriptCount = SafeGetInt32(entity, "RemediationScriptCount") ?? 0,
                ExcessiveEventsAlerted = entity.GetBoolean("ExcessiveEventsAlerted") ?? false,
                FailureSnapshotJson = entity.GetString("FailureSnapshotJson") ?? string.Empty
            };
        }

        // ===== SESSION MANAGEMENT METHODS =====

        /// <summary>
        /// Ensures a DateTime value has DateTimeKind.Utc before writing to Azure Table Storage.
        /// The Azure Data Tables SDK throws NotSupportedException for DateTime values with Kind=Local.
        /// This guards against timestamps that lost their UTC kind during JSON round-trips (e.g. agent spool).
        /// </summary>
        private static DateTime EnsureUtc(DateTime dt) => dt.Kind switch
        {
            DateTimeKind.Utc => dt,
            DateTimeKind.Local => dt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(dt, DateTimeKind.Utc) // Unspecified: assume UTC
        };

        /// <summary>
        /// Azure Table Storage limits string properties to 64KB (32K UTF-16 chars).
        /// Truncate to 30,000 chars to leave buffer for multi-byte characters.
        /// </summary>
        private const int MaxTableStorageStringLength = 30000;

        private string TruncateForTableStorage(string value, string propertyName, string eventId)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxTableStorageStringLength)
                return value;

            _logger.LogWarning("Truncating {PropertyName} for event {EventId}: {OriginalLength} chars -> {MaxLength} chars",
                propertyName, eventId, value.Length, MaxTableStorageStringLength);

            return value.Substring(0, MaxTableStorageStringLength) + "... [truncated]";
        }

        /// <summary>
        /// Stores a session registration
        /// </summary>
        public async Task<bool> StoreSessionAsync(SessionRegistration registration)
        {
            SecurityValidator.EnsureValidGuid(registration.TenantId, "TenantId");
            SecurityValidator.EnsureValidGuid(registration.SessionId, "SessionId");

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // If the agent restarts with the same session ID, preserve timeline/progress fields
                // from the existing session row instead of resetting them to "fresh start".
                DateTime startedAt = registration.StartedAt;
                int currentPhase = (int)EnrollmentPhase.Start;
                string status = SessionStatus.InProgress.ToString();
                int eventCount = 0;
                DateTime? completedAt = null;
                string failureReason = string.Empty;
                bool isPreProvisioned = registration.IsPreProvisioned;
                bool isHybridJoin = registration.IsHybridJoin;
                DateTime? lastEventAt = null;
                int? durationSeconds = null;
                string? diagnosticsBlobName = null;
                DateTime? resumedAt = null;
                DateTime? stalledAt = null;
                string geoCountry = string.Empty;
                string geoRegion = string.Empty;
                string geoCity = string.Empty;
                string geoLoc = string.Empty;
                string? existingIndexRowKey = null;

                try
                {
                    var existing = await tableClient.GetEntityAsync<TableEntity>(registration.TenantId, registration.SessionId);
                    var existingEntity = existing.Value;

                    var existingStartedAt = existingEntity.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                    if (existingStartedAt.HasValue && existingStartedAt.Value < startedAt)
                        startedAt = existingStartedAt.Value;

                    currentPhase = existingEntity.GetInt32("CurrentPhase") ?? currentPhase;
                    status = existingEntity.GetString("Status") ?? status;
                    eventCount = existingEntity.GetInt32("EventCount") ?? eventCount;
                    completedAt = existingEntity.GetDateTimeOffset("CompletedAt")?.UtcDateTime;
                    failureReason = existingEntity.GetString("FailureReason") ?? string.Empty;

                    // Preserve fields set by Merge-mode updates (SetSessionPreProvisionedAsync,
                    // UpdateSessionStatusAsync, UpdateSessionDiagnosticsBlobAsync) that would
                    // otherwise be lost by the UpsertEntity (Replace) below.
                    isPreProvisioned = existingEntity.GetBoolean("IsPreProvisioned") ?? isPreProvisioned;
                    isHybridJoin = existingEntity.GetBoolean("IsHybridJoin") ?? isHybridJoin;
                    lastEventAt = existingEntity.GetDateTimeOffset("LastEventAt")?.UtcDateTime;
                    durationSeconds = existingEntity.GetInt32("DurationSeconds");
                    diagnosticsBlobName = existingEntity.GetString("DiagnosticsBlobName");
                    resumedAt = existingEntity.GetDateTimeOffset("ResumedAt")?.UtcDateTime;
                    stalledAt = existingEntity.GetDateTimeOffset("StalledAt")?.UtcDateTime;
                    geoCountry = existingEntity.GetString("GeoCountry") ?? string.Empty;
                    geoRegion = existingEntity.GetString("GeoRegion") ?? string.Empty;
                    geoCity = existingEntity.GetString("GeoCity") ?? string.Empty;
                    geoLoc = existingEntity.GetString("GeoLoc") ?? string.Empty;
                    existingIndexRowKey = existingEntity.GetString("IndexRowKey");

                    // Guard: never regress a terminal status (Succeeded/Failed) back to InProgress.
                    // StoreSessionAsync uses UpsertEntity (Replace mode) which overwrites all fields.
                    // If UpdateSessionStatusAsync set Status=Succeeded between this read and the write
                    // below (TOCTOU race), the Replace would silently revert the terminal status.
                    // Terminal states are authoritative and irreversible — preserve them unconditionally.
                    if (status == SessionStatus.Succeeded.ToString() || status == SessionStatus.Failed.ToString())
                    {
                        _logger.LogInformation($"Session {registration.SessionId} already in terminal state '{status}', preserving during re-registration");
                    }
                    // WhiteGlove resumption: if the existing session was in Pending state,
                    // this re-registration means the user has received the device and booted it.
                    // Transition back to InProgress for Part 2 of enrollment.
                    else if (status == SessionStatus.Pending.ToString())
                    {
                        _logger.LogInformation($"Session {registration.SessionId} resuming from Pending (WhiteGlove Part 2)");
                        status = SessionStatus.InProgress.ToString();
                        // Store ResumedAt as fallback if whiteglove_resumed event hasn't set it yet
                        if (!resumedAt.HasValue)
                            resumedAt = registration.StartedAt;
                    }
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // New session row - use defaults above.
                }

                // If events were ingested before session registration succeeded, align StartedAt
                // with the earliest event we already have for this session.
                var earliestEventTimestamp = await GetEarliestSessionEventTimestampAsync(registration.TenantId, registration.SessionId);
                if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < startedAt)
                {
                    startedAt = earliestEventTimestamp.Value;
                }

                var entity = new TableEntity(registration.TenantId, registration.SessionId)
                {
                    ["SerialNumber"] = registration.SerialNumber ?? string.Empty,
                    ["Manufacturer"] = registration.Manufacturer ?? string.Empty,
                    ["Model"] = registration.Model ?? string.Empty,
                    ["DeviceName"] = registration.DeviceName ?? string.Empty,
                    ["OsName"] = registration.OsName ?? string.Empty,
                    ["OsBuild"] = registration.OsBuild ?? string.Empty,
                    ["OsDisplayVersion"] = registration.OsDisplayVersion ?? string.Empty,
                    ["OsEdition"] = registration.OsEdition ?? string.Empty,
                    ["OsLanguage"] = registration.OsLanguage ?? string.Empty,
                    ["IsUserDriven"] = registration.IsUserDriven,
                    ["IsPreProvisioned"] = isPreProvisioned,
                    ["IsHybridJoin"] = isHybridJoin,
                    ["StartedAt"] = EnsureUtc(startedAt),
                    ["AgentVersion"] = registration.AgentVersion ?? string.Empty,
                    ["EnrollmentType"] = registration.EnrollmentType ?? "v1",
                    ["CurrentPhase"] = currentPhase,
                    ["Status"] = status,
                    ["EventCount"] = eventCount
                };

                if (completedAt.HasValue)
                    entity["CompletedAt"] = EnsureUtc(completedAt.Value);

                if (!string.IsNullOrWhiteSpace(failureReason))
                    entity["FailureReason"] = failureReason;

                if (lastEventAt.HasValue)
                    entity["LastEventAt"] = EnsureUtc(lastEventAt.Value);

                if (durationSeconds.HasValue)
                    entity["DurationSeconds"] = durationSeconds.Value;

                if (!string.IsNullOrWhiteSpace(diagnosticsBlobName))
                    entity["DiagnosticsBlobName"] = diagnosticsBlobName;

                if (resumedAt.HasValue)
                    entity["ResumedAt"] = EnsureUtc(resumedAt.Value);

                if (stalledAt.HasValue)
                    entity["StalledAt"] = EnsureUtc(stalledAt.Value);

                if (!string.IsNullOrEmpty(geoCountry))
                    entity["GeoCountry"] = geoCountry;
                if (!string.IsNullOrEmpty(geoRegion))
                    entity["GeoRegion"] = geoRegion;
                if (!string.IsNullOrEmpty(geoCity))
                    entity["GeoCity"] = geoCity;
                if (!string.IsNullOrEmpty(geoLoc))
                    entity["GeoLoc"] = geoLoc;

                // Preserve IndexRowKey through the Replace so that:
                // 1. Concurrent UpdateSessionStatusAsync calls can find it (prevents index stale-status)
                // 2. UpsertSessionIndexAsync can detect and delete old index entries when startedAt shifts
                if (!string.IsNullOrEmpty(existingIndexRowKey))
                    entity["IndexRowKey"] = existingIndexRowKey;

                await tableClient.UpsertEntityAsync(entity);

                // Dual-write: upsert into SessionsIndex for time-sorted listing
                await UpsertSessionIndexAsync(entity, startedAt);

                _logger.LogInformation($"Stored session {registration.SessionId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store session {registration.SessionId}");
                return false;
            }
        }

        // ===== EVENT MANAGEMENT METHODS =====

        /// <summary>
        /// Stores an event
        /// </summary>
        public async Task<bool> StoreEventAsync(EnrollmentEvent evt)
        {
            SecurityValidator.EnsureValidGuid(evt.TenantId, "TenantId");
            SecurityValidator.EnsureValidGuid(evt.SessionId, "SessionId");

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);

                // PartitionKey: TenantId_SessionId for efficient querying
                // RowKey: Timestamp_Sequence for ordering
                var partitionKey = $"{evt.TenantId}_{evt.SessionId}";
                var rowKey = $"{evt.Timestamp:yyyyMMddHHmmssfff}_{evt.Sequence:D10}";

                var entity = new TableEntity(partitionKey, rowKey)
                {
                    ["EventId"] = evt.EventId,
                    ["SessionId"] = evt.SessionId,
                    ["TenantId"] = evt.TenantId,
                    ["Timestamp"] = evt.Timestamp,
                    ["EventType"] = evt.EventType ?? string.Empty,
                    ["Severity"] = (int)evt.Severity,
                    ["Source"] = evt.Source ?? string.Empty,
                    ["Phase"] = (int)evt.Phase,
                    ["Message"] = TruncateForTableStorage(evt.Message ?? string.Empty, "Message", evt.EventId),
                    ["Sequence"] = evt.Sequence,
                    ["DataJson"] = TruncateForTableStorage(
                        evt.Data != null && evt.Data.Count > 0
                            ? JsonConvert.SerializeObject(evt.Data)
                            : string.Empty,
                        "DataJson", evt.EventId),
                    ["ReceivedAt"] = evt.ReceivedAt,
                    ["TimestampClamped"] = evt.TimestampClamped
                };

                if (evt.OriginalTimestamp.HasValue)
                    entity["OriginalTimestamp"] = EnsureUtc(evt.OriginalTimestamp.Value);

                // Codex follow-up #3: forward-link columns. Null when absent on the agent
                // payload (pre-#3 events, or events emitted outside the reducer pipeline);
                // skip the entity setter in that case so the column stays unset.
                if (evt.CausedByTransitionStepIndex.HasValue)
                    entity["CausedByTransitionStepIndex"] = evt.CausedByTransitionStepIndex.Value;
                if (evt.CausedBySignalOrdinal.HasValue)
                    entity["CausedBySignalOrdinal"] = evt.CausedBySignalOrdinal.Value;

                await tableClient.UpsertEntityAsync(entity);
                _logger.LogDebug($"Stored event {evt.EventId}");

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to store event {evt.EventId}");
                return false;
            }
        }

        /// <summary>
        /// Stores multiple events as batch transactions (Entity Group Transactions).
        /// All events must share the same PartitionKey (TenantId_SessionId).
        /// Azure Table Storage allows max 100 entities per transaction.
        /// Falls back to individual writes if a batch fails.
        /// </summary>
        /// <returns>List of successfully stored events</returns>
        public async Task<List<EnrollmentEvent>> StoreEventsBatchAsync(List<EnrollmentEvent> events)
        {
            if (events == null || events.Count == 0)
                return new List<EnrollmentEvent>();

            // Validate all events upfront
            foreach (var evt in events)
            {
                SecurityValidator.EnsureValidGuid(evt.TenantId, "TenantId");
                SecurityValidator.EnsureValidGuid(evt.SessionId, "SessionId");
            }

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
            var storedEvents = new List<EnrollmentEvent>();

            // Group by PartitionKey (should be the same for all events in a request, but be safe)
            var groups = events.GroupBy(e => $"{e.TenantId}_{e.SessionId}");

            foreach (var group in groups)
            {
                // Chunk into batches of 100 (Azure Table Storage limit)
                var chunks = group.Select((evt, index) => new { evt, index })
                    .GroupBy(x => x.index / 100)
                    .Select(g => g.Select(x => x.evt).ToList());

                foreach (var chunk in chunks)
                {
                    try
                    {
                        var actions = chunk.Select(evt =>
                        {
                            var partitionKey = $"{evt.TenantId}_{evt.SessionId}";
                            var rowKey = $"{evt.Timestamp:yyyyMMddHHmmssfff}_{evt.Sequence:D10}";

                            var entity = new TableEntity(partitionKey, rowKey)
                            {
                                ["EventId"] = evt.EventId,
                                ["SessionId"] = evt.SessionId,
                                ["TenantId"] = evt.TenantId,
                                ["Timestamp"] = evt.Timestamp,
                                ["EventType"] = evt.EventType ?? string.Empty,
                                ["Severity"] = (int)evt.Severity,
                                ["Source"] = evt.Source ?? string.Empty,
                                ["Phase"] = (int)evt.Phase,
                                ["Message"] = TruncateForTableStorage(evt.Message ?? string.Empty, "Message", evt.EventId),
                                ["Sequence"] = evt.Sequence,
                                ["DataJson"] = TruncateForTableStorage(
                                    evt.Data != null && evt.Data.Count > 0
                                        ? JsonConvert.SerializeObject(evt.Data)
                                        : string.Empty,
                                    "DataJson", evt.EventId),
                                ["ReceivedAt"] = evt.ReceivedAt,
                                ["TimestampClamped"] = evt.TimestampClamped
                            };

                            if (evt.OriginalTimestamp.HasValue)
                                entity["OriginalTimestamp"] = EnsureUtc(evt.OriginalTimestamp.Value);

                            // Codex follow-up #3: forward-link columns (see StoreEventAsync).
                            if (evt.CausedByTransitionStepIndex.HasValue)
                                entity["CausedByTransitionStepIndex"] = evt.CausedByTransitionStepIndex.Value;
                            if (evt.CausedBySignalOrdinal.HasValue)
                                entity["CausedBySignalOrdinal"] = evt.CausedBySignalOrdinal.Value;

                            return new TableTransactionAction(TableTransactionActionType.UpsertReplace, entity);
                        }).ToList();

                        await tableClient.SubmitTransactionAsync(actions);
                        storedEvents.AddRange(chunk);
                        _logger.LogDebug($"Batch stored {chunk.Count} events for partition {group.Key}");
                    }
                    catch (Exception ex)
                    {
                        // Batch failed - fall back to individual writes for this chunk
                        _logger.LogWarning(ex, $"Batch write failed for {chunk.Count} events, falling back to individual writes");

                        foreach (var evt in chunk)
                        {
                            if (await StoreEventAsync(evt))
                            {
                                storedEvents.Add(evt);
                            }
                        }
                    }
                }
            }

            // Update EventSessionIndex (side-table for orphan detection)
            // Fire-and-forget upsert — one write per ingest call, no read needed
            if (storedEvents.Count > 0)
            {
                try
                {
                    var firstEvent = storedEvents[0];
                    var indexClient = _tableServiceClient.GetTableClient(Constants.TableNames.EventSessionIndex);
                    var indexEntity = new TableEntity(firstEvent.TenantId, firstEvent.SessionId)
                    {
                        ["LastIngestAt"] = DateTimeOffset.UtcNow,
                        ["EventCount"] = storedEvents.Count // Will be overwritten on each upsert (not cumulative — acceptable for orphan detection)
                    };
                    await indexClient.UpsertEntityAsync(indexEntity, TableUpdateMode.Merge);
                }
                catch (Exception ex)
                {
                    // Non-critical — don't fail event ingestion if index update fails
                    _logger.LogWarning(ex, "Failed to update EventSessionIndex");
                }
            }

            return storedEvents;
        }

        /// <summary>
        /// Internal SessionsIndex scan with custom RowKey-based cursor. Used by both
        /// <see cref="GetSessionsAsync(string, int?)"/> (drain loop) and
        /// <see cref="GetSessionsPageAsync(string, int?, int, string?)"/> (single page).
        /// </summary>
        private async Task<(List<SessionSummary> Sessions, bool HasMore, string? NextCursor)> FetchSessionsPageInternalAsync(
            string tenantId, int maxResults, string? cursor, int? days)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            try
            {
                var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);

                // Build filter: PartitionKey scope + optional cursor for "Load More"
                var filter = $"PartitionKey eq '{tenantId}'";
                if (!string.IsNullOrEmpty(cursor))
                {
                    filter += $" and RowKey gt '{cursor}'";
                }

                // When days is specified, add RowKey upper bound to limit scan to date range.
                // Inverted-tick ordering: newer sessions have smaller RowKey values.
                // Sessions older than cutoff have RowKey >= cutoffRowKeyPrefix.
                if (days.HasValue)
                {
                    var cutoffRowKeyPrefix = ComputeCutoffRowKeyPrefix(days.Value);
                    filter += $" and RowKey lt '{cutoffRowKeyPrefix}'";
                }

                // Sentinel: fetch one extra row to detect HasMore in a single query.
                // The `days` cutoff is applied as a RowKey filter above — pagination
                // mechanics are independent of the date window so a tenant with more
                // than `maxResults` matching sessions remains fully reachable across
                // multiple calls (was: silently capped at 10k pre-fix).
                var query = indexTableClient.QueryAsync<TableEntity>(
                    filter: filter,
                    maxPerPage: Math.Min(maxResults + 1, 1000)
                );

                var sessions = new List<SessionSummary>();
                await foreach (var entity in query)
                {
                    sessions.Add(MapIndexEntityToSessionSummary(entity));
                    if (sessions.Count > maxResults) break;
                }

                // If SessionsIndex is empty and no cursor, fall back to Sessions table (pre-migration).
                if (sessions.Count == 0 && string.IsNullOrEmpty(cursor))
                {
                    var fallback = await FetchSessionsFromPrimaryTableInternalAsync(tenantId, maxResults, days);
                    return (fallback.Sessions, fallback.HasMore, NextCursor: null);
                }

                var hasMore = sessions.Count > maxResults;
                if (hasMore)
                    sessions.RemoveAt(sessions.Count - 1);

                // Cursor = RowKey of the last returned item (opaque to caller).
                string? nextCursor = null;
                if (hasMore && sessions.Count > 0)
                {
                    var lastSession = sessions[sessions.Count - 1];
                    nextCursor = ComputeIndexRowKey(lastSession.StartedAt, lastSession.SessionId);
                }

                return (sessions, hasMore, nextCursor);
            }
            catch (Exception ex)
            {
                _logger.LogError("Failed to get sessions for tenant {TenantId}: {ExType}: {ExMessage}\n{StackTrace}",
                    tenantId, ex.GetType().Name, ex.Message, ex.StackTrace);
                return (new List<SessionSummary>(), false, null);
            }
        }

        /// <summary>
        /// Drains all sessions for a tenant — newest-first, no row cap. Internally
        /// loops <see cref="GetSessionsPageAsync"/> until the next-token is null.
        /// </summary>
        public async Task<List<SessionSummary>> GetSessionsAsync(string tenantId, int? days = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            var all = new List<SessionSummary>();
            string? token = null;
            do
            {
                var page = await GetSessionsPageAsync(tenantId, days, pageSize: 1000, continuation: token);
                all.AddRange(page.Items);
                token = page.NextRawToken;
            } while (!string.IsNullOrEmpty(token));
            return all;
        }

        /// <summary>
        /// Reads a single page of sessions. Builds on the internal RowKey-cursor
        /// scan so callers don't see the cursor mechanics — only the
        /// <see cref="RawPage{T}"/> envelope with the opaque <c>NextRawToken</c>.
        /// </summary>
        public async Task<RawPage<SessionSummary>> GetSessionsPageAsync(
            string tenantId, int? days, int pageSize, string? continuation)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

            var page = await FetchSessionsPageInternalAsync(tenantId, pageSize, continuation, days);
            return new RawPage<SessionSummary>(page.Sessions, page.HasMore ? page.NextCursor : null);
        }

        /// <summary>
        /// Cross-tenant paged variant of <see cref="GetAllSessionsAsync"/>.
        /// <paramref name="tenantIdFilter"/> optionally restricts to one tenant.
        /// </summary>
        public async Task<RawPage<SessionSummary>> GetAllSessionsPageAsync(
            string? tenantIdFilter, int? days, int pageSize, string? continuation)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

            // When tenantIdFilter is set and is a valid tenantId, route through
            // the per-tenant SessionsIndex scan — natively-ordered, cheaper.
            if (!string.IsNullOrEmpty(tenantIdFilter))
            {
                return await GetSessionsPageAsync(tenantIdFilter!, days, pageSize, continuation);
            }

            var page = await FetchAllSessionsPageInternalAsync(maxResults: pageSize, cursor: continuation, days: days);
            return new RawPage<SessionSummary>(page.Sessions, page.HasMore ? page.NextCursor : null);
        }

        /// <summary>
        /// Drains all sessions across all tenants — newest-first, no row cap.
        /// <paramref name="tenantIdFilter"/> optionally restricts to a single tenant
        /// (routed through the per-tenant index for efficiency).
        /// </summary>
        public async Task<List<SessionSummary>> GetAllSessionsAsync(string? tenantIdFilter = null, int? days = null)
        {
            if (!string.IsNullOrEmpty(tenantIdFilter))
            {
                return await GetSessionsAsync(tenantIdFilter!, days);
            }

            var all = new List<SessionSummary>();
            string? token = null;
            do
            {
                var page = await GetAllSessionsPageAsync(tenantIdFilter: null, days, pageSize: 1000, continuation: token);
                all.AddRange(page.Items);
                token = page.NextRawToken;
            } while (!string.IsNullOrEmpty(token));
            return all;
        }

        /// <summary>
        /// Aggregates the per-tenant dashboard stats in a single SessionsIndex drain.
        /// Computed server-side so the cards don't depend on whatever the client has
        /// paginated into memory.
        /// </summary>
        public async Task<SessionStats> GetSessionStatsAsync(string tenantId, int days)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));

            var sessions = await GetSessionsAsync(tenantId, days);
            return AggregateSessionStats(sessions, days);
        }

        /// <summary>
        /// Cross-tenant variant. Routes through the per-tenant index when
        /// <paramref name="tenantIdFilter"/> is set (cheaper scan).
        /// </summary>
        public async Task<SessionStats> GetAllSessionStatsAsync(string? tenantIdFilter, int days)
        {
            if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));

            var sessions = await GetAllSessionsAsync(tenantIdFilter, days);
            return AggregateSessionStats(sessions, days);
        }

        /// <summary>
        /// Pure aggregation pass over the windowed session list. Today-counters use
        /// UTC midnight as the boundary so every caller sees the same number regardless
        /// of browser timezone.
        /// </summary>
        internal static SessionStats AggregateSessionStats(IReadOnlyList<SessionSummary> sessions, int days)
        {
            var utcMidnight = DateTime.UtcNow.Date;

            int active = 0;
            int succeeded = 0;
            int failed = 0;
            long succeededDurationSeconds = 0;
            int succeededWithDurationCount = 0;
            int totalToday = 0;
            int failedToday = 0;

            foreach (var s in sessions)
            {
                switch (s.Status)
                {
                    case SessionStatus.InProgress:
                        // Card label "Currently enrolling" — literal match to the agent's
                        // in-flight status. Pending (WhiteGlove pre-prov complete, awaiting
                        // user) often lingers for days/weeks and isn't actively enrolling;
                        // Stalled (>60min stale) isn't either. Both belong on dedicated
                        // breakdowns/filters, not on a card that implies live activity.
                        active++;
                        break;
                    case SessionStatus.Succeeded:
                        succeeded++;
                        if (s.DurationSeconds is int d && d > 0)
                        {
                            succeededDurationSeconds += d;
                            succeededWithDurationCount++;
                        }
                        break;
                    case SessionStatus.Failed:
                        failed++;
                        break;
                }

                if (s.StartedAt >= utcMidnight)
                {
                    totalToday++;
                    if (s.Status == SessionStatus.Failed) failedToday++;
                }
            }

            int terminal = succeeded + failed;
            int successRatePct = terminal > 0
                ? (int)Math.Round((double)succeeded / terminal * 100.0)
                : 0;
            int avgDurationMinutes = succeededWithDurationCount > 0
                ? (int)Math.Round((double)succeededDurationSeconds / succeededWithDurationCount / 60.0)
                : 0;

            return new SessionStats
            {
                Days = days,
                ActiveCount = active,
                TotalLastNDays = sessions.Count,
                SucceededLastNDays = succeeded,
                FailedLastNDays = failed,
                SuccessRatePct = successRatePct,
                AvgDurationMinutes = avgDurationMinutes,
                TotalToday = totalToday,
                FailedToday = failedToday,
                ComputedAt = DateTime.UtcNow,
            };
        }

        /// <summary>
        /// Fallback: queries the Sessions table directly (pre-migration, before SessionsIndex is populated).
        /// </summary>
        private async Task<(List<SessionSummary> Sessions, bool HasMore)> FetchSessionsFromPrimaryTableInternalAsync(
            string tenantId, int maxResults, int? days)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
            var filter = $"PartitionKey eq '{tenantId}'";
            var safetyCap = days.HasValue ? 10000 : maxResults + 1;
            var query = tableClient.QueryAsync<TableEntity>(filter: filter, maxPerPage: Math.Min(safetyCap, 1000));

            var sessions = new List<SessionSummary>();
            await foreach (var entity in query)
            {
                sessions.Add(MapToSessionSummary(entity));
                if (sessions.Count >= safetyCap) break;
            }

            sessions = sessions.OrderByDescending(s => s.StartedAt).ToList();

            if (days.HasValue)
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-days.Value);
                sessions = sessions.Where(s => s.StartedAt >= cutoffDate).ToList();
                return (sessions, false);
            }

            var hasMore = sessions.Count > maxResults;
            if (hasMore)
                sessions.RemoveAt(sessions.Count - 1);

            return (sessions, hasMore);
        }

        /// <summary>
        /// Internal cross-tenant scan with custom merge-cursor. Used by both
        /// <see cref="GetAllSessionsAsync(string?, int?)"/> (drain) and
        /// <see cref="GetAllSessionsPageAsync"/> (single page).
        /// </summary>
        private async Task<(List<SessionSummary> Sessions, bool HasMore, string? NextCursor)> FetchAllSessionsPageInternalAsync(
            int maxResults, string? cursor, int? days)
        {
            try
            {
                var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);

                // Step 1: Get tenant IDs from TenantConfiguration (1 row per tenant, not a session scan).
                var configTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.TenantConfiguration);
                var tenantIds = new List<string>();
                await foreach (var entity in configTableClient.QueryAsync<TableEntity>(
                    select: new[] { "PartitionKey" }, maxPerPage: 1000))
                {
                    tenantIds.Add(entity.PartitionKey);
                }

                if (tenantIds.Count == 0 && string.IsNullOrEmpty(cursor))
                {
                    var fallback = await FetchAllSessionsFromPrimaryTableInternalAsync(maxResults, days);
                    return (fallback.Sessions, fallback.HasMore, NextCursor: null);
                }

                // Step 2: Per-tenant fan-out using RowKey ordering (inverted ticks → newest first).
                // Parse cursor into invertedTicks prefix for cross-tenant RowKey filtering.
                string? cursorRowKeyPrefix = null;
                string? cursorSessionId = null;
                if (!string.IsNullOrEmpty(cursor))
                {
                    var underscoreIdx = cursor.IndexOf('_');
                    if (underscoreIdx > 0)
                    {
                        cursorRowKeyPrefix = cursor.Substring(0, underscoreIdx);
                        cursorSessionId = cursor.Substring(underscoreIdx + 1);
                    }
                }

                // Per-tenant fetch budget: maxResults+1 is sufficient because the
                // outer merge-sort only ever surfaces top `maxResults` across all
                // tenants per page. The `days` cutoff is enforced via the RowKey
                // upper-bound filter below — pagination mechanics stay identical
                // whether or not a date window is present (was: 5k per tenant
                // hard cap when days set, silently truncating high-volume tenants).
                var fetchPerTenant = maxResults + 1;

                // Compute RowKey upper bound for date filtering
                string? cutoffRowKeyPrefix = days.HasValue
                    ? ComputeCutoffRowKeyPrefix(days.Value)
                    : null;

                // Parallel fan-out: query all tenants concurrently
                var tasks = tenantIds.Select(async tenantId =>
                {
                    var filter = $"PartitionKey eq '{tenantId}'";
                    if (cursorRowKeyPrefix != null)
                        filter += $" and RowKey ge '{cursorRowKeyPrefix}'";
                    if (cutoffRowKeyPrefix != null)
                        filter += $" and RowKey lt '{cutoffRowKeyPrefix}'";

                    var tenantSessions = new List<SessionSummary>();
                    await foreach (var entity in indexTableClient.QueryAsync<TableEntity>(
                        filter: filter, maxPerPage: Math.Min(fetchPerTenant, 1000)))
                    {
                        tenantSessions.Add(MapIndexEntityToSessionSummary(entity));
                        if (tenantSessions.Count >= fetchPerTenant) break;
                    }
                    return tenantSessions;
                });

                var results = await Task.WhenAll(tasks);
                var allSessions = results.SelectMany(s => s).ToList();

                // Step 3: Merge-sort across tenants by StartedAt descending
                allSessions = allSessions.OrderByDescending(s => s.StartedAt).ToList();

                // Step 4: Skip past the cursor session (handles cross-tenant duplicates at page boundary)
                if (cursorSessionId != null)
                {
                    var cursorIdx = allSessions.FindIndex(s => s.SessionId == cursorSessionId);
                    if (cursorIdx >= 0)
                        allSessions = allSessions.Skip(cursorIdx + 1).ToList();
                }

                var hasMore = allSessions.Count > maxResults;
                allSessions = allSessions.Take(maxResults).ToList();

                string? nextCursor = null;
                if (hasMore && allSessions.Count > 0)
                {
                    var lastSession = allSessions[allSessions.Count - 1];
                    nextCursor = ComputeIndexRowKey(lastSession.StartedAt, lastSession.SessionId);
                }

                return (allSessions, hasMore, nextCursor);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get all sessions");
                return (new List<SessionSummary>(), false, null);
            }
        }

        /// <summary>
        /// Fallback: queries the Sessions table directly for global admin (pre-migration).
        /// </summary>
        private async Task<(List<SessionSummary> Sessions, bool HasMore)> FetchAllSessionsFromPrimaryTableInternalAsync(
            int maxResults, int? days)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
            var safetyCap = days.HasValue ? 10000 : maxResults + 1;
            var query = tableClient.QueryAsync<TableEntity>(maxPerPage: Math.Min(safetyCap, 1000));

            var sessions = new List<SessionSummary>();
            await foreach (var entity in query)
            {
                sessions.Add(MapToSessionSummary(entity));
                if (sessions.Count >= safetyCap) break;
            }

            sessions = sessions.OrderByDescending(s => s.StartedAt).ToList();

            if (days.HasValue)
            {
                var cutoffDate = DateTime.UtcNow.AddDays(-days.Value);
                sessions = sessions.Where(s => s.StartedAt >= cutoffDate).ToList();
                return (sessions, false);
            }

            var hasMore = sessions.Count > maxResults;
            if (hasMore)
                sessions.RemoveAt(sessions.Count - 1);

            return (sessions, hasMore);
        }

        /// <summary>
        /// Gets a specific session
        /// </summary>
        public async Task<SessionSummary?> GetSessionAsync(string tenantId, string sessionId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                return MapToSessionSummary(entity.Value);
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get session {sessionId}");
                return null;
            }
        }

        /// <summary>
        /// Finds the tenantId for a session by scanning SessionsIndex.
        /// Used for Global Admin cross-tenant session lookup when tenantId is unknown.
        /// </summary>
        public async Task<string?> FindSessionTenantIdAsync(string sessionId)
        {
            try
            {
                var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
                await foreach (var entity in indexTableClient.QueryAsync<TableEntity>(
                    filter: $"SessionId eq '{sessionId}'",
                    maxPerPage: 1))
                {
                    return entity.PartitionKey;
                }
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to find tenant for session {sessionId}");
                return null;
            }
        }

        /// <summary>
        /// Updates the session status and current phase.
        /// Uses Merge mode to write only changed fields, reducing ETag conflicts under concurrency.
        /// The caller (IngestEventsFunction) provides earliestEventTimestamp from the current batch;
        /// no redundant Events-table scan is performed here.
        /// Event count is maintained atomically by IncrementSessionEventCountAsync and is not
        /// recounted here — avoiding an expensive full-partition scan on every status change.
        /// </summary>
        /// <param name="completedAt">
        /// Authoritative completion timestamp from the triggering event (e.g. CompletionEvent.Timestamp).
        /// When null — admin-marked terminals, rule-engine, maintenance auto-fail — the writer falls
        /// back to the session's LastEventAt and only to UtcNow as a last resort, so DurationSeconds
        /// reflects when the session went silent rather than when the operator clicked the button.
        /// </param>
        public async Task<bool> UpdateSessionStatusAsync(string tenantId, string sessionId, SessionStatus status, EnrollmentPhase? currentPhase = null, string? failureReason = null, DateTime? completedAt = null, DateTime? earliestEventTimestamp = null, DateTime? latestEventTimestamp = null, bool? isPreProvisioned = null, bool? isUserDriven = null, DateTime? resumedAt = null, DateTime? stalledAt = null, bool clearStalledAt = false, bool clearFailureReason = false, string? failureSource = null, string? adminMarkedAction = null, string? failureSnapshotJson = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            const int maxRetries = 5;
            int retryCount = 0;

            while (retryCount < maxRetries)
            {
                try
                {
                    var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                    // Read the existing entity to check idempotency guards and compute derived fields
                    var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                    var session = entity.Value;

                    // Idempotency: if the session is already in a terminal state (Succeeded/Failed),
                    // do not overwrite it with another terminal state to prevent duplicate notifications.
                    var existingStatusStr = session.GetString("Status");
                    if (status == SessionStatus.Succeeded || status == SessionStatus.Failed)
                    {
                        if (existingStatusStr == SessionStatus.Succeeded.ToString() || existingStatusStr == SessionStatus.Failed.ToString())
                        {
                            _logger.LogInformation($"Session {sessionId} already in terminal state '{existingStatusStr}', skipping status update to '{status}'");
                            return false;
                        }
                    }

                    // Build a Merge update with only the fields that actually change
                    var update = new TableEntity(tenantId, sessionId);

                    // Status transitions from IngestEventsFunction: Succeeded, Failed, Pending, Stalled,
                    // and InProgress (the last one only for healing a Stalled session back to active).
                    // Guard: never regress a Pending (WhiteGlove) session to Stalled via the ingest
                    // path — WhiteGlove sessions are deliberately long-lived and handled via re-registration.
                    if (status == SessionStatus.Stalled && existingStatusStr == SessionStatus.Pending.ToString())
                    {
                        _logger.LogInformation($"Session {sessionId} is in Pending (WhiteGlove) state, skipping Stalled transition");
                        return false;
                    }

                    // Guard: only allow InProgress transition if the current state is Stalled (healing).
                    // Other callers must not regress terminal or Pending state via this path.
                    if (status == SessionStatus.InProgress && existingStatusStr != SessionStatus.Stalled.ToString())
                    {
                        _logger.LogInformation($"Session {sessionId} is in state '{existingStatusStr}', not Stalled — InProgress heal no-op");
                        return false;
                    }

                    update["Status"] = status.ToString();

                    // Update current phase if provided.
                    if (currentPhase.HasValue)
                    {
                        update["CurrentPhase"] = (int)currentPhase.Value;
                    }

                    // Materialize the terminal phase regardless of the phase carried by the
                    // triggering event. The V2 DecisionEngine emits enrollment_complete /
                    // enrollment_failed with Phase=Unknown (feedback_phase_strategy forbids
                    // non-phase events from declaring a phase), which would otherwise leave
                    // CurrentPhase at -1 (or its previous in-flight value) on terminal sessions
                    // and break the Web UI PhaseTimeline. Backend is the right place to
                    // materialize the "terminal => Complete/Failed" invariant.
                    if (status == SessionStatus.Succeeded)
                    {
                        update["CurrentPhase"] = (int)EnrollmentPhase.Complete;
                    }
                    else if (status == SessionStatus.Failed)
                    {
                        update["CurrentPhase"] = (int)EnrollmentPhase.Failed;
                    }

                    // Align StartedAt with the earliest event timestamp provided by the caller
                    var currentStartedAt = session.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MaxValue;
                    if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < currentStartedAt)
                    {
                        update["StartedAt"] = EnsureUtc(earliestEventTimestamp.Value);
                    }

                    // Set completion time if succeeded or failed
                    if (status == SessionStatus.Succeeded || status == SessionStatus.Failed)
                    {
                        var effectiveCompletedAt = EnsureUtc(ResolveCompletionTimestamp(
                            completedAt,
                            session.GetDateTimeOffset("LastEventAt")?.UtcDateTime,
                            DateTime.UtcNow));
                        update["CompletedAt"] = effectiveCompletedAt;

                        // Check if this is a WhiteGlove session with a stored Part 1 duration
                        var existingIsPreProvisioned = session.GetBoolean("IsPreProvisioned") ?? false;
                        var existingResumedAt = session.GetDateTimeOffset("ResumedAt")?.UtcDateTime;
                        var existingDurationSeconds = SafeGetInt32(session, "DurationSeconds");

                        if (existingIsPreProvisioned && existingResumedAt.HasValue && existingDurationSeconds.HasValue)
                        {
                            // WhiteGlove: combined duration = Part 1 (stored) + Part 2 (ResumedAt → completion)
                            // This excludes the pause between pre-provisioning and user enrollment.
                            var part2Seconds = (int)(effectiveCompletedAt - existingResumedAt.Value).TotalSeconds;
                            if (part2Seconds > 0)
                                update["DurationSeconds"] = existingDurationSeconds.Value + part2Seconds;
                        }
                        else
                        {
                            // Standard session (or WhiteGlove without stored Part 1 data — fallback):
                            // Read earliest event from Events table — authoritative source, immune to
                            // concurrent StartedAt update races. This is a single-row lookup (maxPerPage: 1)
                            // and only happens once per session lifecycle (at completion).
                            var earliestStoredEvent = await GetEarliestSessionEventTimestampAsync(tenantId, sessionId);
                            var durationStart = earliestStoredEvent ?? currentStartedAt;
                            if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < durationStart)
                                durationStart = earliestEventTimestamp.Value;

                            if (durationStart < effectiveCompletedAt)
                                update["DurationSeconds"] = (int)(effectiveCompletedAt - durationStart).TotalSeconds;
                        }
                    }
                    // WhiteGlove Part 1 complete: compute and store Part 1 duration (earliest event → latest event).
                    // This value is used by the dashboard and serves as the authoritative Part 1 duration
                    // for future Part 2 combined-duration calculations.
                    else if (status == SessionStatus.Pending)
                    {
                        if (latestEventTimestamp.HasValue)
                        {
                            var earliestStoredEvent = await GetEarliestSessionEventTimestampAsync(tenantId, sessionId);
                            var durationStart = earliestStoredEvent ?? currentStartedAt;
                            if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < durationStart)
                                durationStart = earliestEventTimestamp.Value;

                            if (durationStart < latestEventTimestamp.Value)
                                update["DurationSeconds"] = (int)(latestEventTimestamp.Value - durationStart).TotalSeconds;
                        }
                    }

                    // Track the most recent event timestamp for excessive data sender detection
                    if (latestEventTimestamp.HasValue)
                    {
                        var currentLastEventAt = session.GetDateTimeOffset("LastEventAt")?.UtcDateTime;
                        if (!currentLastEventAt.HasValue || latestEventTimestamp.Value > currentLastEventAt.Value)
                            update["LastEventAt"] = EnsureUtc(latestEventTimestamp.Value);
                    }

                    // Set failure reason if failed
                    if (status == SessionStatus.Failed && !string.IsNullOrEmpty(failureReason))
                    {
                        update["FailureReason"] = failureReason;
                    }

                    // Persist the failure-state snapshot when provided. Only the maintenance
                    // 5h-timeout path supplies this today (Hybrid User-Driven completion-gap
                    // fix, 2026-05-01); other terminal paths leave the field untouched, which
                    // is fine — null/empty means "no snapshot available" and the UI falls back
                    // to the existing FailureReason rendering.
                    if (status == SessionStatus.Failed && !string.IsNullOrEmpty(failureSnapshotJson))
                    {
                        update["FailureSnapshotJson"] = failureSnapshotJson;
                    }

                    // Record the origin of a Failed status (agent / rule:<id> / manual). When the caller
                    // passes null we leave the existing value untouched — concurrent updates from different
                    // sources shouldn't stomp on each other's attribution.
                    if (status == SessionStatus.Failed && !string.IsNullOrEmpty(failureSource))
                    {
                        update["FailureSource"] = failureSource;
                    }
                    // Heal: clear attribution when the session is un-failed (e.g. back to InProgress).
                    if (clearFailureReason)
                    {
                        update["FailureSource"] = string.Empty;
                    }

                    // AdminMarkedAction marks an administrator-driven terminal override — set only by
                    // MarkSessionSucceededFunction / MarkSessionFailedFunction. This is the authoritative
                    // trigger for the AdminAction response-field sent to agents: an agent-reported
                    // terminal (status=Succeeded/Failed with this field null) does NOT fire AdminAction.
                    if (!string.IsNullOrEmpty(adminMarkedAction))
                    {
                        update["AdminMarkedAction"] = adminMarkedAction;
                    }

                    // Set IsPreProvisioned flag atomically with the status update (WhiteGlove)
                    if (isPreProvisioned.HasValue)
                    {
                        update["IsPreProvisioned"] = isPreProvisioned.Value;
                    }

                    // Set IsUserDriven flag atomically (WhiteGlove Part 1 → false, Part 2 → true)
                    if (isUserDriven.HasValue)
                    {
                        update["IsUserDriven"] = isUserDriven.Value;
                    }

                    // Store ResumedAt timestamp for WhiteGlove Part 2 (user enrollment start).
                    // Used to compute Duration 2 (user enrollment only) for Teams notifications.
                    if (resumedAt.HasValue)
                    {
                        update["ResumedAt"] = EnsureUtc(resumedAt.Value);
                    }

                    // Stalled-state bookkeeping: the agent emits session_stalled after 60 min idle,
                    // or the 2h maintenance sweep sets the state for completely silent agents.
                    if (stalledAt.HasValue)
                    {
                        update["StalledAt"] = EnsureUtc(stalledAt.Value);
                        if (!string.IsNullOrEmpty(failureReason))
                            update["FailureReason"] = failureReason;
                    }

                    // Heal: clear StalledAt + Stalled-phase FailureReason when the session goes
                    // back to InProgress (a new real event proved the agent is alive again).
                    if (clearStalledAt)
                    {
                        update["StalledAt"] = (DateTime?)null;
                    }
                    if (clearFailureReason)
                    {
                        update["FailureReason"] = string.Empty;
                    }

                    // Merge mode: only the fields set above are written; all other fields remain untouched.
                    // This drastically reduces ETag conflicts when concurrent requests update different fields.
                    await tableClient.UpdateEntityAsync(update, session.ETag, TableUpdateMode.Merge);

                    // Dual-write: keep SessionsIndex in sync
                    if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < currentStartedAt)
                    {
                        // StartedAt shifted — recompute IndexRowKey via full upsert (delete-old + create-new)
                        var fullEntity = (await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId)).Value;
                        await UpsertSessionIndexAsync(fullEntity, earliestEventTimestamp.Value);
                    }
                    else
                    {
                        var indexRowKey = session.GetString("IndexRowKey");

                        // Fallback: if IndexRowKey was lost (e.g. StoreSessionAsync Replace race),
                        // compute it from StartedAt + SessionId so the index still gets updated.
                        if (string.IsNullOrEmpty(indexRowKey))
                        {
                            var sessionStartedAt = session.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                            if (sessionStartedAt.HasValue)
                            {
                                indexRowKey = ComputeIndexRowKey(sessionStartedAt.Value, sessionId);
                                _logger.LogWarning("Session {SessionId}: IndexRowKey was null, computed fallback from StartedAt", sessionId);
                            }
                        }

                        await MergeSessionIndexAsync(tenantId, indexRowKey, update);
                    }

                    _logger.LogInformation($"Updated session {sessionId} status to {status}");
                    return true;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 412) // Precondition Failed (ETag conflict)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        // All ETag-based retries exhausted. Perform one final unconditional write
                        // to guarantee the status transition succeeds. This is safe because:
                        // 1. Merge mode only touches fields we explicitly set
                        // 2. We re-read and re-check the idempotency guard below
                        // 3. The fields we write are authoritative from this code path
                        _logger.LogWarning($"Session {sessionId}: ETag retries exhausted, attempting unconditional merge write for status={status}");

                        try
                        {
                            var forceTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                            var freshEntity = await forceTableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                            var freshSession = freshEntity.Value;

                            // Re-check idempotency: do not overwrite a terminal state
                            var freshStatusStr = freshSession.GetString("Status");
                            if ((status == SessionStatus.Succeeded || status == SessionStatus.Failed) &&
                                (freshStatusStr == SessionStatus.Succeeded.ToString() || freshStatusStr == SessionStatus.Failed.ToString()))
                            {
                                _logger.LogInformation($"Session {sessionId} reached terminal state '{freshStatusStr}' during retries, skipping force write");
                                return false;
                            }

                            var forceUpdate = new TableEntity(tenantId, sessionId);

                            forceUpdate["Status"] = status.ToString();

                            if (currentPhase.HasValue)
                                forceUpdate["CurrentPhase"] = (int)currentPhase.Value;

                            // Codex follow-up (882fef64 PR3-PR5 review): mirror the
                            // terminal-phase override from the normal path (line ~1019). V2
                            // emits enrollment_complete / enrollment_failed with Phase=Unknown
                            // (per feedback_phase_strategy), so currentPhase.HasValue is false
                            // here. Without this override the force-update path leaves
                            // CurrentPhase at its prior (in-flight or -1) value forever
                            // when ETag retries are exhausted exactly at terminal transition.
                            if (status == SessionStatus.Succeeded)
                            {
                                forceUpdate["CurrentPhase"] = (int)EnrollmentPhase.Complete;
                            }
                            else if (status == SessionStatus.Failed)
                            {
                                forceUpdate["CurrentPhase"] = (int)EnrollmentPhase.Failed;
                            }

                            var freshStartedAt = freshSession.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MaxValue;
                            if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < freshStartedAt)
                                forceUpdate["StartedAt"] = EnsureUtc(earliestEventTimestamp.Value);

                            if (status == SessionStatus.Succeeded || status == SessionStatus.Failed)
                            {
                                var effectiveCompletedAt = EnsureUtc(ResolveCompletionTimestamp(
                                    completedAt,
                                    freshSession.GetDateTimeOffset("LastEventAt")?.UtcDateTime,
                                    DateTime.UtcNow));
                                forceUpdate["CompletedAt"] = effectiveCompletedAt;

                                var freshIsPreProvisioned = freshSession.GetBoolean("IsPreProvisioned") ?? false;
                                var freshResumedAt = freshSession.GetDateTimeOffset("ResumedAt")?.UtcDateTime;
                                var freshDurationSeconds = SafeGetInt32(freshSession, "DurationSeconds");

                                if (freshIsPreProvisioned && freshResumedAt.HasValue && freshDurationSeconds.HasValue)
                                {
                                    // WhiteGlove: combined duration = Part 1 (stored) + Part 2 (ResumedAt → completion)
                                    var part2Seconds = (int)(effectiveCompletedAt - freshResumedAt.Value).TotalSeconds;
                                    if (part2Seconds > 0)
                                        forceUpdate["DurationSeconds"] = freshDurationSeconds.Value + part2Seconds;
                                }
                                else
                                {
                                    var earliestStoredEvent = await GetEarliestSessionEventTimestampAsync(tenantId, sessionId);
                                    var durationStart = earliestStoredEvent ?? freshStartedAt;
                                    if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < durationStart)
                                        durationStart = earliestEventTimestamp.Value;

                                    if (durationStart < effectiveCompletedAt)
                                        forceUpdate["DurationSeconds"] = (int)(effectiveCompletedAt - durationStart).TotalSeconds;
                                }
                            }
                            else if (status == SessionStatus.Pending)
                            {
                                if (latestEventTimestamp.HasValue)
                                {
                                    var earliestStoredEvent = await GetEarliestSessionEventTimestampAsync(tenantId, sessionId);
                                    var durationStart = earliestStoredEvent ?? freshStartedAt;
                                    if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < durationStart)
                                        durationStart = earliestEventTimestamp.Value;

                                    if (durationStart < latestEventTimestamp.Value)
                                        forceUpdate["DurationSeconds"] = (int)(latestEventTimestamp.Value - durationStart).TotalSeconds;
                                }
                            }

                            if (latestEventTimestamp.HasValue)
                            {
                                var freshLastEventAt = freshSession.GetDateTimeOffset("LastEventAt")?.UtcDateTime;
                                if (!freshLastEventAt.HasValue || latestEventTimestamp.Value > freshLastEventAt.Value)
                                    forceUpdate["LastEventAt"] = EnsureUtc(latestEventTimestamp.Value);
                            }

                            if (status == SessionStatus.Failed && !string.IsNullOrEmpty(failureReason))
                                forceUpdate["FailureReason"] = failureReason;

                            // Mirror the FailureSnapshotJson from the regular path so the
                            // force-merge fallback never silently drops the snapshot.
                            if (status == SessionStatus.Failed && !string.IsNullOrEmpty(failureSnapshotJson))
                                forceUpdate["FailureSnapshotJson"] = failureSnapshotJson;

                            if (isPreProvisioned.HasValue)
                                forceUpdate["IsPreProvisioned"] = isPreProvisioned.Value;

                            if (isUserDriven.HasValue)
                                forceUpdate["IsUserDriven"] = isUserDriven.Value;

                            if (resumedAt.HasValue)
                                forceUpdate["ResumedAt"] = EnsureUtc(resumedAt.Value);

                            // Unconditional merge write — ETag.All bypasses concurrency check
                            await forceTableClient.UpdateEntityAsync(forceUpdate, ETag.All, TableUpdateMode.Merge);

                            // Dual-write: keep SessionsIndex in sync
                            if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < freshStartedAt)
                            {
                                // StartedAt shifted — recompute IndexRowKey via full upsert
                                var updatedEntity = (await forceTableClient.GetEntityAsync<TableEntity>(tenantId, sessionId)).Value;
                                await UpsertSessionIndexAsync(updatedEntity, earliestEventTimestamp.Value);
                            }
                            else
                            {
                                var forceIndexRowKey = freshSession.GetString("IndexRowKey");
                                if (string.IsNullOrEmpty(forceIndexRowKey))
                                {
                                    var forceStartedAt2 = freshSession.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                                    if (forceStartedAt2.HasValue)
                                    {
                                        forceIndexRowKey = ComputeIndexRowKey(forceStartedAt2.Value, sessionId);
                                        _logger.LogWarning("Session {SessionId}: IndexRowKey was null in force-write path, computed fallback from StartedAt", sessionId);
                                    }
                                }
                                await MergeSessionIndexAsync(tenantId, forceIndexRowKey, forceUpdate);
                            }

                            _logger.LogInformation($"Force-updated session {sessionId} status to {status} (unconditional merge after ETag exhaustion)");
                            return true;
                        }
                        catch (Exception forceEx)
                        {
                            _logger.LogError(forceEx, $"Force-write also failed for session {sessionId} status update to {status}");
                            return false;
                        }
                    }

                    // Exponential backoff with jitter to decorrelate concurrent retries
                    var baseDelay = 50 * (int)Math.Pow(2, retryCount - 1);
                    await Task.Delay(baseDelay + Random.Shared.Next(0, baseDelay));
                    _logger.LogDebug($"Retrying session {sessionId} update (attempt {retryCount}/{maxRetries}) after ETag conflict");
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 429 || ex.Status == 503 || ex.Status == 408)
                {
                    // Transient Azure Table Storage errors — retry with backoff instead of
                    // immediately returning false. Without this, a single throttle (429) or
                    // service hiccup (503/408) during the WhiteGlove drain causes the entire
                    // status update to fail silently, leaving the session in a broken state.
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        _logger.LogError(ex, $"Session {sessionId}: transient error {ex.Status} persisted after {maxRetries} retries for status={status}");
                        return false;
                    }

                    var baseDelay = 100 * (int)Math.Pow(2, retryCount - 1);
                    await Task.Delay(baseDelay + Random.Shared.Next(0, baseDelay));
                    _logger.LogWarning($"Session {sessionId}: transient error {ex.Status}, retrying (attempt {retryCount}/{maxRetries})");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to update session {sessionId} status");
                    return false;
                }
            }

            return false;
        }

        /// <summary>
        /// Increments the session event count without touching status or phase fields.
        /// Uses Merge mode to safely handle concurrent updates.
        /// The caller provides earliestEventTimestamp from the current batch;
        /// no redundant Events-table scan is performed here.
        /// </summary>
        public async Task IncrementSessionEventCountAsync(string tenantId, string sessionId, int increment, DateTime? earliestEventTimestamp = null, DateTime? latestEventTimestamp = null, EnrollmentPhase? currentPhase = null, int platformScriptIncrement = 0, int remediationScriptIncrement = 0)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            const int maxRetries = 5;
            int retryCount = 0;

            while (retryCount < maxRetries)
            {
                try
                {
                    var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                    var entityResponse = await tableClient.GetEntityIfExistsAsync<TableEntity>(tenantId, sessionId);
                    if (!entityResponse.HasValue)
                    {
                        _logger.LogWarning("Session {SessionId} not found when incrementing event count — session may have been cleaned up or not yet registered", sessionId);
                        return;
                    }
                    var entity = entityResponse.Value!;
                    var currentCount = entity.GetInt32("EventCount") ?? 0;

                    var update = new TableEntity(tenantId, sessionId)
                    {
                        ["EventCount"] = currentCount + increment
                    };

                    // Update current phase if a phase-change event was in the batch
                    if (currentPhase.HasValue)
                    {
                        update["CurrentPhase"] = (int)currentPhase.Value;
                    }

                    // Align StartedAt with the earliest event timestamp provided by the caller
                    var currentStartedAt = entity.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MaxValue;
                    if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < currentStartedAt)
                    {
                        update["StartedAt"] = EnsureUtc(earliestEventTimestamp.Value);
                    }

                    // Track the most recent event timestamp for excessive data sender detection
                    if (latestEventTimestamp.HasValue)
                    {
                        var currentLastEventAt = entity.GetDateTimeOffset("LastEventAt")?.UtcDateTime;
                        if (!currentLastEventAt.HasValue || latestEventTimestamp.Value > currentLastEventAt.Value)
                            update["LastEventAt"] = EnsureUtc(latestEventTimestamp.Value);
                    }

                    // Increment script execution counters
                    if (platformScriptIncrement > 0)
                    {
                        var current = entity.GetInt32("PlatformScriptCount") ?? 0;
                        update["PlatformScriptCount"] = current + platformScriptIncrement;
                    }
                    if (remediationScriptIncrement > 0)
                    {
                        var current = entity.GetInt32("RemediationScriptCount") ?? 0;
                        update["RemediationScriptCount"] = current + remediationScriptIncrement;
                    }

                    await tableClient.UpdateEntityAsync(update, entity.ETag, TableUpdateMode.Merge);

                    // Dual-write: keep SessionsIndex in sync
                    if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < currentStartedAt)
                    {
                        // StartedAt shifted — the IndexRowKey (inverted ticks) must be recomputed.
                        // Re-read the full entity (now updated) and upsert to handle delete-old + create-new.
                        var fullEntity = (await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId)).Value;
                        await UpsertSessionIndexAsync(fullEntity, earliestEventTimestamp.Value);
                    }
                    else
                    {
                        // No StartedAt change — efficient merge into existing index entry
                        var indexRowKey = entity.GetString("IndexRowKey");
                        if (string.IsNullOrEmpty(indexRowKey))
                        {
                            var incStartedAt = entity.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                            if (incStartedAt.HasValue)
                                indexRowKey = ComputeIndexRowKey(incStartedAt.Value, sessionId);
                        }
                        await MergeSessionIndexAsync(tenantId, indexRowKey, update);
                    }

                    return;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 412)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        _logger.LogWarning($"Failed to increment event count for session {sessionId} after {maxRetries} retries due to ETag conflicts");
                        return;
                    }
                    // Exponential backoff with jitter to decorrelate concurrent retries
                    var baseDelay = 50 * (int)Math.Pow(2, retryCount - 1);
                    await Task.Delay(baseDelay + Random.Shared.Next(0, baseDelay));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to increment event count for session {sessionId}");
                    return;
                }
            }
        }

        /// <summary>
        /// Gets all events for a specific session
        /// </summary>
        public async Task<List<EnrollmentEvent>> GetSessionEventsAsync(string tenantId, string sessionId, int maxResults = 1000)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
                var events = new List<EnrollmentEvent>();

                // Events are stored with PartitionKey = "{TenantId}_{SessionId}"
                var partitionKey = $"{tenantId}_{sessionId}";

                var query = tableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{partitionKey}'",
                    maxPerPage: maxResults
                );

                await foreach (var entity in query)
                {
                    events.Add(MapToEnrollmentEvent(entity));
                }

                // Sort by Sequence ascending — the authoritative event order
                // (assigned atomically via Interlocked.Increment on the agent)
                return events.OrderBy(e => e.Sequence).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get events for session {sessionId}");
                return new List<EnrollmentEvent>();
            }
        }

        /// <summary>
        /// Reads a single page of session events. Honours the supplied <paramref name="pageSize"/>
        /// and resumes from <paramref name="continuation"/> when supplied. The returned page's
        /// items are sorted by Sequence ascending; the next-page cursor is null when this was
        /// the last page.
        /// </summary>
        public async Task<RawPage<EnrollmentEvent>> GetSessionEventsPageAsync(
            string tenantId, string sessionId, int pageSize, string? continuation)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be >= 1");

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
                var partitionKey = $"{tenantId}_{sessionId}";

                var (entities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                    client: tableClient,
                    filter: $"PartitionKey eq '{partitionKey}'",
                    pageSize: pageSize,
                    continuation: continuation);

                var events = new List<EnrollmentEvent>(entities.Count);
                foreach (var entity in entities)
                {
                    events.Add(MapToEnrollmentEvent(entity));
                }
                events.Sort((a, b) => a.Sequence.CompareTo(b.Sequence));

                return new RawPage<EnrollmentEvent>(events, nextRawToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get events page for session {SessionId}", sessionId);
                return RawPage<EnrollmentEvent>.Empty;
            }
        }

        /// <summary>
        /// Gets events for a specific session filtered by event type (server-side OData filter).
        /// Much more efficient than GetSessionEventsAsync when only one event type is needed.
        /// </summary>
        public async Task<List<EnrollmentEvent>> GetSessionEventsByTypeAsync(string tenantId, string sessionId, string eventType, int maxResults = 200)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
                var events = new List<EnrollmentEvent>();

                var partitionKey = $"{tenantId}_{sessionId}";
                var filter = $"PartitionKey eq '{ODataSanitizer.EscapeValue(partitionKey)}' and EventType eq '{ODataSanitizer.EscapeValue(eventType)}'";

                var query = tableClient.QueryAsync<TableEntity>(
                    filter: filter,
                    maxPerPage: maxResults
                );

                await foreach (var entity in query)
                {
                    events.Add(MapToEnrollmentEvent(entity));
                }

                return events.OrderBy(e => e.Sequence).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get events by type {EventType} for session {SessionId}", eventType, sessionId);
                return new List<EnrollmentEvent>();
            }
        }

        /// <summary>
        /// Stores the diagnostics blob name on an existing session (Merge-mode, single field update).
        /// </summary>
        public async Task UpdateSessionDiagnosticsBlobAsync(string tenantId, string sessionId, string blobName)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);

                var update = new TableEntity(tenantId, sessionId)
                {
                    ["DiagnosticsBlobName"] = blobName
                };

                await tableClient.UpdateEntityAsync(update, entity.Value.ETag, Azure.Data.Tables.TableUpdateMode.Merge);

                // Dual-write: merge into SessionsIndex
                var indexRowKey = entity.Value.GetString("IndexRowKey");
                await MergeSessionIndexAsync(tenantId, indexRowKey, update);

                _logger.LogInformation($"Stored diagnostics blob name for session {sessionId}: {blobName}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, $"Failed to store diagnostics blob name for session {sessionId}");
            }
        }

        /// <summary>
        /// Sets the IsPreProvisioned flag (and optionally Status) on an existing session via
        /// unconditional Merge-mode write. Uses ETag.All to bypass optimistic-concurrency conflicts,
        /// making this suitable as a last-resort fallback when ETag-based updates have been exhausted.
        /// </summary>
        public async Task SetSessionPreProvisionedAsync(string tenantId, string sessionId, bool isPreProvisioned, SessionStatus? status = null, bool? isUserDriven = null)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

            var update = new TableEntity(tenantId, sessionId)
            {
                ["IsPreProvisioned"] = isPreProvisioned
            };

            if (status.HasValue)
            {
                update["Status"] = status.Value.ToString();
            }

            if (isUserDriven.HasValue)
            {
                update["IsUserDriven"] = isUserDriven.Value;
            }

            await tableClient.UpdateEntityAsync(update, ETag.All, TableUpdateMode.Merge);

            // Dual-write: read IndexRowKey and merge into SessionsIndex
            try
            {
                var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId,
                    select: new[] { "IndexRowKey" });
                var indexRowKey = entity.Value.GetString("IndexRowKey");
                await MergeSessionIndexAsync(tenantId, indexRowKey, update);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync session index for SetPreProvisioned {SessionId}", sessionId);
            }

            _logger.LogInformation($"Set IsPreProvisioned={isPreProvisioned}, Status={status?.ToString() ?? "(unchanged)"}, IsUserDriven={isUserDriven?.ToString() ?? "(unchanged)"} for session {sessionId} (unconditional merge)");
        }

        /// <summary>
        /// Updates the session's geo-location fields via unconditional Merge-mode write.
        /// Only writes non-null values; skips if all values are null/empty or geo is already populated.
        /// Non-fatal: geo is supplementary data, failures are logged as warnings.
        /// </summary>
        public async Task UpdateSessionGeoAsync(string tenantId, string sessionId,
            string? country, string? region, string? city, string? loc)
        {
            if (string.IsNullOrEmpty(country) && string.IsNullOrEmpty(region) &&
                string.IsNullOrEmpty(city) && string.IsNullOrEmpty(loc))
                return;

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // Check if geo fields are already populated (avoid redundant writes)
                string? indexRowKey = null;
                try
                {
                    var existing = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                    var existingCountry = existing.Value.GetString("GeoCountry");
                    if (!string.IsNullOrEmpty(existingCountry))
                    {
                        _logger.LogDebug("Session {SessionId} already has geo data, skipping update", sessionId);
                        return;
                    }
                    indexRowKey = existing.Value.GetString("IndexRowKey");
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return; // Session does not exist yet
                }

                var update = new TableEntity(tenantId, sessionId)
                {
                    ["GeoCountry"] = country ?? string.Empty,
                    ["GeoRegion"] = region ?? string.Empty,
                    ["GeoCity"] = city ?? string.Empty,
                    ["GeoLoc"] = loc ?? string.Empty
                };

                await tableClient.UpdateEntityAsync(update, ETag.All, TableUpdateMode.Merge);

                // Dual-write: merge into SessionsIndex
                await MergeSessionIndexAsync(tenantId, indexRowKey, update);

                _logger.LogDebug("Updated geo for session {SessionId}: {City}, {Region}, {Country}", sessionId, city, region, country);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update geo for session {SessionId}", sessionId);
            }
        }

        /// <summary>
        /// Stores the IME agent version on an existing session (Merge-mode, single field update).
        /// Non-fatal: failures are logged as warnings and do not block ingest.
        /// </summary>
        public async Task UpdateSessionImeAgentVersionAsync(string tenantId, string sessionId, string version)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var entity = new TableEntity(tenantId, sessionId)
                {
                    ["ImeAgentVersion"] = version
                };
                await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Merge);
            }
            catch (Exception ex)
            {
                // Non-fatal — don't block ingest
                _logger.LogWarning(ex, "Failed to update ImeAgentVersion for session {SessionId}", sessionId);
            }
        }

        /// <summary>
        /// Deletes a session from storage
        /// </summary>
        public async Task<bool> DeleteSessionAsync(string tenantId, string sessionId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);

                // Read IndexRowKey before deleting so we can also delete the index entry
                string? indexRowKey = null;
                try
                {
                    var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId,
                        select: new[] { "IndexRowKey" });
                    indexRowKey = entity.Value.GetString("IndexRowKey");
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    // Session already gone — nothing to delete
                    return true;
                }

                await tableClient.DeleteEntityAsync(tenantId, sessionId);

                // Dual-write: delete from SessionsIndex
                await DeleteSessionIndexAsync(tenantId, indexRowKey);

                _logger.LogInformation($"Deleted session {sessionId} for tenant {tenantId}");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to delete session {sessionId}");
                return false;
            }
        }

        // ===== SESSION/EVENT MAPPING HELPERS =====

        /// <summary>
        /// Maps a TableEntity to EnrollmentEvent
        /// </summary>
        private EnrollmentEvent MapToEnrollmentEvent(TableEntity entity)
        {
            // Events table uses composite PartitionKey: "{TenantId}_{SessionId}".
            // Prefer the stored TenantId property; fall back to extracting from the composite key.
            var partitionKey = entity.PartitionKey ?? string.Empty;
            var tenantId = entity.GetString("TenantId");
            if (string.IsNullOrEmpty(tenantId) && partitionKey.Contains('_'))
            {
                var separatorIndex = partitionKey.IndexOf('_');
                tenantId = partitionKey.Substring(0, separatorIndex);
            }

            return new EnrollmentEvent
            {
                EventId = entity.GetString("EventId") ?? string.Empty,
                SessionId = entity.GetString("SessionId") ?? string.Empty,
                TenantId = tenantId ?? partitionKey,
                Timestamp = DateTime.SpecifyKind(
                    entity.GetDateTimeOffset("Timestamp")?.UtcDateTime
                    ?? entity.GetDateTime("Timestamp")
                    ?? DateTime.UtcNow, DateTimeKind.Utc),
                EventType = entity.GetString("EventType") ?? string.Empty,
                Severity = (EventSeverity)(entity.GetInt32("Severity") ?? 0),
                Source = entity.GetString("Source") ?? string.Empty,
                Phase = (EnrollmentPhase)(entity.GetInt32("Phase") ?? 0),
                Message = entity.GetString("Message") ?? string.Empty,
                Sequence = entity.GetInt64("Sequence") ?? 0,
                Data = DeserializeEventData(entity.GetString("DataJson")),
                RowKey = entity.RowKey,
                ReceivedAt = entity.GetDateTimeOffset("ReceivedAt")?.UtcDateTime,
                OriginalTimestamp = entity.GetDateTimeOffset("OriginalTimestamp")?.UtcDateTime,
                TimestampClamped = entity.GetBoolean("TimestampClamped") ?? false,
                // Codex follow-up #3 — forward-link columns (nullable for legacy rows).
                CausedByTransitionStepIndex = entity.GetInt64("CausedByTransitionStepIndex"),
                CausedBySignalOrdinal = entity.GetInt64("CausedBySignalOrdinal"),
            };
        }

        /// <summary>
        /// Maps a TableEntity to SessionSummary
        /// </summary>
        private SessionSummary MapToSessionSummary(TableEntity entity)
        {
            // All typed getters (GetInt32, GetDateTime, etc.) throw InvalidOperationException
            // when a property exists but has a different type (e.g. legacy data stored as string
            // instead of int). Use safe helpers to handle type mismatches gracefully.
            var startedAt = SafeGetDateTime(entity, "StartedAt") ?? DateTime.UtcNow;
            var completedAt = SafeGetDateTime(entity, "CompletedAt");

            // Parse status with error handling and case-insensitivity
            var statusString = entity.GetString("Status") ?? "InProgress";
            if (!Enum.TryParse<SessionStatus>(statusString, ignoreCase: true, out var status))
            {
                _logger.LogWarning($"Failed to parse status '{statusString}' for session {entity.RowKey}, defaulting to Unknown");
                status = SessionStatus.Unknown;
            }

            return new SessionSummary
            {
                SessionId = entity.RowKey,
                TenantId = entity.PartitionKey,
                SerialNumber = entity.GetString("SerialNumber") ?? string.Empty,
                DeviceName = entity.GetString("DeviceName") ?? string.Empty,
                Manufacturer = entity.GetString("Manufacturer") ?? string.Empty,
                Model = entity.GetString("Model") ?? string.Empty,
                StartedAt = startedAt,
                CompletedAt = completedAt,
                CurrentPhase = SafeGetInt32(entity, "CurrentPhase") ?? 0,
                CurrentPhaseDetail = entity.GetString("CurrentPhaseDetail") ?? string.Empty,
                Status = status,
                FailureReason = entity.GetString("FailureReason") ?? string.Empty,
                FailureSource = entity.GetString("FailureSource") ?? string.Empty,
                AdminMarkedAction = entity.GetString("AdminMarkedAction"),
                PendingActionsJson = entity.GetString("PendingActionsJson") ?? string.Empty,
                PendingActionsQueuedAt = SafeGetDateTime(entity, "PendingActionsQueuedAt"),
                EventCount = SafeGetInt32(entity, "EventCount") ?? 0,
                DurationSeconds = ComputeEffectiveDuration(entity, status, startedAt, completedAt),
                EnrollmentType = entity.GetString("EnrollmentType") ?? "v1",
                DiagnosticsBlobName = entity.GetString("DiagnosticsBlobName"),
                LastEventAt = SafeGetDateTime(entity, "LastEventAt"),
                IsPreProvisioned = entity.GetBoolean("IsPreProvisioned") ?? false,
                IsHybridJoin = entity.GetBoolean("IsHybridJoin") ?? false,
                ResumedAt = SafeGetDateTime(entity, "ResumedAt"),
                StalledAt = SafeGetDateTime(entity, "StalledAt"),
                OsName = entity.GetString("OsName") ?? string.Empty,
                OsBuild = entity.GetString("OsBuild") ?? string.Empty,
                OsDisplayVersion = entity.GetString("OsDisplayVersion") ?? string.Empty,
                OsEdition = entity.GetString("OsEdition") ?? string.Empty,
                OsLanguage = entity.GetString("OsLanguage") ?? string.Empty,
                IsUserDriven = entity.GetBoolean("IsUserDriven") ?? false,
                AgentVersion = entity.GetString("AgentVersion") ?? string.Empty,
                ImeAgentVersion = entity.GetString("ImeAgentVersion") ?? string.Empty,
                GeoCountry = entity.GetString("GeoCountry") ?? string.Empty,
                GeoRegion = entity.GetString("GeoRegion") ?? string.Empty,
                GeoCity = entity.GetString("GeoCity") ?? string.Empty,
                GeoLoc = entity.GetString("GeoLoc") ?? string.Empty,
                PlatformScriptCount = SafeGetInt32(entity, "PlatformScriptCount") ?? 0,
                RemediationScriptCount = SafeGetInt32(entity, "RemediationScriptCount") ?? 0,
                ExcessiveEventsAlerted = entity.GetBoolean("ExcessiveEventsAlerted") ?? false,
                FailureSnapshotJson = entity.GetString("FailureSnapshotJson") ?? string.Empty
            };
        }

        /// <summary>
        /// Resolves the timestamp recorded as <c>CompletedAt</c> when a session transitions to
        /// Succeeded/Failed. Caller-supplied <paramref name="completedAt"/> wins (agent CompletionEvent);
        /// otherwise falls back to the last observed event time so admin-marked terminals,
        /// rule-engine, and maintenance auto-fail anchor on when the session actually went
        /// silent rather than on the click/tick time. Caller is responsible for normalizing the
        /// returned value to UTC kind via <c>EnsureUtc</c> before persisting.
        /// </summary>
        internal static DateTime ResolveCompletionTimestamp(DateTime? completedAt, DateTime? lastEventAt, DateTime nowUtc)
            => completedAt ?? lastEventAt ?? nowUtc;

        /// <summary>
        /// Computes the effective duration for dashboard display.
        /// For WhiteGlove Part 2 InProgress sessions: Part 1 stored duration + (now - ResumedAt).
        /// For completed/Pending sessions: uses stored DurationSeconds (set by UpdateSessionStatusAsync).
        /// For other InProgress sessions: falls back to wall-clock time from StartedAt.
        /// </summary>
        private int ComputeEffectiveDuration(TableEntity entity, SessionStatus status, DateTime startedAt, DateTime? completedAt)
        {
            var storedDuration = SafeGetInt32(entity, "DurationSeconds");
            var isPreProvisioned = entity.GetBoolean("IsPreProvisioned") ?? false;
            var resumedAt = SafeGetDateTime(entity, "ResumedAt");

            // WhiteGlove Part 2 in progress: Part 1 duration (stored) + running Part 2 time
            if (isPreProvisioned && resumedAt.HasValue && storedDuration.HasValue
                && status == SessionStatus.InProgress)
            {
                var part2Running = (int)(DateTime.UtcNow - resumedAt.Value).TotalSeconds;
                return storedDuration.Value + Math.Max(0, part2Running);
            }

            // All other cases: use stored value or compute fallback
            if (storedDuration.HasValue)
                return storedDuration.Value;

            if (completedAt.HasValue)
                return (int)(completedAt.Value - startedAt).TotalSeconds;

            return (int)(DateTime.UtcNow - startedAt).TotalSeconds;
        }

        /// <summary>
        /// Returns the earliest event timestamp persisted for a session, if any.
        /// Events are written with RowKey "{Timestamp}_{Sequence}", so querying the partition
        /// and taking the first row yields the earliest event.
        /// </summary>
        private async Task<DateTime?> GetEarliestSessionEventTimestampAsync(string tenantId, string sessionId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
                var partitionKey = $"{tenantId}_{sessionId}";
                var query = tableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{partitionKey}'",
                    maxPerPage: 1,
                    select: new[] { "Timestamp", "RowKey" }
                );

                await foreach (var entity in query)
                {
                    return entity.GetDateTimeOffset("Timestamp")?.UtcDateTime
                           ?? entity.GetDateTime("Timestamp");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"Could not determine earliest event timestamp for session {sessionId}");
            }

            return null;
        }

        #region IME Version History

        /// <summary>
        /// Records an IME version sighting. If the version is new, inserts it with FirstSeenAt.
        /// If already known, updates LastSeenAt and increments SessionCount via Merge.
        /// Returns true if this was a newly discovered version.
        /// </summary>
        public async Task<bool> RecordImeVersionAsync(string version, string tenantId, string sessionId)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.ImeVersionHistory);
            var now = DateTime.UtcNow;

            // Try insert first — succeeds only for genuinely new versions
            var newEntity = new TableEntity("Global", version)
            {
                ["FirstSeenAt"] = now,
                ["FirstSeenSessionId"] = sessionId,
                ["FirstSeenTenantId"] = tenantId,
                ["LastSeenAt"] = now,
                ["SessionCount"] = 1
            };

            try
            {
                await tableClient.AddEntityAsync(newEntity);
                return true; // New version discovered
            }
            catch (RequestFailedException ex) when (ex.Status == 409)
            {
                // Version already known — update LastSeenAt and increment SessionCount
            }

            // Merge-update for known versions: bump LastSeenAt + SessionCount
            try
            {
                var existing = await tableClient.GetEntityAsync<TableEntity>("Global", version,
                    select: new[] { "SessionCount" });
                var currentCount = existing.Value.GetInt32("SessionCount") ?? 0;

                var mergeEntity = new TableEntity("Global", version)
                {
                    ["LastSeenAt"] = now,
                    ["SessionCount"] = currentCount + 1
                };
                await tableClient.UpsertEntityAsync(mergeEntity, TableUpdateMode.Merge);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update ImeVersionHistory for version {Version}", version);
            }

            return false;
        }

        /// <summary>
        /// Returns all known IME versions ordered by FirstSeenAt descending.
        /// </summary>
        public async Task<List<ImeVersionHistoryEntry>> GetImeVersionHistoryAsync()
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.ImeVersionHistory);
            var results = new List<ImeVersionHistoryEntry>();

            try
            {
                var query = tableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq 'Global'");

                await foreach (var entity in query)
                {
                    results.Add(new ImeVersionHistoryEntry
                    {
                        Version = entity.RowKey,
                        FirstSeenAt = entity.GetDateTime("FirstSeenAt") ?? DateTime.MinValue,
                        FirstSeenSessionId = entity.GetString("FirstSeenSessionId") ?? string.Empty,
                        FirstSeenTenantId = entity.GetString("FirstSeenTenantId") ?? string.Empty,
                        LastSeenAt = entity.GetDateTime("LastSeenAt") ?? DateTime.MinValue,
                        SessionCount = entity.GetInt32("SessionCount") ?? 0
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query ImeVersionHistory");
            }

            return results.OrderByDescending(e => e.FirstSeenAt).ToList();
        }

        #endregion

        #region Excessive-Event Detection

        /// <summary>
        /// Returns sessions in the given tenant whose EventCount exceeds the threshold.
        /// Projects only the fields needed for the idempotency check — avoids full entity read.
        /// Never throws: on failure returns an empty list (maintenance scan is best-effort).
        /// </summary>
        public async Task<List<SessionSummary>> GetSessionsWithEventCountAboveAsync(string tenantId, int threshold)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));

            var matches = new List<SessionSummary>();
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var query = tableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{tenantId}' and EventCount gt {threshold}",
                    select: new[] { "PartitionKey", "RowKey", "EventCount", "ExcessiveEventsAlerted" });

                await foreach (var entity in query)
                {
                    matches.Add(new SessionSummary
                    {
                        TenantId = entity.PartitionKey,
                        SessionId = entity.RowKey,
                        EventCount = SafeGetInt32(entity, "EventCount") ?? 0,
                        ExcessiveEventsAlerted = entity.GetBoolean("ExcessiveEventsAlerted") ?? false
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query sessions with EventCount > {Threshold} for tenant {TenantId}", threshold, tenantId);
            }
            return matches;
        }

        /// <summary>
        /// Sets ExcessiveEventsAlerted=true on the session entity to make the ops-alert emission
        /// idempotent across maintenance runs. Best-effort Merge write; failures are logged only.
        /// </summary>
        public async Task MarkExcessiveEventsAlertedAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var update = new TableEntity(tenantId, sessionId)
                {
                    ["ExcessiveEventsAlerted"] = true
                };
                await tableClient.UpdateEntityAsync(update, ETag.All, TableUpdateMode.Merge);

                // Dual-write into SessionsIndex so dashboard queries see the flag without a separate read.
                try
                {
                    var entity = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId,
                        select: new[] { "IndexRowKey" });
                    var indexRowKey = entity.Value.GetString("IndexRowKey");
                    await MergeSessionIndexAsync(tenantId, indexRowKey, update);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "SessionsIndex merge skipped for ExcessiveEventsAlerted on {SessionId}", sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark session {SessionId} as ExcessiveEventsAlerted", sessionId);
            }
        }

        #endregion
    }
}
