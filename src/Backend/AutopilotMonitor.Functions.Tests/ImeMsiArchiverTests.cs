using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Ime;
using AutopilotMonitor.Shared.Models;
using Azure;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The IME-installer archiver downloads a newly sighted IME build into the permanent
/// ime-archive container. Its contract: NEVER throw (worker decides retry from the result),
/// treat the agent-supplied URL as untrusted (allowlist, else canonical fallback), enforce
/// the size cap both at Content-Length preflight and mid-stream, and stay idempotent under
/// queue re-delivery (409 on the write-once upload == archived). Tests pin every status
/// branch via a recording subclass — no Azurite, no HTTP.
/// </summary>
public class ImeMsiArchiverTests
{
    private const string Version = "1.104.102.0";
    private const string EventUrl = "https://imeswdb-afd-secondary.manage.microsoft.com/IntuneWindowsAgent.msi";

    // =========================================================================
    // URL allowlist (static — the SSRF boundary)
    // =========================================================================

    [Theory]
    [InlineData(EventUrl, true)]
    [InlineData("https://swda01-mscdn.manage.microsoft.com/IntuneWindowsAgent.msi", true)]
    [InlineData("https://manage.microsoft.com/IntuneWindowsAgent.msi", true)]                  // apex host
    [InlineData("https://x.manage.microsoft.com/path/IntuneWindowsAgent.msi?sv=1&sig=2", true)] // query ignored
    [InlineData("https://x.manage.microsoft.com/intunewindowsagent.MSI", true)]                // case-insensitive
    [InlineData("http://imeswdb-afd-secondary.manage.microsoft.com/IntuneWindowsAgent.msi", false)] // https only
    [InlineData("https://evil.example.com/IntuneWindowsAgent.msi", false)]
    [InlineData("https://manage.microsoft.com.evil.com/IntuneWindowsAgent.msi", false)]        // suffix spoof
    [InlineData("https://xmanage.microsoft.com/IntuneWindowsAgent.msi", false)]                // needs the dot
    [InlineData("https://x.manage.microsoft.com/SomethingElse.msi", false)]                    // wrong filename
    [InlineData("https://x.manage.microsoft.com/IntuneWindowsAgent.msi.exe", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsAllowedMsiUrl_cases(string? url, bool expected)
    {
        Assert.Equal(expected, ImeMsiArchiver.IsAllowedMsiUrl(url));
    }

    // =========================================================================
    // Version guard (blob path is built from untrusted input)
    // =========================================================================

    [Theory]
    [InlineData("")]
    [InlineData("1.104.102.0.1")]           // too many components
    [InlineData("1.104../escape")]
    [InlineData("latest")]
    [InlineData("1.104.102.0 ")]
    public async Task ArchiveAsync_BadVersion_PermanentFailure_NoDownload(string version)
    {
        var archiver = new RecordingArchiver();

        var result = await archiver.ArchiveAsync(Envelope(version: version));

        Assert.False(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.FailedBadVersion, result.Status);
        Assert.False(result.Retryable);
        Assert.Equal(0, archiver.DownloadCalls + archiver.UploadCalls);
    }

    // =========================================================================
    // URL selection
    // =========================================================================

    [Fact]
    public async Task ArchiveAsync_AllowedEventUrl_IsUsed()
    {
        var archiver = new RecordingArchiver();

        var result = await archiver.ArchiveAsync(Envelope(url: EventUrl));

        Assert.True(result.Success);
        Assert.Equal(EventUrl, archiver.DownloadLastUrl);
        Assert.Equal(EventUrl, result.SourceUrl);
    }

    [Theory]
    [InlineData("https://evil.example.com/IntuneWindowsAgent.msi")] // allowlist reject
    [InlineData(null)]                                              // pre-enrichment agent
    public async Task ArchiveAsync_MissingOrRejectedEventUrl_FallsBackToCanonical(string? url)
    {
        var archiver = new RecordingArchiver();

        var result = await archiver.ArchiveAsync(Envelope(url: url));

        Assert.True(result.Success);
        Assert.Equal(ImeMsiArchiver.CanonicalMsiUrl, archiver.DownloadLastUrl);
        Assert.Equal(ImeMsiArchiver.CanonicalMsiUrl, result.SourceUrl);
    }

    // =========================================================================
    // Happy path
    // =========================================================================

    [Fact]
    public async Task ArchiveAsync_HappyPath_UploadsMsiAndProvenance_WithHashAndSize()
    {
        var payload = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(payload);
        var expectedSha = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var archiver = new RecordingArchiver { Payload = payload };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.True(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.Archived, result.Status);
        Assert.Equal($"{Version}/IntuneWindowsAgent.msi", result.BlobPath);
        Assert.Equal(expectedSha, result.Sha256);
        Assert.Equal(payload.Length, result.SizeBytes);
        Assert.Equal(1, archiver.UploadCalls);
        Assert.Equal($"{Version}/IntuneWindowsAgent.msi", archiver.UploadLastPath);
        Assert.Equal(payload.Length, archiver.UploadedByteCount);
        Assert.Equal(1, archiver.ProvenanceCalls);
        Assert.Equal($"{Version}/provenance.json", archiver.ProvenanceLastPath);
        Assert.Contains(expectedSha, archiver.ProvenanceLastJson);
        Assert.Contains(Version, archiver.ProvenanceLastJson);
    }

    // =========================================================================
    // Size cap — preflight and mid-stream
    // =========================================================================

    [Fact]
    public async Task ArchiveAsync_ContentLengthAboveCap_Rejected_NoUpload()
    {
        var archiver = new RecordingArchiver
        {
            MaxDownloadSizeMB = 1,
            Payload = new byte[2 * 1024 * 1024],
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.False(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.FailedTooLarge, result.Status);
        Assert.False(result.Retryable);
        Assert.Equal(0, archiver.UploadCalls);
    }

    [Fact]
    public async Task ArchiveAsync_UnknownContentLength_CapStillEnforcedMidStream()
    {
        // A lying/absent Content-Length must not bypass the cap — the hashing wrapper
        // aborts the stream as soon as the byte count crosses it.
        var archiver = new RecordingArchiver
        {
            MaxDownloadSizeMB = 1,
            Payload = new byte[2 * 1024 * 1024],
            ReportContentLength = false,
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.False(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.FailedTooLarge, result.Status);
        Assert.False(result.Retryable);
        Assert.Equal(0, archiver.ProvenanceCalls);
    }

    [Fact]
    public async Task ArchiveAsync_CapZero_MeansUnlimited()
    {
        var archiver = new RecordingArchiver
        {
            MaxDownloadSizeMB = 0,
            Payload = new byte[2 * 1024 * 1024],
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.True(result.Success);
    }

    // =========================================================================
    // Idempotency under queue re-delivery
    // =========================================================================

    [Fact]
    public async Task ArchiveAsync_BlobAlreadyExists_TreatedAsArchived_NotRetryable()
    {
        var archiver = new RecordingArchiver
        {
            UploadException = new RequestFailedException(409, "BlobAlreadyExists"),
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.True(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.Archived, result.Status);
        Assert.False(result.Retryable);
        Assert.Equal($"{Version}/IntuneWindowsAgent.msi", result.BlobPath);
    }

    // =========================================================================
    // Transient failures — retryable, never thrown
    // =========================================================================

    [Fact]
    public async Task ArchiveAsync_DownloadFails_RetryableDownloadStatus()
    {
        var archiver = new RecordingArchiver
        {
            DownloadException = new HttpRequestException("boom"),
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.False(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.FailedDownload, result.Status);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task ArchiveAsync_Timeout_RetryableTimeoutStatus()
    {
        var archiver = new RecordingArchiver
        {
            DownloadException = new OperationCanceledException(),
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.False(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.FailedTimeout, result.Status);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task ArchiveAsync_UnexpectedUploadError_RetryableErrorStatus()
    {
        var archiver = new RecordingArchiver
        {
            UploadException = new InvalidOperationException("storage hiccup"),
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.False(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.FailedError, result.Status);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task ArchiveAsync_CallerCancellation_Propagates()
    {
        // Host shutdown must reach the worker's dispatch loop so the message stays queued.
        var archiver = new RecordingArchiver();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        archiver.DownloadException = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => archiver.ArchiveAsync(Envelope(), cts.Token));
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static ImeMsiArchiveEnvelope Envelope(string version = Version, string? url = EventUrl) =>
        new()
        {
            Version = version,
            MsiDownloadUrl = url,
            MsiMatchedBy = "productVersion",
            TenantId = "11111111-1111-1111-1111-111111111111",
            SessionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
            EnqueuedAt = DateTime.UtcNow,
        };

    private sealed class RecordingArchiver : ImeMsiArchiver
    {
        public byte[] Payload { get; set; } = new byte[2048];
        public bool ReportContentLength { get; set; } = true;
        public int MaxDownloadSizeMB { get; set; } = 250;
        public Exception? DownloadException { get; set; }
        public Exception? UploadException { get; set; }

        public int DownloadCalls { get; private set; }
        public string? DownloadLastUrl { get; private set; }
        public int UploadCalls { get; private set; }
        public string? UploadLastPath { get; private set; }
        public long UploadedByteCount { get; private set; }
        public int ProvenanceCalls { get; private set; }
        public string? ProvenanceLastPath { get; private set; }
        public string ProvenanceLastJson { get; private set; } = string.Empty;

        protected override Task<AdminConfiguration> GetAdminConfigurationAsync()
            => Task.FromResult(new AdminConfiguration { MaxImeMsiDownloadSizeMB = MaxDownloadSizeMB });

        protected override Task<(Stream Content, long ContentLength)> OpenDownloadAsync(
            string url, CancellationToken cancellationToken)
        {
            DownloadCalls++;
            DownloadLastUrl = url;
            if (DownloadException is not null) throw DownloadException;
            return Task.FromResult(
                ((Stream)new MemoryStream(Payload), ReportContentLength ? Payload.LongLength : -1L));
        }

        protected override async Task UploadMsiAsync(string blobPath, Stream content, CancellationToken cancellationToken)
        {
            UploadCalls++;
            UploadLastPath = blobPath;
            // Drain the stream like the real blob upload would — this is what drives the
            // hashing/cap wrapper (and mid-stream aborts surface here as exceptions).
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
                UploadedByteCount += read;
            if (UploadException is not null) throw UploadException;
        }

        protected override Task UploadProvenanceAsync(string blobPath, string json, CancellationToken cancellationToken)
        {
            ProvenanceCalls++;
            ProvenanceLastPath = blobPath;
            ProvenanceLastJson = json;
            return Task.CompletedTask;
        }
    }
}
