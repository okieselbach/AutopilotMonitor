using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Offboarding;
using AutopilotMonitor.Shared.Models.Offboarding;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Rev-6-F1 + Rev-7-F2 + Rev-8-F1: full coverage of the blob-IO contracts on
/// <see cref="BlobOffboardingExpectationsStore"/> via a Dict-backed fake that overrides the
/// four <c>protected virtual</c> seams. The fake reproduces Azure Blob Storage's two
/// "exists" status codes (409 BlobAlreadyExists + 412 ConditionNotMet) so the resume path
/// is exercised under both. POCO/validation cases live next to the IO cases for full
/// surface coverage.
/// </summary>
public class OffboardingExpectationsStoreTests
{
    private const string TenantId = "99999999-9999-9999-9999-999999999999";
    private const string HistoryRowKey = "20260518091523123_99999999-9999-9999-9999-999999999999";

    // ── Validation gates ────────────────────────────────────────────────────────

    [Fact]
    public async Task TryUploadInitial_RejectsNonGuidTenantId()
    {
        var store = new FakeStore();
        var payload = BuildPayload(tenantId: "not-a-guid");

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryUploadInitialAsync(payload));
    }

    [Fact]
    public async Task TryUploadInitial_RejectsEmptyHistoryRowKey()
    {
        var store = new FakeStore();
        var payload = BuildPayload(historyRowKey: "");

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryUploadInitialAsync(payload));
    }

    [Fact]
    public async Task TryUploadInitial_RejectsSchemaVersionZero()
    {
        var store = new FakeStore();
        var payload = BuildPayload();
        payload.SchemaVersion = 0;

        await Assert.ThrowsAsync<ArgumentException>(() => store.TryUploadInitialAsync(payload));
    }

    [Fact]
    public async Task UpdateWithEtagCas_RequiresIfMatchEtag()
    {
        var store = new FakeStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.UpdateWithEtagCasAsync(BuildPayload(), ""));
    }

    [Fact]
    public async Task TryDownload_RejectsNonGuidTenantId()
    {
        var store = new FakeStore();
        await Assert.ThrowsAsync<ArgumentException>(() => store.TryDownloadAsync("not-a-guid", HistoryRowKey));
    }

    // ── Initial upload (If-None-Match=*) ────────────────────────────────────────

    [Fact]
    public async Task TryUploadInitial_FirstWrite_ReturnsTrue()
    {
        var store = new FakeStore();
        var inserted = await store.TryUploadInitialAsync(BuildPayload());
        Assert.True(inserted);
        Assert.Single(store.Blobs);
        Assert.Equal(
            BlobOffboardingExpectationsStore.BuildBlobName(TenantId, HistoryRowKey),
            store.Blobs.Keys.Single());
    }

    [Fact]
    public async Task TryUploadInitial_Duplicate_409BlobAlreadyExists_ReturnsFalse()
    {
        var store = new FakeStore();
        await store.TryUploadInitialAsync(BuildPayload());

        // Simulate the "older" Azure response code for the same condition.
        store.NextOverwriteRefusal = OverwriteRefusal.Status409;

        var inserted = await store.TryUploadInitialAsync(BuildPayload());
        Assert.False(inserted);
    }

    [Fact]
    public async Task TryUploadInitial_Duplicate_412ConditionNotMet_ReturnsFalse()
    {
        // High-finding: Azure Blob Storage emits 412 ConditionNotMet for the SAME
        // "If-None-Match=* on existing blob" case the SDK historically returned 409 for.
        // Without the 412-branch the resume path breaks and the worker poisons.
        var store = new FakeStore();
        await store.TryUploadInitialAsync(BuildPayload());

        store.NextOverwriteRefusal = OverwriteRefusal.Status412ConditionNotMet;

        var inserted = await store.TryUploadInitialAsync(BuildPayload());
        Assert.False(inserted);
    }

    [Fact]
    public async Task TryUploadInitial_Duplicate_BlobErrorCodeOnly_ReturnsFalse()
    {
        // Some SDK paths surface the failure as Status=0 with a populated ErrorCode.
        var store = new FakeStore();
        await store.TryUploadInitialAsync(BuildPayload());

        store.NextOverwriteRefusal = OverwriteRefusal.ErrorCodeBlobAlreadyExists;

        var inserted = await store.TryUploadInitialAsync(BuildPayload());
        Assert.False(inserted);
    }

    [Fact]
    public async Task TryUploadInitial_Propagates500_NotASilentResume()
    {
        var store = new FakeStore { NextOverwriteRefusal = OverwriteRefusal.Status500 };
        await Assert.ThrowsAsync<RequestFailedException>(() => store.TryUploadInitialAsync(BuildPayload()));
    }

    // ── Download (404 → null) ──────────────────────────────────────────────────

    [Fact]
    public async Task TryDownload_404_Returns_NullPayload_AndNullEtag()
    {
        var store = new FakeStore();
        var (payload, etag) = await store.TryDownloadAsync(TenantId, HistoryRowKey);
        Assert.Null(payload);
        Assert.Null(etag);
    }

    [Fact]
    public async Task TryDownload_AfterUpload_RoundTripsAllFields()
    {
        var store = new FakeStore();
        var original = BuildPayload();
        original.EnumerationCompleted = true;
        original.EnumeratedSessionCount = 2;
        original.Expectations.Add(new OffboardingExpectation { SessionId = "s1", ManifestId = "m1", Outcome = "Enqueued" });
        original.Expectations.Add(new OffboardingExpectation { SessionId = "s2", ManifestId = null, Outcome = "SessionNotFound" });

        await store.TryUploadInitialAsync(original);

        var (round, etag) = await store.TryDownloadAsync(TenantId, HistoryRowKey);
        Assert.NotNull(round);
        Assert.NotNull(etag);
        Assert.Equal(2, round!.EnumeratedSessionCount);
        Assert.True(round.EnumerationCompleted);
        Assert.Equal(2, round.Expectations.Count);
        Assert.Equal("Enqueued", round.Expectations[0].Outcome);
        Assert.Null(round.Expectations[1].ManifestId);
    }

    // ── Update with ETag CAS ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateWithEtagCas_MatchingEtag_ReturnsNewEtag()
    {
        var store = new FakeStore();
        await store.TryUploadInitialAsync(BuildPayload());
        var (round, etag) = await store.TryDownloadAsync(TenantId, HistoryRowKey);

        round!.Expectations.Add(new OffboardingExpectation { SessionId = "s1", Outcome = "Enqueued", ManifestId = "m1" });

        var newEtag = await store.UpdateWithEtagCasAsync(round, etag!);
        Assert.NotNull(newEtag);
        Assert.NotEqual(etag, newEtag);

        var (refetched, _) = await store.TryDownloadAsync(TenantId, HistoryRowKey);
        Assert.Single(refetched!.Expectations);
    }

    [Fact]
    public async Task UpdateWithEtagCas_StaleEtag_Throws412()
    {
        var store = new FakeStore();
        await store.TryUploadInitialAsync(BuildPayload());

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => store.UpdateWithEtagCasAsync(BuildPayload(), ifMatchEtag: "\"0xSTALE\""));
        Assert.Equal(412, ex.Status);
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesBlob_WhenPresent()
    {
        var store = new FakeStore();
        await store.TryUploadInitialAsync(BuildPayload());
        Assert.Single(store.Blobs);

        await store.DeleteAsync(TenantId, HistoryRowKey);
        Assert.Empty(store.Blobs);
    }

    [Fact]
    public async Task Delete_404_IsIdempotent()
    {
        var store = new FakeStore();
        // No upload first — blob doesn't exist.
        await store.DeleteAsync(TenantId, HistoryRowKey); // must not throw
        Assert.Empty(store.Blobs);
    }

    // ── Container choice (Rev-8-F1) ────────────────────────────────────────────

    [Fact]
    public void Container_IsOffboardingState_NotDeletionManifests()
    {
        // Container choice is part of the resilience design — wiping the wrong container
        // (Rev-8-F1: deletion-manifests would orphan Expectations during Phase 2.E).
        Assert.Equal("offboarding-state", AutopilotMonitor.Shared.Constants.BlobContainers.OffboardingState);
        Assert.NotEqual(
            AutopilotMonitor.Shared.Constants.BlobContainers.DeletionManifests,
            AutopilotMonitor.Shared.Constants.BlobContainers.OffboardingState);
    }

    [Fact]
    public void BlobName_FollowsTenantPrefixSchemaForLifecycleSweep()
    {
        var name = BlobOffboardingExpectationsStore.BuildBlobName(TenantId, HistoryRowKey);
        Assert.StartsWith($"{TenantId}/", name);
        Assert.EndsWith(".expectations.json", name);
        Assert.Contains(HistoryRowKey, name);
    }

    [Fact]
    public void Defaults_ForceCallersToSetEnumerationCompletedExplicitly()
    {
        // Rev-7-F2 disambiguation lives at the POCO level — the drain probe relies on
        // EnumerationCompleted defaulting to false so a half-built payload cannot
        // accidentally pass the "0 expectations + completed=true" happy path.
        var payload = new OffboardingExpectations();
        Assert.False(payload.EnumerationCompleted);
        Assert.Equal(0, payload.EnumeratedSessionCount);
        Assert.Equal(1, payload.SchemaVersion);
        Assert.Empty(payload.Expectations);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static OffboardingExpectations BuildPayload(
        string tenantId = TenantId,
        string historyRowKey = HistoryRowKey)
        => new()
        {
            SchemaVersion = 1,
            TenantId = tenantId,
            HistoryRowKey = historyRowKey,
            CreatedAt = new DateTime(2026, 5, 18, 9, 15, 23, DateTimeKind.Utc),
            EnumerationCompleted = false,
            EnumeratedSessionCount = 0,
        };

    /// <summary>
    /// Selects how the next call to <see cref="FakeStore.WriteIfNoneMatchAsync"/> simulates an
    /// "exists" refusal — needed because Azure switches between codes depending on tier/SDK.
    /// </summary>
    private enum OverwriteRefusal
    {
        Auto,
        Status409,
        Status412ConditionNotMet,
        ErrorCodeBlobAlreadyExists,
        Status500,
    }

    /// <summary>
    /// Dict-backed fake that overrides the four protected virtual seams. Mirrors the
    /// production Azure semantics for If-None-Match=*, If-Match, 404, and DeleteIfExists.
    /// </summary>
    private sealed class FakeStore : BlobOffboardingExpectationsStore
    {
        public Dictionary<string, (byte[] Bytes, string ETag)> Blobs { get; } = new();
        public OverwriteRefusal NextOverwriteRefusal { get; set; } = OverwriteRefusal.Auto;
        private int _etagCounter;

        public FakeStore()
            : base(
                new BlobStorageService(
                    new BlobServiceClient("UseDevelopmentStorage=true"),
                    NullLogger<BlobStorageService>.Instance,
                    usesManagedIdentity: false),
                NullLogger<BlobOffboardingExpectationsStore>.Instance)
        {
        }

        protected override Task<(bool Inserted, string? ETag)> WriteIfNoneMatchAsync(
            string blobName, byte[] payload, CancellationToken ct)
        {
            // Allow tests to inject a specific refusal even though the underlying state would
            // already make a real Azure call fail — needed to pin BOTH the 409 and 412 paths.
            if (NextOverwriteRefusal == OverwriteRefusal.Status500)
            {
                NextOverwriteRefusal = OverwriteRefusal.Auto;
                throw new RequestFailedException(500, "ServerError", "InternalError", null);
            }

            if (Blobs.ContainsKey(blobName))
            {
                var refusal = NextOverwriteRefusal == OverwriteRefusal.Auto
                    ? OverwriteRefusal.Status409
                    : NextOverwriteRefusal;
                NextOverwriteRefusal = OverwriteRefusal.Auto;

                RequestFailedException ex = refusal switch
                {
                    OverwriteRefusal.Status409 =>
                        new RequestFailedException(409, "Conflict", BlobErrorCode.BlobAlreadyExists.ToString(), null),
                    OverwriteRefusal.Status412ConditionNotMet =>
                        new RequestFailedException(412, "ConditionNotMet", BlobErrorCode.ConditionNotMet.ToString(), null),
                    OverwriteRefusal.ErrorCodeBlobAlreadyExists =>
                        new RequestFailedException(0, "ErrorCodeOnly", BlobErrorCode.BlobAlreadyExists.ToString(), null),
                    _ => throw new InvalidOperationException("Unhandled refusal mode"),
                };

                if (IsAlreadyExists(ex)) return Task.FromResult<(bool, string?)>((false, null));
                throw ex;
            }

            var etag = $"\"0xFAKE_{++_etagCounter}\"";
            Blobs[blobName] = (payload, etag);
            return Task.FromResult<(bool, string?)>((true, etag));
        }

        protected override Task<(byte[]? Payload, string? ETag)> ReadBlobAsync(
            string blobName, CancellationToken ct)
        {
            if (!Blobs.TryGetValue(blobName, out var entry))
                return Task.FromResult<(byte[]?, string?)>((null, null));
            return Task.FromResult<(byte[]?, string?)>((entry.Bytes, entry.ETag));
        }

        protected override Task<string> WriteWithIfMatchAsync(
            string blobName, byte[] payload, string ifMatchEtag, CancellationToken ct)
        {
            if (!Blobs.TryGetValue(blobName, out var entry))
                throw new RequestFailedException(404, "NotFound", "BlobNotFound", null);
            if (entry.ETag != ifMatchEtag)
                throw new RequestFailedException(412, "ConditionNotMet", BlobErrorCode.ConditionNotMet.ToString(), null);

            var etag = $"\"0xFAKE_{++_etagCounter}\"";
            Blobs[blobName] = (payload, etag);
            return Task.FromResult(etag);
        }

        protected override Task DeleteBlobIfExistsAsync(string blobName, CancellationToken ct)
        {
            Blobs.Remove(blobName);
            return Task.CompletedTask;
        }
    }
}
