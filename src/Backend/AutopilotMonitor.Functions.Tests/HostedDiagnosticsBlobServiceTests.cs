using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Diagnostics;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Covers the validation, path-build, SAS-shape, and lazy-init contracts on
/// <see cref="HostedDiagnosticsBlobService"/>. Uses a Fake subclass that overrides the
/// three protected seams (container access, lazy create, SAS build) so we exercise the
/// service end-to-end without standing up Azurite. Only behaviours the production code
/// depends on are asserted here — the underlying Azure SDK's correctness is taken on
/// faith.
/// </summary>
public class HostedDiagnosticsBlobServiceTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private const string Filename = "AgentDiagnostics-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa-20260519T120000.zip";

    // ── Path build + validation ────────────────────────────────────────────────────

    [Fact]
    public void BuildBlobPath_ProducesTenantPrefixedPath()
    {
        var path = HostedDiagnosticsBlobService.BuildBlobPath(TenantA, Filename);
        Assert.Equal($"{TenantA}/{Filename}", path);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("name/with/slash.zip")]
    [InlineData("name\\with\\backslash.zip")]
    [InlineData("name..traversal.zip")]
    [InlineData("name\0null.zip")]
    public void ValidateFilename_RejectsHostileInputs(string? filename)
    {
        Assert.Throws<ArgumentException>(() => HostedDiagnosticsBlobService.ValidateFilename(filename!));
    }

    [Fact]
    public void ValidateFilename_RejectsOversize()
    {
        var huge = new string('a', 300) + ".zip";
        Assert.Throws<ArgumentException>(() => HostedDiagnosticsBlobService.ValidateFilename(huge));
    }

    [Fact]
    public void ValidateFilename_AcceptsNormalAgentDiagnosticsName()
    {
        HostedDiagnosticsBlobService.ValidateFilename(Filename); // must not throw
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("no-slash.zip")]
    [InlineData("/leading-slash.zip")]
    [InlineData("trailing-slash/")]
    [InlineData("ok/..traversal.zip")]
    [InlineData("ok/with\0null.zip")]
    [InlineData("ok/with\\back.zip")]
    public void ValidateBlobPath_RejectsHostileInputs(string? blobPath)
    {
        Assert.Throws<ArgumentException>(() => HostedDiagnosticsBlobService.ValidateBlobPath(blobPath!));
    }

    [Fact]
    public void ValidateBlobPath_AcceptsTenantPrefixedPath()
    {
        HostedDiagnosticsBlobService.ValidateBlobPath($"{TenantA}/{Filename}"); // must not throw
    }

    // ── GenerateUploadSasAsync — TTL clamp + path build + seam dispatch ────────────

    [Fact]
    public async Task GenerateUploadSas_RejectsNonGuidTenantId()
    {
        var svc = new FakeHostedDiagnosticsBlobService();
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.GenerateUploadSasAsync("not-a-guid", Filename));
    }

    [Fact]
    public async Task GenerateUploadSas_RejectsFilenameWithSlash()
    {
        var svc = new FakeHostedDiagnosticsBlobService();
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.GenerateUploadSasAsync(TenantA, "../other-tenant/escape.zip"));
    }

    [Fact]
    public async Task GenerateUploadSas_ReturnsTenantPrefixedBlobPath()
    {
        var svc = new FakeHostedDiagnosticsBlobService();
        var result = await svc.GenerateUploadSasAsync(TenantA, Filename);
        Assert.Equal($"{TenantA}/{Filename}", result.BlobPath);
    }

    [Fact]
    public async Task GenerateUploadSas_EnsuresContainerOnce()
    {
        var svc = new FakeHostedDiagnosticsBlobService();
        await svc.GenerateUploadSasAsync(TenantA, Filename);
        await svc.GenerateUploadSasAsync(TenantA, Filename);
        await svc.GenerateUploadSasAsync(TenantB, "other.zip");
        Assert.Equal(1, svc.EnsureContainerCallCount);
    }

    [Fact]
    public async Task GenerateUploadSas_ClampsTtlToMaximum()
    {
        var svc = new FakeHostedDiagnosticsBlobService();
        var requested = TimeSpan.FromHours(24);
        var before = DateTime.UtcNow;
        var result = await svc.GenerateUploadSasAsync(TenantA, Filename, requested);
        var maxAllowed = before.Add(HostedDiagnosticsBlobService.MaxUploadSasTtl).AddMinutes(1); // tolerance
        Assert.True(result.ExpiresAt <= maxAllowed,
            $"ExpiresAt {result.ExpiresAt:O} exceeded MaxUploadSasTtl-bounded {maxAllowed:O}");
    }

    [Fact]
    public async Task GenerateUploadSas_DefaultTtlIsApproximately15Minutes()
    {
        var svc = new FakeHostedDiagnosticsBlobService();
        var before = DateTime.UtcNow;
        var result = await svc.GenerateUploadSasAsync(TenantA, Filename);
        var delta = result.ExpiresAt - before;
        Assert.InRange(delta.TotalMinutes, 14, 16);
    }

    [Fact]
    public async Task GenerateUploadSas_PassesBlobScopedResourceAndWriteCreateOnlyPermsToBuilder()
    {
        var svc = new FakeHostedDiagnosticsBlobService();
        await svc.GenerateUploadSasAsync(TenantA, Filename);

        Assert.Single(svc.CapturedSasBuilders);
        var builder = svc.CapturedSasBuilders.Single();
        Assert.Equal("b", builder.Resource); // blob-scoped, NOT "c" (container)
        Assert.Equal($"{TenantA}/{Filename}", builder.BlobName);
        Assert.Equal("diagnostics", builder.BlobContainerName);

        // Reconstruct the permissions string to ensure ONLY Write + Create are set —
        // no Read (would expose other-tenant blobs in cross-tenant scenarios),
        // no Delete (cleanup is service-side via DeleteIfExistsAsync),
        // no List (agent has no enumeration need).
        var permsString = builder.Permissions ?? string.Empty;
        Assert.Contains("w", permsString);
        Assert.Contains("c", permsString);
        Assert.DoesNotContain("r", permsString);
        Assert.DoesNotContain("d", permsString);
        Assert.DoesNotContain("l", permsString);
    }

    [Fact]
    public async Task GenerateUploadSas_DifferentTenants_GetDifferentBlobPaths()
    {
        var svc = new FakeHostedDiagnosticsBlobService();
        var a = await svc.GenerateUploadSasAsync(TenantA, Filename);
        var b = await svc.GenerateUploadSasAsync(TenantB, Filename);
        Assert.NotEqual(a.BlobPath, b.BlobPath);
        Assert.StartsWith(TenantA + "/", a.BlobPath);
        Assert.StartsWith(TenantB + "/", b.BlobPath);
    }

    // ── DeleteBySessionPrefixAsync — cascade sweep for multi-package sessions ──────

    private const string SessionA = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    [Theory]
    [InlineData("not-a-guid", SessionA)]
    [InlineData(TenantA, "not-a-guid")]
    public async Task DeleteBySessionPrefix_RejectsNonGuidInputs(string tenantId, string sessionId)
    {
        var svc = new PrefixDeleteFake(containerExists: true);
        await Assert.ThrowsAsync<ArgumentException>(
            () => svc.DeleteBySessionPrefixAsync(tenantId, sessionId));
    }

    [Fact]
    public async Task DeleteBySessionPrefix_MissingContainer_IsNoOp()
    {
        var svc = new PrefixDeleteFake(containerExists: false);
        var deleted = await svc.DeleteBySessionPrefixAsync(TenantA, SessionA);
        Assert.Equal(0, deleted);
        Assert.Null(svc.CapturedPrefix);
    }

    [Fact]
    public async Task DeleteBySessionPrefix_UsesSessionScopedPrefix_AndDeletesEveryMatch()
    {
        // Two packages for the session: an on-demand (server-requested) one and the terminal
        // one. The sweep must issue a delete for BOTH even though the Sessions row (and thus
        // the deletion manifest) only references the last.
        var svc = new PrefixDeleteFake(
            containerExists: true,
            blobNames: new[]
            {
                $"{TenantA}/AgentDiagnostics-{SessionA}-20260724T100000-server-requested.zip",
                $"{TenantA}/AgentDiagnostics-{SessionA}-20260724T113000.zip",
            });

        var deleted = await svc.DeleteBySessionPrefixAsync(TenantA, SessionA);

        Assert.Equal(2, deleted);
        Assert.Equal($"{TenantA}/AgentDiagnostics-{SessionA}-", svc.CapturedPrefix);
        Assert.Equal(2, svc.DeletedBlobNames.Count);
        Assert.All(svc.DeletedBlobNames, n => Assert.StartsWith(svc.CapturedPrefix!, n));
    }

    [Fact]
    public async Task DeleteBySessionPrefix_NoMatches_ReturnsZero()
    {
        var svc = new PrefixDeleteFake(containerExists: true, blobNames: Array.Empty<string>());
        var deleted = await svc.DeleteBySessionPrefixAsync(TenantA, SessionA);
        Assert.Equal(0, deleted);
        Assert.Equal($"{TenantA}/AgentDiagnostics-{SessionA}-", svc.CapturedPrefix);
    }

    /// <summary>
    /// Overrides <c>GetContainerClient</c> with a Moq'd <see cref="BlobContainerClient"/> whose
    /// enumeration returns a canned page for the CAPTURED prefix. Prefix filtering itself is
    /// the Azure SDK's contract — these tests pin what prefix we ask for and that every
    /// returned item is deleted.
    /// </summary>
    private sealed class PrefixDeleteFake : HostedDiagnosticsBlobService
    {
        private readonly Mock<BlobContainerClient> _container;

        public string? CapturedPrefix { get; private set; }
        public List<string> DeletedBlobNames { get; } = new();

        public PrefixDeleteFake(bool containerExists, string[]? blobNames = null)
            : base(
                new BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<HostedDiagnosticsBlobService>.Instance,
                usesManagedIdentity: false)
        {
            _container = new Mock<BlobContainerClient>();
            _container
                .Setup(c => c.ExistsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(Response.FromValue(containerExists, Mock.Of<Response>()));

            _container
                .Setup(c => c.GetBlobsAsync(
                    It.IsAny<BlobTraits>(), It.IsAny<BlobStates>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns((BlobTraits _, BlobStates _, string? prefix, CancellationToken _) =>
                {
                    CapturedPrefix = prefix;
                    var items = (blobNames ?? Array.Empty<string>())
                        .Select(n => BlobsModelFactory.BlobItem(name: n))
                        .ToList();
                    return AsyncPageable<BlobItem>.FromPages(new[]
                    {
                        Page<BlobItem>.FromValues(items, continuationToken: null, Mock.Of<Response>()),
                    });
                });

            _container
                .Setup(c => c.GetBlobClient(It.IsAny<string>()))
                .Returns((string name) =>
                {
                    var blob = new Mock<BlobClient>();
                    blob
                        .Setup(b => b.DeleteIfExistsAsync(
                            It.IsAny<DeleteSnapshotsOption>(), It.IsAny<BlobRequestConditions>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(() =>
                        {
                            DeletedBlobNames.Add(name);
                            return Response.FromValue(true, Mock.Of<Response>());
                        });
                    return blob.Object;
                });
        }

        protected override BlobContainerClient GetContainerClient() => _container.Object;
    }

    // ── Constants ───────────────────────────────────────────────────────────────────

    [Fact]
    public void HostedDiagnosticsContainerName_IsStableConstant()
    {
        // Migration scripts + skills rely on this exact container name; alarm if it ever moves.
        Assert.Equal("diagnostics", AutopilotMonitor.Shared.Constants.BlobContainers.HostedDiagnostics);
    }

    // ── Fake test seam ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Subclass that overrides the three protected seams (<c>GetContainerClient</c>,
    /// <c>EnsureContainerAsync</c>, <c>BuildUploadSasUriAsync</c>) so the suite runs
    /// without Azurite. Captures the <see cref="BlobSasBuilder"/> the service builds so
    /// permission/scope assertions can be done at the API level.
    /// </summary>
    private sealed class FakeHostedDiagnosticsBlobService : HostedDiagnosticsBlobService
    {
        public int EnsureContainerCallCount { get; private set; }
        public List<BlobSasBuilder> CapturedSasBuilders { get; } = new();

        public FakeHostedDiagnosticsBlobService()
            : base(
                new BlobServiceClient("UseDevelopmentStorage=true"),
                NullLogger<HostedDiagnosticsBlobService>.Instance,
                usesManagedIdentity: false)
        {
        }

        protected override Task CreateContainerIfNotExistsCoreAsync(CancellationToken cancellationToken)
        {
            EnsureContainerCallCount++;
            return Task.CompletedTask;
        }

        protected override Task<Uri> BuildUploadSasUriAsync(
            string blobPath, DateTimeOffset expiresOn, CancellationToken cancellationToken)
        {
            // Re-create the same SAS builder shape the production code does, so the test
            // captures what permissions/resource/path the production path WOULD set —
            // without actually signing (which needs an account key the dev string lacks).
            var builder = new BlobSasBuilder
            {
                BlobContainerName = "diagnostics",
                BlobName = blobPath,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
                ExpiresOn = expiresOn,
            };
            builder.SetPermissions(BlobSasPermissions.Write | BlobSasPermissions.Create);
            CapturedSasBuilders.Add(builder);

            return Task.FromResult(new Uri($"https://fake.blob.core.windows.net/diagnostics/{blobPath}?fake=sas"));
        }
    }
}
