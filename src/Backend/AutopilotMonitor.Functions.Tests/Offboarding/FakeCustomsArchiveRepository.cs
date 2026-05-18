using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Offboarding;

namespace AutopilotMonitor.Functions.Tests.Offboarding;

/// <summary>
/// In-memory <see cref="ITenantCustomsArchiveRepository"/> for PR3.B handler + endpoint tests.
/// Idempotent upsert keyed by (PK, RK); supports the four query shapes the admin API uses.
/// </summary>
internal sealed class FakeCustomsArchiveRepository : ITenantCustomsArchiveRepository
{
    private readonly Dictionary<string, TenantOffboardingCustomsArchiveEntry> _store
        = new(System.StringComparer.Ordinal);

    public IReadOnlyDictionary<string, TenantOffboardingCustomsArchiveEntry> Store => _store;

    private static string Key(string pk, string rk) => pk + "|" + rk;

    public Task UpsertAsync(TenantOffboardingCustomsArchiveEntry entry, CancellationToken ct = default)
    {
        _store[Key(entry.PartitionKey, entry.RowKey)] = Clone(entry);
        return Task.CompletedTask;
    }

    public Task<int> CountByRunAndTableAsync(string normalizedTenantId, string historyRowKey, string originalTable, CancellationToken ct = default)
    {
        var pk = $"{normalizedTenantId}_{historyRowKey}";
        var count = _store.Values.Count(e =>
            e.PartitionKey == pk && e.OriginalTable == originalTable);
        return Task.FromResult(count);
    }

    public async IAsyncEnumerable<TenantOffboardingCustomsArchiveEntry> QueryByRunAsync(
        string normalizedTenantId, string historyRowKey,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var pk = $"{normalizedTenantId}_{historyRowKey}";
        foreach (var entry in _store.Values.Where(e => e.PartitionKey == pk))
        {
            yield return Clone(entry);
        }
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<TenantOffboardingCustomsArchiveEntry> QueryByTenantAsync(
        string normalizedTenantId, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var prefix = normalizedTenantId + "_";
        foreach (var entry in _store.Values.Where(e => e.PartitionKey.StartsWith(prefix)))
        {
            yield return Clone(entry);
        }
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<TenantOffboardingCustomsArchiveEntry> QueryAllAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var entry in _store.Values)
        {
            yield return Clone(entry);
        }
        await Task.CompletedTask;
    }

    public Task<TenantOffboardingCustomsArchiveEntry?> TryGetEntryAsync(
        string partitionKey, string rowKey, CancellationToken ct = default)
    {
        _store.TryGetValue(Key(partitionKey, rowKey), out var entry);
        return Task.FromResult<TenantOffboardingCustomsArchiveEntry?>(entry == null ? null : Clone(entry));
    }

    public Task DeleteEntryAsync(string partitionKey, string rowKey, CancellationToken ct = default)
    {
        _store.Remove(Key(partitionKey, rowKey));
        return Task.CompletedTask;
    }

    public Task<int> DeleteRunAsync(string normalizedTenantId, string historyRowKey, CancellationToken ct = default)
    {
        var pk = $"{normalizedTenantId}_{historyRowKey}";
        var toDelete = _store.Keys.Where(k => k.StartsWith(pk + "|")).ToList();
        foreach (var k in toDelete) _store.Remove(k);
        return Task.FromResult(toDelete.Count);
    }

    private static TenantOffboardingCustomsArchiveEntry Clone(TenantOffboardingCustomsArchiveEntry e) => new()
    {
        PartitionKey = e.PartitionKey,
        RowKey = e.RowKey,
        TenantId = e.TenantId,
        OriginalTable = e.OriginalTable,
        OriginalPartitionKey = e.OriginalPartitionKey,
        OriginalRowKey = e.OriginalRowKey,
        EntityJson = e.EntityJson,
        HistoryRowKey = e.HistoryRowKey,
        ArchivedAt = e.ArchivedAt,
        ArchivedBy = e.ArchivedBy,
    };
}
