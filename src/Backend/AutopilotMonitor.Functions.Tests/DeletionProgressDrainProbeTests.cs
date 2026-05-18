using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Offboarding;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Plan §7.4 step 5 + §10.1: drain predicate is true only when the progress blob has
/// CompletedAt != null AND TombstoneStarted == true. 404 is "not yet done", not an error.
/// </summary>
public class DeletionProgressDrainProbeTests
{
    private const string TenantId   = "44444444-4444-4444-4444-444444444444";
    private const string SessionId  = "55555555-5555-5555-5555-555555555555";
    private const string ManifestId = "manifest-1";

    [Fact]
    public async Task IsCompleted_TrueOnlyWhen_CompletedAtSet_AndTombstoneStarted()
    {
        var sut = new DeletionProgressDrainProbe(
            new FakeBlob(new DeletionProgress
            {
                CompletedAt = DateTime.UtcNow,
                TombstoneStarted = true,
            }),
            NullLogger<DeletionProgressDrainProbe>.Instance);

        Assert.True(await sut.IsCascadeCompletedAsync(TenantId, SessionId, ManifestId));
    }

    [Fact]
    public async Task IsCompleted_FalseWhen_CompletedAtNull()
    {
        var sut = new DeletionProgressDrainProbe(
            new FakeBlob(new DeletionProgress { TombstoneStarted = true, CompletedAt = null }),
            NullLogger<DeletionProgressDrainProbe>.Instance);

        Assert.False(await sut.IsCascadeCompletedAsync(TenantId, SessionId, ManifestId));
    }

    [Fact]
    public async Task IsCompleted_FalseWhen_TombstoneNotStarted_RevWalkthrough()
    {
        // If CompletedAt got somehow set without TombstoneStarted, the cascade did not
        // finish the FINAL step — drain must say "no" to defend against truncated progress.
        var sut = new DeletionProgressDrainProbe(
            new FakeBlob(new DeletionProgress { CompletedAt = DateTime.UtcNow, TombstoneStarted = false }),
            NullLogger<DeletionProgressDrainProbe>.Instance);

        Assert.False(await sut.IsCascadeCompletedAsync(TenantId, SessionId, ManifestId));
    }

    [Fact]
    public async Task IsCompleted_FalseOn404_NotAnError()
    {
        var sut = new DeletionProgressDrainProbe(
            new FakeBlob(throwOnDownload: new RequestFailedException(404, "NotFound", "BlobNotFound", null)),
            NullLogger<DeletionProgressDrainProbe>.Instance);

        Assert.False(await sut.IsCascadeCompletedAsync(TenantId, SessionId, ManifestId));
    }

    [Fact]
    public async Task IsCompleted_PropagatesNon404StorageError()
    {
        // 503 / 500 are real transients the worker must surface so the queue retries.
        var sut = new DeletionProgressDrainProbe(
            new FakeBlob(throwOnDownload: new RequestFailedException(503, "ServiceUnavailable", "ServerBusy", null)),
            NullLogger<DeletionProgressDrainProbe>.Instance);

        await Assert.ThrowsAsync<RequestFailedException>(
            () => sut.IsCascadeCompletedAsync(TenantId, SessionId, ManifestId));
    }

    // ── Fake BlobStorageService that overrides only the download seam we need ──

    private sealed class FakeBlob : BlobStorageService
    {
        private readonly DeletionProgress? _progress;
        private readonly RequestFailedException? _throw;

        public FakeBlob(DeletionProgress progress)
            : base(new BlobServiceClient("UseDevelopmentStorage=true"), NullLogger<BlobStorageService>.Instance, usesManagedIdentity: false)
        {
            _progress = progress;
        }

        public FakeBlob(RequestFailedException throwOnDownload)
            : base(new BlobServiceClient("UseDevelopmentStorage=true"), NullLogger<BlobStorageService>.Instance, usesManagedIdentity: false)
        {
            _throw = throwOnDownload;
        }

        public override Task<(DeletionProgress Progress, string ETag)> DownloadDeletionProgressAsync(
            string tenantId, string sessionId, string manifestId, CancellationToken cancellationToken = default)
        {
            if (_throw != null) throw _throw;
            return Task.FromResult((_progress!, "\"0xFAKE\""));
        }
    }
}
