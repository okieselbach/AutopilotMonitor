using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Verifies the gzip + SHA-256 + IfNoneMatch behaviour of the new manifest upload/download
/// helpers on <see cref="BlobStorageService"/>. Uses a small subclass that overrides the two
/// virtual blob-IO seams — much simpler than wrestling with <c>BlobsModelFactory</c>'s
/// shifting parameter list, and matches repo convention (no Azurite).
/// </summary>
public class BlobStorageServiceDeletionManifestTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task Upload_then_download_round_trips_manifest()
    {
        var manifest = SampleManifest();
        var sut = new FakeBlobStorageService();

        var pointer = await sut.UploadDeletionManifestAsync(manifest);

        Assert.Equal($"{TenantId}/{SessionId}/{manifest.ManifestId}.snapshot.json.gz", pointer.BlobName);
        Assert.Equal("deletion-manifests", pointer.ContainerName);
        Assert.True(pointer.SizeBytes > 0);
        Assert.Matches("^[0-9a-f]{64}$", pointer.SnapshotSha256);
        Assert.NotNull(sut.LastWrittenBytes);
        Assert.Equal(sut.LastWrittenBytes!.Length, pointer.SizeBytes);

        // Replay back through Download — uses the bytes the Upload captured.
        var roundTripped = await sut.DownloadDeletionManifestAsync(TenantId, SessionId, manifest.ManifestId);

        Assert.Equal(manifest.ManifestId, roundTripped.ManifestId);
        Assert.Equal(manifest.TenantId, roundTripped.TenantId);
        Assert.Equal(manifest.SessionId, roundTripped.SessionId);
        Assert.Equal(manifest.Reason, roundTripped.Reason);
        Assert.Equal(manifest.Steps.Count, roundTripped.Steps.Count);
        Assert.Equal(manifest.SchemaHash, roundTripped.SchemaHash);
    }

    [Fact]
    public async Task Upload_sets_content_encoding_gzip_and_sha256_metadata()
    {
        var manifest = SampleManifest();
        var sut = new FakeBlobStorageService();

        await sut.UploadDeletionManifestAsync(manifest);

        Assert.NotNull(sut.LastWrittenOptions);
        Assert.Equal("application/json", sut.LastWrittenOptions!.HttpHeaders!.ContentType);
        Assert.Equal("gzip", sut.LastWrittenOptions.HttpHeaders.ContentEncoding);
        Assert.NotNull(sut.LastWrittenOptions.Metadata);
        Assert.True(sut.LastWrittenOptions.Metadata!.ContainsKey("sha256"));
        Assert.Matches("^[0-9a-f]{64}$", sut.LastWrittenOptions.Metadata["sha256"]);
        // Plan §1 P1: snapshot is written exactly once → IfNoneMatch=* makes overwrites fail loud.
        Assert.NotNull(sut.LastWrittenOptions.Conditions);
        Assert.Equal(ETag.All, sut.LastWrittenOptions.Conditions!.IfNoneMatch);
    }

    [Fact]
    public async Task Upload_payload_is_gzipped_and_decompresses_to_canonical_manifest_json()
    {
        var manifest = SampleManifest();
        var sut = new FakeBlobStorageService();

        await sut.UploadDeletionManifestAsync(manifest);

        var captured = sut.LastWrittenBytes;
        Assert.NotNull(captured);
        // Magic bytes 0x1F 0x8B identify the payload as GZip.
        Assert.Equal(0x1F, captured![0]);
        Assert.Equal(0x8B, captured[1]);

        // Gunzip and confirm the bytes are valid manifest JSON.
        using var input = new MemoryStream(captured);
        using var gunzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gunzip.CopyTo(output);
        var decoded = JsonSerializer.Deserialize<DeletionManifest>(output.ToArray(), DeletionManifestJson.SerializerOptions);
        Assert.NotNull(decoded);
        Assert.Equal(manifest.ManifestId, decoded!.ManifestId);
    }

    [Fact]
    public async Task Download_fails_loud_on_sha256_mismatch()
    {
        var manifest = SampleManifest();
        var sut = new FakeBlobStorageService();
        await sut.UploadDeletionManifestAsync(manifest);

        // Tamper with the captured metadata so the recorded sha differs from the actual hash.
        sut.LastWrittenOptions!.Metadata!["sha256"] = new string('0', 64);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => sut.DownloadDeletionManifestAsync(TenantId, SessionId, manifest.ManifestId));
        Assert.Contains("SHA-256 mismatch", ex.Message);
    }

    [Fact]
    public async Task Download_fails_loud_when_sha256_metadata_missing()
    {
        var manifest = SampleManifest();
        var sut = new FakeBlobStorageService();
        await sut.UploadDeletionManifestAsync(manifest);

        sut.LastWrittenOptions!.Metadata!.Remove("sha256");

        var ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => sut.DownloadDeletionManifestAsync(TenantId, SessionId, manifest.ManifestId));
        Assert.Contains("missing the required 'sha256' metadata", ex.Message);
    }

    [Fact]
    public async Task Upload_fails_loud_on_overwrite_attempt()
    {
        var manifest = SampleManifest();
        var sut = new FakeBlobStorageService
        {
            // Simulate the IfNoneMatch=* race — second producer for the same manifestId hits 409.
            UploadException = new RequestFailedException(409, "BlobAlreadyExists", "BlobAlreadyExists", innerException: null),
        };

        var ex = await Assert.ThrowsAsync<RequestFailedException>(
            () => sut.UploadDeletionManifestAsync(manifest));
        Assert.Equal(409, ex.Status);
    }

    [Fact]
    public async Task Upload_throws_on_missing_required_fields()
    {
        var sut = new FakeBlobStorageService();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => sut.UploadDeletionManifestAsync(null!));
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.UploadDeletionManifestAsync(new DeletionManifest()));
    }

    // ---------------------------------------------------------------- Test fixture ----

    private static DeletionManifest SampleManifest() => new DeletionManifest
    {
        ManifestId = "0123456789ABCDEF_FEDCBA9876543210",
        TenantId = TenantId,
        SessionId = SessionId,
        CreatedAt = new DateTime(2026, 5, 12, 10, 0, 0, DateTimeKind.Utc),
        CreatedBy = new DeletionActor { Type = "admin", Actor = "alice@example.com" },
        Reason = "admin_delete",
        RetentionContext = new DeletionRetentionContext { TenantRetentionDays = 90 },
        Steps = new List<DeletionStep>
        {
            new DeletionStep { Order = 1, Table = "Events", Class = DeletionStepClass.PkBySession, RowCount = 0 },
        },
        SchemaHash = "sha256:placeholder",
    };

    /// <summary>
    /// Captures the bytes + options written by Upload and replays them on Download. The
    /// production overrides hit Azure Blob Storage; this fake bypasses the SDK entirely so
    /// tests assert behaviour without sweating the BlobsModelFactory shape.
    /// </summary>
    private sealed class FakeBlobStorageService : BlobStorageService
    {
        public byte[]? LastWrittenBytes { get; private set; }
        public BlobUploadOptions? LastWrittenOptions { get; private set; }
        public Exception? UploadException { get; set; }

        public FakeBlobStorageService()
            : base(new BlobServiceClient("UseDevelopmentStorage=true"), NullLogger<BlobStorageService>.Instance, usesManagedIdentity: false)
        {
        }

        protected internal override Task WriteDeletionManifestBlobAsync(
            string blobName, byte[] gzipped, BlobUploadOptions options, CancellationToken cancellationToken)
        {
            if (UploadException != null) throw UploadException;
            LastWrittenBytes = gzipped;
            LastWrittenOptions = options;
            return Task.CompletedTask;
        }

        protected internal override Task<(byte[] Gzipped, IDictionary<string, string>? Metadata)>
            ReadDeletionManifestBlobAsync(string blobName, CancellationToken cancellationToken)
        {
            if (LastWrittenBytes == null)
            {
                throw new InvalidOperationException("Test must call UploadDeletionManifestAsync before DownloadDeletionManifestAsync.");
            }
            return Task.FromResult<(byte[], IDictionary<string, string>?)>(
                (LastWrittenBytes, LastWrittenOptions?.Metadata));
        }
    }
}
