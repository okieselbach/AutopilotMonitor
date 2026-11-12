using System.Net;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using AutopilotMonitor.Functions.Services.Vulnerability;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="KevDataService"/> answers lookups from the catalog it holds and never fetches on
/// that path: a scan asks once per CVE. The catalog changes only through a refresh, and a refresh
/// that fails or comes back empty says so and leaves the loaded catalog in service.
/// </summary>
public class KevDataServiceTests
{
    /// <summary>Answers each request with what the test queued for it and counts the requests.</summary>
    private sealed class CisaStub : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Answer { get; set; } = () => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(Answer());
        }
    }

    private static Func<HttpResponseMessage> Catalog(params string[] cveIds) =>
        () => NvdFixture.Json(ReferenceDataFixture.KevCatalogJson(cveIds));

    [Fact]
    public void Lookup_BeforeAnythingIsLoaded_FindsNothingAndSendsNoRequest()
    {
        var cisa = new CisaStub { Answer = Catalog("CVE-2023-4863") };
        var kev = ReferenceDataFixture.Kev(cisa);

        Assert.Null(kev.Lookup("CVE-2023-4863"));
        Assert.Null(kev.Lookup(null));
        Assert.Null(kev.Lookup("  "));

        Assert.Equal(0, cisa.Requests);
        Assert.Equal(0, kev.CatalogSize);
    }

    [Fact]
    public async Task Refresh_LoadsTheCatalog_AndLookupsReadItWithoutFetching()
    {
        var cisa = new CisaStub { Answer = Catalog("CVE-2023-4863", "CVE-2024-0001") };
        var blob = new Mock<BlobClient>();
        var kev = ReferenceDataFixture.Kev(cisa, blob);

        Assert.True(await kev.RefreshAsync());

        var entry = kev.Lookup("cve-2023-4863"); // ids compare without case
        Assert.Equal("CVE-2023-4863", entry!.CveID);
        Assert.Equal("2026-10-22", entry.DueDate);
        Assert.Equal("Apply mitigations per vendor instructions.", entry.RequiredAction);
        Assert.Equal("Unknown", entry.KnownRansomwareCampaignUse);
        Assert.Null(kev.Lookup("CVE-2020-9999"));
        Assert.Equal(2, kev.CatalogSize);
        Assert.Equal(1, cisa.Requests);
        // The snapshot is kept for the next cold start.
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Refresh_ReplacesTheCatalog()
    {
        var cisa = new CisaStub { Answer = Catalog("CVE-2023-4863") };
        var kev = ReferenceDataFixture.Kev(cisa);
        await kev.RefreshAsync();

        cisa.Answer = Catalog("CVE-2024-0001");
        Assert.True(await kev.RefreshAsync());

        Assert.Null(kev.Lookup("CVE-2023-4863"));
        Assert.NotNull(kev.Lookup("CVE-2024-0001"));
        Assert.Equal(1, kev.CatalogSize);
    }

    private static HttpResponseMessage NoCatalog(string kind) => kind switch
    {
        "server error" => new HttpResponseMessage(HttpStatusCode.BadGateway),
        "empty list" => NvdFixture.Json("""{"title":"CISA Catalog","count":0,"vulnerabilities":[]}"""),
        "no list" => NvdFixture.Json("""{"title":"CISA Catalog"}"""),
        "not json" => NvdFixture.Json("<html>maintenance</html>"),
        "connection lost" => throw new HttpRequestException("connection reset"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [InlineData("server error")]
    [InlineData("empty list")]
    [InlineData("no list")]
    [InlineData("not json")]
    [InlineData("connection lost")]
    public async Task Refresh_FailedOrEmptyAnswer_ReportsItAndKeepsTheLoadedCatalog(string answer)
    {
        var cisa = new CisaStub { Answer = Catalog("CVE-2023-4863") };
        var blob = new Mock<BlobClient>();
        var kev = ReferenceDataFixture.Kev(cisa, blob);
        Assert.True(await kev.RefreshAsync());
        var loadedAt = kev.LastRefresh;

        cisa.Answer = () => NoCatalog(answer);
        var refreshed = await kev.RefreshAsync();

        Assert.False(refreshed);
        Assert.NotNull(kev.Lookup("CVE-2023-4863"));
        Assert.Equal(1, kev.CatalogSize);
        Assert.Equal(loadedAt, kev.LastRefresh);
        // Nothing new to keep: only the successful refresh wrote the snapshot.
        blob.Verify(b => b.UploadAsync(It.IsAny<Stream>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureHydrated_LoadsTheSnapshotFromItsBlob_WithoutAskingCisa()
    {
        var snapshot = new BlobBackedReferenceCache<KevEntry>.Snapshot
        {
            LastRefresh = "2026-11-11T03:00:00.0000000Z",
            Entries = { ["CVE-2023-4863"] = new KevEntry { CveID = "CVE-2023-4863", DueDate = "2023-10-04" } },
        };
        var blob = new Mock<BlobClient>();
        blob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Response.FromValue(true, Mock.Of<Response>()));
        blob.Setup(b => b.DownloadContentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Response.FromValue(
            BlobsModelFactory.BlobDownloadResult(content: BinaryData.FromString(JsonSerializer.Serialize(snapshot))),
            Mock.Of<Response>()));
        var cisa = new CisaStub();
        var kev = ReferenceDataFixture.Kev(cisa, blob);

        await kev.EnsureHydratedAsync();
        await kev.EnsureHydratedAsync();

        Assert.Equal("2023-10-04", kev.Lookup("CVE-2023-4863")!.DueDate);
        Assert.Equal(new DateTime(2026, 11, 11, 3, 0, 0, DateTimeKind.Utc), kev.LastRefresh);
        Assert.Equal(0, cisa.Requests);
        blob.Verify(b => b.DownloadContentAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
