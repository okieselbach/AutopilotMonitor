using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// treat the agent-supplied URL as untrusted (allowlist, else distribution hosts only),
/// enforce the size cap both at Content-Length preflight and mid-stream, stay idempotent
/// under queue re-delivery (409 on the write-once upload == archived) — and, since
/// 2026-08-29, NEVER file bytes under a version they are not: every candidate download's
/// ProductVersion is checked against the observed version and the hosts are walked in
/// order until one matches. Tests pin every status branch via a recording subclass — no
/// Azurite, no HTTP, no disk.
/// </summary>
public class ImeMsiArchiverTests
{
    private const string Version = "1.104.102.0";
    private const string EventUrl = "https://imeswdb-afd-secondary.manage.microsoft.com/IntuneWindowsAgent.msi";
    private const string PrimaryUrl = "https://imeswda-afd-primary.manage.microsoft.com/IntuneWindowsAgent.msi";
    private const string SecondaryUrl = "https://imeswdb-afd-secondary.manage.microsoft.com/IntuneWindowsAgent.msi";
    private const string HotfixUrl = "https://imeswda-afd-hotfix.manage.microsoft.com/IntuneWindowsAgent.msi";

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
    // Candidate order — event URL first, then every distribution host, no duplicates
    // =========================================================================

    [Fact]
    public void BuildCandidateUrls_EventUrlFirst_ThenHosts_Deduplicated()
    {
        var custom = "https://swda01-mscdn.manage.microsoft.com/IntuneWindowsAgent.msi";

        Assert.Equal(new[] { custom, PrimaryUrl, SecondaryUrl, HotfixUrl }, ImeMsiArchiver.BuildCandidateUrls(custom));
        // Event URL equal to a host (any casing) is not tried twice.
        Assert.Equal(new[] { SecondaryUrl.ToUpperInvariant(), PrimaryUrl, HotfixUrl },
            ImeMsiArchiver.BuildCandidateUrls(SecondaryUrl.ToUpperInvariant()));
        Assert.Equal(new[] { PrimaryUrl, SecondaryUrl, HotfixUrl }, ImeMsiArchiver.BuildCandidateUrls(null));
        Assert.Equal(new[] { PrimaryUrl, SecondaryUrl, HotfixUrl },
            ImeMsiArchiver.BuildCandidateUrls("https://evil.example.com/IntuneWindowsAgent.msi"));
    }

    [Theory]
    [InlineData("1.105.103.0", "1.105.103.0", true)]
    [InlineData("1.105.103.0", "1.105.103", true)]   // trailing zero dropped by the MSI
    [InlineData("1.105.103", "1.105.103.0", true)]
    [InlineData(" 1.105.103.0 ", "1.105.103.0", true)]
    [InlineData("1.105.103.0", "1.104.102.0", false)]
    [InlineData("1.105.103.0", null, false)]         // unreadable package never matches
    [InlineData("1.105.103.0", "", false)]
    [InlineData("1.105.103.0", "latest", false)]
    public void VersionsMatch_cases(string observed, string? productVersion, bool expected)
    {
        Assert.Equal(expected, ImeMsiArchiver.VersionsMatch(observed, productVersion));
    }

    // =========================================================================
    // Re-queue rule for later sightings of a known version
    // =========================================================================

    public static IEnumerable<object?[]> RequeueCases()
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var fresh = now.AddHours(-1);
        var stale = now.AddHours(-25);
        static object?[] Case(bool isNew, string? status, DateTime? updatedAt, string? url, string? matchedBy, bool expected) =>
            new object?[] { isNew, status, updatedAt, url, matchedBy, expected };

        yield return Case(false, "Failed:VersionMismatch", stale, HotfixUrl, "productVersion", true);
        yield return Case(false, "Failed:VersionMismatch", null, HotfixUrl, "productVersion", true);   // never stamped
        yield return Case(false, null, null, HotfixUrl, "productVersion", true);                        // predates archiver / manual backfill
        yield return Case(false, "Queued", stale, HotfixUrl, "productVersion", true);                   // lost message, backoff elapsed
        yield return Case(false, "Failed:Download", stale, HotfixUrl, "productVersion", true);          // poisoned after retries
        yield return Case(false, "Failed:TooLarge", stale, HotfixUrl, "productVersion", true);          // cap may have changed
        yield return Case(false, "Failed:VersionMismatch", fresh, HotfixUrl, "productVersion", false);  // backoff
        yield return Case(false, "Queued", fresh, HotfixUrl, "productVersion", false);                  // already in flight
        yield return Case(false, "Archived", stale, HotfixUrl, "productVersion", false);
        yield return Case(false, "Failed:BadVersion", stale, HotfixUrl, "productVersion", false);       // permanent
        yield return Case(false, "Failed:VersionMismatch", stale, HotfixUrl, "fileName", false);        // URL not authoritative
        yield return Case(false, "Failed:VersionMismatch", stale, HotfixUrl, null, false);
        yield return Case(false, "Failed:VersionMismatch", stale, null, "productVersion", false);       // no URL
        yield return Case(false, "Failed:VersionMismatch", stale, "https://evil.example.com/IntuneWindowsAgent.msi", "productVersion", false);
        yield return Case(true, null, null, HotfixUrl, "productVersion", false);                        // first sighting has its own path
    }

    [Theory]
    [MemberData(nameof(RequeueCases))]
    public void ShouldRequeueOnSighting_cases(bool isNew, string? status, DateTime? updatedAt, string? url, string? matchedBy, bool expected)
    {
        var now = new DateTime(2026, 8, 29, 12, 0, 0, DateTimeKind.Utc);
        var sighting = new ImeVersionSighting { IsNew = isNew, MsiArchiveStatus = status, MsiArchiveUpdatedAt = updatedAt };

        Assert.Equal(expected, ImeMsiArchiver.ShouldRequeueOnSighting(sighting, url, matchedBy, now));
    }

    [Fact]
    public void ShouldRequeueOnSighting_NullSighting_False()
    {
        Assert.False(ImeMsiArchiver.ShouldRequeueOnSighting(null, HotfixUrl, "productVersion", DateTime.UtcNow));
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

    /// <summary>
    /// The same guard gates the GLOBAL ImeVersionHistory row (RecordImeVersionAsync) — a
    /// device-reported string that cannot be a Windows Installer ProductVersion must create
    /// nothing anywhere. MSI bounds: major/minor ≤ 255, build ≤ 65535.
    /// </summary>
    [Theory]
    [InlineData("1.105.103.0", true)]
    [InlineData("1.105.103", true)]
    [InlineData("1.0", true)]
    [InlineData("1.86.999.0", true)]           // plausible future build — only the archiver's ProductVersion check can refute it
    [InlineData("1", false)]                    // single component
    [InlineData("1.86.999999.0", false)]        // build > 65535
    [InlineData("256.1.1.0", false)]            // major > 255
    [InlineData("1.256.1.0", false)]            // minor > 255
    [InlineData("1.1.1.99999", false)]          // revision > 65535
    [InlineData("1.104.102.0.1", false)]
    [InlineData("1.104../escape", false)]
    [InlineData("<script>", false)]
    [InlineData("1.105.103.0 ", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPlausibleVersion_cases(string? version, bool expected)
    {
        Assert.Equal(expected, ImeMsiArchiver.IsPlausibleVersion(version));
    }

    [Fact]
    public void ShouldRequeueOnSighting_RejectedSighting_False()
    {
        // A rejected string has no row; an "unarchived" (null) status must not read as "re-queue".
        var rejected = new ImeVersionSighting { Rejected = true };

        Assert.False(ImeMsiArchiver.ShouldRequeueOnSighting(rejected, HotfixUrl, "productVersion", DateTime.UtcNow));
    }

    // =========================================================================
    // URL selection + version verification
    // =========================================================================

    [Fact]
    public async Task ArchiveAsync_AllowedEventUrl_ServingObservedVersion_IsUsedFirst()
    {
        var archiver = new RecordingArchiver();

        var result = await archiver.ArchiveAsync(Envelope(url: EventUrl));

        Assert.True(result.Success);
        Assert.Equal(1, archiver.DownloadCalls);
        Assert.Equal(EventUrl, archiver.DownloadUrls.Single());
        Assert.Equal(EventUrl, result.SourceUrl);
    }

    [Theory]
    [InlineData("https://evil.example.com/IntuneWindowsAgent.msi")] // allowlist reject
    [InlineData(null)]                                              // pre-enrichment agent
    public async Task ArchiveAsync_MissingOrRejectedEventUrl_StartsWithPrimaryHost(string? url)
    {
        var archiver = new RecordingArchiver();

        var result = await archiver.ArchiveAsync(Envelope(url: url));

        Assert.True(result.Success);
        Assert.Equal(PrimaryUrl, archiver.DownloadUrls.Single());
        Assert.Equal(PrimaryUrl, result.SourceUrl);
    }

    /// <summary>
    /// THE 2026-08-29 incident: the event carried no URL, the secondary host still served
    /// the previous build and it got archived under the new version. Now the wrong build is
    /// detected and the walk continues to the host that actually serves the version.
    /// </summary>
    [Fact]
    public async Task ArchiveAsync_HostsServeOtherBuilds_WalksUntilProductVersionMatches()
    {
        var archiver = new RecordingArchiver
        {
            ProductVersionByUrl =
            {
                [PrimaryUrl] = "1.105.101.0",
                [SecondaryUrl] = "1.104.102.0",
                [HotfixUrl] = "1.105.103.0",
            },
        };

        var result = await archiver.ArchiveAsync(Envelope(version: "1.105.103.0", url: null));

        Assert.True(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.Archived, result.Status);
        Assert.Equal(new[] { PrimaryUrl, SecondaryUrl, HotfixUrl }, archiver.DownloadUrls);
        Assert.Equal(HotfixUrl, result.SourceUrl);
        Assert.Equal("1.105.103.0/IntuneWindowsAgent.msi", result.BlobPath);
        Assert.Equal(1, archiver.UploadCalls);
        // Provenance records the verified version and the full walk.
        Assert.Contains("\"productVersion\": \"1.105.103.0\"", archiver.ProvenanceLastJson);
        Assert.Contains("version-mismatch", archiver.ProvenanceLastJson);
        Assert.Contains("1.105.101.0", archiver.ProvenanceLastJson);
        Assert.Contains("\"urlFromEvent\": false", archiver.ProvenanceLastJson);
    }

    [Fact]
    public async Task ArchiveAsync_EventUrlServesWrongBuild_FallsThroughToHosts()
    {
        var custom = "https://swda01-mscdn.manage.microsoft.com/IntuneWindowsAgent.msi";
        var archiver = new RecordingArchiver
        {
            ProductVersionByUrl = { [custom] = "1.103.101.0", [PrimaryUrl] = Version },
        };

        var result = await archiver.ArchiveAsync(Envelope(url: custom));

        Assert.True(result.Success);
        Assert.Equal(new[] { custom, PrimaryUrl }, archiver.DownloadUrls);
        Assert.Equal(PrimaryUrl, result.SourceUrl);
    }

    [Fact]
    public async Task ArchiveAsync_NoHostServesVersion_VersionMismatch_NotRetryable_NoUpload()
    {
        var archiver = new RecordingArchiver { DefaultProductVersion = "1.104.102.0" };

        var result = await archiver.ArchiveAsync(Envelope(version: "1.105.103.0", url: EventUrl));

        Assert.False(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.FailedVersionMismatch, result.Status);
        Assert.False(result.Retryable);
        Assert.Null(result.SourceUrl);
        Assert.Equal(3, archiver.DownloadCalls); // event URL == secondary → deduped
        Assert.Equal(0, archiver.UploadCalls);
        Assert.Equal(0, archiver.ProvenanceCalls);
    }

    [Fact]
    public async Task ArchiveAsync_UnreadablePackage_TreatedAsMismatch()
    {
        var archiver = new RecordingArchiver { DefaultProductVersion = null };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.False(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.FailedVersionMismatch, result.Status);
        Assert.Equal(0, archiver.UploadCalls);
    }

    [Fact]
    public async Task ArchiveAsync_UploadReceivesTheVerifiedBytes()
    {
        var right = new byte[4096];
        new Random(1).NextBytes(right);
        var wrong = new byte[4096];
        new Random(2).NextBytes(wrong);
        var archiver = new RecordingArchiver
        {
            PayloadByUrl = { [PrimaryUrl] = wrong, [SecondaryUrl] = right },
            ProductVersionByUrl = { [PrimaryUrl] = "0.0.0.1", [SecondaryUrl] = Version },
        };

        var result = await archiver.ArchiveAsync(Envelope(url: null));

        Assert.True(result.Success);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(right)).ToLowerInvariant(), result.Sha256);
        Assert.Equal(right, archiver.UploadedBytes);
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
        Assert.Contains("\"urlFromEvent\": true", archiver.ProvenanceLastJson);
    }

    // =========================================================================
    // Size cap — preflight and mid-stream (applies per candidate)
    // =========================================================================

    [Fact]
    public async Task ArchiveAsync_ContentLengthAboveCap_OnEveryHost_Rejected_NoUpload()
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
    public async Task ArchiveAsync_OversizedFirstHost_SmallerMatchingHostStillArchives()
    {
        var archiver = new RecordingArchiver
        {
            MaxDownloadSizeMB = 1,
            PayloadByUrl = { [PrimaryUrl] = new byte[2 * 1024 * 1024], [SecondaryUrl] = new byte[2048] },
        };

        var result = await archiver.ArchiveAsync(Envelope(url: null));

        Assert.True(result.Success);
        Assert.Equal(SecondaryUrl, result.SourceUrl);
        Assert.Contains("too-large", archiver.ProvenanceLastJson);
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
            ProvenanceExists = true,
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.True(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.Archived, result.Status);
        Assert.False(result.Retryable);
        Assert.Equal($"{Version}/IntuneWindowsAgent.msi", result.BlobPath);
        // Sidecar already there — the re-delivery must not rewrite it.
        Assert.Equal(0, archiver.ProvenanceCalls);
    }

    // =========================================================================
    // Provenance-write gap (daily review 2026-08-18): MSI upload is write-once,
    // so a first attempt that dies between the MSI and the provenance upload
    // funnels every retry into the 409 path — which must heal the sidecar.
    // =========================================================================

    [Fact]
    public async Task ArchiveAsync_ProvenanceUploadFails_RetryableError()
    {
        var archiver = new RecordingArchiver
        {
            ProvenanceException = new RequestFailedException(500, "storage hiccup"),
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.False(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.FailedError, result.Status);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task ArchiveAsync_RedeliveryWithMissingProvenance_HealsSidecarFromArchivedBytes()
    {
        // The archived blob's content deliberately differs from this attempt's re-download —
        // the healed provenance must describe the ARCHIVED bytes, not the fresh download.
        var archivedPayload = new byte[1024];
        new Random(7).NextBytes(archivedPayload);
        var expectedSha = Convert.ToHexString(SHA256.HashData(archivedPayload)).ToLowerInvariant();
        var archiver = new RecordingArchiver
        {
            UploadException = new RequestFailedException(409, "BlobAlreadyExists"),
            ProvenanceExists = false,
            ArchivedPayload = archivedPayload,
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.True(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.Archived, result.Status);
        Assert.False(result.Retryable);
        Assert.Equal(expectedSha, result.Sha256);
        Assert.Equal(archivedPayload.Length, result.SizeBytes);
        Assert.Equal(1, archiver.ProvenanceCalls);
        Assert.Equal($"{Version}/provenance.json", archiver.ProvenanceLastPath);
        Assert.Contains(expectedSha, archiver.ProvenanceLastJson);
        Assert.Contains(Version, archiver.ProvenanceLastJson);
    }

    [Fact]
    public async Task ArchiveAsync_ProvenanceFailure_ThenRedelivery_EndsHealed()
    {
        // THE regression scenario end to end on one instance: attempt 1 uploads the MSI but
        // the provenance write dies; attempt 2 (queue re-delivery) gets 409 on the write-once
        // MSI and must still produce the sidecar.
        var archiver = new RecordingArchiver
        {
            ProvenanceException = new RequestFailedException(500, "storage hiccup"),
        };
        var first = await archiver.ArchiveAsync(Envelope());
        Assert.False(first.Success);
        Assert.True(first.Retryable);

        archiver.ProvenanceException = null;
        archiver.UploadException = new RequestFailedException(409, "BlobAlreadyExists");
        archiver.ProvenanceExists = false; // attempt 1 never landed the sidecar
        var second = await archiver.ArchiveAsync(Envelope());

        Assert.True(second.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.Archived, second.Status);
        Assert.Equal($"{Version}/provenance.json", archiver.ProvenanceLastPath);
        Assert.NotNull(second.Sha256);
    }

    [Fact]
    public async Task ArchiveAsync_HealFails_RetryableError()
    {
        // Heal errors ride the normal retry ladder — the next re-delivery tries again.
        var archiver = new RecordingArchiver
        {
            UploadException = new RequestFailedException(409, "BlobAlreadyExists"),
            ProvenanceExists = false,
            ArchivedOpenException = new RequestFailedException(500, "read failed"),
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.False(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.FailedError, result.Status);
        Assert.True(result.Retryable);
        Assert.Equal(0, archiver.ProvenanceCalls);
    }

    // =========================================================================
    // Transient failures — retryable, never thrown; one bad host does not end the walk
    // =========================================================================

    [Fact]
    public async Task ArchiveAsync_AllHostsFailToDownload_RetryableDownloadStatus()
    {
        var archiver = new RecordingArchiver
        {
            DownloadException = new HttpRequestException("boom"),
        };

        var result = await archiver.ArchiveAsync(Envelope());

        Assert.False(result.Success);
        Assert.Equal(ImeMsiArchiver.Statuses.FailedDownload, result.Status);
        Assert.True(result.Retryable);
        Assert.Equal(3, archiver.DownloadCalls);
    }

    [Fact]
    public async Task ArchiveAsync_FirstHostDown_NextHostArchives()
    {
        var archiver = new RecordingArchiver
        {
            DownloadExceptionByUrl = { [PrimaryUrl] = new HttpRequestException("503") },
        };

        var result = await archiver.ArchiveAsync(Envelope(url: null));

        Assert.True(result.Success);
        Assert.Equal(SecondaryUrl, result.SourceUrl);
        Assert.Contains("download-failed", archiver.ProvenanceLastJson);
    }

    [Fact]
    public async Task ArchiveAsync_TransientFailurePlusMismatch_ReportsTransient_SoTheQueueRetries()
    {
        // One host down, the rest serving another build: the down host might have had the
        // version — worth the queue's retry ladder rather than a permanent mismatch.
        var archiver = new RecordingArchiver
        {
            DefaultProductVersion = "0.0.0.1",
            DownloadExceptionByUrl = { [HotfixUrl] = new HttpRequestException("503") },
        };

        var result = await archiver.ArchiveAsync(Envelope(url: null));

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
        public Dictionary<string, byte[]> PayloadByUrl { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>ProductVersion the fake reader reports for a URL's bytes; falls back to <see cref="DefaultProductVersion"/>.</summary>
        public Dictionary<string, string?> ProductVersionByUrl { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Default = the envelope version under test, i.e. every host serves the right build unless told otherwise.</summary>
        public string? DefaultProductVersion { get; set; } = Version;
        public bool ReportContentLength { get; set; } = true;
        public int MaxDownloadSizeMB { get; set; } = 250;
        public Exception? DownloadException { get; set; }
        public Dictionary<string, Exception> DownloadExceptionByUrl { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception? UploadException { get; set; }
        public Exception? ProvenanceException { get; set; }
        public bool ProvenanceExists { get; set; } = true;
        public byte[]? ArchivedPayload { get; set; }
        public Exception? ArchivedOpenException { get; set; }

        public int DownloadCalls => DownloadUrls.Count;
        public List<string> DownloadUrls { get; } = new();
        public int UploadCalls { get; private set; }
        public string? UploadLastPath { get; private set; }
        public long UploadedByteCount { get; private set; }
        public byte[] UploadedBytes { get; private set; } = Array.Empty<byte>();
        public int ProvenanceCalls { get; private set; }
        public string? ProvenanceLastPath { get; private set; }
        public string ProvenanceLastJson { get; private set; } = string.Empty;

        private string? _currentUrl;

        protected override Task<AdminConfiguration> GetAdminConfigurationAsync()
            => Task.FromResult(new AdminConfiguration { MaxImeMsiDownloadSizeMB = MaxDownloadSizeMB });

        protected override Task<(Stream Content, long ContentLength)> OpenDownloadAsync(
            string url, CancellationToken cancellationToken)
        {
            DownloadUrls.Add(url);
            _currentUrl = url;
            if (DownloadExceptionByUrl.TryGetValue(url, out var perUrl)) throw perUrl;
            if (DownloadException is not null) throw DownloadException;
            var payload = PayloadByUrl.TryGetValue(url, out var p) ? p : Payload;
            return Task.FromResult(
                ((Stream)new MemoryStream(payload), ReportContentLength ? payload.LongLength : -1L));
        }

        protected override Stream CreateSpool() => new MemoryStream();

        protected override string? ReadProductVersion(Stream msi)
        {
            // The bytes are opaque test payloads — resolve "what version is this" by origin.
            Assert.True(msi.CanSeek);
            Assert.Equal(0, msi.Position);
            return _currentUrl is not null && ProductVersionByUrl.TryGetValue(_currentUrl, out var v)
                ? v
                : DefaultProductVersion;
        }

        protected override async Task UploadMsiAsync(string blobPath, Stream content, CancellationToken cancellationToken)
        {
            UploadCalls++;
            UploadLastPath = blobPath;
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, cancellationToken);
            UploadedBytes = ms.ToArray();
            UploadedByteCount += UploadedBytes.Length;
            if (UploadException is not null) throw UploadException;
        }

        protected override Task UploadProvenanceAsync(string blobPath, string json, CancellationToken cancellationToken)
        {
            if (ProvenanceException is not null) throw ProvenanceException;
            ProvenanceCalls++;
            ProvenanceLastPath = blobPath;
            ProvenanceLastJson = json;
            return Task.CompletedTask;
        }

        protected override Task<bool> ProvenanceExistsAsync(string blobPath, CancellationToken cancellationToken)
            => Task.FromResult(ProvenanceExists);

        protected override Task<Stream> OpenArchivedMsiAsync(string blobPath, CancellationToken cancellationToken)
        {
            if (ArchivedOpenException is not null) throw ArchivedOpenException;
            return Task.FromResult<Stream>(new MemoryStream(ArchivedPayload ?? Payload));
        }
    }
}
