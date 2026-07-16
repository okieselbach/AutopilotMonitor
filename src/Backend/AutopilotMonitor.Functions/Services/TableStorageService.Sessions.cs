using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
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

                // Build index entity from the single SessionsIndex field manifest (lean read-model contract).
                var indexEntity = BuildSessionIndexEntity(sessionEntity, indexRowKey, startedAt);

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
        /// Single source of truth for the SessionsIndex field projection. The SessionsIndex is a
        /// FULL MIRROR of the primary Sessions row, differing only in RowKey (inverted-tick prefix
        /// for newest-first / date-range / cursor paging that the GUID-keyed Sessions table cannot
        /// do). It serves the session list / search / stats AND the <c>/api/raw/sessions</c> endpoint
        /// (which returns every stored index column verbatim, see <c>RawEntityProjection</c>) directly
        /// — no hydration from Sessions — so it must carry the same column set the session lifecycle
        /// writes. This builder is the ONE place that set is defined; it must stay a SUPERSET of every
        /// field any <c>MergeSessionIndexAsync</c> call site writes, otherwise a StartedAt-shift full
        /// upsert would drop a merged field until the next merge (the recurring drift bug, e.g.
        /// ab90423b). Keep aligned with <c>MapToSessionSummary</c> (read side).
        ///
        /// Not mirrored here: fields owned by separate write subsystems that do not touch the index —
        /// <c>PendingActionsJson</c>/<c>PendingActionsQueuedAt</c> (ServerActions) and
        /// <c>DeletionState</c>/<c>PendingDeletionManifestId</c> (deletion CAS). They are primary-only
        /// by construction (no index writer); routing them through the index sync is a follow-up.
        /// </summary>
        internal static TableEntity BuildSessionIndexEntity(TableEntity sessionEntity, string indexRowKey, DateTime startedAt)
        {
            var tenantId = sessionEntity.PartitionKey;
            var sessionId = sessionEntity.RowKey;

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
                ["IsSelfDeployingProfile"] = sessionEntity.GetBoolean("IsSelfDeployingProfile") ?? false,
                ["AgentVersion"] = sessionEntity.GetString("AgentVersion") ?? string.Empty,
                // Search-filterable column: search/MCP push an OData filter on ImeAgentVersion against
                // the index, so it MUST be projected (was previously absent → filter matched nothing).
                ["ImeAgentVersion"] = sessionEntity.GetString("ImeAgentVersion") ?? string.Empty,
                ["OsName"] = sessionEntity.GetString("OsName") ?? string.Empty,
                ["OsBuild"] = sessionEntity.GetString("OsBuild") ?? string.Empty,
                ["OsDisplayVersion"] = sessionEntity.GetString("OsDisplayVersion") ?? string.Empty,
                ["OsEdition"] = sessionEntity.GetString("OsEdition") ?? string.Empty,
                ["OsLanguage"] = sessionEntity.GetString("OsLanguage") ?? string.Empty,
                ["GeoCountry"] = sessionEntity.GetString("GeoCountry") ?? string.Empty,
                ["GeoRegion"] = sessionEntity.GetString("GeoRegion") ?? string.Empty,
                ["GeoCity"] = sessionEntity.GetString("GeoCity") ?? string.Empty,
                ["GeoLoc"] = sessionEntity.GetString("GeoLoc") ?? string.Empty,
                // Always-present counts/flags — mirror Sessions defaults (read side defaults to 0/false).
                ["PlatformScriptCount"] = sessionEntity.GetInt32("PlatformScriptCount") ?? 0,
                ["RemediationScriptCount"] = sessionEntity.GetInt32("RemediationScriptCount") ?? 0,
                // Search-filterable column (rebootCountMin/Max push an OData filter on the index),
                // so it MUST be projected — otherwise a StartedAt-shift full upsert drops it.
                ["RebootCount"] = sessionEntity.GetInt32("RebootCount") ?? 0,
                ["ExcessiveEventsAlerted"] = sessionEntity.GetBoolean("ExcessiveEventsAlerted") ?? false,
                ["ExcessiveEventsAutoActioned"] = sessionEntity.GetBoolean("ExcessiveEventsAutoActioned") ?? false
            };

            // Nullable fields — only written when present.
            var completedAt = sessionEntity.GetDateTimeOffset("CompletedAt")?.UtcDateTime;
            if (completedAt.HasValue)
                indexEntity["CompletedAt"] = EnsureUtc(completedAt.Value);

            var failureReason = sessionEntity.GetString("FailureReason");
            if (!string.IsNullOrEmpty(failureReason))
                indexEntity["FailureReason"] = failureReason;

            var failureSource = sessionEntity.GetString("FailureSource");
            if (!string.IsNullOrEmpty(failureSource))
                indexEntity["FailureSource"] = failureSource;

            // Backend-declared success justification — rendered as the "reconciled" badge in the
            // index-served session list, so it must survive a StartedAt-shift full upsert.
            var reconcileReason = sessionEntity.GetString("ReconcileReason");
            if (!string.IsNullOrEmpty(reconcileReason))
                indexEntity["ReconcileReason"] = reconcileReason;

            var failureSnapshotJson = sessionEntity.GetString("FailureSnapshotJson");
            if (!string.IsNullOrEmpty(failureSnapshotJson))
                indexEntity["FailureSnapshotJson"] = failureSnapshotJson;

            // AdminMarkedAction is rendered as the "manual" badge in the dashboard session LIST
            // (index-served), so it belongs in the projection — otherwise a StartedAt-shift full
            // upsert drops it until the next merge.
            var adminMarkedAction = sessionEntity.GetString("AdminMarkedAction");
            if (!string.IsNullOrEmpty(adminMarkedAction))
                indexEntity["AdminMarkedAction"] = adminMarkedAction;

            var durationSeconds = sessionEntity.GetInt32("DurationSeconds");
            if (durationSeconds.HasValue)
                indexEntity["DurationSeconds"] = durationSeconds.Value;

            var diagnosticsBlobName = sessionEntity.GetString("DiagnosticsBlobName");
            if (!string.IsNullOrEmpty(diagnosticsBlobName))
                indexEntity["DiagnosticsBlobName"] = diagnosticsBlobName;

            var diagnosticsBlobDestination = sessionEntity.GetString("DiagnosticsBlobDestination");
            if (!string.IsNullOrEmpty(diagnosticsBlobDestination))
                indexEntity["DiagnosticsBlobDestination"] = diagnosticsBlobDestination;

            var lastEventAt = sessionEntity.GetDateTimeOffset("LastEventAt")?.UtcDateTime;
            if (lastEventAt.HasValue)
                indexEntity["LastEventAt"] = EnsureUtc(lastEventAt.Value);

            var resumedAt = sessionEntity.GetDateTimeOffset("ResumedAt")?.UtcDateTime;
            if (resumedAt.HasValue)
                indexEntity["ResumedAt"] = EnsureUtc(resumedAt.Value);

            var stalledAt = sessionEntity.GetDateTimeOffset("StalledAt")?.UtcDateTime;
            if (stalledAt.HasValue)
                indexEntity["StalledAt"] = EnsureUtc(stalledAt.Value);

            return indexEntity;
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
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                // Benign race: the SessionsIndex row doesn't exist yet (merge runs before the full
                // upsert that creates it, e.g. a session predating the index dual-write / awaiting
                // backfill). The next full-upsert path rebuilds the row. Downgrade to Debug so it
                // doesn't pollute the exceptions table as a tracked error.
                _logger.LogDebug("Session index merge skipped (row not found yet) for {TenantId}/{IndexRowKey}", tenantId, indexRowKey);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to merge session index for {TenantId}/{IndexRowKey}", tenantId, indexRowKey);
            }
        }

        /// <summary>
        /// Centralizes the SessionsIndex dual-write decision shared by the session-mutation paths
        /// (UpdateSessionStatusAsync normal + ETag force-write, IncrementSessionEventCountAsync): when
        /// an earlier event shifted StartedAt the index RowKey (inverted ticks) changes, so rebuild the
        /// row via a full upsert (delete-old + create-new); otherwise merge just the changed fields,
        /// resolving the IndexRowKey from the already-read session entity with a StartedAt fallback.
        /// </summary>
        /// <param name="sessionEntity">the session row already read on the mutation path (source of IndexRowKey / StartedAt fallback).</param>
        /// <param name="mergeFields">the changed-field entity to merge when StartedAt did NOT shift.</param>
        /// <param name="currentStartedAt">the session's current StartedAt (pre-shift).</param>
        /// <param name="earliestEventTimestamp">earliest observed event time; a value &lt; currentStartedAt means StartedAt shifted.</param>
        private async Task SyncSessionIndexAsync(
            string tenantId,
            string sessionId,
            TableEntity sessionEntity,
            TableEntity mergeFields,
            DateTime currentStartedAt,
            DateTime? earliestEventTimestamp)
        {
            if (earliestEventTimestamp.HasValue && earliestEventTimestamp.Value < currentStartedAt)
            {
                // StartedAt shifted — recompute IndexRowKey via full upsert (delete-old + create-new).
                var sessionsClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var fullEntity = (await sessionsClient.GetEntityAsync<TableEntity>(tenantId, sessionId)).Value;
                await UpsertSessionIndexAsync(fullEntity, earliestEventTimestamp.Value);
            }
            else
            {
                var indexRowKey = sessionEntity.GetString("IndexRowKey");

                // Fallback: if IndexRowKey was lost (e.g. StoreSessionAsync Replace race), compute it
                // from StartedAt + SessionId so the index still gets updated.
                if (string.IsNullOrEmpty(indexRowKey))
                {
                    var sessionStartedAt = sessionEntity.GetDateTimeOffset("StartedAt")?.UtcDateTime;
                    if (sessionStartedAt.HasValue)
                    {
                        indexRowKey = ComputeIndexRowKey(sessionStartedAt.Value, sessionId);
                        _logger.LogWarning("Session {SessionId}: IndexRowKey was null, computed fallback from StartedAt", sessionId);
                    }
                }

                await MergeSessionIndexAsync(tenantId, indexRowKey, mergeFields);
            }
        }

        /// <summary>
        /// Maps an index entity (from SessionsIndex table) to SessionSummary.
        /// The key difference from the primary-table mapping: SessionId comes from a stored property
        /// instead of RowKey (which contains the inverted-tick key in the index).
        /// </summary>
        // internal (not private) so SessionStatsProjectionEquivalenceTests can pin that a row
        // carrying only SessionStatsProjection maps to the same stat-relevant fields as a full row.
        internal SessionSummary MapIndexEntityToSessionSummary(TableEntity entity)
            => MapToSessionSummary(entity, entity.GetString("SessionId") ?? ExtractSessionIdFromIndexRowKey(entity.RowKey));

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
                // Cumulative script counters are maintained incrementally by IncrementSessionEventCountAsync.
                // They MUST survive re-registration — the Replace below would otherwise zero them on every
                // agent restart, undercounting scripts run before the restart and drifting Sessions vs. SessionsIndex.
                int platformScriptCount = 0;
                int remediationScriptCount = 0;
                // RebootCount is a cumulative counter maintained by IncrementSessionEventCountAsync.
                // It MUST survive re-registration — and a reboot is the very thing that triggers a
                // fresh agent registration, so a Replace that zeroed it would lose the in-flight count
                // every time it matters most. (The terminal recount would still self-correct the final
                // value, but the live value must not regress mid-enrollment.)
                int rebootCount = 0;
                DateTime? completedAt = null;
                string failureReason = string.Empty;
                string reconcileReason = string.Empty;
                bool isPreProvisioned = registration.IsPreProvisioned;
                bool isHybridJoin = registration.IsHybridJoin;
                bool isSelfDeployingProfile = registration.IsSelfDeployingProfile;
                DateTime? lastEventAt = null;
                int? durationSeconds = null;
                string? diagnosticsBlobName = null;
                string? diagnosticsBlobDestination = null;
                DateTime? resumedAt = null;
                DateTime? stalledAt = null;
                string geoCountry = string.Empty;
                string geoRegion = string.Empty;
                string geoCity = string.Empty;
                string geoLoc = string.Empty;
                string? existingIndexRowKey = null;
                // PR3: cascade-delete state-machine columns. Re-registration MUST preserve
                // these — losing them would silently clear the lock and let the agent's writes
                // race the in-flight cascade. Empty/null is fine on first registration.
                string? existingDeletionState = null;
                string? existingPendingDeletionManifestId = null;

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
                    platformScriptCount = existingEntity.GetInt32("PlatformScriptCount") ?? platformScriptCount;
                    remediationScriptCount = existingEntity.GetInt32("RemediationScriptCount") ?? remediationScriptCount;
                    rebootCount = existingEntity.GetInt32("RebootCount") ?? rebootCount;
                    completedAt = existingEntity.GetDateTimeOffset("CompletedAt")?.UtcDateTime;
                    failureReason = existingEntity.GetString("FailureReason") ?? string.Empty;
                    reconcileReason = existingEntity.GetString("ReconcileReason") ?? string.Empty;

                    // Preserve fields set by Merge-mode updates (SetSessionPreProvisionedAsync,
                    // UpdateSessionStatusAsync, UpdateSessionDiagnosticsBlobAsync) that would
                    // otherwise be lost by the UpsertEntity (Replace) below.
                    isPreProvisioned = existingEntity.GetBoolean("IsPreProvisioned") ?? isPreProvisioned;
                    isHybridJoin = existingEntity.GetBoolean("IsHybridJoin") ?? isHybridJoin;
                    // Sticky-true OR (not plain preserve): a re-registration after a boot where
                    // the Autopilot policy cache was momentarily unreadable must not pin the
                    // self-deploying marker to false forever — once observed true, it stays true.
                    isSelfDeployingProfile = (existingEntity.GetBoolean("IsSelfDeployingProfile") ?? false) || isSelfDeployingProfile;
                    lastEventAt = existingEntity.GetDateTimeOffset("LastEventAt")?.UtcDateTime;
                    durationSeconds = existingEntity.GetInt32("DurationSeconds");
                    diagnosticsBlobName = existingEntity.GetString("DiagnosticsBlobName");
                    diagnosticsBlobDestination = existingEntity.GetString("DiagnosticsBlobDestination");
                    resumedAt = existingEntity.GetDateTimeOffset("ResumedAt")?.UtcDateTime;
                    stalledAt = existingEntity.GetDateTimeOffset("StalledAt")?.UtcDateTime;
                    geoCountry = existingEntity.GetString("GeoCountry") ?? string.Empty;
                    geoRegion = existingEntity.GetString("GeoRegion") ?? string.Empty;
                    geoCity = existingEntity.GetString("GeoCity") ?? string.Empty;
                    geoLoc = existingEntity.GetString("GeoLoc") ?? string.Empty;
                    existingIndexRowKey = existingEntity.GetString("IndexRowKey");
                    existingDeletionState = existingEntity.GetString("DeletionState");
                    existingPendingDeletionManifestId = existingEntity.GetString("PendingDeletionManifestId");

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
                    ["IsSelfDeployingProfile"] = isSelfDeployingProfile,
                    ["StartedAt"] = EnsureUtc(startedAt),
                    ["AgentVersion"] = registration.AgentVersion ?? string.Empty,
                    ["EnrollmentType"] = registration.EnrollmentType ?? "v1",
                    ["CurrentPhase"] = currentPhase,
                    ["Status"] = status,
                    ["EventCount"] = eventCount,
                    ["PlatformScriptCount"] = platformScriptCount,
                    ["RemediationScriptCount"] = remediationScriptCount,
                    ["RebootCount"] = rebootCount
                };

                if (completedAt.HasValue)
                    entity["CompletedAt"] = EnsureUtc(completedAt.Value);

                if (!string.IsNullOrWhiteSpace(failureReason))
                    entity["FailureReason"] = failureReason;

                if (!string.IsNullOrWhiteSpace(reconcileReason))
                    entity["ReconcileReason"] = reconcileReason;

                if (lastEventAt.HasValue)
                    entity["LastEventAt"] = EnsureUtc(lastEventAt.Value);

                if (durationSeconds.HasValue)
                    entity["DurationSeconds"] = durationSeconds.Value;

                if (!string.IsNullOrWhiteSpace(diagnosticsBlobName))
                    entity["DiagnosticsBlobName"] = diagnosticsBlobName;

                if (!string.IsNullOrWhiteSpace(diagnosticsBlobDestination))
                    entity["DiagnosticsBlobDestination"] = diagnosticsBlobDestination;

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

                // PR3 (codex round 2 follow-up): ETag-bound CAS write loop. The plain
                // UpsertEntity (Replace) had a TOCTOU race against the cascade-delete producer:
                // (T1) StoreSessionAsync reads row → DeletionState=None, captures preserved fields.
                // (T2) Producer concurrently CAS-es None → Preparing.
                // (T3) StoreSessionAsync writes Replace → silently clobbers DeletionState=Preparing.
                // ETag-bound UpdateEntity makes T3 fail with 412 instead, the loop re-reads the
                // fresh row (now DeletionState=Preparing), re-stamps the cascade-lock columns
                // onto our entity, and retries. The initial-read extract above is preserved as a
                // best-effort hint; the per-iteration fresh read is what actually wins the race.
                const int MaxStoreSessionCasAttempts = 5;
                for (var casAttempt = 0; ; casAttempt++)
                {
                    Azure.ETag? freshEtag = null;
                    TableEntity? freshEntity = null;
                    try
                    {
                        var freshResponse = await tableClient.GetEntityAsync<TableEntity>(
                            registration.TenantId, registration.SessionId,
                            select: new[]
                            {
                                "DeletionState", "PendingDeletionManifestId",
                                // Terminal tuple — re-read so a terminal verdict that landed in the
                                // fresh-read window isn't reverted by the Replace below (see guard).
                                "Status", "CurrentPhase", "CompletedAt", "FailureReason", "ReconcileReason",
                                "FailureSnapshotJson", "FailureSource", "AdminMarkedAction", "DurationSeconds",
                            });
                        freshEtag = freshResponse.Value.ETag;
                        freshEntity = freshResponse.Value;
                    }
                    catch (RequestFailedException ex) when (ex.Status == 404)
                    {
                        // Row was deleted between the initial extract above and now (cascade
                        // tombstone happened, or maintenance cleanup raced). Fall through to
                        // AddEntity — re-creates the row from registration data only.
                    }

                    // Stamp cascade-lock columns from the FRESH read so the Replace below
                    // doesn't silently clear them. This kicks in even when the initial read
                    // saw None and the producer transitioned to Preparing in between.
                    var freshDeletionState = freshEntity?.GetString("DeletionState");
                    var freshPendingDeletionManifestId = freshEntity?.GetString("PendingDeletionManifestId");
                    if (!string.IsNullOrEmpty(freshDeletionState))
                        entity["DeletionState"] = freshDeletionState;
                    else if (!string.IsNullOrEmpty(existingDeletionState))
                        entity["DeletionState"] = existingDeletionState;
                    if (!string.IsNullOrEmpty(freshPendingDeletionManifestId))
                        entity["PendingDeletionManifestId"] = freshPendingDeletionManifestId;
                    else if (!string.IsNullOrEmpty(existingPendingDeletionManifestId))
                        entity["PendingDeletionManifestId"] = existingPendingDeletionManifestId;

                    // Guard the fresh-read window against a just-landed terminal. The initial-read
                    // guard at the top only sees the status as of the FIRST read; a terminal verdict
                    // (Succeeded/Failed/Incomplete — e.g. maintenance setting Incomplete, or a late
                    // completion setting Succeeded) can land between that read and this ETag-bound
                    // Replace. Without this the Replace would silently regress the terminal back to the
                    // stale registration status (and lose its completion/failure tuple). Terminal states
                    // are authoritative and irreversible — re-stamp the fresh terminal tuple verbatim so
                    // re-registration can never revert them (parity with IsTerminalTransitionAllowed).
                    var freshStatus = freshEntity?.GetString("Status");
                    if (freshStatus == SessionStatus.Succeeded.ToString()
                        || freshStatus == SessionStatus.Failed.ToString()
                        || freshStatus == SessionStatus.Incomplete.ToString())
                    {
                        entity["Status"] = freshStatus;
                        foreach (var col in new[] { "CurrentPhase", "CompletedAt", "FailureReason", "ReconcileReason", "FailureSnapshotJson", "FailureSource", "AdminMarkedAction", "DurationSeconds" })
                        {
                            if (freshEntity!.TryGetValue(col, out var val) && val is not null)
                                entity[col] = val;
                            else
                                entity.Remove(col); // absent on the fresh terminal row → don't carry a stale value through the Replace
                        }
                        _logger.LogInformation($"Session {registration.SessionId}: terminal '{freshStatus}' landed during re-registration, preserving it over the Replace");
                    }

                    try
                    {
                        if (freshEtag.HasValue)
                        {
                            await tableClient.UpdateEntityAsync(entity, freshEtag.Value, TableUpdateMode.Replace);
                        }
                        else
                        {
                            await tableClient.AddEntityAsync(entity);
                        }
                        break;
                    }
                    catch (RequestFailedException ex) when ((ex.Status == 412 || ex.Status == 409) && casAttempt < MaxStoreSessionCasAttempts - 1)
                    {
                        _logger.LogDebug(
                            "StoreSessionAsync ETag CAS conflict (status={Status}, attempt={Attempt}); retrying for tenant={TenantId} session={SessionId}",
                            ex.Status, casAttempt + 1, registration.TenantId, registration.SessionId);
                        continue;
                    }
                }

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

                foreach (var rawChunk in chunks)
                {
                    // Dedup by RowKey within the chunk. The RowKey is {Timestamp:ms}_{Sequence:D10};
                    // two events sharing the same millisecond AND Sequence map to one row and would
                    // collapse under UpsertReplace anyway — but Azure rejects the WHOLE transaction
                    // with InvalidDuplicateRow if both appear in one batch (then we degrade to slow
                    // per-event writes and track a noisy TableTransactionFailedException). Keeping the
                    // last occurrence (matches UpsertReplace last-wins) lets the batch succeed cleanly.
                    var chunk = rawChunk
                        .GroupBy(evt => $"{evt.Timestamp:yyyyMMddHHmmssfff}_{evt.Sequence:D10}")
                        .Select(g => g.Last())
                        .ToList();

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
            string tenantId, int maxResults, string? cursor, int? days, IEnumerable<string>? select = null)
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
                // select == null → full mirror row (every existing caller). The stats drain passes a
                // narrow projection so it doesn't materialize the ~40-column row just to tally statuses.
                var query = indexTableClient.QueryAsync<TableEntity>(
                    filter: filter,
                    maxPerPage: Math.Min(maxResults + 1, 1000),
                    select: select
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
        /// <summary>
        /// True when a BOUNDED (delegated/MSP) caller asks for a single-tenant drill that is OUTSIDE its
        /// managed set. The middleware enforces the allow-list for a cross-tenant target, but NOT when
        /// ?tenantId= equals the caller's own JWT tenant (crossTenant=false short-circuits the delegated
        /// scoped-route check) — so the single-tenant drill in the repo must re-check the bound itself.
        /// </summary>
        private static bool DrillOutsideBound(IReadOnlyCollection<string>? allowedTenantIds, string? tenantIdFilter)
            => allowedTenantIds != null
                && !string.IsNullOrEmpty(tenantIdFilter)
                && !new HashSet<string>(allowedTenantIds, StringComparer.OrdinalIgnoreCase).Contains(tenantIdFilter!);

        public async Task<RawPage<SessionSummary>> GetAllSessionsPageAsync(
            string? tenantIdFilter, int? days, int pageSize, string? continuation,
            IReadOnlyCollection<string>? allowedTenantIds = null)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

            // Bounded (delegated) single-tenant drill outside the managed set → empty, never the per-tenant scan.
            if (DrillOutsideBound(allowedTenantIds, tenantIdFilter))
                return new RawPage<SessionSummary>(new List<SessionSummary>(), null);

            // When tenantIdFilter is set and is a valid tenantId, route through
            // the per-tenant SessionsIndex scan — natively-ordered, cheaper.
            if (!string.IsNullOrEmpty(tenantIdFilter))
            {
                return await GetSessionsPageAsync(tenantIdFilter!, days, pageSize, continuation);
            }

            var page = await FetchAllSessionsPageInternalAsync(maxResults: pageSize, cursor: continuation, days: days, allowedTenantIds: allowedTenantIds);
            return new RawPage<SessionSummary>(page.Sessions, page.HasMore ? page.NextCursor : null);
        }

        /// <summary>
        /// Drains all sessions across all tenants — newest-first, no row cap.
        /// <paramref name="tenantIdFilter"/> optionally restricts to a single tenant
        /// (routed through the per-tenant index for efficiency).
        /// </summary>
        public async Task<List<SessionSummary>> GetAllSessionsAsync(string? tenantIdFilter = null, int? days = null, IReadOnlyCollection<string>? allowedTenantIds = null)
        {
            // Bounded (delegated) single-tenant drill outside the managed set → empty (covers stats too,
            // which funnel through here). See DrillOutsideBound.
            if (DrillOutsideBound(allowedTenantIds, tenantIdFilter))
                return new List<SessionSummary>();

            if (!string.IsNullOrEmpty(tenantIdFilter))
            {
                return await GetSessionsAsync(tenantIdFilter!, days);
            }

            var all = new List<SessionSummary>();
            string? token = null;
            do
            {
                var page = await GetAllSessionsPageAsync(tenantIdFilter: null, days, pageSize: 1000, continuation: token, allowedTenantIds: allowedTenantIds);
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
        // Columns AggregateSessionStats (and the ComputeEffectiveDuration it relies on for Succeeded
        // sessions) actually reads, plus PartitionKey/RowKey/SessionId for the TenantId map + page
        // cursor. Everything else on the ~40-column SessionsIndex mirror (FailureSnapshotJson, Os*,
        // Geo*, PendingActionsJson, …) is never consumed by the tally, so the stats drain skips it.
        // Crucially IsPreProvisioned/ResumedAt are omitted on purpose: they only feed the WhiteGlove
        // Part-2 duration branch, which is gated on status==InProgress, and the stats never read
        // DurationSeconds for InProgress sessions — so dropping them cannot change any reported value.
        // Equivalence is pinned by SessionStatsProjectionEquivalenceTests. Non-projected columns map
        // to null/default via the Safe*/`?? ` getters in MapToSessionSummary — no missing-column throw.
        // internal so the equivalence test derives its simulated projected row from this exact set
        // (any future column drop here is automatically reflected in the test's faithfulness).
        internal static readonly string[] SessionStatsProjection =
            { "PartitionKey", "RowKey", "SessionId", "Status", "StartedAt", "CompletedAt", "DurationSeconds" };

        public async Task<SessionStats> GetSessionStatsAsync(string tenantId, int days)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));

            // Stats are a pure status/duration tally, so drain the window with a projected
            // SessionsIndex scan instead of GetSessionsAsync's full-row materialization. Same
            // cursor/HasMore/fallback mechanics as the list drain (via FetchSessionsPageInternalAsync),
            // just a narrower column set — the returned partial summaries never leave this method.
            var sessions = new List<SessionSummary>();
            string? cursor = null;
            do
            {
                var page = await FetchSessionsPageInternalAsync(
                    tenantId, maxResults: 1000, cursor: cursor, days: days, select: SessionStatsProjection);
                sessions.AddRange(page.Sessions);
                cursor = page.HasMore ? page.NextCursor : null;
            } while (!string.IsNullOrEmpty(cursor));

            return AggregateSessionStats(sessions, days);
        }

        /// <summary>
        /// Cross-tenant variant. Routes through the per-tenant index when
        /// <paramref name="tenantIdFilter"/> is set (cheaper scan).
        /// </summary>
        public async Task<SessionStats> GetAllSessionStatsAsync(string? tenantIdFilter, int days, IReadOnlyCollection<string>? allowedTenantIds = null)
        {
            if (days < 1) throw new ArgumentOutOfRangeException(nameof(days));

            var sessions = await GetAllSessionsAsync(tenantIdFilter, days, allowedTenantIds);
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
            int incomplete = 0;
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
                    case SessionStatus.Incomplete:
                        // Terminal, non-failure. Reported separately, kept out of the failure rate.
                        incomplete++;
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
                IncompleteLastNDays = incomplete,
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
            int maxResults, string? cursor, int? days, IReadOnlyCollection<string>? allowedTenantIds = null)
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

                // Delegated ("MSP") bound: restrict the cross-tenant fan-out to the caller's managed subset.
                // This is the SAME per-tenant loop the Global Admin aggregate uses — only the tenant set
                // shrinks, so the merge/cursor/pagination below stay identical. Comparison is case-insensitive
                // because AllowedTenantIds is lowercased while the config PartitionKey casing is not guaranteed.
                if (allowedTenantIds != null)
                {
                    var allowed = new HashSet<string>(allowedTenantIds, StringComparer.OrdinalIgnoreCase);
                    tenantIds = tenantIds.Where(t => allowed.Contains(t)).ToList();
                }

                if (tenantIds.Count == 0 && string.IsNullOrEmpty(cursor))
                {
                    // A BOUNDED (delegated/MSP) request must NEVER fall back to the unbounded primary-table
                    // scan: an empty allowed set (managed tenant has no config row, config momentarily empty,
                    // or a casing mismatch) means "none of YOUR tenants" → empty page, not ALL tenants. The
                    // primary-table fallback is only the GA all-tenants safety net for a fresh/empty config.
                    if (allowedTenantIds != null)
                        return (new List<SessionSummary>(), false, null);

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
        /// Open (non-terminal) sessions of the same physical device within one tenant: Pending,
        /// InProgress, Stalled or AwaitingUser rows matching the SerialNumber. Server-side
        /// filtered partition query with a narrow projection — SerialNumber is not a key column,
        /// so this scans the tenant partition, which is acceptable for the once-per-registration
        /// supersede pass (misclassification audit 2026-07-16). Fail-soft: returns an empty list
        /// on storage errors so registration is never blocked.
        /// </summary>
        public async Task<List<SessionSummary>> GetOpenSessionsForDeviceAsync(string tenantId, string serialNumber)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            if (string.IsNullOrWhiteSpace(serialNumber))
                return new List<SessionSummary>();

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var filter = $"PartitionKey eq '{tenantId}' " +
                             $"and SerialNumber eq '{ODataSanitizer.EscapeValue(serialNumber)}' " +
                             $"and (Status eq 'Pending' or Status eq 'InProgress' or Status eq 'Stalled' or Status eq 'AwaitingUser')";
                var select = new[]
                {
                    "PartitionKey", "RowKey", "Status", "StartedAt", "ResumedAt", "LastEventAt",
                    "SerialNumber", "DeviceName", "IsPreProvisioned", "Model"
                };

                var sessions = new List<SessionSummary>();
                await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: filter, select: select))
                {
                    sessions.Add(MapToSessionSummary(entity));
                }
                return sessions;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get open sessions for device serial in tenant {tenantId}");
                return new List<SessionSummary>();
            }
        }

        /// <summary>
        /// Finds the tenantId for a session by scanning SessionsIndex.
        /// Used for Global Admin cross-tenant session lookup when tenantId is unknown.
        /// </summary>
        public async Task<string?> FindSessionTenantIdAsync(string sessionId)
        {
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
                await foreach (var entity in indexTableClient.QueryAsync<TableEntity>(
                    filter: $"SessionId eq '{ODataSanitizer.EscapeValue(sessionId)}'",
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
        /// The caller (EventIngestProcessor) provides earliestEventTimestamp from the current batch;
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
        /// <summary>
        /// Pure gate for the terminal/reconcile status rules (tasks/enrollment-status-reclassification.md).
        /// Returns whether a write to <paramref name="incoming"/> may proceed given the persisted
        /// <paramref name="existingStatus"/> string:
        /// <list type="bullet">
        /// <item>Succeeded may UPGRADE any non-Succeeded state (late completion reconcile); idempotent no-op if already Succeeded.</item>
        /// <item>Failed / Incomplete (silent-terminal verdicts) never overwrite an existing terminal (Succeeded/Failed/Incomplete).</item>
        /// <item>AwaitingUser never regresses an existing terminal.</item>
        /// <item>Non-terminal incoming statuses (InProgress/Stalled/Pending) are allowed here and governed by their own downstream guards.</item>
        /// </list>
        /// Static + pure so the reconcile matrix is unit-testable without Table Storage.
        /// </summary>
        internal static bool IsTerminalTransitionAllowed(string? existingStatus, SessionStatus incoming)
        {
            bool existingTerminal = existingStatus == SessionStatus.Succeeded.ToString()
                || existingStatus == SessionStatus.Failed.ToString()
                || existingStatus == SessionStatus.Incomplete.ToString();

            return incoming switch
            {
                SessionStatus.Succeeded => existingStatus != SessionStatus.Succeeded.ToString(),
                SessionStatus.Failed or SessionStatus.Incomplete => !existingTerminal,
                SessionStatus.AwaitingUser => !existingTerminal,
                _ => true,
            };
        }

        /// <summary>
        /// Computes the <c>ReconcileReason</c> to persist for a Succeeded transition — the
        /// operator-facing justification when the BACKEND (not the agent) declared the success.
        /// The reconcile hygiene clears FailureReason/FailureSnapshotJson on every Succeeded
        /// write, so without this field a sweep-reconciled session (session 294ab5b4) is
        /// indistinguishable from an agent-reported completion.
        /// <list type="bullet">
        /// <item>Admin-marked successes return null: attribution lives in AdminMarkedAction ("manual" badge).</item>
        /// <item>A caller-supplied reason (the maintenance sweep passes its classifier verdict) is persisted verbatim.</item>
        /// <item>A reason-less upgrade of a prior Failed/Incomplete/AwaitingUser verdict (late completion
        /// reconcile) synthesizes a text from the prior status.</item>
        /// <item>Normal agent completions (prior InProgress/Stalled/Pending, no reason) return null — the
        /// field stays absent.</item>
        /// </list>
        /// Static + pure so the matrix is unit-testable without Table Storage.
        /// </summary>
        internal static string? ComputeReconcileReason(string? existingStatus, string? reason, string? adminMarkedAction)
        {
            if (!string.IsNullOrEmpty(adminMarkedAction))
                return null;

            if (!string.IsNullOrEmpty(reason))
                return reason;

            bool priorWasBackendVerdictOrFailure = existingStatus == SessionStatus.Failed.ToString()
                || existingStatus == SessionStatus.Incomplete.ToString()
                || existingStatus == SessionStatus.AwaitingUser.ToString();
            return priorWasBackendVerdictOrFailure
                ? $"Late completion report received — upgraded prior '{existingStatus}' verdict"
                : null;
        }

        public async Task<bool> UpdateSessionStatusAsync(string tenantId, string sessionId, SessionStatus status, EnrollmentPhase? currentPhase = null, string? failureReason = null, DateTime? completedAt = null, DateTime? earliestEventTimestamp = null, DateTime? latestEventTimestamp = null, bool? isPreProvisioned = null, bool? isUserDriven = null, DateTime? resumedAt = null, DateTime? stalledAt = null, bool clearStalledAt = false, bool clearFailureReason = false, string? failureSource = null, string? adminMarkedAction = null, string? failureSnapshotJson = null, bool allowTerminalReclassification = false)
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

                    // Terminal / reconcile transition gate (tasks/enrollment-status-reclassification.md):
                    // a genuine completion (Succeeded) may UPGRADE a prior Failed/Incomplete/AwaitingUser
                    // verdict — a device that reached the desktop is enrolled regardless of an earlier timeout
                    // guess — but the silent-terminal verdicts (Failed/Incomplete) and AwaitingUser never
                    // overwrite an existing terminal. Non-terminal incoming statuses fall through to their
                    // own guards below.
                    var existingStatusStr = session.GetString("Status");
                    // allowTerminalReclassification (admin retro-reconcile ONLY, misclassification
                    // audit 2026-07-16): permits rewriting an existing terminal verdict — e.g. a
                    // legacy pre-classifier "Session timed out" Failed graduating to Incomplete.
                    // Hand-marked sessions stay untouchable (checked below), and an idempotent
                    // same-status write is still refused.
                    if (allowTerminalReclassification)
                    {
                        if (existingStatusStr == status.ToString())
                        {
                            _logger.LogInformation($"Session {sessionId}: reclassification to identical status '{status}' — no-op");
                            return false;
                        }
                        if (!string.IsNullOrEmpty(session.GetString("AdminMarkedAction")))
                        {
                            _logger.LogInformation($"Session {sessionId}: admin-marked terminal — reclassification refused");
                            return false;
                        }
                    }
                    else if (!IsTerminalTransitionAllowed(existingStatusStr, status))
                    {
                        _logger.LogInformation($"Session {sessionId}: transition '{existingStatusStr}' → '{status}' not allowed (terminal/reconcile guard), skipping");
                        return false;
                    }

                    // Preserve an administrator's explicit terminal decision: the late-completion reconcile
                    // (Succeeded upgrading a Failed/Incomplete row) must never silently override a session an
                    // admin marked terminal by hand (AdminMarkedAction is set only by the Mark*Session functions).
                    if (status == SessionStatus.Succeeded
                        && !string.IsNullOrEmpty(session.GetString("AdminMarkedAction"))
                        && (existingStatusStr == SessionStatus.Failed.ToString()
                            || existingStatusStr == SessionStatus.Incomplete.ToString()))
                    {
                        _logger.LogInformation($"Session {sessionId}: admin-marked terminal '{existingStatusStr}', not auto-reconciling to Succeeded");
                        return false;
                    }

                    // Build a Merge update with only the fields that actually change
                    var update = new TableEntity(tenantId, sessionId);

                    // Status transitions from EventIngestProcessor: Succeeded, Failed, Pending, Stalled,
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
                        // Reconcile hygiene: a session that (re)reaches Succeeded carries no failure reason or
                        // snapshot — clear anything left from a prior Failed/Incomplete/AwaitingUser verdict so
                        // a late-completed device doesn't keep showing a stale "timed out" reason.
                        update["FailureReason"] = string.Empty;
                        update["FailureSnapshotJson"] = string.Empty;
                        // Transparency: when the backend (not the agent) declares the success — sweep
                        // reconcile or late-completion upgrade — the justification the hygiene above
                        // just wiped must survive somewhere operators can see it.
                        var reconcileReason = ComputeReconcileReason(existingStatusStr, failureReason, adminMarkedAction);
                        if (reconcileReason != null)
                            update["ReconcileReason"] = reconcileReason;
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
                        // EventCount/RebootCount are NOT reconciled here. The ingest path runs the
                        // authoritative recount (ReconcileSessionCountersAsync) as the LAST counter
                        // write — after IncrementSessionEventCountAsync — so it can't be re-inflated
                        // by the generic increment, and it also covers the already-terminal
                        // batch-replay case where this method early-returns. Reconciling here
                        // (before the increment, and only on the normal non-force path) would do
                        // neither.
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
                    // Incomplete is terminal but not a completion — stamp a terminal timestamp so the
                    // session reads as closed, but do NOT compute DurationSeconds (a ~grace-length span
                    // would skew duration stats, and Incomplete is excluded from those anyway).
                    else if (status == SessionStatus.Incomplete)
                    {
                        update["CompletedAt"] = EnsureUtc(ResolveCompletionTimestamp(
                            completedAt,
                            session.GetDateTimeOffset("LastEventAt")?.UtcDateTime,
                            DateTime.UtcNow));
                    }

                    // Track the most recent event timestamp for excessive data sender detection
                    if (latestEventTimestamp.HasValue)
                    {
                        var currentLastEventAt = session.GetDateTimeOffset("LastEventAt")?.UtcDateTime;
                        if (!currentLastEventAt.HasValue || latestEventTimestamp.Value > currentLastEventAt.Value)
                            update["LastEventAt"] = EnsureUtc(latestEventTimestamp.Value);
                    }

                    // Set the reason string for the sweep-authored states. Failed keeps its failure
                    // reason; Incomplete and AwaitingUser carry the informational classification reason
                    // (same field reuse as the Stalled path below), so the UI can explain the state.
                    if ((status == SessionStatus.Failed || status == SessionStatus.Incomplete || status == SessionStatus.AwaitingUser)
                        && !string.IsNullOrEmpty(failureReason))
                    {
                        update["FailureReason"] = failureReason;
                    }

                    // Persist the failure-state snapshot when provided. Only the maintenance
                    // 5h-timeout path supplies this today (Hybrid User-Driven completion-gap
                    // fix, 2026-05-01); other terminal paths leave the field untouched, which
                    // is fine — null/empty means "no snapshot available" and the UI falls back
                    // to the existing FailureReason rendering.
                    if ((status == SessionStatus.Failed || status == SessionStatus.Incomplete || status == SessionStatus.AwaitingUser)
                        && !string.IsNullOrEmpty(failureSnapshotJson))
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

                    // Dual-write: keep SessionsIndex in sync (StartedAt-shift → full upsert, else merge).
                    await SyncSessionIndexAsync(tenantId, sessionId, session, update, currentStartedAt, earliestEventTimestamp);

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

                            // Re-check the transition guard against the freshly-read status using the
                            // SAME pure predicate as the normal path (IsTerminalTransitionAllowed) — the
                            // old hard-coded Succeeded/Failed check predated Incomplete/AwaitingUser and
                            // would let an unconditional force-write regress a terminal that landed during
                            // our retries (e.g. an Incomplete verdict stomping a concurrently-written
                            // Succeeded/Failed, or a second Incomplete overwriting the first).
                            var freshStatusStr = freshSession.GetString("Status");
                            // Mirror the normal path's override semantics (admin retro-reconcile):
                            // same-status and admin-marked rows stay refused even under override.
                            if (allowTerminalReclassification)
                            {
                                if (freshStatusStr == status.ToString()
                                    || !string.IsNullOrEmpty(freshSession.GetString("AdminMarkedAction")))
                                {
                                    _logger.LogInformation($"Session {sessionId}: reclassification force write refused (same status or admin-marked)");
                                    return false;
                                }
                            }
                            else if (!IsTerminalTransitionAllowed(freshStatusStr, status))
                            {
                                _logger.LogInformation($"Session {sessionId}: transition '{freshStatusStr}' → '{status}' not allowed (terminal/reconcile guard), skipping force write");
                                return false;
                            }

                            // Preserve an administrator's explicit terminal decision — mirror the normal
                            // path: a late-completion reconcile (Succeeded upgrading Failed/Incomplete) must
                            // never silently override a session an admin marked terminal by hand.
                            if (status == SessionStatus.Succeeded
                                && !string.IsNullOrEmpty(freshSession.GetString("AdminMarkedAction"))
                                && (freshStatusStr == SessionStatus.Failed.ToString()
                                    || freshStatusStr == SessionStatus.Incomplete.ToString()))
                            {
                                _logger.LogInformation($"Session {sessionId}: admin-marked terminal '{freshStatusStr}', not auto-reconciling to Succeeded (force path)");
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
                                // Mirror the normal path: a (re)Succeeded session carries no failure
                                // reason/snapshot — clear anything left from a prior Failed/Incomplete/
                                // AwaitingUser verdict so a late-completed device shows no stale reason.
                                forceUpdate["FailureReason"] = string.Empty;
                                forceUpdate["FailureSnapshotJson"] = string.Empty;
                                // Mirror the normal path's backend-verdict transparency (computed against
                                // the FRESH status so the synthesized upgrade text names the right prior).
                                var forceReconcileReason = ComputeReconcileReason(freshStatusStr, failureReason, adminMarkedAction);
                                if (forceReconcileReason != null)
                                    forceUpdate["ReconcileReason"] = forceReconcileReason;
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
                            // Mirror the normal path: Incomplete is terminal but not a completion — stamp a
                            // terminal timestamp so the session reads as closed, but do NOT compute
                            // DurationSeconds (a ~grace-length span would skew stats, and Incomplete is
                            // excluded from those anyway). AwaitingUser is non-terminal → no CompletedAt.
                            else if (status == SessionStatus.Incomplete)
                            {
                                forceUpdate["CompletedAt"] = EnsureUtc(ResolveCompletionTimestamp(
                                    completedAt,
                                    freshSession.GetDateTimeOffset("LastEventAt")?.UtcDateTime,
                                    DateTime.UtcNow));
                            }

                            if (latestEventTimestamp.HasValue)
                            {
                                var freshLastEventAt = freshSession.GetDateTimeOffset("LastEventAt")?.UtcDateTime;
                                if (!freshLastEventAt.HasValue || latestEventTimestamp.Value > freshLastEventAt.Value)
                                    forceUpdate["LastEventAt"] = EnsureUtc(latestEventTimestamp.Value);
                            }

                            // Mirror the normal path: Incomplete/AwaitingUser carry the classification
                            // reason too (same field reuse as Failed), not just Failed — otherwise the
                            // force-merge fallback persists a reason-less Incomplete/AwaitingUser under
                            // ETag contention and the UI can't explain the state.
                            if ((status == SessionStatus.Failed || status == SessionStatus.Incomplete || status == SessionStatus.AwaitingUser)
                                && !string.IsNullOrEmpty(failureReason))
                                forceUpdate["FailureReason"] = failureReason;

                            // Mirror the FailureSnapshotJson from the regular path so the force-merge
                            // fallback never silently drops the snapshot — the maintenance timeout path
                            // supplies it for Incomplete/AwaitingUser as well, not only Failed.
                            if ((status == SessionStatus.Failed || status == SessionStatus.Incomplete || status == SessionStatus.AwaitingUser)
                                && !string.IsNullOrEmpty(failureSnapshotJson))
                                forceUpdate["FailureSnapshotJson"] = failureSnapshotJson;

                            // Mirror FailureSource (failure attribution: agent / rule:<id> / manual)
                            // from the regular path — otherwise the force-merge fallback silently
                            // drops the origin of a Failed status under ETag contention.
                            if (status == SessionStatus.Failed && !string.IsNullOrEmpty(failureSource))
                                forceUpdate["FailureSource"] = failureSource;

                            // Mirror AdminMarkedAction — the authoritative trigger for the
                            // AdminAction response-field sent to agents (set only by Mark
                            // Succeeded/Failed). Dropping it here would silently neutralize an
                            // administrator-driven terminal override exactly when ETag retries
                            // are exhausted at the terminal transition.
                            if (!string.IsNullOrEmpty(adminMarkedAction))
                                forceUpdate["AdminMarkedAction"] = adminMarkedAction;

                            if (isPreProvisioned.HasValue)
                                forceUpdate["IsPreProvisioned"] = isPreProvisioned.Value;

                            if (isUserDriven.HasValue)
                                forceUpdate["IsUserDriven"] = isUserDriven.Value;

                            if (resumedAt.HasValue)
                                forceUpdate["ResumedAt"] = EnsureUtc(resumedAt.Value);

                            // Unconditional merge write — ETag.All bypasses concurrency check
                            await forceTableClient.UpdateEntityAsync(forceUpdate, ETag.All, TableUpdateMode.Merge);

                            // Dual-write: keep SessionsIndex in sync (StartedAt-shift → full upsert, else merge).
                            await SyncSessionIndexAsync(tenantId, sessionId, freshSession, forceUpdate, freshStartedAt, earliestEventTimestamp);

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
        /// Returns the post-merge snapshot (RMW read + applied increments) so the ingest hot path
        /// can skip its follow-up GetSessionAsync; null on missing row / exhausted retries / error.
        /// <para>
        /// Retry semantics: these read-modify-write increments are NOT idempotent under the
        /// agent's at-least-once retry (rows dedupe, counters double). EventCount and RebootCount
        /// are self-corrected at terminal transitions by <see cref="ReconcileSessionCountersAsync"/>;
        /// PlatformScriptCount/RemediationScriptCount stay increment-only — their drift is bounded
        /// (scripts per session are few) and no enforcement heuristic reads them.
        /// </para>
        /// </summary>
        public async Task<SessionSummary?> IncrementSessionEventCountAsync(string tenantId, string sessionId, int increment, DateTime? earliestEventTimestamp = null, DateTime? latestEventTimestamp = null, EnrollmentPhase? currentPhase = null, int platformScriptIncrement = 0, int remediationScriptIncrement = 0, int rebootIncrement = 0)
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
                        return null;
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

                    // Incremental reboot count (live value during enrollment). Callers pass
                    // rebootIncrement=0 on terminal batches: there the ingest path instead runs
                    // ReconcileSessionCountersAsync as the LAST counter write (authoritative
                    // distinct count from the Events table), which self-corrects any at-least-once
                    // double-count accumulated here.
                    if (rebootIncrement > 0)
                    {
                        var current = entity.GetInt32("RebootCount") ?? 0;
                        update["RebootCount"] = current + rebootIncrement;
                    }

                    await tableClient.UpdateEntityAsync(update, entity.ETag, TableUpdateMode.Merge);

                    // Dual-write: keep SessionsIndex in sync (StartedAt-shift → full upsert, else merge).
                    await SyncSessionIndexAsync(tenantId, sessionId, entity, update, currentStartedAt, earliestEventTimestamp);

                    // Apply the merged fields onto the RMW read and map it through the shared
                    // mapper — the post-merge snapshot the caller would otherwise re-read.
                    foreach (var kvp in update)
                    {
                        if (kvp.Key is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag")
                            continue;
                        entity[kvp.Key] = kvp.Value;
                    }
                    return MapToSessionSummary(entity);
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 412)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        _logger.LogWarning($"Failed to increment event count for session {sessionId} after {maxRetries} retries due to ETag conflicts");
                        return null;
                    }
                    // Exponential backoff with jitter to decorrelate concurrent retries
                    var baseDelay = 50 * (int)Math.Pow(2, retryCount - 1);
                    await Task.Delay(baseDelay + Random.Shared.Next(0, baseDelay));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to increment event count for session {sessionId}");
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Reconciles the stored <c>EventCount</c> and <c>RebootCount</c> with the authoritative
        /// row counts from the Events table. The ingest path calls this as the LAST counter write
        /// on terminal batches (after <see cref="IncrementSessionEventCountAsync"/>), so it always
        /// wins over the per-batch incremental values — self-correcting any at-least-once retry
        /// double-count (event rows have deterministic RowKeys and dedupe on UpsertReplace; the
        /// read-modify-write increments do not) and covering already-terminal batch replays (where
        /// <c>UpdateSessionStatusAsync</c> early-returns without touching the row). Fail-soft: if
        /// the authoritative counts can't be determined (storage error → null), the live values
        /// are left untouched rather than being zeroed. Idempotent: no-ops when the row is
        /// already correct. PlatformScriptCount/RemediationScriptCount stay increment-only (their
        /// drift is bounded and they feed no enforcement heuristics; EventCount does).
        /// </summary>
        public async Task ReconcileSessionCountersAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            var authoritative = await CountSessionEventRowsAsync(tenantId, sessionId);
            if (!authoritative.HasValue)
                return; // storage error — keep the incremental live values, do not zero them

            var (totalEvents, rebootEvents) = authoritative.Value;

            const int maxRetries = 5;
            int retryCount = 0;

            while (retryCount < maxRetries)
            {
                try
                {
                    var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                    var entityResponse = await tableClient.GetEntityIfExistsAsync<TableEntity>(tenantId, sessionId);
                    if (!entityResponse.HasValue)
                        return;
                    var entity = entityResponse.Value!;

                    // No-op when already correct — avoids a needless write + index sync on the
                    // common terminal-batch replay (and on the normal path once the live values
                    // already matched the events).
                    var update = new TableEntity(tenantId, sessionId);
                    bool drifted = false;
                    if ((entity.GetInt32("EventCount") ?? 0) != totalEvents)
                    {
                        update["EventCount"] = totalEvents;
                        drifted = true;
                    }
                    if ((entity.GetInt32("RebootCount") ?? 0) != rebootEvents)
                    {
                        update["RebootCount"] = rebootEvents;
                        drifted = true;
                    }
                    if (!drifted)
                        return;

                    var currentStartedAt = entity.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.MaxValue;

                    await tableClient.UpdateEntityAsync(update, entity.ETag, TableUpdateMode.Merge);

                    // Dual-write: keep SessionsIndex in sync (merge — no StartedAt shift here).
                    await SyncSessionIndexAsync(tenantId, sessionId, entity, update, currentStartedAt, null);

                    return;
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 412)
                {
                    retryCount++;
                    if (retryCount >= maxRetries)
                    {
                        _logger.LogWarning($"Failed to reconcile session counters for session {sessionId} after {maxRetries} retries due to ETag conflicts");
                        return;
                    }
                    var baseDelay = 50 * (int)Math.Pow(2, retryCount - 1);
                    await Task.Delay(baseDelay + Random.Shared.Next(0, baseDelay));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to reconcile session counters for session {sessionId}");
                    return;
                }
            }
        }

        /// <summary>
        /// Gets all events for a specific session. Fail-soft: storage exceptions degrade to an
        /// empty list (read-only UI/metrics surfaces prefer a blank timeline over a 500) — which
        /// makes a read FAILURE indistinguishable from a genuinely empty session. Callers whose
        /// retry semantics depend on observing failures (queue workers) must use
        /// <see cref="GetSessionEventsStrictAsync"/> instead.
        /// </summary>
        public async Task<List<EnrollmentEvent>> GetSessionEventsAsync(string tenantId, string sessionId, int maxResults = 1000)
        {
            // Validate before the catch — malformed GUIDs are caller bugs and must not be
            // masked as "no events" (same behaviour as before the strict/soft split).
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                return await GetSessionEventsStrictAsync(tenantId, sessionId, maxResults);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to get events for session {sessionId}");
                return new List<EnrollmentEvent>();
            }
        }

        /// <summary>
        /// Strict variant of <see cref="GetSessionEventsAsync"/>: storage exceptions PROPAGATE.
        /// Used on the queue-worker paths (rule analysis, vulnerability correlation) where a
        /// swallowed read failure would convert a retryable transient fault into silent data
        /// loss — the worker would delete its message after "analyzing" a truncated stream.
        /// A genuinely empty session still returns an empty list (successful query, zero rows).
        /// </summary>
        public async Task<List<EnrollmentEvent>> GetSessionEventsStrictAsync(string tenantId, string sessionId, int maxResults = 1000)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

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
        /// Literal-row variant of <see cref="GetSessionEventsPageAsync"/> backing the single-session
        /// path of <c>/api/raw/events</c>. Emits the raw <c>Events</c> rows verbatim (DataJson as the
        /// stored string, Severity/Phase as the stored ints, no error-code enrichment) instead of
        /// mapped <see cref="EnrollmentEvent"/> objects. Items are ordered by Sequence ascending —
        /// the same authoritative order the mapped path uses.
        /// </summary>
        public async Task<RawPage<IReadOnlyDictionary<string, object?>>> GetSessionEventsRawPageAsync(
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

                var rows = entities
                    .OrderBy(e => e.GetInt64("Sequence") ?? 0L)
                    .Select(e => (IReadOnlyDictionary<string, object?>)RawEntityProjection.ToDictionary(e))
                    .ToList();

                return new RawPage<IReadOnlyDictionary<string, object?>>(rows, nextRawToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get raw events page for session {SessionId}", sessionId);
                return RawPage<IReadOnlyDictionary<string, object?>>.Empty;
            }
        }

        /// <summary>
        /// Literal-row variant of <see cref="GetSessionEventsByTypeAsync"/> backing the cross-session
        /// path of <c>/api/raw/events</c>. Returns raw <c>Events</c> rows of one event type for a
        /// session, ordered by Sequence ascending.
        /// </summary>
        public async Task<List<IReadOnlyDictionary<string, object?>>> GetSessionEventsRawByTypeAsync(
            string tenantId, string sessionId, string eventType, int maxResults = 200)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
                var rows = new List<(long Seq, IReadOnlyDictionary<string, object?> Row)>();

                var partitionKey = $"{tenantId}_{sessionId}";
                var filter = $"PartitionKey eq '{ODataSanitizer.EscapeValue(partitionKey)}' and EventType eq '{ODataSanitizer.EscapeValue(eventType)}'";

                var query = tableClient.QueryAsync<TableEntity>(
                    filter: filter,
                    maxPerPage: maxResults);

                await foreach (var entity in query)
                {
                    rows.Add((entity.GetInt64("Sequence") ?? 0L, RawEntityProjection.ToDictionary(entity)));
                }

                return rows.OrderBy(r => r.Seq).Select(r => r.Row).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get raw events by type {EventType} for session {SessionId}", eventType, sessionId);
                return new List<IReadOnlyDictionary<string, object?>>();
            }
        }

        /// <summary>
        /// Stores the diagnostics blob name + destination on an existing session
        /// (Merge-mode, two-field update written together so the download path always
        /// sees a consistent (name, destination) pair). Destination encodes whether the
        /// blob lives in the customer's SAS-backed container or in the backend's hosted
        /// container — required so that a later tenant switch from one destination to
        /// the other doesn't break downloads for already-uploaded sessions.
        /// </summary>
        public async Task UpdateSessionDiagnosticsBlobAsync(
            string tenantId, string sessionId, string blobName, string? destination = null)
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

                if (!string.IsNullOrWhiteSpace(destination))
                {
                    update["DiagnosticsBlobDestination"] = destination;
                }

                await tableClient.UpdateEntityAsync(update, entity.Value.ETag, Azure.Data.Tables.TableUpdateMode.Merge);

                // Dual-write: merge into SessionsIndex
                var indexRowKey = entity.Value.GetString("IndexRowKey");
                await MergeSessionIndexAsync(tenantId, indexRowKey, update);

                _logger.LogInformation(
                    "Stored diagnostics blob name for session {SessionId}: {BlobName} (destination={Destination})",
                    sessionId, blobName, destination ?? "(unchanged)");
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
        /// <para>
        /// Uses <see cref="TableClient.UpdateEntityAsync(ITableEntity, ETag, TableUpdateMode, System.Threading.CancellationToken)"/>
        /// with <see cref="ETag.All"/>, not <c>UpsertEntityAsync</c>: a tombstoned Sessions row
        /// must stay tombstoned. The previous Upsert would silently recreate a partial Sessions
        /// row (PK/RK + ImeAgentVersion only) after the cascade-delete worker had removed it,
        /// breaking the manifest-snapshot invariant. 404 here means "row already gone" and is
        /// the correct no-op outcome.
        /// </para>
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
                await tableClient.UpdateEntityAsync(entity, ETag.All, TableUpdateMode.Merge);

                // Mirror to SessionsIndex — ImeAgentVersion is a search-filterable index column
                // (search/MCP push an OData filter on it against the index), so it must be kept in
                // sync or the filter matches nothing and the listed value is always empty.
                var idxRef = await tableClient.GetEntityAsync<TableEntity>(
                    tenantId, sessionId, select: new[] { "IndexRowKey" });
                var indexRowKey = idxRef.Value.GetString("IndexRowKey");
                if (!string.IsNullOrEmpty(indexRowKey))
                {
                    await MergeSessionIndexAsync(tenantId, indexRowKey,
                        new TableEntity(tenantId, indexRowKey) { ["ImeAgentVersion"] = version });
                }
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogDebug(
                    "UpdateSessionImeAgentVersion no-op: Sessions row {Tenant}/{Session} is absent (tombstoned or never registered)",
                    tenantId, sessionId);
            }
            catch (Exception ex)
            {
                // Non-fatal — don't block ingest
                _logger.LogWarning(ex, "Failed to update ImeAgentVersion for session {SessionId}", sessionId);
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
        /// Maps a TableEntity from the primary Sessions table to SessionSummary.
        /// SessionId is the entity RowKey on the primary table.
        /// </summary>
        // internal (not private) so UsageMetricsProjectionEquivalenceTests can pin that a Sessions
        // row carrying only UsageMetricsSessionProjection maps to the same usage-relevant fields
        // as a full row.
        internal SessionSummary MapToSessionSummary(TableEntity entity)
            => MapToSessionSummary(entity, entity.RowKey);

        /// <summary>
        /// Core entity → SessionSummary mapper shared by the primary-table and SessionsIndex paths.
        /// The two tables only differ in how SessionId is sourced (RowKey vs. stored property), so
        /// callers pass the resolved <paramref name="sessionId"/> and everything else maps identically.
        /// </summary>
        private SessionSummary MapToSessionSummary(TableEntity entity, string sessionId)
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
                _logger.LogWarning($"Failed to parse status '{statusString}' for session {sessionId}, defaulting to Unknown");
                status = SessionStatus.Unknown;
            }

            return new SessionSummary
            {
                SessionId = sessionId,
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
                ReconcileReason = entity.GetString("ReconcileReason") ?? string.Empty,
                AdminMarkedAction = entity.GetString("AdminMarkedAction"),
                PendingActionsJson = entity.GetString("PendingActionsJson") ?? string.Empty,
                PendingActionsQueuedAt = SafeGetDateTime(entity, "PendingActionsQueuedAt"),
                EventCount = SafeGetInt32(entity, "EventCount") ?? 0,
                DurationSeconds = ComputeEffectiveDuration(entity, status, startedAt, completedAt),
                EnrollmentType = entity.GetString("EnrollmentType") ?? "v1",
                DiagnosticsBlobName = entity.GetString("DiagnosticsBlobName"),
                // Legacy rows that predate this field surface as null → download path treats
                // null as "CustomerSas" for back-compat.
                DiagnosticsBlobDestination = entity.GetString("DiagnosticsBlobDestination"),
                LastEventAt = SafeGetDateTime(entity, "LastEventAt"),
                IsPreProvisioned = entity.GetBoolean("IsPreProvisioned") ?? false,
                IsHybridJoin = entity.GetBoolean("IsHybridJoin") ?? false,
                IsSelfDeployingProfile = entity.GetBoolean("IsSelfDeployingProfile") ?? false,
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
                RebootCount = SafeGetInt32(entity, "RebootCount") ?? 0,
                ExcessiveEventsAlerted = entity.GetBoolean("ExcessiveEventsAlerted") ?? false,
                ExcessiveEventsAutoActioned = entity.GetBoolean("ExcessiveEventsAutoActioned") ?? false,
                FailureSnapshotJson = entity.GetString("FailureSnapshotJson") ?? string.Empty,
                // PR3: cascade-delete state-machine columns. Empty/null on legacy rows is fine —
                // SessionDeletionGuard treats null/empty as "None" (no cascade in flight).
                DeletionState = entity.GetString("DeletionState") ?? string.Empty,
                PendingDeletionManifestId = entity.GetString("PendingDeletionManifestId"),
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

        /// <summary>
        /// Returns the authoritative (total, reboot) event-row counts for a session, or <c>null</c>
        /// if they could not be determined (storage error). Events are written with UpsertReplace
        /// and a unique RowKey per event, so the number of stored rows equals the true distinct
        /// count — making this an idempotent, retry-immune authoritative count (used to reconcile
        /// EventCount + RebootCount at the terminal transition). Single partition scan with a
        /// RowKey+EventType projection so both counts cost one query.
        /// </summary>
        private async Task<(int Total, int Reboots)?> CountSessionEventRowsAsync(string tenantId, string sessionId)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Events);
                var partitionKey = $"{tenantId}_{sessionId}";
                var query = tableClient.QueryAsync<TableEntity>(
                    filter: $"PartitionKey eq '{partitionKey}'",
                    select: new[] { "RowKey", "EventType" }
                );

                int total = 0, reboots = 0;
                await foreach (var entity in query)
                {
                    total++;
                    if (entity.GetString("EventType") == Constants.EventTypes.SystemRebootDetected)
                        reboots++;
                }
                return (total, reboots);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, $"Could not count event rows for session {sessionId}");
                return null;
            }
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
                    // SerialNumber + ExcessiveEventsAutoActioned added for the auto-block/kill
                    // path in MaintenanceService — lets us decide and execute in one round-trip
                    // per qualifying session, without a follow-up GetSessionAsync.
                    select: new[] { "PartitionKey", "RowKey", "EventCount", "SerialNumber", "ExcessiveEventsAlerted", "ExcessiveEventsAutoActioned" });

                await foreach (var entity in query)
                {
                    matches.Add(new SessionSummary
                    {
                        TenantId = entity.PartitionKey,
                        SessionId = entity.RowKey,
                        EventCount = SafeGetInt32(entity, "EventCount") ?? 0,
                        SerialNumber = entity.GetString("SerialNumber") ?? string.Empty,
                        ExcessiveEventsAlerted = entity.GetBoolean("ExcessiveEventsAlerted") ?? false,
                        ExcessiveEventsAutoActioned = entity.GetBoolean("ExcessiveEventsAutoActioned") ?? false
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

        /// <summary>
        /// Sets ExcessiveEventsAutoActioned=true on the session entity so the maintenance
        /// auto-block/kill emission is idempotent. Best-effort Merge write; failures are
        /// logged only (warn-path stays intact even if this fails).
        /// </summary>
        public async Task MarkExcessiveEventsAutoActionedAsync(string tenantId, string sessionId)
        {
            SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
            SecurityValidator.EnsureValidGuid(sessionId, nameof(sessionId));

            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                var update = new TableEntity(tenantId, sessionId)
                {
                    ["ExcessiveEventsAutoActioned"] = true
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
                    _logger.LogDebug(ex, "SessionsIndex merge skipped for ExcessiveEventsAutoActioned on {SessionId}", sessionId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to mark session {SessionId} as ExcessiveEventsAutoActioned", sessionId);
            }
        }

        #endregion
    }
}
