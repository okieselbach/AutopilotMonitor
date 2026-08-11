using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services.Tables;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Deletion
{
    /// <summary>
    /// Read-only enumerator that produces a <see cref="DeletionManifest"/> snapshot of every
    /// row a cascade-delete would target for a given (tenant, session). PR1 ships only the
    /// builder; the producer + worker that actually use the manifest land in PR3 / PR4.
    /// <para>
    /// Per plan §1 P1 / §3: the manifest IS the backup. A row not captured here will not be
    /// deleted; a row captured here can be restored byte-faithful from the JSON dump. The
    /// builder never deletes, never CAS-locks, never uploads — it only reads.
    /// </para>
    /// </summary>
    public class DeletionManifestBuilder
    {
        private readonly ISessionDeletionInventoryReader _reader;
        private readonly ILogger<DeletionManifestBuilder> _logger;

        // Per-row dump for the rare 0-row table is allowed; per-class step is always emitted.
        private static readonly JsonSerializerOptions HashSerializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
        };

        public DeletionManifestBuilder(ISessionDeletionInventoryReader reader, ILogger<DeletionManifestBuilder> logger)
        {
            _reader = reader;
            _logger = logger;
        }

        /// <summary>
        /// Builds a complete cascade snapshot for the given session. Read-only; safe to call
        /// from a preview endpoint. The returned manifest carries full row dumps suitable for
        /// both forward execution (cascade) and reverse execution (restore).
        /// <para>
        /// Pass <paramref name="preAllocatedManifestId"/> when the caller has already CAS-stamped
        /// a ManifestId on the Sessions row (producer's <c>None → Preparing</c> step). The
        /// builder then computes <c>SchemaHash</c> against the FINAL ManifestId, so post-build
        /// overwrites by the caller don't invalidate the hash. The preview endpoint passes null
        /// (transient ID, never persisted to a Sessions row).
        /// </para>
        /// </summary>
        public virtual async Task<DeletionManifest> BuildAsync(
            string tenantId,
            string sessionId,
            string reason,
            DeletionActor actor,
            DeletionRetentionContext retentionContext,
            CancellationToken cancellationToken = default,
            string? preAllocatedManifestId = null)
        {
            var manifest = new DeletionManifest
            {
                ManifestId = preAllocatedManifestId ?? NewManifestId(),
                ManifestVersion = 1,
                TenantId = tenantId,
                SessionId = sessionId,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = actor,
                Reason = reason,
                RetentionContext = retentionContext,
            };

            // Step A — load the Sessions + SessionsIndex rows up front. Both are needed for the
            // FINAL tombstone step; the Sessions row also gives us IndexRowKey + DiagnosticsBlobName
            // + DiagnosticsBlobDestination (the destination travels with the manifest so the
            // cascade can route deletion to the right store even if the tenant later switches modes).
            var sessionRow = await _reader.GetSessionRowAsync(tenantId, sessionId, cancellationToken);
            TableEntity? sessionsIndexRow = null;
            if (sessionRow != null)
            {
                manifest.DiagnosticsBlobName = sessionRow.GetString("DiagnosticsBlobName");
                manifest.DiagnosticsBlobDestination = sessionRow.GetString("DiagnosticsBlobDestination");
                sessionsIndexRow = await ResolveSessionsIndexRowAsync(tenantId, sessionId, sessionRow, cancellationToken);
            }

            var safeTenantId = ODataSanitizer.EscapeValue(tenantId);
            var safeSessionId = ODataSanitizer.EscapeValue(sessionId);
            var compositeSessionPk = $"{tenantId}_{sessionId}";

            // ---- Steps 1-15: cascade tables, in the §3 order. ----
            await AddPkBySessionStepAsync(manifest, order: 1, table: Constants.TableNames.Events, compositeSessionPk, safeTenantId, safeSessionId, cancellationToken);
            await AddPkBySessionStepAsync(manifest, order: 2, table: Constants.TableNames.RuleResults, compositeSessionPk, safeTenantId, safeSessionId, cancellationToken);
            await AddPropTenantPkStepAsync(manifest, order: 3, table: Constants.TableNames.AppInstallSummaries, tenantId, sessionId, safeTenantId, safeSessionId, cancellationToken);
            await AddPkRkExactStepAsync(manifest, order: 4, table: Constants.TableNames.VulnerabilityReports, partitionKey: compositeSessionPk, rowKey: "report", cancellationToken);
            await AddPkRkExactStepAsync(manifest, order: 5, table: Constants.TableNames.DeviceSnapshot, partitionKey: tenantId, rowKey: sessionId, cancellationToken);
            await AddPkRkExactStepAsync(manifest, order: 6, table: Constants.TableNames.EventSessionIndex, partitionKey: tenantId, rowKey: sessionId, cancellationToken);
            await AddPkBySessionStepAsync(manifest, order: 7, table: Constants.TableNames.Signals, compositeSessionPk, safeTenantId, safeSessionId, cancellationToken);
            await AddPkBySessionStepAsync(manifest, order: 8, table: Constants.TableNames.DecisionTransitions, compositeSessionPk, safeTenantId, safeSessionId, cancellationToken);
            await AddDiscriminatorPkRkSuffixStepAsync(manifest, order: 9, table: Constants.TableNames.EventTypeIndex, tenantId, sessionId, safeTenantId, cancellationToken);
            await AddDiscriminatorPkRkExactStepAsync(manifest, order: 10, table: Constants.TableNames.CveIndex, tenantId, sessionId, safeTenantId, cancellationToken);
            await AddDiscriminatorPkPropStepAsync(manifest, order: 11, table: Constants.TableNames.SessionsByTerminal, tenantId, sessionId, safeTenantId, safeSessionId, cancellationToken);
            await AddDiscriminatorPkPropStepAsync(manifest, order: 12, table: Constants.TableNames.SessionsByStage, tenantId, sessionId, safeTenantId, safeSessionId, cancellationToken);
            await AddDiscriminatorPkPropStepAsync(manifest, order: 13, table: Constants.TableNames.DeadEndsByReason, tenantId, sessionId, safeTenantId, safeSessionId, cancellationToken);
            await AddDiscriminatorPkPropStepAsync(manifest, order: 14, table: Constants.TableNames.ClassifierVerdictsByIdLevel, tenantId, sessionId, safeTenantId, safeSessionId, cancellationToken);
            await AddDiscriminatorPkPropStepAsync(manifest, order: 15, table: Constants.TableNames.SignalsByKind, tenantId, sessionId, safeTenantId, safeSessionId, cancellationToken);

            // ---- Step 16: F1 time-attribution breakdown (PK=tenant, RK=sessionId; PR2). ----
            await AddPkRkExactStepAsync(manifest, order: 16, table: Constants.TableNames.SessionTimeBreakdowns, partitionKey: tenantId, rowKey: sessionId, cancellationToken);

            // ---- Steps 17-19: session annotations (PK=tenant, RK="{sessionId}_{lane}"). ----
            // Lanes are a closed set of 3, so exact-RK steps cover every possible row.
            var annotationOrder = 17;
            foreach (var lane in AnnotationLanes.All)
            {
                await AddPkRkExactStepAsync(manifest, order: annotationOrder++, table: Constants.TableNames.SessionAnnotations, partitionKey: tenantId, rowKey: $"{sessionId}_{lane}", cancellationToken);
            }

            // ---- Steps 20 + 21: SoftwareInventory side-row (omit both for pre-side-row sessions). ----
            var contributionsRow = await _reader.GetEntityOrNullAsync(
                Constants.TableNames.SessionInventoryContributions, tenantId, sessionId, cancellationToken);
            if (contributionsRow != null)
            {
                AddSoftwareInventoryDecrementStep(manifest, order: 20, contributionsRow);
                AddContributionsRowStep(manifest, order: 21, contributionsRow);
            }

            // ---- Step 22: Tombstone (SessionsIndex first, then Sessions). ----
            AddTombstoneStep(manifest, order: 22, sessionsIndexRow, sessionRow);

            // PreflightCounts derive from each step's RowCount, plus the AGGREGATE decrements length.
            manifest.PreflightCounts = ComputePreflightCounts(manifest);

            // SchemaHash is over the entire manifest content (with SchemaHash field blanked) so the
            // worker can detect tampering between snapshot upload and pickup.
            manifest.SchemaHash = ComputeSchemaHash(manifest);

            return manifest;
        }

        // ============================================================ Step builders ============

        private async Task AddPkBySessionStepAsync(
            DeletionManifest manifest, int order, string table,
            string compositeSessionPk, string safeTenantId, string safeSessionId,
            CancellationToken cancellationToken)
        {
            var filter = $"PartitionKey eq '{safeTenantId}_{safeSessionId}'";
            var rows = new List<DeletionRowDump>();
            await foreach (var entity in _reader.QueryAsync(table, filter, cancellationToken))
            {
                rows.Add(TableEntityDumpConverter.MapEntityToDump(entity));
            }
            manifest.Steps.Add(new DeletionStep
            {
                Order = order,
                Table = table,
                Class = DeletionStepClass.PkBySession,
                RowCount = rows.Count,
                Rows = rows,
            });
        }

        private async Task AddPropTenantPkStepAsync(
            DeletionManifest manifest, int order, string table,
            string tenantId, string sessionId, string safeTenantId, string safeSessionId,
            CancellationToken cancellationToken)
        {
            var filter = $"PartitionKey eq '{safeTenantId}' and SessionId eq '{safeSessionId}'";
            var rows = new List<DeletionRowDump>();
            await foreach (var entity in _reader.QueryAsync(table, filter, cancellationToken))
            {
                rows.Add(TableEntityDumpConverter.MapEntityToDump(entity));
            }
            manifest.Steps.Add(new DeletionStep
            {
                Order = order,
                Table = table,
                Class = DeletionStepClass.PropTenantPk,
                RowCount = rows.Count,
                Rows = rows,
            });
        }

        private async Task AddPkRkExactStepAsync(
            DeletionManifest manifest, int order, string table,
            string partitionKey, string rowKey,
            CancellationToken cancellationToken)
        {
            var entity = await _reader.GetEntityOrNullAsync(table, partitionKey, rowKey, cancellationToken);
            var rows = new List<DeletionRowDump>();
            if (entity != null)
            {
                rows.Add(TableEntityDumpConverter.MapEntityToDump(entity));
            }
            manifest.Steps.Add(new DeletionStep
            {
                Order = order,
                Table = table,
                Class = DeletionStepClass.PkRkExact,
                RowCount = rows.Count,
                Rows = rows,
            });
        }

        private async Task AddDiscriminatorPkRkSuffixStepAsync(
            DeletionManifest manifest, int order, string table,
            string tenantId, string sessionId, string safeTenantId,
            CancellationToken cancellationToken)
        {
            // Server-side: PK prefix scan. Client-side: RK ends with "_{sessionId}".
            var filter = $"PartitionKey ge '{safeTenantId}_' and PartitionKey lt '{safeTenantId}_~'";
            var suffix = $"_{sessionId}";
            var rows = new List<DeletionRowDump>();
            await foreach (var entity in _reader.QueryAsync(table, filter, cancellationToken))
            {
                if (entity.RowKey != null && entity.RowKey.EndsWith(suffix, StringComparison.Ordinal))
                {
                    rows.Add(TableEntityDumpConverter.MapEntityToDump(entity));
                }
            }
            manifest.Steps.Add(new DeletionStep
            {
                Order = order,
                Table = table,
                Class = DeletionStepClass.DiscriminatorPkRkSuffix,
                RowCount = rows.Count,
                Rows = rows,
            });
        }

        private async Task AddDiscriminatorPkRkExactStepAsync(
            DeletionManifest manifest, int order, string table,
            string tenantId, string sessionId, string safeTenantId,
            CancellationToken cancellationToken)
        {
            // Server-side: PK prefix scan. Client-side: RK == sessionId.
            var filter = $"PartitionKey ge '{safeTenantId}_' and PartitionKey lt '{safeTenantId}_~'";
            var rows = new List<DeletionRowDump>();
            await foreach (var entity in _reader.QueryAsync(table, filter, cancellationToken))
            {
                if (string.Equals(entity.RowKey, sessionId, StringComparison.Ordinal))
                {
                    rows.Add(TableEntityDumpConverter.MapEntityToDump(entity));
                }
            }
            manifest.Steps.Add(new DeletionStep
            {
                Order = order,
                Table = table,
                Class = DeletionStepClass.DiscriminatorPkRkExact,
                RowCount = rows.Count,
                Rows = rows,
            });
        }

        private async Task AddDiscriminatorPkPropStepAsync(
            DeletionManifest manifest, int order, string table,
            string tenantId, string sessionId, string safeTenantId, string safeSessionId,
            CancellationToken cancellationToken)
        {
            // Server-side: PK prefix scan AND SessionId eq '{sessionId}'. Eligible because every
            // DISCRIMINATOR_PK_PROP table writes the SessionId property explicitly (see IndexRowKeys
            // call sites; the writers project SessionId so the server-side filter is reliable).
            var filter = $"PartitionKey ge '{safeTenantId}_' and PartitionKey lt '{safeTenantId}_~' and SessionId eq '{safeSessionId}'";
            var rows = new List<DeletionRowDump>();
            await foreach (var entity in _reader.QueryAsync(table, filter, cancellationToken))
            {
                rows.Add(TableEntityDumpConverter.MapEntityToDump(entity));
            }
            manifest.Steps.Add(new DeletionStep
            {
                Order = order,
                Table = table,
                Class = DeletionStepClass.DiscriminatorPkProp,
                RowCount = rows.Count,
                Rows = rows,
            });
        }

        private void AddSoftwareInventoryDecrementStep(DeletionManifest manifest, int order, TableEntity contributionsRow)
        {
            var decrements = DecodeDecrementKeys(contributionsRow);
            manifest.Steps.Add(new DeletionStep
            {
                Order = order,
                Step = DeletionStepNames.SoftwareInventoryDecrement,
                Class = DeletionStepClass.Aggregate,
                RowCount = decrements.Count,
                Rows = new List<DeletionRowDump>(),
                Decrements = decrements,
            });
        }

        private void AddContributionsRowStep(DeletionManifest manifest, int order, TableEntity contributionsRow)
        {
            manifest.Steps.Add(new DeletionStep
            {
                Order = order,
                Table = Constants.TableNames.SessionInventoryContributions,
                Class = DeletionStepClass.PkRkExact,
                RowCount = 1,
                Rows = new List<DeletionRowDump> { TableEntityDumpConverter.MapEntityToDump(contributionsRow) },
            });
        }

        private void AddTombstoneStep(DeletionManifest manifest, int order, TableEntity? sessionsIndexRow, TableEntity? sessionRow)
        {
            var rows = new List<DeletionRowDump>(2);
            // §5 PR4 mandates SessionsIndex deleted FIRST so UI listings drop the session before the canonical row goes.
            if (sessionsIndexRow != null)
            {
                rows.Add(TableEntityDumpConverter.MapEntityToDump(sessionsIndexRow));
            }
            if (sessionRow != null)
            {
                rows.Add(TableEntityDumpConverter.MapEntityToDump(sessionRow));
            }
            manifest.Steps.Add(new DeletionStep
            {
                Order = order,
                Step = DeletionStepNames.Tombstone,
                Class = DeletionStepClass.Final,
                RowCount = rows.Count,
                Rows = rows,
            });
        }

        // ============================================================ Mapping + helpers ========

        /// <summary>
        /// Locates the SessionsIndex row corresponding to the given Sessions row. Tries the
        /// direct (PartitionKey, RowKey) lookup first using <c>Sessions.IndexRowKey</c>; falls
        /// back to a partition-targeted scan on <c>PartitionKey eq {tenantId} and SessionId eq
        /// {sessionId}</c> when the IndexRowKey is missing or stale, so a partially-corrupted
        /// Sessions row can't strand a SessionsIndex orphan after cascade.
        /// </summary>
        private async Task<TableEntity?> ResolveSessionsIndexRowAsync(
            string tenantId, string sessionId, TableEntity sessionRow, CancellationToken cancellationToken)
        {
            var indexRowKey = sessionRow.GetString("IndexRowKey");
            if (!string.IsNullOrEmpty(indexRowKey))
            {
                var byKey = await _reader.GetSessionsIndexRowAsync(tenantId, indexRowKey!, cancellationToken);
                if (byKey != null) return byKey;
            }

            // Fallback: partition-targeted property scan. SessionsIndex stores SessionId as a
            // typed column on every row; a single match is expected.
            var safeTenantId = ODataSanitizer.EscapeValue(tenantId);
            var safeSessionId = ODataSanitizer.EscapeValue(sessionId);
            var filter = $"PartitionKey eq '{safeTenantId}' and SessionId eq '{safeSessionId}'";
            await foreach (var match in _reader.QueryAsync(Constants.TableNames.SessionsIndex, filter, cancellationToken))
            {
                return match;
            }
            return null;
        }

        // MapEntityToDump / ConvertToPropValue / ParseJson lifted to TableEntityDumpConverter
        // (Services/Tables) so the critical-table backup pipeline can share the exact same
        // EDM-typed row-dump representation. No behavior change.

        /// <summary>
        /// Decodes the side-row's <c>SoftwareKeysJson</c> (raw JSON or gzip+Base64 per §17.7) into
        /// the canonical <see cref="DeletionDecrementKey"/> list. Sorted deterministically by
        /// (Vendor, Name, Version) so the manifest is byte-stable across builds with identical input.
        /// </summary>
        private List<DeletionDecrementKey> DecodeDecrementKeys(TableEntity contributionsRow)
        {
            var rawJson = contributionsRow.GetString("SoftwareKeysJson") ?? string.Empty;
            var isCompressed = contributionsRow.GetBoolean("IsCompressed") ?? false;
            if (string.IsNullOrEmpty(rawJson))
            {
                return new List<DeletionDecrementKey>();
            }

            string decoded;
            if (isCompressed)
            {
                try
                {
                    var gz = Convert.FromBase64String(rawJson);
                    using var input = new MemoryStream(gz);
                    using var gunzip = new GZipStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    gunzip.CopyTo(output);
                    decoded = Encoding.UTF8.GetString(output.ToArray());
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException(
                        $"SessionInventoryContributions row for tenant {contributionsRow.PartitionKey} session {contributionsRow.RowKey} " +
                        "is marked IsCompressed=true but SoftwareKeysJson does not decode as Base64+gzip", ex);
                }
            }
            else
            {
                decoded = rawJson;
            }

            List<DeletionDecrementKey> keys;
            try
            {
                keys = JsonSerializer.Deserialize<List<DeletionDecrementKey>>(decoded, DeletionManifestJson.SerializerOptions)
                       ?? new List<DeletionDecrementKey>();
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(
                    $"SessionInventoryContributions.SoftwareKeysJson for tenant {contributionsRow.PartitionKey} session {contributionsRow.RowKey} " +
                    "is not valid JSON for List<DeletionDecrementKey>", ex);
            }

            return keys
                .OrderBy(k => k.Vendor, StringComparer.Ordinal)
                .ThenBy(k => k.Name, StringComparer.Ordinal)
                .ThenBy(k => k.Version, StringComparer.Ordinal)
                .ToList();
        }

        private static Dictionary<string, int> ComputePreflightCounts(DeletionManifest manifest)
        {
            // Convention: well-known camelCase keys per the §3 "preflightCounts" example.
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var step in manifest.Steps)
            {
                if (step.Class == DeletionStepClass.Final) continue; // Tombstone is not a "count" entry
                if (step.Class == DeletionStepClass.Aggregate)
                {
                    counts["softwareInventoryDecrement"] = step.Decrements?.Count ?? 0;
                    continue;
                }
                if (string.IsNullOrEmpty(step.Table)) continue;
                // Sum, don't overwrite: SessionAnnotations emits one step per lane on the same table.
                var key = CamelCase(step.Table!);
                counts[key] = counts.TryGetValue(key, out var existing) ? existing + step.RowCount : step.RowCount;
            }
            return counts;
        }

        private static string CamelCase(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToLowerInvariant(s[0]) + s.Substring(1);
        }

        private static string ComputeSchemaHash(DeletionManifest manifest)
        {
            // Hash over the manifest with SchemaHash blanked out, so the field can reproducibly
            // be compared by the worker after download. See plan §3.
            var savedHash = manifest.SchemaHash;
            manifest.SchemaHash = string.Empty;
            try
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(manifest, HashSerializerOptions);
                using var sha = SHA256.Create();
                var hash = sha.ComputeHash(json);
                return "sha256:" + Convert.ToHexString(hash).ToLowerInvariant();
            }
            finally
            {
                manifest.SchemaHash = savedHash;
            }
        }

        private static string NewManifestId()
        {
            // ULID-shaped string: lex-sortable timestamp prefix + random tail. PR1 doesn't need a
            // strict ULID library; the producer (PR3) will revisit if downstream consumers require
            // monotonic ordering across producers.
            var ticks = DateTime.UtcNow.Ticks;
            var prefix = ticks.ToString("X16");
            var tail = Guid.NewGuid().ToString("N").Substring(0, 16).ToUpperInvariant();
            return prefix + "_" + tail;
        }
    }
}
