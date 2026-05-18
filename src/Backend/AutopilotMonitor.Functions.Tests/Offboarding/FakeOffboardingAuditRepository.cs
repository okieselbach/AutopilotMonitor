using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models.Offboarding;
using Azure;

namespace AutopilotMonitor.Functions.Tests.Offboarding;

/// <summary>
/// In-memory <see cref="IOffboardingAuditRepository"/> for handler/cleanup tests. Records the
/// latest write per (PK, RK) so the test can assert lifecycle transitions, and mirrors the
/// production ETag-CAS contract on the pointer (412 on mismatched ETag).
/// </summary>
internal sealed class FakeOffboardingAuditRepository : IOffboardingAuditRepository
{
    public Dictionary<string, OffboardingMarkerEntry> Markers { get; } = new();
    public Dictionary<string, OffboardingHistoryEntry> History { get; } = new();
    public Dictionary<string, (OffboardingByTenantPointer Pointer, string ETag)> Pointers { get; } = new();
    public List<string> MarkerWrites { get; } = new();
    public List<string> HistoryWrites { get; } = new();

    /// <summary>When set, the next call to <see cref="UpsertHistoryAsync"/> throws — used by
    /// poison-path tests to simulate a transient storage failure inside the Failed-state
    /// transition so the Worker's reorder invariant can be exercised.</summary>
    public Exception? ThrowOnNextHistoryUpsert { get; set; }

    private int _etagCounter;
    private string NextEtag() => $"\"0xFAKE_{++_etagCounter}\"";

    public Task<OffboardingMarkerEntry?> TryGetMarkerAsync(string normalizedTenantId, CancellationToken ct = default)
        => Task.FromResult(Markers.TryGetValue(normalizedTenantId, out var m) ? Clone(m) : null);

    public Task InsertMarkerAsync(OffboardingMarkerEntry marker, CancellationToken ct = default)
    {
        if (Markers.ContainsKey(marker.RowKey))
            throw new RequestFailedException(409, "Conflict", "EntityAlreadyExists", null);
        Markers[marker.RowKey] = Clone(marker);
        MarkerWrites.Add(marker.Status);
        return Task.CompletedTask;
    }

    public Task UpsertMarkerAsync(OffboardingMarkerEntry marker, CancellationToken ct = default)
    {
        Markers[marker.RowKey] = Clone(marker);
        MarkerWrites.Add(marker.Status);
        return Task.CompletedTask;
    }

    public Task DeleteMarkerAsync(string normalizedTenantId, CancellationToken ct = default)
    {
        Markers.Remove(normalizedTenantId);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<OffboardingMarkerEntry> QueryMarkersAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var marker in Markers.Values)
        {
            yield return Clone(marker);
        }
        await Task.CompletedTask;
    }

    public Task InsertHistoryAsync(OffboardingHistoryEntry history, CancellationToken ct = default)
    {
        if (History.ContainsKey(history.RowKey))
            throw new RequestFailedException(409, "Conflict", "EntityAlreadyExists", null);
        History[history.RowKey] = Clone(history);
        HistoryWrites.Add(history.Status);
        return Task.CompletedTask;
    }

    public Task<OffboardingHistoryEntry?> TryGetHistoryAsync(string historyRowKey, CancellationToken ct = default)
        => Task.FromResult(History.TryGetValue(historyRowKey, out var h) ? Clone(h) : null);

    public Task UpsertHistoryAsync(OffboardingHistoryEntry history, CancellationToken ct = default)
    {
        if (ThrowOnNextHistoryUpsert is { } ex)
        {
            ThrowOnNextHistoryUpsert = null;
            throw ex;
        }
        History[history.RowKey] = Clone(history);
        HistoryWrites.Add(history.Status);
        return Task.CompletedTask;
    }

    public Task<(OffboardingByTenantPointer? Pointer, string? ETag)> TryGetByTenantPointerAsync(
        string normalizedTenantId, CancellationToken ct = default)
    {
        if (Pointers.TryGetValue(normalizedTenantId, out var entry))
        {
            return Task.FromResult<(OffboardingByTenantPointer?, string?)>((Clone(entry.Pointer), entry.ETag));
        }
        return Task.FromResult<(OffboardingByTenantPointer?, string?)>((null, null));
    }

    public Task InsertByTenantPointerAsync(OffboardingByTenantPointer pointer, CancellationToken ct = default)
    {
        if (Pointers.ContainsKey(pointer.RowKey))
            throw new RequestFailedException(409, "Conflict", "EntityAlreadyExists", null);
        Pointers[pointer.RowKey] = (Clone(pointer), NextEtag());
        return Task.CompletedTask;
    }

    public Task UpdateByTenantPointerWithEtagAsync(
        OffboardingByTenantPointer pointer, string ifMatchEtag, CancellationToken ct = default)
    {
        if (!Pointers.TryGetValue(pointer.RowKey, out var entry))
            throw new RequestFailedException(404, "NotFound", "ResourceNotFound", null);
        if (entry.ETag != ifMatchEtag)
            throw new RequestFailedException(412, "ConditionNotMet", "UpdateConditionNotSatisfied", null);
        Pointers[pointer.RowKey] = (Clone(pointer), NextEtag());
        return Task.CompletedTask;
    }

    private static OffboardingMarkerEntry Clone(OffboardingMarkerEntry m) => new()
    {
        PartitionKey = m.PartitionKey, RowKey = m.RowKey,
        TenantId = m.TenantId, OffboardingHistoryRowKey = m.OffboardingHistoryRowKey,
        InitiatedAt = m.InitiatedAt, InitiatedBy = m.InitiatedBy,
        Status = m.Status, CompletedAt = m.CompletedAt,
        FailedAt = m.FailedAt, FailedPhase = m.FailedPhase,
    };

    private static OffboardingHistoryEntry Clone(OffboardingHistoryEntry h) => new()
    {
        PartitionKey = h.PartitionKey, RowKey = h.RowKey,
        TenantId = h.TenantId, DomainName = h.DomainName, InitiatedBy = h.InitiatedBy,
        OffboardedAt = h.OffboardedAt, CompletedAt = h.CompletedAt,
        EarliestProcessingAt = h.EarliestProcessingAt,
        Status = h.Status,
        DeletedRowCountsJson = h.DeletedRowCountsJson, TotalRowsDeleted = h.TotalRowsDeleted,
        DeletedBlobCount = h.DeletedBlobCount, CascadeSessionsEnqueued = h.CascadeSessionsEnqueued,
        ErrorMessage = h.ErrorMessage, RetryCount = h.RetryCount,
        DrainCompletedAt = h.DrainCompletedAt,
        EnumerationStartedAt = h.EnumerationStartedAt,
        EnumerationCompletedBeforeUpload = h.EnumerationCompletedBeforeUpload,
        CustomGatherRulesArchived = h.CustomGatherRulesArchived,
        CustomAnalyzeRulesArchived = h.CustomAnalyzeRulesArchived,
        ImeLogPatternOverridesArchived = h.ImeLogPatternOverridesArchived,
        ReonboardedAt = h.ReonboardedAt, ReonboardedBy = h.ReonboardedBy,
        CustomsAutoWipedOnReonboard = h.CustomsAutoWipedOnReonboard,
    };

    private static OffboardingByTenantPointer Clone(OffboardingByTenantPointer p) => new()
    {
        PartitionKey = p.PartitionKey, RowKey = p.RowKey, TenantId = p.TenantId,
        LatestHistoryRowKey = p.LatestHistoryRowKey, LatestStatus = p.LatestStatus,
        LatestUpdatedAt = p.LatestUpdatedAt, OffboardCount = p.OffboardCount,
    };
}
