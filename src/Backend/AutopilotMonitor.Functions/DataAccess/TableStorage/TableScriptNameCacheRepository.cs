using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Graph;
using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.DataAccess.TableStorage;

/// <summary>
/// Table-storage implementation of <see cref="IScriptNameCacheRepository"/>. Single table,
/// PartitionKey = tenantId, RowKey = <c>"{Kind}_{ScriptId}"</c>. The per-kind meta row uses
/// RowKey = <c>"{Kind}_$meta"</c>; the <c>$</c> ensures it can never collide with a real
/// script identifier (Graph IDs are GUIDs).
/// <para>
/// Fail-loud: this repo does NOT swallow Azure storage exceptions. The Resolver wraps calls
/// in a permissive try/catch so the user-facing endpoint degrades to "no names" rather than
/// 500ing. The repo itself behaves like <see cref="TableOffboardingAuditRepository"/>.
/// </para>
/// </summary>
public sealed class TableScriptNameCacheRepository : IScriptNameCacheRepository
{
    private const string MetaRowKeySuffix = "_$meta";

    private readonly TableClient _tableClient;

    public TableScriptNameCacheRepository(
        TableStorageService storage)
    {
        _tableClient = storage.GetTableClient(Constants.TableNames.ScriptNameCache);
    }

    public async Task<IReadOnlyDictionary<ScriptRef, ScriptDisplayNameEntry>> GetManyAsync(
        string tenantId, IReadOnlyCollection<ScriptRef> refs, CancellationToken ct = default)
    {
        SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
        var result = new Dictionary<ScriptRef, ScriptDisplayNameEntry>(refs.Count);
        if (refs.Count == 0) return result;

        // Point-lookups in parallel; Table Storage SDK guarantees a fresh client per call.
        var tasks = new List<Task<(ScriptRef Ref, ScriptDisplayNameEntry? Entry)>>(refs.Count);
        foreach (var r in refs)
        {
            tasks.Add(TryFetchAsync(tenantId, r, ct));
        }
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var (refKey, entry) in results)
        {
            if (entry != null)
            {
                result[refKey] = entry;
            }
        }
        return result;
    }

    public async Task UpsertManyAsync(string tenantId, IReadOnlyCollection<ScriptDisplayNameEntry> entries, CancellationToken ct = default)
    {
        SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
        if (entries.Count == 0) return;

        // Sequential upsert keeps semantics simple; cache writes are off the user hot path
        // (background fill after a Resolver fetch). For the typical 100-row tenant snapshot
        // this finishes well within a second.
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            await _tableClient.UpsertEntityAsync(StoreEntry(tenantId, entry), TableUpdateMode.Replace, ct).ConfigureAwait(false);
        }
    }

    public async Task<ScriptNameCacheMeta?> TryGetMetaAsync(string tenantId, ScriptKind kind, CancellationToken ct = default)
    {
        SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
        try
        {
            var response = await _tableClient.GetEntityAsync<TableEntity>(
                tenantId, BuildMetaRowKey(kind), cancellationToken: ct).ConfigureAwait(false);
            return MapMeta(response.Value);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public Task UpsertMetaAsync(ScriptNameCacheMeta meta, CancellationToken ct = default)
    {
        if (meta == null) throw new ArgumentNullException(nameof(meta));
        SecurityValidator.EnsureValidGuid(meta.TenantId, $"{nameof(meta)}.TenantId");
        return _tableClient.UpsertEntityAsync(StoreMeta(meta), TableUpdateMode.Replace, ct);
    }

    public async IAsyncEnumerable<ScriptDisplayNameEntry> QueryExpiredAsync(
        DateTimeOffset positiveCutoff,
        DateTimeOffset notFoundCutoff,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Scan the whole table. The cache is small relative to other tables (max ~200 rows
        // per tenant × tenants) and the cleanup runs once daily, so a full scan is fine.
        await foreach (var entity in _tableClient.QueryAsync<TableEntity>(cancellationToken: ct))
        {
            ct.ThrowIfCancellationRequested();
            if (entity.RowKey.EndsWith(MetaRowKeySuffix, StringComparison.Ordinal)) continue; // meta rows live forever

            var entry = MapEntry(entity);
            var cutoff = entry.IsNotFound ? notFoundCutoff : positiveCutoff;
            if (entry.FetchedAt < cutoff)
            {
                yield return entry;
            }
        }
    }

    public async Task DeleteAsync(string tenantId, ScriptKind kind, string scriptId, CancellationToken ct = default)
    {
        SecurityValidator.EnsureValidGuid(tenantId, nameof(tenantId));
        if (string.IsNullOrWhiteSpace(scriptId)) throw new ArgumentException("scriptId required", nameof(scriptId));

        try
        {
            await _tableClient.DeleteEntityAsync(tenantId, BuildRowKey(kind, scriptId), ETag.All, ct).ConfigureAwait(false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            // Idempotent.
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<(ScriptRef, ScriptDisplayNameEntry?)> TryFetchAsync(string tenantId, ScriptRef r, CancellationToken ct)
    {
        try
        {
            var response = await _tableClient.GetEntityAsync<TableEntity>(
                tenantId, BuildRowKey(r.Kind, r.Id), cancellationToken: ct).ConfigureAwait(false);
            return (r, MapEntry(response.Value));
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return (r, null);
        }
    }

    private static string BuildRowKey(ScriptKind kind, string scriptId) => $"{kind}_{scriptId}";

    private static string BuildMetaRowKey(ScriptKind kind) => $"{kind}{MetaRowKeySuffix}";

    private static TableEntity StoreEntry(string tenantId, ScriptDisplayNameEntry e) => new(tenantId, BuildRowKey(e.Kind, e.ScriptId))
    {
        ["Kind"] = e.Kind.ToString(),
        ["ScriptId"] = e.ScriptId,
        ["DisplayName"] = e.DisplayName,
        ["FileName"] = e.FileName,
        ["FetchedAt"] = e.FetchedAt.UtcDateTime,
        ["IsNotFound"] = e.IsNotFound,
    };

    private static ScriptDisplayNameEntry MapEntry(TableEntity e)
    {
        // Parse kind out of the row key (canonical source) rather than the "Kind" column
        // in case someone tampered with the row directly.
        var sep = e.RowKey.IndexOf('_');
        var kindStr = sep > 0 ? e.RowKey[..sep] : e.RowKey;
        var scriptId = sep > 0 && sep < e.RowKey.Length - 1 ? e.RowKey[(sep + 1)..] : string.Empty;
        Enum.TryParse<ScriptKind>(kindStr, ignoreCase: true, out var kind);

        var fetchedAtUtc = e.GetDateTime("FetchedAt") ?? DateTime.UtcNow;
        return new ScriptDisplayNameEntry
        {
            TenantId = e.PartitionKey,
            Kind = kind,
            ScriptId = scriptId,
            DisplayName = e.GetString("DisplayName"),
            FileName = e.GetString("FileName"),
            FetchedAt = new DateTimeOffset(fetchedAtUtc, TimeSpan.Zero),
            IsNotFound = e.GetBoolean("IsNotFound") ?? false,
        };
    }

    private static TableEntity StoreMeta(ScriptNameCacheMeta m) => new(m.TenantId, BuildMetaRowKey(m.Kind))
    {
        ["Kind"] = m.Kind.ToString(),
        ["LastFullRefreshAt"] = m.LastFullRefreshAt.UtcDateTime,
    };

    private static ScriptNameCacheMeta MapMeta(TableEntity e)
    {
        var sep = e.RowKey.IndexOf('_');
        var kindStr = sep > 0 ? e.RowKey[..sep] : e.RowKey;
        Enum.TryParse<ScriptKind>(kindStr, ignoreCase: true, out var kind);
        var lastRefreshUtc = e.GetDateTime("LastFullRefreshAt") ?? DateTime.MinValue;
        return new ScriptNameCacheMeta
        {
            TenantId = e.PartitionKey,
            Kind = kind,
            LastFullRefreshAt = new DateTimeOffset(lastRefreshUtc, TimeSpan.Zero),
        };
    }
}
