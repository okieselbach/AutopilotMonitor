using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Vulnerability;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services
{
    public partial class TableStorageService
    {
        // ===== EVENT TYPE INDEX =====

        /// <summary>
        /// Upserts entries into the EventTypeIndex table for each distinct event type in the batch.
        /// PartitionKey = {tenantId}_{eventType}, RowKey = {invertedTicks}_{sessionId}
        /// This enables efficient "find all sessions with event X" queries.
        /// </summary>
        public async Task UpsertEventTypeIndexBatchAsync(string tenantId, string sessionId, IEnumerable<AutopilotMonitor.Shared.Models.EnrollmentEvent> events)
        {
            try
            {
                var eventList = events.ToList();
                if (eventList.Count == 0) return;

                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.EventTypeIndex);

                // Obtain stable StartedAt for deterministic RowKey (one GET per batch call)
                DateTime startedAt;
                try
                {
                    var sessionsTable = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
                    var sessionEntity = await sessionsTable.GetEntityAsync<TableEntity>(tenantId, sessionId,
                        select: new[] { "StartedAt" });
                    startedAt = sessionEntity.Value.GetDateTimeOffset("StartedAt")?.UtcDateTime ?? DateTime.UtcNow;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    startedAt = eventList.Min(e => e.Timestamp);
                }

                var rowKey = $"{(DateTime.MaxValue.Ticks - startedAt.Ticks):D19}_{sessionId}";

                var groups = eventList.GroupBy(e => e.EventType);

                foreach (var group in groups)
                {
                    var eventType = group.Key;
                    var partitionKey = $"{tenantId}_{eventType}";

                    var batchMaxSeverity = (int)group.Max(e => e.Severity);
                    var batchSources = group
                        .Where(e => !string.IsNullOrEmpty(e.Source))
                        .Select(e => e.Source!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    for (int retry = 0; retry < 3; retry++)
                    {
                        try
                        {
                            int existingMaxSeverity = -2;
                            var existingSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            ETag etag = default;
                            bool exists = false;

                            try
                            {
                                var existing = await tableClient.GetEntityAsync<TableEntity>(partitionKey, rowKey);
                                existingMaxSeverity = existing.Value.GetInt32("MaxSeverity") ?? -2;
                                var sourcesStr = existing.Value.GetString("Sources") ?? "";
                                if (!string.IsNullOrEmpty(sourcesStr))
                                {
                                    foreach (var s in sourcesStr.Split(','))
                                        existingSources.Add(s.Trim());
                                }
                                etag = existing.Value.ETag;
                                exists = true;
                            }
                            catch (RequestFailedException ex2) when (ex2.Status == 404) { }

                            var mergedMaxSeverity = Math.Max(existingMaxSeverity, batchMaxSeverity);
                            existingSources.UnionWith(batchSources);
                            var mergedSources = string.Join(",", existingSources.OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
                            var mergedSeverityName = ((AutopilotMonitor.Shared.Models.EventSeverity)mergedMaxSeverity).ToString();

                            var entity = new TableEntity(partitionKey, rowKey)
                            {
                                ["SessionId"] = sessionId,
                                ["TenantId"] = tenantId,
                                ["EventType"] = eventType,
                                ["MaxSeverity"] = mergedMaxSeverity,
                                ["MaxSeverityName"] = mergedSeverityName,
                                ["Sources"] = mergedSources,
                            };

                            if (exists)
                                await tableClient.UpdateEntityAsync(entity, etag, TableUpdateMode.Replace);
                            else
                                await tableClient.UpsertEntityAsync(entity);

                            break;
                        }
                        catch (RequestFailedException ex3) when (ex3.Status == 412 && retry < 2)
                        {
                            // ETag conflict — retry with fresh read
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upsert EventTypeIndex for session {SessionId}", sessionId);
            }
        }

        // ===== DEVICE SNAPSHOT =====

        private static readonly HashSet<string> _deviceSnapshotEventTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "tpm_status",
            "autopilot_profile",
            "secureboot_status",
            "bitlocker_status",
            "hardware_spec",
            "network_interface_info",
            "aad_join_status"
        };

        /// <summary>
        /// Upserts a DeviceSnapshot entry for the session using one Props_{eventType} JSON column
        /// per device event type. This stores ALL properties (including arrays) without explicit mapping.
        /// New agent properties become searchable automatically — zero code changes required.
        /// Existing columns are preserved (first-seen wins) via merge-on-write.
        /// </summary>
        public async Task UpsertDeviceSnapshotAsync(string tenantId, string sessionId, IEnumerable<AutopilotMonitor.Shared.Models.EnrollmentEvent> events)
        {
            try
            {
                var relevantEvents = events.Where(e => _deviceSnapshotEventTypes.Contains(e.EventType)).ToList();
                if (relevantEvents.Count == 0) return;

                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceSnapshot);

                TableEntity entity;
                try
                {
                    var existing = await tableClient.GetEntityAsync<TableEntity>(tenantId, sessionId);
                    entity = existing.Value;
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    entity = new TableEntity(tenantId, sessionId)
                    {
                        ["SessionId"] = sessionId,
                        ["TenantId"] = tenantId,
                    };
                }

                foreach (var evt in relevantEvents)
                {
                    if (evt.Data == null || evt.Data.Count == 0) continue;

                    var columnName = $"Props_{evt.EventType}";

                    // First-seen wins: don't overwrite existing event data
                    if (entity.ContainsKey(columnName) && entity.GetString(columnName) != null)
                        continue;

                    try
                    {
                        var data = evt.Data;

                        // Compute derived values for hardware_spec
                        if (evt.EventType.Equals("hardware_spec", StringComparison.OrdinalIgnoreCase))
                        {
                            var hasSSD = DetectHasSSD(data);
                            if (hasSSD.HasValue && !data.ContainsKey("hasSSD"))
                                data["hasSSD"] = hasSSD.Value;
                        }

                        var json = System.Text.Json.JsonSerializer.Serialize(data);

                        // Size guard: warn if approaching 64 KB column limit
                        if (json.Length > 50_000)
                        {
                            _logger.LogWarning(
                                "DeviceSnapshot column {Column} for session {SessionId} is {Size} bytes (approaching 64KB limit)",
                                columnName, sessionId, json.Length);
                        }

                        entity[columnName] = json;
                    }
                    catch (Exception evtEx)
                    {
                        _logger.LogDebug(evtEx, "DeviceSnapshot: error processing event type {EventType}", evt.EventType);
                    }
                }

                await tableClient.UpsertEntityAsync(entity);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upsert DeviceSnapshot for session {SessionId}", sessionId);
            }
        }

        internal static bool? DetectHasSSD(Dictionary<string, object> data)
        {
            try
            {
                if (!data.TryGetValue("disks", out var disksObj) || disksObj == null) return null;
                if (disksObj is not System.Collections.IEnumerable diskList) return null;

                foreach (var disk in diskList)
                {
                    if (disk is not Dictionary<string, object> diskDict) continue;

                    if (diskDict.TryGetValue("mediaType", out var mt))
                    {
                        // The agent reports composite media types such as "NVMe SSD", so match on
                        // substring rather than equality (an exact compare missed every real disk).
                        var mtStr = mt?.ToString() ?? "";
                        if (mtStr.IndexOf("SSD", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            mtStr.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
                return false;
            }
            catch
            {
                return null;
            }
        }

        // ===== CVE INDEX =====

        /// <summary>
        /// Upserts CVE index entries so sessions can be searched by CVE identifier.
        /// PartitionKey = {tenantId}_{cveId}, RowKey = sessionId
        /// Uses individual parallel upserts (not batch transactions) because PK differs per CVE.
        /// </summary>
        public async Task UpsertCveIndexEntriesAsync(string tenantId, string sessionId, List<Dictionary<string, object>> findings)
        {
            try
            {
                var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.CveIndex);
                var tasks = new List<Task>();

                foreach (var finding in findings)
                {
                    var softwareName = finding.TryGetValue("softwareName", out var sn) ? sn?.ToString() ?? "" : "";
                    var overallRisk = finding.TryGetValue("riskLevel", out var rl) ? rl?.ToString() ?? "" : "";

                    if (!finding.TryGetValue("vulnerabilities", out var vulnsObj) || vulnsObj == null) continue;
                    if (vulnsObj is not System.Collections.IEnumerable vulnList) continue;

                    foreach (var vulnObj in vulnList)
                    {
                        Dictionary<string, object>? vuln = null;
                        if (vulnObj is Dictionary<string, object> vd)
                            vuln = vd;

                        if (vuln == null) continue;

                        var cveId = vuln.TryGetValue("cveId", out var cid) ? cid?.ToString() : null;
                        if (string.IsNullOrEmpty(cveId)) continue;

                        var partitionKey = $"{tenantId}_{cveId}";
                        double cvssScore = 0;
                        try { if (vuln.TryGetValue("cvssScore", out var cs) && cs != null) cvssScore = Convert.ToDouble(cs); } catch { }

                        var cvssSeverity = vuln.TryGetValue("cvssSeverity", out var csvs) ? csvs?.ToString() ?? "" : "";
                        bool isKev = false;
                        try { if (vuln.TryGetValue("isKev", out var ik) && ik is bool ikb) isKev = ikb; } catch { }

                        var entity = new TableEntity(partitionKey, sessionId)
                        {
                            ["SessionId"] = sessionId,
                            ["TenantId"] = tenantId,
                            ["CveId"] = cveId,
                            ["SoftwareName"] = softwareName,
                            ["CvssScore"] = cvssScore,
                            ["CvssSeverity"] = cvssSeverity,
                            ["IsKev"] = isKev,
                            ["OverallRisk"] = overallRisk,
                            ["DetectedAt"] = DateTime.UtcNow,
                        };

                        tasks.Add(tableClient.UpsertEntityAsync(entity));
                    }
                }

                if (tasks.Count > 0)
                    await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to upsert CveIndex entries for session {SessionId}", sessionId);
            }
        }

        // ===== SEARCH METHODS =====

        /// <summary>Typeahead only inspects sessions started within this many days (recent-window scan bound).</summary>
        private const int QuickSearchWindowDays = 90;

        /// <summary>Hard upper bound on index rows a single typeahead request may scan (cost backstop).</summary>
        private const int QuickSearchMaxScannedRows = 5000;

        /// <summary>
        /// Columns the typeahead scan projects: the three matched identity fields, Status + StartedAt
        /// for the result, and RowKey for the SessionId fallback (<see cref="ExtractSessionIdFromIndexRowKey"/>).
        /// internal so <c>QuickSearchProjectionEquivalenceTests</c> derives its keep-set from this
        /// array — dropping a column here immediately fails the equivalence test.
        /// </summary>
        internal static readonly string[] QuickSearchProjection =
            { "RowKey", "SessionId", "SerialNumber", "DeviceName", "Status", "StartedAt" };

        /// <summary>
        /// Lightweight typeahead search: matches SessionId, SerialNumber, or DeviceName.
        /// Supports exact contains match (priority) and fuzzy match (edit distance &lt;= 2).
        /// When tenantId is null, searches across all tenants (Global Admin).
        /// </summary>
        public async Task<List<QuickSearchResult>> QuickSearchSessionsAsync(string? tenantId, string query, int limit = 10)
        {
            var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
            var exactResults = new List<QuickSearchResult>();
            var fuzzyResults = new List<QuickSearchResult>();
            var q = query.Trim();

            // Bound the scan so a single typeahead request cannot walk an ever-growing index:
            //   1. Recent-window bound — RowKey is the inverted-tick prefix (newest-first), so
            //      `RowKey lt '{cutoff}'` keeps the scan to sessions started within the window.
            //      Typeahead targets recent enrollments; older sessions are out of scope.
            //   2. Hard scanned-row cap — backstop for the fuzzy-only / no-match case, which
            //      otherwise reads every remaining row in the partition (or the whole table for
            //      global scope) because exact-over-fuzzy ordering forbids an early break.
            var windowBound = $"RowKey lt '{ComputeCutoffRowKeyPrefix(QuickSearchWindowDays)}'";
            string filter = !string.IsNullOrEmpty(tenantId)
                ? $"PartitionKey eq '{tenantId}' and {windowBound}"
                : windowBound;

            var scannedRows = 0;
            // Project to only the typeahead-matched fields (+ RowKey for the SessionId fallback at
            // ExtractSessionIdFromIndexRowKey). Avoids pulling the full ~40-column SessionsIndex
            // mirror for up to QuickSearchMaxScannedRows rows on every keystroke-driven request.
            await foreach (var entity in indexTableClient.QueryAsync<TableEntity>(
                filter: filter, select: QuickSearchProjection))
            {
                if (++scannedRows > QuickSearchMaxScannedRows)
                    break;

                var (sessionId, serial, deviceName) = ReadQuickSearchIdentity(entity);

                // Phase 1: exact substring match (highest priority)
                string? matchedField = null;
                if (sessionId.Contains(q, StringComparison.OrdinalIgnoreCase))
                    matchedField = "sessionId";
                else if (serial.Contains(q, StringComparison.OrdinalIgnoreCase))
                    matchedField = "serialNumber";
                else if (deviceName.Contains(q, StringComparison.OrdinalIgnoreCase))
                    matchedField = "deviceName";

                if (matchedField != null)
                {
                    exactResults.Add(BuildQuickSearchResult(entity, sessionId, serial, deviceName, matchedField));
                    if (exactResults.Count >= limit) break;
                    continue;
                }

                // Phase 2: fuzzy match — only if we don't have enough exact results yet
                if (exactResults.Count + fuzzyResults.Count >= limit)
                    continue;

                if (FuzzyContains(serial, q, maxDistance: 2))
                    matchedField = "serialNumber";
                else if (FuzzyContains(deviceName, q, maxDistance: 2))
                    matchedField = "deviceName";

                if (matchedField != null)
                {
                    fuzzyResults.Add(BuildQuickSearchResult(entity, sessionId, serial, deviceName, matchedField));
                }
            }

            // Exact matches first, then fuzzy, capped to limit
            exactResults.AddRange(fuzzyResults);
            return exactResults.Count > limit ? exactResults.GetRange(0, limit) : exactResults;
        }

        /// <summary>
        /// Resolves the three identity fields a typeahead row matches on, applying the SessionId ->
        /// RowKey fallback. internal + static so <c>QuickSearchProjectionEquivalenceTests</c> can drive
        /// the exact production read against a full vs a projected row.
        /// </summary>
        internal static (string SessionId, string Serial, string DeviceName) ReadQuickSearchIdentity(TableEntity entity)
            => (entity.GetString("SessionId") ?? ExtractSessionIdFromIndexRowKey(entity.RowKey),
                entity.GetString("SerialNumber") ?? string.Empty,
                entity.GetString("DeviceName") ?? string.Empty);

        // internal (not private) so QuickSearchProjectionEquivalenceTests can pin that a row carrying
        // only QuickSearchProjection produces the same result (Status/StartedAt) as a full mirror row.
        internal QuickSearchResult BuildQuickSearchResult(
            TableEntity entity, string sessionId, string serial, string deviceName, string matchedField)
        {
            var statusString = entity.GetString("Status") ?? "InProgress";
            if (!Enum.TryParse<SessionStatus>(statusString, ignoreCase: true, out var status))
                status = SessionStatus.Unknown;

            return new QuickSearchResult
            {
                SessionId = sessionId,
                SerialNumber = serial,
                DeviceName = deviceName,
                Status = status,
                StartedAt = SafeGetDateTime(entity, "StartedAt") ?? DateTime.UtcNow,
                MatchedField = matchedField,
            };
        }

        /// <summary>
        /// Checks if any substring of <paramref name="haystack"/> of length <paramref name="needle"/>.Length
        /// is within edit distance <paramref name="maxDistance"/> of the needle (case-insensitive).
        /// Uses a sliding-window Levenshtein approach for efficiency.
        /// </summary>
        private static bool FuzzyContains(string haystack, string needle, int maxDistance)
        {
            if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle))
                return false;

            var h = haystack.ToLowerInvariant();
            var n = needle.ToLowerInvariant();
            var nLen = n.Length;

            if (nLen > h.Length + maxDistance)
                return false;

            // Classic Levenshtein with early-termination: compute full distance between needle
            // and every substring of haystack, but bail out once we find a match.
            // We use a single-row DP optimisation.
            var prev = new int[nLen + 1];
            var curr = new int[nLen + 1];

            for (int j = 0; j <= nLen; j++)
                prev[j] = j;

            for (int i = 1; i <= h.Length; i++)
            {
                curr[0] = 0; // Allow matching to start at any position in haystack
                var minInRow = int.MaxValue;

                for (int j = 1; j <= nLen; j++)
                {
                    var cost = h[i - 1] == n[j - 1] ? 0 : 1;
                    curr[j] = Math.Min(
                        Math.Min(curr[j - 1] + 1, prev[j] + 1),
                        prev[j - 1] + cost);
                    minInRow = Math.Min(minInRow, curr[j]);
                }

                // If the best possible score in this row already exceeds threshold, this row contributes nothing
                // but we can't prune early because curr[0]=0 resets at each position.

                if (curr[nLen] <= maxDistance)
                    return true;

                (prev, curr) = (curr, prev);
            }

            return false;
        }

        /// <summary>
        /// Searches enrollment sessions by filter. Uses DeviceSnapshot index for hardware filters,
        /// otherwise scans Sessions table with OData filtering.
        /// </summary>
        public async Task<List<SessionSummary>> SearchSessionsAsync(string? tenantId, SessionSearchFilter filter)
        {
            if (filter.HasDeviceSnapshotFilters)
                return await SearchSessionsByDeviceSnapshotAsync(tenantId, filter);
            else
                return await SearchSessionsByScanAsync(tenantId, filter);
        }

        /// <summary>
        /// Paged variant of <see cref="SearchSessionsAsync"/> backing
        /// <c>/api/search/sessions</c> + <c>/api/global/search/sessions</c>.
        /// The <c>filter.Limit</c> field is ignored — pagination uses
        /// <paramref name="pageSize"/> + <paramref name="continuation"/>.
        /// Both code paths (scan + device-snapshot) emit Azure-Tables raw
        /// continuation tokens; the caller's filter fingerprint must include
        /// the filter args so a token from one path can't be replayed into
        /// the other.
        /// </summary>
        public async Task<RawPage<SessionSummary>> SearchSessionsPageAsync(
            string? tenantId, SessionSearchFilter filter, int pageSize, string? continuation)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
            return filter.HasDeviceSnapshotFilters
                ? await SearchSessionsByDeviceSnapshotPageAsync(tenantId, filter, pageSize, continuation)
                : await SearchSessionsByScanPageAsync(tenantId, filter, pageSize, continuation);
        }

        private async Task<RawPage<SessionSummary>> SearchSessionsByScanPageAsync(
            string? tenantId, SessionSearchFilter filter, int pageSize, string? continuation)
        {
            var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
            var oDataFilter = BuildSearchScanFilter(tenantId, filter);

            var (entities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                client: indexTableClient,
                filter: oDataFilter,
                pageSize: pageSize,
                continuation: continuation);

            var sessions = new List<SessionSummary>(entities.Count);
            foreach (var entity in entities)
            {
                var session = MapIndexEntityToSessionSummary(entity);
                if (!MatchesScanClientFilters(session, filter)) continue;
                sessions.Add(session);
            }
            // Note: a page may legitimately contain fewer than pageSize items after
            // client-side filters; callers should follow nextLink until absent for
            // forensics-grade exact-count semantics (Plan §"consume until absent").
            return new RawPage<SessionSummary>(sessions, nextRawToken);
        }

        /// <summary>
        /// Literal-row variant of <see cref="SearchSessionsByScanPageAsync"/> backing
        /// <c>/api/raw/sessions</c>. Reuses the exact same OData push-down
        /// (<see cref="BuildSearchScanFilter"/>) and residual client-side predicate
        /// (<see cref="MatchesScanClientFilters"/>) as the enriched scan path, but emits the raw
        /// <c>SessionsIndex</c> rows verbatim (every stored column) instead of mapped
        /// <see cref="SessionSummary"/> DTOs. The <see cref="MapIndexEntityToSessionSummary"/> call
        /// is used only to evaluate the keep/drop predicate — the DTO itself is discarded.
        /// </summary>
        public async Task<RawPage<IReadOnlyDictionary<string, object?>>> SearchSessionsRawPageAsync(
            string? tenantId, SessionSearchFilter filter, int pageSize, string? continuation)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

            var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
            var oDataFilter = BuildSearchScanFilter(tenantId, filter);

            var (entities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                client: indexTableClient,
                filter: oDataFilter,
                pageSize: pageSize,
                continuation: continuation);

            var rows = new List<IReadOnlyDictionary<string, object?>>(entities.Count);
            foreach (var entity in entities)
            {
                var session = MapIndexEntityToSessionSummary(entity);
                if (!MatchesScanClientFilters(session, filter)) continue;
                rows.Add(RawEntityProjection.ToDictionary(entity));
            }

            return new RawPage<IReadOnlyDictionary<string, object?>>(rows, nextRawToken);
        }

        private async Task<RawPage<SessionSummary>> SearchSessionsByDeviceSnapshotPageAsync(
            string? tenantId, SessionSearchFilter filter, int pageSize, string? continuation)
        {
            var snapshotTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceSnapshot);
            var oDataFilter = string.IsNullOrEmpty(tenantId) ? null : $"PartitionKey eq '{tenantId}'";

            var (entities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                client: snapshotTableClient,
                filter: oDataFilter,
                pageSize: pageSize,
                continuation: continuation);

            var sessionIds = new List<string>();
            foreach (var entity in entities)
            {
                var properties = ReconstructDeviceProperties(entity);
                if (properties == null || properties.Count == 0) continue;
                if (!MatchesAllDeviceFilters(properties, filter.DeviceProperties!)) continue;
                sessionIds.Add(entity.RowKey);
            }

            if (sessionIds.Count == 0)
            {
                return new RawPage<SessionSummary>(Array.Empty<SessionSummary>(), nextRawToken);
            }

            var sessions = await BatchGetSessionsAsync(tenantId, sessionIds);
            sessions = ApplyBasicFilters(sessions, filter);
            return new RawPage<SessionSummary>(sessions, nextRawToken);
        }

        private static string? BuildSearchScanFilter(string? tenantId, SessionSearchFilter filter)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(tenantId))
                parts.Add($"PartitionKey eq '{ODataSanitizer.EscapeValue(tenantId)}'");
            if (!string.IsNullOrEmpty(filter.Status))
                parts.Add($"Status eq '{ODataSanitizer.EscapeValue(filter.Status)}'");
            if (!string.IsNullOrEmpty(filter.Manufacturer))
                parts.Add($"Manufacturer eq '{ODataSanitizer.EscapeValue(filter.Manufacturer)}'");
            if (!string.IsNullOrEmpty(filter.Model))
                parts.Add($"Model eq '{ODataSanitizer.EscapeValue(filter.Model)}'");
            if (!string.IsNullOrEmpty(filter.EnrollmentType))
                parts.Add($"EnrollmentType eq '{ODataSanitizer.EscapeValue(filter.EnrollmentType)}'");
            if (!string.IsNullOrEmpty(filter.DeviceName))
            {
                var safeName = ODataSanitizer.EscapeValue(filter.DeviceName);
                parts.Add($"DeviceName ge '{safeName}' and DeviceName lt '{safeName}~'");
            }
            if (!string.IsNullOrEmpty(filter.OsBuild))
            {
                var safeBuild = ODataSanitizer.EscapeValue(filter.OsBuild);
                parts.Add($"OsBuild ge '{safeBuild}' and OsBuild lt '{safeBuild}~'");
            }
            // AgentVersion: exact match wins over prefix. Both push to OData so a
            // sparse subset (e.g. "all V2 enrollments" — typically <1% of sessions)
            // doesn't burn N round-trips of empty pages while the index walks past
            // the dense majority. Prefix uses the same `ge/lt {value}~` range
            // trick that DeviceName / OsBuild already use — '~' (0x7E) sorts after
            // every printable ASCII, so the upper bound captures every key starting
            // with the prefix.
            if (!string.IsNullOrEmpty(filter.AgentVersion))
            {
                parts.Add($"AgentVersion eq '{ODataSanitizer.EscapeValue(filter.AgentVersion)}'");
            }
            else if (!string.IsNullOrEmpty(filter.AgentVersionPrefix))
            {
                var safe = ODataSanitizer.EscapeValue(filter.AgentVersionPrefix);
                parts.Add($"AgentVersion ge '{safe}' and AgentVersion lt '{safe}~'");
            }
            if (!string.IsNullOrEmpty(filter.ImeAgentVersion))
            {
                parts.Add($"ImeAgentVersion eq '{ODataSanitizer.EscapeValue(filter.ImeAgentVersion)}'");
            }
            else if (!string.IsNullOrEmpty(filter.ImeAgentVersionPrefix))
            {
                var safe = ODataSanitizer.EscapeValue(filter.ImeAgentVersionPrefix);
                parts.Add($"ImeAgentVersion ge '{safe}' and ImeAgentVersion lt '{safe}~'");
            }
            // RebootCount range pushed to OData (numeric, no quoting). "Machines with many
            // reboots" (RebootCountMin) is a sparse subset, so server-side filtering avoids
            // walking empty pages — same rationale as the AgentVersion prefix above. Legacy
            // index rows that predate the projected RebootCount column lack the property and
            // are excluded by the bound (they carry no reboot data, which is the intended result).
            if (filter.RebootCountMin.HasValue)
                parts.Add($"RebootCount ge {filter.RebootCountMin.Value}");
            if (filter.RebootCountMax.HasValue)
                parts.Add($"RebootCount le {filter.RebootCountMax.Value}");
            return parts.Count == 0 ? null : string.Join(" and ", parts);
        }

        /// <summary>
        /// Client-side RebootCount range predicate. Applied on every search path that does NOT
        /// push the bound to OData (device-snapshot batch-get and the legacy unpaged scan), and
        /// redundantly on the OData scan paths as a correctness backstop. Keeps a
        /// <c>deviceProperties + rebootCountMin</c> query from leaking sub-threshold sessions.
        /// </summary>
        internal static bool MatchesRebootCountBounds(SessionSummary session, SessionSearchFilter filter)
        {
            if (filter.RebootCountMin.HasValue && session.RebootCount < filter.RebootCountMin.Value) return false;
            if (filter.RebootCountMax.HasValue && session.RebootCount > filter.RebootCountMax.Value) return false;
            return true;
        }

        private static bool MatchesScanClientFilters(SessionSummary session, SessionSearchFilter filter)
        {
            if (!string.IsNullOrEmpty(filter.SerialNumber) &&
                !string.Equals(session.SerialNumber, filter.SerialNumber, StringComparison.OrdinalIgnoreCase))
                return false;
            if (filter.IsPreProvisioned.HasValue && session.IsPreProvisioned != filter.IsPreProvisioned.Value) return false;
            if (filter.IsHybridJoin.HasValue && session.IsHybridJoin != filter.IsHybridJoin.Value) return false;
            if (filter.IsSelfDeployingProfile.HasValue && session.IsSelfDeployingProfile != filter.IsSelfDeployingProfile.Value) return false;
            if (!string.IsNullOrEmpty(filter.GeoCountry) &&
                !string.Equals(session.GeoCountry, filter.GeoCountry, StringComparison.OrdinalIgnoreCase))
                return false;
            if (filter.StartedAfter.HasValue && session.StartedAt < filter.StartedAfter.Value) return false;
            if (filter.StartedBefore.HasValue && session.StartedAt > filter.StartedBefore.Value) return false;
            // RebootCount is also pushed to OData in BuildSearchScanFilter; this is a defensive
            // backstop so the bound holds even if that push-down is ever weakened.
            if (!MatchesRebootCountBounds(session, filter)) return false;
            // AgentVersion / ImeAgentVersion (exact + prefix) are pushed to OData
            // in BuildSearchScanFilter — the server has already filtered them out
            // before we see the page. No client-side check needed.
            return true;
        }

        private async Task<List<SessionSummary>> SearchSessionsByDeviceSnapshotAsync(string? tenantId, SessionSearchFilter filter)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.DeviceSnapshot);
            var oDataFilter = string.IsNullOrEmpty(tenantId) ? null : $"PartitionKey eq '{tenantId}'";

            var sessionIds = new List<string>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: oDataFilter))
            {
                // Reconstruct properties from Props_* columns
                var properties = ReconstructDeviceProperties(entity);
                if (properties == null || properties.Count == 0) continue;

                // Apply all device property filters
                if (!MatchesAllDeviceFilters(properties, filter.DeviceProperties!))
                    continue;

                sessionIds.Add(entity.RowKey); // RowKey = sessionId in DeviceSnapshot
                if (sessionIds.Count >= filter.Limit * 3) break; // over-fetch to allow for missing sessions
            }

            if (sessionIds.Count == 0) return new List<SessionSummary>();

            // Batch-get SessionSummaries from Sessions table
            var sessions = await BatchGetSessionsAsync(tenantId, sessionIds);

            // Apply any additional basic filters
            sessions = ApplyBasicFilters(sessions, filter);

            return sessions.Take(filter.Limit).ToList();
        }

        /// <summary>
        /// Reconstructs a flat property dictionary from all Props_* JSON columns.
        /// Keys use "eventType.propertyName" convention (e.g. "tpm_status.specVersion").
        /// </summary>
        private static Dictionary<string, object> ReconstructDeviceProperties(TableEntity entity)
        {
            var properties = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in entity.Keys)
            {
                if (!key.StartsWith("Props_", StringComparison.Ordinal)) continue;

                var eventType = key.Substring(6); // strip "Props_"
                var json = entity.GetString(key);
                if (string.IsNullOrEmpty(json)) continue;

                try
                {
                    var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(
                        json, _jsonDeserializeOptions);
                    if (data == null) continue;

                    foreach (var (propKey, propValue) in data)
                    {
                        if (propValue != null)
                            properties[$"{eventType}.{propKey}"] = propValue;
                    }
                }
                catch
                {
                    // Malformed JSON — skip this column
                }
            }

            return properties;
        }

        private static readonly System.Text.Json.JsonSerializerOptions _jsonDeserializeOptions = new()
        {
            PropertyNameCaseInsensitive = true,
        };

        /// <summary>
        /// Checks whether all filter expressions match against the property dictionary.
        /// Supports: exact string match, boolean match, numeric operators (>=, <=, >, <),
        /// and array substring search.
        /// </summary>
        private static bool MatchesAllDeviceFilters(
            Dictionary<string, object> properties,
            Dictionary<string, string> filters)
        {
            foreach (var (filterKey, filterValue) in filters)
            {
                if (!properties.TryGetValue(filterKey, out var actual) || actual == null)
                    return false;

                if (!MatchesDevicePropertyFilter(actual, filterValue))
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Matches a single property value against a filter expression.
        /// Filter syntax:
        ///   ">=8"  → numeric greater-than-or-equal
        ///   "<=5"  → numeric less-than-or-equal
        ///   ">8"   → numeric greater-than
        ///   "<5"   → numeric less-than
        ///   "ARM*" → case-insensitive prefix match (trailing wildcard)
        ///   "True"/"False" → boolean match
        ///   anything else → case-insensitive string equality
        /// For array values: checks if filterValue appears as substring in any element.
        /// </summary>
        internal static bool MatchesDevicePropertyFilter(object actual, string filterValue)
        {
            var actualStr = ConvertToString(actual);

            // Handle array values: check if filter matches any element
            if (actual is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var element in je.EnumerateArray())
                {
                    var elemStr = element.ToString();
                    if (elemStr != null && elemStr.Contains(filterValue, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                return false;
            }

            // Trailing-wildcard prefix match, e.g. "ARM*" matches "ARM" and "ARM64".
            // Only a single trailing '*' is treated as a wildcard; embedded '*' are literal.
            if (filterValue.Length > 1 && filterValue.EndsWith("*") &&
                !filterValue.AsSpan(0, filterValue.Length - 1).Contains('*'))
            {
                var prefix = filterValue.Substring(0, filterValue.Length - 1);
                return actualStr.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            }

            // Numeric range operators
            if (filterValue.StartsWith(">=") && double.TryParse(filterValue.AsSpan(2), out var geVal))
                return double.TryParse(actualStr, out var av1) && av1 >= geVal;
            if (filterValue.StartsWith("<=") && double.TryParse(filterValue.AsSpan(2), out var leVal))
                return double.TryParse(actualStr, out var av2) && av2 <= leVal;
            if (filterValue.StartsWith(">") && !filterValue.StartsWith(">=") && double.TryParse(filterValue.AsSpan(1), out var gtVal))
                return double.TryParse(actualStr, out var av3) && av3 > gtVal;
            if (filterValue.StartsWith("<") && !filterValue.StartsWith("<=") && double.TryParse(filterValue.AsSpan(1), out var ltVal))
                return double.TryParse(actualStr, out var av4) && av4 < ltVal;

            // Default: case-insensitive string equality (covers booleans too)
            return string.Equals(actualStr, filterValue, StringComparison.OrdinalIgnoreCase);
        }

        private static string ConvertToString(object value)
        {
            if (value is System.Text.Json.JsonElement jsonEl)
            {
                return jsonEl.ValueKind switch
                {
                    System.Text.Json.JsonValueKind.String => jsonEl.GetString() ?? "",
                    System.Text.Json.JsonValueKind.True => "True",
                    System.Text.Json.JsonValueKind.False => "False",
                    System.Text.Json.JsonValueKind.Number => jsonEl.GetRawText(),
                    _ => jsonEl.ToString()
                };
            }
            return value?.ToString() ?? "";
        }

        private async Task<List<SessionSummary>> SearchSessionsByScanAsync(string? tenantId, SessionSearchFilter filter)
        {
            var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);

            var filterParts = new List<string>();
            if (!string.IsNullOrEmpty(tenantId))
                filterParts.Add($"PartitionKey eq '{ODataSanitizer.EscapeValue(tenantId)}'");
            if (!string.IsNullOrEmpty(filter.Status))
                filterParts.Add($"Status eq '{ODataSanitizer.EscapeValue(filter.Status)}'");
            if (!string.IsNullOrEmpty(filter.Manufacturer))
                filterParts.Add($"Manufacturer eq '{ODataSanitizer.EscapeValue(filter.Manufacturer)}'");
            if (!string.IsNullOrEmpty(filter.Model))
                filterParts.Add($"Model eq '{ODataSanitizer.EscapeValue(filter.Model)}'");
            if (!string.IsNullOrEmpty(filter.EnrollmentType))
                filterParts.Add($"EnrollmentType eq '{ODataSanitizer.EscapeValue(filter.EnrollmentType)}'");
            if (!string.IsNullOrEmpty(filter.DeviceName))
            {
                var safeName = ODataSanitizer.EscapeValue(filter.DeviceName);
                filterParts.Add($"DeviceName ge '{safeName}' and DeviceName lt '{safeName}~'");
            }
            if (!string.IsNullOrEmpty(filter.OsBuild))
            {
                var safeBuild = ODataSanitizer.EscapeValue(filter.OsBuild);
                filterParts.Add($"OsBuild ge '{safeBuild}' and OsBuild lt '{safeBuild}~'");
            }

            var oDataFilter = filterParts.Count > 0 ? string.Join(" and ", filterParts) : null;

            var sessions = new List<SessionSummary>();
            await foreach (var entity in indexTableClient.QueryAsync<TableEntity>(filter: oDataFilter))
            {
                var session = MapIndexEntityToSessionSummary(entity);

                // Client-side filters
                if (!string.IsNullOrEmpty(filter.SerialNumber) &&
                    !string.Equals(session.SerialNumber, filter.SerialNumber, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (filter.IsPreProvisioned.HasValue && session.IsPreProvisioned != filter.IsPreProvisioned.Value) continue;
                if (filter.IsHybridJoin.HasValue && session.IsHybridJoin != filter.IsHybridJoin.Value) continue;
                if (filter.IsSelfDeployingProfile.HasValue && session.IsSelfDeployingProfile != filter.IsSelfDeployingProfile.Value) continue;
                if (!string.IsNullOrEmpty(filter.GeoCountry) &&
                    !string.Equals(session.GeoCountry, filter.GeoCountry, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (filter.StartedAfter.HasValue && session.StartedAt < filter.StartedAfter.Value) continue;
                if (filter.StartedBefore.HasValue && session.StartedAt > filter.StartedBefore.Value) continue;
                if (!string.IsNullOrEmpty(filter.AgentVersion) &&
                    !string.Equals(session.AgentVersion, filter.AgentVersion, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!string.IsNullOrEmpty(filter.ImeAgentVersion) &&
                    !string.Equals(session.ImeAgentVersion, filter.ImeAgentVersion, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (string.IsNullOrEmpty(filter.AgentVersion)
                    && !string.IsNullOrEmpty(filter.AgentVersionPrefix)
                    && (session.AgentVersion == null
                        || !session.AgentVersion.StartsWith(filter.AgentVersionPrefix!, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (string.IsNullOrEmpty(filter.ImeAgentVersion)
                    && !string.IsNullOrEmpty(filter.ImeAgentVersionPrefix)
                    && (session.ImeAgentVersion == null
                        || !session.ImeAgentVersion.StartsWith(filter.ImeAgentVersionPrefix!, StringComparison.OrdinalIgnoreCase)))
                    continue;
                // RebootCount is not in this path's OData (filterParts above) — apply client-side.
                if (!MatchesRebootCountBounds(session, filter)) continue;

                sessions.Add(session);
                if (sessions.Count >= filter.Limit) break;
            }

            return sessions;
        }

        private List<SessionSummary> ApplyBasicFilters(List<SessionSummary> sessions, SessionSearchFilter filter)
        {
            return sessions.Where(s =>
            {
                if (!string.IsNullOrEmpty(filter.Status) && s.Status.ToString() != filter.Status) return false;
                if (!string.IsNullOrEmpty(filter.SerialNumber) &&
                    !string.Equals(s.SerialNumber, filter.SerialNumber, StringComparison.OrdinalIgnoreCase)) return false;
                if (filter.IsPreProvisioned.HasValue && s.IsPreProvisioned != filter.IsPreProvisioned.Value) return false;
                if (filter.IsHybridJoin.HasValue && s.IsHybridJoin != filter.IsHybridJoin.Value) return false;
                if (filter.IsSelfDeployingProfile.HasValue && s.IsSelfDeployingProfile != filter.IsSelfDeployingProfile.Value) return false;
                if (!string.IsNullOrEmpty(filter.GeoCountry) &&
                    !string.Equals(s.GeoCountry, filter.GeoCountry, StringComparison.OrdinalIgnoreCase)) return false;
                if (filter.StartedAfter.HasValue && s.StartedAt < filter.StartedAfter.Value) return false;
                if (filter.StartedBefore.HasValue && s.StartedAt > filter.StartedBefore.Value) return false;
                if (!string.IsNullOrEmpty(filter.AgentVersion) &&
                    !string.Equals(s.AgentVersion, filter.AgentVersion, StringComparison.OrdinalIgnoreCase)) return false;
                if (!string.IsNullOrEmpty(filter.ImeAgentVersion) &&
                    !string.Equals(s.ImeAgentVersion, filter.ImeAgentVersion, StringComparison.OrdinalIgnoreCase)) return false;
                if (string.IsNullOrEmpty(filter.AgentVersion)
                    && !string.IsNullOrEmpty(filter.AgentVersionPrefix)
                    && (s.AgentVersion == null
                        || !s.AgentVersion.StartsWith(filter.AgentVersionPrefix!, StringComparison.OrdinalIgnoreCase)))
                    return false;
                if (string.IsNullOrEmpty(filter.ImeAgentVersion)
                    && !string.IsNullOrEmpty(filter.ImeAgentVersionPrefix)
                    && (s.ImeAgentVersion == null
                        || !s.ImeAgentVersion.StartsWith(filter.ImeAgentVersionPrefix!, StringComparison.OrdinalIgnoreCase)))
                    return false;
                // Device-snapshot path has no RebootCount OData push-down — enforce it here so a
                // deviceProperties + rebootCountMin query can't return sub-threshold sessions.
                if (!MatchesRebootCountBounds(s, filter)) return false;
                return true;
            }).ToList();
        }

        private async Task<List<SessionSummary>> BatchGetSessionsAsync(string? tenantId, List<string> sessionIds)
        {
            var sessionsTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.Sessions);
            var semaphore = new System.Threading.SemaphoreSlim(20, 20);

            var tasks = sessionIds.Select(async sessionId =>
            {
                await semaphore.WaitAsync();
                try
                {
                    // Try to determine the tenantId for this session
                    var partitionKey = tenantId ?? string.Empty;
                    if (string.IsNullOrEmpty(partitionKey))
                    {
                        // Cross-tenant: scan SessionsIndex for this sessionId
                        var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
                        await foreach (var idxEntity in indexTableClient.QueryAsync<TableEntity>(
                            filter: $"SessionId eq '{sessionId}'",
                            maxPerPage: 1))
                        {
                            return MapIndexEntityToSessionSummary(idxEntity);
                        }
                        return null;
                    }

                    var response = await sessionsTableClient.GetEntityAsync<TableEntity>(partitionKey, sessionId);
                    return MapToSessionSummary(response.Value);
                }
                catch (RequestFailedException ex) when (ex.Status == 404)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to get session {SessionId}", sessionId);
                    return null;
                }
                finally
                {
                    semaphore.Release();
                }
            });

            var results = await Task.WhenAll(tasks);
            return results.Where(s => s != null).Select(s => s!).ToList();
        }

        /// <summary>
        /// Paged EventTypeIndex walk that drives
        /// <c>QueryRawEventsFunction</c>'s cross-session walk. Returns up to
        /// <paramref name="pageSize"/> distinct sessions plus the underlying
        /// Azure-Tables continuation; caller follows the wire <c>nextLink</c>
        /// to drain the full recall set.
        /// </summary>
        public async Task<RawPage<SessionSummary>> SearchSessionsByEventPageAsync(
            string? tenantId, string eventType, string? source, string? severity, string? phase,
            int pageSize, string? continuation)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.EventTypeIndex);

            // Build OData filter (PartitionKey eq for tenant-scoped, EventType eq
            // for cross-tenant) — no limit*2 over-fetch; pagination handles depth.
            int? minSeverity = null;
            if (!string.IsNullOrEmpty(severity) &&
                Enum.TryParse<AutopilotMonitor.Shared.Models.EventSeverity>(severity, ignoreCase: true, out var parsedSeverity))
            {
                minSeverity = (int)parsedSeverity;
            }

            // Tenant-scoped: partition-targeted query (PK exact match).
            // Cross-tenant: server-side filter on the EventType column. The
            // PartitionKey shape is `{tenantId}_{eventType}` and tenantIds may
            // contain dashes but never underscores, while eventType itself may
            // contain underscores (e.g. `app_install_failed`) — so the previous
            // PK-suffix split via LastIndexOf('_') silently filtered out every
            // multi-underscore event type. Filtering on the EventType column
            // avoids that ambiguity entirely.
            string? oDataFilter;
            if (!string.IsNullOrEmpty(tenantId))
            {
                oDataFilter = $"PartitionKey eq '{ODataSanitizer.EscapeValue(tenantId)}_{ODataSanitizer.EscapeValue(eventType)}'";
                if (minSeverity.HasValue)
                    oDataFilter += $" and MaxSeverity ge {minSeverity.Value}";
            }
            else
            {
                oDataFilter = $"EventType eq '{ODataSanitizer.EscapeValue(eventType)}'";
                if (minSeverity.HasValue)
                    oDataFilter += $" and MaxSeverity ge {minSeverity.Value}";
            }

            var (entities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                client: tableClient,
                filter: oDataFilter,
                pageSize: pageSize,
                continuation: continuation);

            var sessionIds = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {

                if (!string.IsNullOrEmpty(source))
                {
                    var sources = entity.GetString("Sources") ?? string.Empty;
                    if (!sources.Contains(source, StringComparison.OrdinalIgnoreCase)) continue;
                }

                var sessionId = entity.GetString("SessionId");
                if (!string.IsNullOrEmpty(sessionId) && seen.Add(sessionId))
                {
                    sessionIds.Add(sessionId);
                }
            }

            if (sessionIds.Count == 0)
            {
                return new RawPage<SessionSummary>(Array.Empty<SessionSummary>(), nextRawToken);
            }

            var sessions = await BatchGetSessionsAsync(tenantId, sessionIds);
            return new RawPage<SessionSummary>(sessions, nextRawToken);
        }

        /// <summary>
        /// Searches sessions affected by a specific CVE using the CveIndex.
        /// </summary>
        public async Task<List<SessionSummary>> SearchSessionsByCveAsync(
            string? tenantId, string cveId, double? minCvssScore, string? overallRisk, int limit = 50)
        {
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.CveIndex);

            string oDataFilter;
            if (!string.IsNullOrEmpty(tenantId))
                oDataFilter = $"PartitionKey eq '{ODataSanitizer.EscapeValue(tenantId)}_{ODataSanitizer.EscapeValue(cveId)}'";
            else
            {
                var safeCveId = ODataSanitizer.EscapeValue(cveId);
                oDataFilter = $"PartitionKey ge '{safeCveId}' and PartitionKey lt '{safeCveId}~'";
            }

            var sessionIds = new List<string>();
            await foreach (var entity in tableClient.QueryAsync<TableEntity>(filter: oDataFilter))
            {
                if (minCvssScore.HasValue)
                {
                    var score = entity.GetDouble("CvssScore");
                    if (score == null || score < minCvssScore.Value) continue;
                }
                if (!string.IsNullOrEmpty(overallRisk))
                {
                    var risk = entity.GetString("OverallRisk");
                    if (!string.Equals(risk, overallRisk, StringComparison.OrdinalIgnoreCase)) continue;
                }

                var sessionId = entity.GetString("SessionId") ?? entity.RowKey;
                if (!string.IsNullOrEmpty(sessionId) && !sessionIds.Contains(sessionId))
                    sessionIds.Add(sessionId);

                if (sessionIds.Count >= limit * 2) break;
            }

            if (sessionIds.Count == 0) return new List<SessionSummary>();

            // Extract tenantId from PartitionKey for cross-tenant lookup if needed
            var sessions = await BatchGetSessionsAsync(tenantId, sessionIds);
            return sessions.Take(limit).ToList();
        }

        /// <summary>
        /// Paged variant of <see cref="SearchSessionsByCveAsync"/>. Walks the
        /// CveIndex partition page-by-page so callers can drain the full set of
        /// devices affected by a CVE — the legacy unpaged variant capped at
        /// limit*2 candidate sessions and silently dropped the rest, which made
        /// "how many of my devices are exposed to CVE-X" impossible to answer
        /// truthfully on large tenants.
        /// </summary>
        public async Task<RawPage<SessionSummary>> SearchSessionsByCvePageAsync(
            string? tenantId, string cveId, double? minCvssScore, string? overallRisk,
            int pageSize, string? continuation)
        {
            if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));

            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.CveIndex);

            var oDataFilter = BuildCveIndexSearchFilter(tenantId, cveId);

            var (entities, nextRawToken) = await AzureTablesPaginator.FetchPageAsync<TableEntity>(
                client: tableClient,
                filter: oDataFilter,
                pageSize: pageSize,
                continuation: continuation);

            var sessionIds = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entity in entities)
            {
                if (minCvssScore.HasValue)
                {
                    var score = entity.GetDouble("CvssScore");
                    if (score == null || score < minCvssScore.Value) continue;
                }
                if (!string.IsNullOrEmpty(overallRisk))
                {
                    var risk = entity.GetString("OverallRisk");
                    if (!string.Equals(risk, overallRisk, StringComparison.OrdinalIgnoreCase)) continue;
                }

                var sessionId = entity.GetString("SessionId") ?? entity.RowKey;
                if (!string.IsNullOrEmpty(sessionId) && seen.Add(sessionId))
                {
                    sessionIds.Add(sessionId);
                }
            }

            if (sessionIds.Count == 0)
                return new RawPage<SessionSummary>(Array.Empty<SessionSummary>(), nextRawToken);

            var sessions = await BatchGetSessionsAsync(tenantId, sessionIds);
            return new RawPage<SessionSummary>(sessions, nextRawToken);
        }

        /// <summary>
        /// Builds the CveIndex OData filter for the per-CVE session search.
        /// CveIndex PartitionKey is <c>{tenantId}_{cveId}</c>.
        /// <list type="bullet">
        /// <item>Tenant-scoped: exact PK match — partition-targeted, cheap.</item>
        /// <item>Cross-tenant (tenantId null): the PK begins with the tenant GUID,
        /// NOT the cveId, so a PK range on the cveId matches nothing. We filter on
        /// the <c>CveId</c> property instead — a server-side scan across partitions,
        /// the same shape <see cref="ScanCveIndexAsync"/> uses for fleet aggregation.</item>
        /// </list>
        /// Exposed internal for regression testing (the old PK-range form silently
        /// returned 0 cross-tenant results — see SearchSessionsByCveFilterTests).
        /// </summary>
        internal static string BuildCveIndexSearchFilter(string? tenantId, string cveId)
        {
            var safeCveId = ODataSanitizer.EscapeValue(cveId);
            return string.IsNullOrEmpty(tenantId)
                ? $"CveId eq '{safeCveId}'"
                : $"PartitionKey eq '{ODataSanitizer.EscapeValue(tenantId)}_{safeCveId}'";
        }

        /// <summary>
        /// Scans the CveIndex for fleet-wide vulnerability aggregation. Tenant-scoped
        /// uses a partition-prefix range (`{tenantId}_` .. `{tenantId}_~`) so Azure
        /// targets only that tenant's CVE partitions; cross-tenant (tenantId null) is
        /// a bounded full-table scan — each row carries TenantId + CveId, so the
        /// aggregate is correct without the loose suffix-range the per-CVE search
        /// has to use. Reads at most <paramref name="maxRows"/> rows and reports
        /// whether the cap was hit so the caller can flag partial results.
        /// </summary>
        public async Task<(IReadOnlyList<CveExposureEntry> Rows, bool Truncated)> ScanCveIndexAsync(
            string? tenantId, int maxRows, CancellationToken ct = default)
        {
            if (maxRows < 1) maxRows = 1;
            var tableClient = _tableServiceClient.GetTableClient(Constants.TableNames.CveIndex);

            string? filter = null;
            if (!string.IsNullOrEmpty(tenantId))
            {
                var safe = ODataSanitizer.EscapeValue(tenantId);
                filter = $"PartitionKey ge '{safe}_' and PartitionKey lt '{safe}_~'";
            }

            var select = new[]
            {
                "SessionId", "TenantId", "CveId", "SoftwareName",
                "CvssScore", "CvssSeverity", "IsKev", "OverallRisk", "DetectedAt",
            };

            var rows = new List<CveExposureEntry>(Math.Min(maxRows, 2048));
            var truncated = false;
            await foreach (var e in tableClient.QueryAsync<TableEntity>(
                filter: filter, maxPerPage: 1000, select: select, cancellationToken: ct))
            {
                if (rows.Count >= maxRows) { truncated = true; break; }

                // TenantId column is always written, but fall back to the partition
                // key prefix (`{tenantId}_{cveId}`) for any legacy row missing it.
                var tid = e.GetString("TenantId");
                if (string.IsNullOrEmpty(tid))
                {
                    var us = e.PartitionKey.IndexOf('_');
                    tid = us > 0 ? e.PartitionKey.Substring(0, us) : string.Empty;
                }

                rows.Add(new CveExposureEntry
                {
                    TenantId = tid ?? string.Empty,
                    CveId = e.GetString("CveId") ?? string.Empty,
                    SessionId = e.GetString("SessionId") ?? e.RowKey,
                    SoftwareName = e.GetString("SoftwareName") ?? string.Empty,
                    CvssScore = e.GetDouble("CvssScore") ?? 0,
                    CvssSeverity = e.GetString("CvssSeverity") ?? string.Empty,
                    IsKev = e.GetBoolean("IsKev") ?? false,
                    OverallRisk = e.GetString("OverallRisk") ?? string.Empty,
                    DetectedAt = e.GetDateTime("DetectedAt"),
                });
            }

            return (rows, truncated);
        }

        /// <summary>
        /// Returns aggregated session metrics grouped by tenant, filtered to the last <paramref name="days"/> days.
        /// Leverages the SessionsIndex inverted-tick RowKey ordering for an efficient server-side range scan.
        /// </summary>
        /// <summary>
        /// Columns the status tally consumes: PartitionKey (grouping) + Status. internal so
        /// <c>MetricsSummaryProjectionEquivalenceTests</c> derives its keep-set from this array.
        /// </summary>
        internal static readonly string[] MetricsSummaryProjection = { "PartitionKey", "Status" };

        /// <summary>
        /// Tally one SessionsIndex row into its tenant's status buckets. Reads ONLY the two
        /// projected columns (PartitionKey + Status), so a projected row must produce identical
        /// buckets to a full mirror row — pinned by <c>MetricsSummaryProjectionEquivalenceTests</c>.
        /// </summary>
        internal static void TallyMetricsSummaryRow(Dictionary<string, SessionStatusBuckets> groups, TableEntity entity)
        {
            var pk = entity.PartitionKey;
            var statusStr = entity.GetString("Status") ?? "InProgress";
            groups.TryGetValue(pk, out var g);
            groups[pk] = g.Add(statusStr);
        }

        public async Task<List<object>> GetMetricsSummaryAsync(string? tenantId, int days = 30)
        {
            // Clamp to a sane range so callers can't accidentally trigger an unbounded scan.
            if (days < 1) days = 1;
            if (days > 365) days = 365;

            var indexTableClient = _tableServiceClient.GetTableClient(Constants.TableNames.SessionsIndex);
            var cutoffRowKeyPrefix = ComputeCutoffRowKeyPrefix(days);

            var filterParts = new List<string> { $"RowKey lt '{cutoffRowKeyPrefix}'" };
            if (!string.IsNullOrEmpty(tenantId))
                filterParts.Add($"PartitionKey eq '{tenantId}'");
            var oDataFilter = string.Join(" and ", filterParts);

            // Tally every status into its own bucket so the buckets always reconcile to total
            // (SessionStatusBuckets enforces this by construction — Pending/Stalled/Unknown no
            // longer vanish from the summary). See SessionStatusBuckets in MetricsMath.cs.
            var groups = new Dictionary<string, SessionStatusBuckets>(StringComparer.OrdinalIgnoreCase);

            // Project to the two columns the tally consumes. The SessionsIndex row is a full
            // ~40-column mirror (incl. the large FailureSnapshotJson), so reading it whole for a
            // status count multiplied transfer ~10-20x for nothing on this cross-partition scan.
            await foreach (var entity in indexTableClient.QueryAsync<TableEntity>(
                filter: oDataFilter, select: MetricsSummaryProjection))
            {
                TallyMetricsSummaryRow(groups, entity);
            }

            return groups.Select(kvp =>
            {
                var b = kvp.Value;
                // Honest failure rate: failed over the TERMINAL outcomes only (Succeeded + Failed).
                // Incomplete (unknown, non-failure) is deliberately excluded from the denominator, and
                // non-terminal states (InProgress/AwaitingUser/Pending/Stalled) never belonged in it.
                var terminal = b.Succeeded + b.Failed;
                return (object)new
                {
                    tenantId = kvp.Key,
                    totalSessions = b.Total,
                    succeeded = b.Succeeded,
                    failed = b.Failed,
                    inProgress = b.InProgress,
                    pending = b.Pending,
                    stalled = b.Stalled,
                    awaitingUser = b.AwaitingUser,
                    incomplete = b.Incomplete,
                    other = b.Other,
                    failureRate = terminal > 0
                        ? Math.Round((double)b.Failed / terminal * 100, 1)
                        : 0.0,
                    windowDays = days
                };
            }).ToList();
        }
    }
}
