using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Offboarding;
using AutopilotMonitor.Shared.Models.Offboarding;
using Azure;

namespace AutopilotMonitor.Functions.Tests.Offboarding;

/// <summary>
/// In-memory <see cref="IOffboardingExpectationsStore"/> with the production If-None-Match=*
/// and If-Match ETag-CAS semantics. Tracks delete attempts so tests can assert idempotency.
/// </summary>
internal sealed class FakeOffboardingExpectationsStore : IOffboardingExpectationsStore
{
    private readonly Dictionary<string, (OffboardingExpectations Payload, string ETag)> _blobs = new();
    private int _etagCounter;

    public int DeleteCallCount { get; private set; }
    public bool ThrowOnDelete { get; set; }

    public Task<bool> TryUploadInitialAsync(OffboardingExpectations payload, CancellationToken ct = default)
    {
        var key = Key(payload.TenantId, payload.HistoryRowKey);
        if (_blobs.ContainsKey(key)) return Task.FromResult(false);
        _blobs[key] = (Clone(payload), NextEtag());
        return Task.FromResult(true);
    }

    public Task<(OffboardingExpectations? Payload, string? ETag)> TryDownloadAsync(
        string tenantId, string historyRowKey, CancellationToken ct = default)
    {
        var key = Key(tenantId, historyRowKey);
        if (!_blobs.TryGetValue(key, out var entry))
            return Task.FromResult<(OffboardingExpectations?, string?)>((null, null));
        return Task.FromResult<(OffboardingExpectations?, string?)>((Clone(entry.Payload), entry.ETag));
    }

    public Task<string> UpdateWithEtagCasAsync(OffboardingExpectations payload, string ifMatchEtag, CancellationToken ct = default)
    {
        var key = Key(payload.TenantId, payload.HistoryRowKey);
        if (!_blobs.TryGetValue(key, out var entry))
            throw new RequestFailedException(404, "NotFound", "BlobNotFound", null);
        if (entry.ETag != ifMatchEtag)
            throw new RequestFailedException(412, "ConditionNotMet", "ConditionNotMet", null);
        var newEtag = NextEtag();
        _blobs[key] = (Clone(payload), newEtag);
        return Task.FromResult(newEtag);
    }

    public Task DeleteAsync(string tenantId, string historyRowKey, CancellationToken ct = default)
    {
        DeleteCallCount++;
        if (ThrowOnDelete) throw new InvalidOperationException("simulated blob-delete failure");
        _blobs.Remove(Key(tenantId, historyRowKey));
        return Task.CompletedTask;
    }

    /// <summary>Pre-seed a blob (used to drive the drain probe directly without going via TryUploadInitial).</summary>
    public void Seed(OffboardingExpectations payload)
    {
        _blobs[Key(payload.TenantId, payload.HistoryRowKey)] = (Clone(payload), NextEtag());
    }

    public bool BlobExists(string tenantId, string historyRowKey)
        => _blobs.ContainsKey(Key(tenantId, historyRowKey));

    private static string Key(string tenantId, string historyRowKey) => $"{tenantId}|{historyRowKey}";
    private string NextEtag() => $"\"0xFAKE_EXP_{++_etagCounter}\"";

    private static OffboardingExpectations Clone(OffboardingExpectations p)
    {
        var clone = new OffboardingExpectations
        {
            SchemaVersion = p.SchemaVersion,
            TenantId = p.TenantId,
            HistoryRowKey = p.HistoryRowKey,
            CreatedAt = p.CreatedAt,
            EnumerationCompleted = p.EnumerationCompleted,
            EnumeratedSessionCount = p.EnumeratedSessionCount,
        };
        foreach (var e in p.Expectations)
        {
            clone.Expectations.Add(new OffboardingExpectation
            {
                SessionId = e.SessionId,
                ManifestId = e.ManifestId,
                Outcome = e.Outcome,
                RetryCount = e.RetryCount,
            });
        }
        return clone;
    }
}
