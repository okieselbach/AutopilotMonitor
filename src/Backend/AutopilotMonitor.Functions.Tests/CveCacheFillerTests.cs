using System.Globalization;
using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <see cref="CveCacheFiller"/> is the one path from an NVD answer to a product's cache row.
/// Pinned here: a row holds only the product's own version ranges, an answer NVD did not give is
/// never written, a marker for missing data never overwrites a row, and a rewrite hands back the
/// columns the replace-mode write would otherwise lose.
/// </summary>
[Collection(NvdTestState.Collection)]
public sealed class CveCacheFillerTests : IDisposable
{
    private const string WidgetCpe = "cpe:2.3:a:contoso:widget";
    private static readonly string WidgetKey = CveCacheFiller.CacheKey(WidgetCpe);

    public CveCacheFillerTests() => NvdTestState.Reset();

    public void Dispose() => NvdTestState.Reset();

    private static NvdCpeMatch Match(string criteria, string? endExcluding = null, bool vulnerable = true) => new()
    {
        Vulnerable = vulnerable,
        Criteria = criteria,
        VersionEndExcluding = endExcluding,
    };

    private static NvdCveResponse Response(params NvdVulnerabilityItem[] items) =>
        new() { TotalResults = items.Length, Vulnerabilities = items.ToList() };

    private static CveCacheFiller Filler(
        Mock<IVulnerabilityRepository> repo, Func<NvdRequest, HttpResponseMessage> nvdAnswer, EpssApiClient? epss = null) =>
        new(repo.Object, NvdFixture.Client(new NvdStubHandler(nvdAnswer)), NullLogger<CveCacheFiller>.Instance, epss);

    // -----------------------------------------------------------------------
    // Project
    // -----------------------------------------------------------------------

    [Fact]
    public void Project_KeepsOnlyTheRangesOfTheProductItself()
    {
        // A prefix compare on the string would take acrobat_reader_dc and acrobat_dc for acrobat.
        var response = Response(
            NvdFixture.Cve("CVE-2025-0001", 7.8,
                Match("cpe:2.3:a:adobe:acrobat:*:*:*:*:*:*:*:*", "24.0"),
                Match("cpe:2.3:a:adobe:acrobat_reader_dc:*:*:*:*:continuous:*:*:*", "25.0"),
                Match("cpe:2.3:a:adobe:acrobat_dc:*:*:*:*:continuous:*:*:*", "25.0"),
                Match("cpe:2.3:o:microsoft:windows:-:*:*:*:*:*:*:*")),
            NvdFixture.Cve("CVE-2025-0002", 9.8,
                Match("cpe:2.3:a:adobe:acrobat_reader_dc:*:*:*:*:continuous:*:*:*", "25.0")));

        var cves = CveCacheFiller.Project(response, "cpe:2.3:a:adobe:acrobat");

        var kept = Assert.Single(cves);
        Assert.Equal("CVE-2025-0001", kept.CveId);
        var range = Assert.Single(kept.AffectedVersions);
        Assert.Equal("cpe:2.3:a:adobe:acrobat:*:*:*:*:*:*:*:*", range.Criteria);
        Assert.Equal("24.0", range.VersionEndExcluding);
    }

    [Fact]
    public void Project_DropsOwnRangesThatAreNotVulnerable_AndACveLeftWithoutOne()
    {
        var response = Response(
            NvdFixture.Cve("CVE-2025-0003", 7.5,
                Match("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*", "3.0"),
                Match("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*", "9.0", vulnerable: false)),
            NvdFixture.Cve("CVE-2025-0004", 7.5,
                Match("cpe:2.3:a:contoso:widget:*:*:*:*:*:*:*:*", "9.0", vulnerable: false)));

        var kept = Assert.Single(CveCacheFiller.Project(response, WidgetCpe));

        Assert.Equal("CVE-2025-0003", kept.CveId);
        Assert.Equal("3.0", Assert.Single(kept.AffectedVersions).VersionEndExcluding);
    }

    /// <summary>
    /// Shaped like the public CVE-2024-3661: one CVE names two products and gives a version range
    /// for the other one only. The first product's row must not inherit that range.
    /// </summary>
    [Fact]
    public void Project_CveNamingTwoProducts_KeepsOnlyTheOwnRange()
    {
        const string otherCpe = "cpe:2.3:a:fabrikam:secure_client";
        var response = Response(NvdFixture.Cve("CVE-2024-3661", 7.6,
            Match("cpe:2.3:a:contoso:widget:-:*:*:*:*:*:*:*"),
            Match("cpe:2.3:a:fabrikam:secure_client:*:*:*:*:*:*:*:*", "24.06.1")));

        var own = Assert.Single(CveCacheFiller.Project(response, WidgetCpe));
        var other = Assert.Single(CveCacheFiller.Project(response, otherCpe));

        var ownRange = Assert.Single(own.AffectedVersions);
        Assert.Equal("cpe:2.3:a:contoso:widget:-:*:*:*:*:*:*:*", ownRange.Criteria);
        Assert.Null(ownRange.VersionEndExcluding);
        Assert.Equal("24.06.1", Assert.Single(other.AffectedVersions).VersionEndExcluding);

        // Read back through the filter, the widget is not judged by the other product's range.
        Assert.Empty(CveVersionFilter.Filter(new[] { own }, WidgetCpe, "3.2.0").Vulnerabilities);
        Assert.Single(CveVersionFilter.Filter(new[] { other }, otherCpe, "3.2.0").Vulnerabilities);
    }

    [Theory]
    [InlineData("2023-09-13", "2023-09-13")]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    public void Project_TakesTheExploitedDateFromNvd(string? cisaExploitAdd, string? expected)
    {
        var item = NvdFixture.Cve("CVE-2025-0005", 8.8, NvdFixture.Below(WidgetCpe, "2.0"));
        item.Cve!.CisaExploitAdd = cisaExploitAdd;

        Assert.Equal(expected, Assert.Single(CveCacheFiller.Project(Response(item), WidgetCpe)).KevAdded);
    }

    [Fact]
    public void Project_FallsBackToCvss30_AndReportsUnknownWithoutMetrics()
    {
        var onlyV30 = NvdFixture.Cve("CVE-2025-0006", 0, NvdFixture.Below(WidgetCpe, "2.0"));
        onlyV30.Cve!.Metrics = new NvdMetrics
        {
            CvssMetricV30 = { new NvdCvssMetricV31 { CvssData = new NvdCvssData { BaseScore = 6.5, BaseSeverity = "MEDIUM", VectorString = "CVSS:3.0/AV:N" } } },
        };
        var noMetrics = NvdFixture.Cve("CVE-2025-0007", 0, NvdFixture.Below(WidgetCpe, "2.0"));
        noMetrics.Cve!.Metrics = null;

        var cves = CveCacheFiller.Project(Response(onlyV30, noMetrics), WidgetCpe);

        Assert.Equal(6.5, cves[0].CvssScore);
        Assert.Equal("MEDIUM", cves[0].CvssSeverity);
        Assert.Equal("CVSS:3.0/AV:N", cves[0].CvssVector);
        Assert.Equal(0, cves[1].CvssScore);
        Assert.Equal("UNKNOWN", cves[1].CvssSeverity);
        Assert.Null(cves[1].CvssVector);
    }

    [Fact]
    public void Project_CutsTheDescriptionAt500Chars()
    {
        var tooLong = NvdFixture.Cve("CVE-2025-0008", 5.0, NvdFixture.Below(WidgetCpe, "2.0"));
        tooLong.Cve!.Descriptions = new List<NvdDescription>
        {
            new() { Lang = "es", Value = "Otra descripcion." },
            new() { Lang = "en", Value = new string('a', 501) },
        };
        var fits = NvdFixture.Cve("CVE-2025-0009", 5.0, NvdFixture.Below(WidgetCpe, "2.0"));
        fits.Cve!.Descriptions = new List<NvdDescription> { new() { Lang = "en", Value = new string('b', 500) } };

        var cves = CveCacheFiller.Project(Response(tooLong, fits), WidgetCpe);

        Assert.Equal(new string('a', 497) + "...", cves[0].Description);
        Assert.Equal(new string('b', 500), cves[1].Description);
    }

    // -----------------------------------------------------------------------
    // FillAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Fill_WritesOneRowInTheCurrentShape_AndReturnsItsEntry()
    {
        var repo = new Mock<IVulnerabilityRepository>();
        Dictionary<string, object>? written = null;
        repo.Setup(r => r.UpsertCveCacheEntryAsync(WidgetKey, CveCacheFiller.RowKey, It.IsAny<Dictionary<string, object>>()))
            .Callback<string, string, Dictionary<string, object>>((_, _, row) => written = row)
            .Returns(Task.CompletedTask);
        var listed = NvdFixture.Cve("CVE-2025-0010", 8.8, NvdFixture.Below(WidgetCpe, "2.0"));
        listed.Cve!.CisaExploitAdd = "2025-03-01";
        // NVD reports more than it sends here; the row keeps the figure NVD gave.
        var filler = Filler(repo, _ => NvdFixture.Page(4711, new[]
        {
            listed,
            NvdFixture.Cve("CVE-2025-0011", 5.3, NvdFixture.Below(WidgetCpe, "1.5")),
            NvdFixture.Cve("CVE-2025-0012", 9.8, NvdFixture.Below("cpe:2.3:a:fabrikam:gadget", "7.0")),
        }));

        var started = DateTime.UtcNow;
        var result = await filler.FillAsync(WidgetCpe, CancellationToken.None);

        Assert.Equal(CveCacheFillOutcome.Filled, result.Outcome);
        Assert.Equal(4711, result.NvdTotal);
        Assert.Equal(2, result.InWindow);
        Assert.Equal(2, result.Kept);
        Assert.Equal(new[] { "CVE-2025-0010", "CVE-2025-0011" }, result.Entry!.Cves.Select(c => c.CveId));
        Assert.InRange(result.Entry.CachedAt, started, DateTime.UtcNow);

        repo.Verify(r => r.UpsertCveCacheEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>>()), Times.Once);
        Assert.NotNull(written);
        Assert.Equal(CveCacheFiller.SchemaVersion, written!["SchemaVersion"]);
        Assert.Equal(2, written["SchemaVersion"]);
        Assert.Equal("filled", written["State"]);
        Assert.Equal(WidgetCpe, written["CpeUri"]);
        Assert.Equal(2, written["TotalCves"]);
        Assert.Equal(4711, written["NvdTotal"]);
        Assert.Equal(result.Entry.CachedAt.ToString("o"), written["CachedAt"]);

        var stored = JsonSerializer.Deserialize<List<CachedCve>>((string)written["CveDataJson"], CveCacheFiller.JsonOptions)!;
        Assert.Equal(new[] { "CVE-2025-0010", "CVE-2025-0011" }, stored.Select(c => c.CveId));
        Assert.Equal("2025-03-01", stored[0].KevAdded);
        Assert.Equal("2.0", Assert.Single(stored[0].AffectedVersions).VersionEndExcluding);
    }

    [Fact]
    public async Task Fill_SplitsALargePayloadOverSeveralColumns_AndTheReaderPutsItBackTogether()
    {
        var repo = new Mock<IVulnerabilityRepository>();
        Dictionary<string, object>? written = null;
        repo.Setup(r => r.UpsertCveCacheEntryAsync(WidgetKey, CveCacheFiller.RowKey, It.IsAny<Dictionary<string, object>>()))
            .Callback<string, string, Dictionary<string, object>>((_, _, row) => written = row)
            .Returns(Task.CompletedTask);
        var items = Enumerable.Range(1, 80).Select(n =>
        {
            var item = NvdFixture.Cve($"CVE-2025-1{n:000}", 7.5, NvdFixture.Below(WidgetCpe, "2.0"));
            item.Cve!.Descriptions = new List<NvdDescription> { new() { Lang = "en", Value = new string('d', 500) } };
            return item;
        }).ToArray();
        var filler = Filler(repo, _ => NvdFixture.Page(items));

        var result = await filler.FillAsync(WidgetCpe, CancellationToken.None);

        Assert.NotNull(written);
        Assert.False(written!.ContainsKey("CveDataJson"));
        Assert.True(int.Parse((string)written["CveDataJson_ChunkCount"], CultureInfo.InvariantCulture) >= 2);
        Assert.Equal(80, written["TotalCves"]);

        var readBack = filler.EntryFromRow(written, WidgetKey);
        Assert.Equal(result.Entry!.Cves.Select(c => c.CveId), readBack!.Cves.Select(c => c.CveId));
        Assert.Equal(80, readBack.Cves.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task Fill_AnswerNvdDidNotGive_WritesNothing(HttpStatusCode status, bool throttled)
    {
        var repo = new Mock<IVulnerabilityRepository>(MockBehavior.Strict);
        var filler = Filler(repo, _ => NvdFixture.Status(status));

        var result = await filler.FillAsync(WidgetCpe, CancellationToken.None);

        Assert.Equal(throttled ? CveCacheFillOutcome.Throttled : CveCacheFillOutcome.Failed, result.Outcome);
        Assert.Null(result.Entry);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Fill_RowCannotBeWritten_StillReturnsTheData()
    {
        var repo = new Mock<IVulnerabilityRepository>();
        repo.Setup(r => r.UpsertCveCacheEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>>()))
            .ThrowsAsync(new Azure.RequestFailedException(503, "busy"));
        var filler = Filler(repo, _ => NvdFixture.Page(NvdFixture.Cve("CVE-2025-0013", 8.8, NvdFixture.Below(WidgetCpe, "2.0"))));

        var result = await filler.FillAsync(WidgetCpe, CancellationToken.None);

        Assert.Equal(CveCacheFillOutcome.Filled, result.Outcome);
        Assert.Equal("CVE-2025-0013", Assert.Single(result.Entry!.Cves).CveId);
    }

    [Fact]
    public async Task Fill_AddsEpssScoresInOneBatch_AndCachesWithoutThemWhenFirstFails()
    {
        var repo = new Mock<IVulnerabilityRepository>();
        var epssRequests = 0;
        var epssUp = true;
        var epss = new EpssApiClient(new HttpClient(new LambdaHandler(_ =>
        {
            epssRequests++;
            return epssUp
                ? NvdFixture.Json("""{"status":"OK","data":[{"cve":"CVE-2025-0014","epss":"0.42","percentile":"0.97","date":"2026-11-11"}]}""")
                : NvdFixture.Status(HttpStatusCode.BadGateway);
        })), NullLogger<EpssApiClient>.Instance);
        var filler = Filler(repo, _ => NvdFixture.Page(
            NvdFixture.Cve("CVE-2025-0014", 8.8, NvdFixture.Below(WidgetCpe, "2.0")),
            NvdFixture.Cve("CVE-2025-0015", 5.0, NvdFixture.Below(WidgetCpe, "2.0"))), epss);

        var scored = await filler.FillAsync(WidgetCpe, CancellationToken.None);
        epssUp = false;
        var unscored = await filler.FillAsync(WidgetCpe, CancellationToken.None);

        Assert.Equal(2, epssRequests);
        Assert.Equal(0.42, scored.Entry!.Cves.Single(c => c.CveId == "CVE-2025-0014").EpssScore);
        Assert.Null(scored.Entry.Cves.Single(c => c.CveId == "CVE-2025-0015").EpssScore);
        Assert.Equal(CveCacheFillOutcome.Filled, unscored.Outcome);
        Assert.All(unscored.Entry!.Cves, c => Assert.Null(c.EpssScore));
    }

    // -----------------------------------------------------------------------
    // MarkPendingAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task MarkPending_OnlyEverAddsTheRow()
    {
        var repo = new Mock<IVulnerabilityRepository>(MockBehavior.Strict);
        Dictionary<string, object>? marker = null;
        repo.Setup(r => r.TryAddCveCacheEntryAsync(WidgetKey, CveCacheFiller.RowKey, It.IsAny<Dictionary<string, object>>()))
            .Callback<string, string, Dictionary<string, object>>((_, _, row) => marker = row)
            .ReturnsAsync(false); // a row is already there: it stays as it is
        var filler = Filler(repo, _ => NvdFixture.Page());

        var before = DateTime.UtcNow;
        await filler.MarkPendingAsync(WidgetCpe);

        Assert.NotNull(marker);
        Assert.Equal("pending", marker!["State"]);
        Assert.Equal(WidgetCpe, marker["CpeUri"]);
        Assert.Equal(CveCacheFiller.SchemaVersion, marker["SchemaVersion"]);
        var requestedAt = DateTime.Parse((string)marker["RequestedAt"], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.InRange(requestedAt, before, DateTime.UtcNow);
        Assert.False(marker.ContainsKey("CachedAt"));
        // Nothing else was called: an upsert would replace a filled row with the marker.
        repo.Verify(r => r.TryAddCveCacheEntryAsync(WidgetKey, CveCacheFiller.RowKey, It.IsAny<Dictionary<string, object>>()), Times.Once);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MarkPending_StorageFailure_IsSwallowed()
    {
        var repo = new Mock<IVulnerabilityRepository>();
        repo.Setup(r => r.TryAddCveCacheEntryAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, object>>()))
            .ThrowsAsync(new Azure.RequestFailedException(503, "busy"));

        await Filler(repo, _ => NvdFixture.Page()).MarkPendingAsync(WidgetCpe);

        repo.Verify(r => r.TryAddCveCacheEntryAsync(WidgetKey, CveCacheFiller.RowKey, It.IsAny<Dictionary<string, object>>()), Times.Once);
    }

    // -----------------------------------------------------------------------
    // ReadAsync / EntryFromRow
    // -----------------------------------------------------------------------

    private static readonly DateTime CachedAt = new(2026, 11, 3, 4, 5, 6, DateTimeKind.Utc);

    private static async Task<CveCacheEntry?> ReadAsync(Dictionary<string, object>? row)
    {
        var repo = new Mock<IVulnerabilityRepository>();
        repo.Setup(r => r.GetCveCacheEntryAsync(WidgetKey, CveCacheFiller.RowKey)).ReturnsAsync(row);
        return await Filler(repo, _ => NvdFixture.Page()).ReadAsync(WidgetCpe);
    }

    [Fact]
    public async Task Read_FilledRow_RoundTripsItsCvesAndFreshnessStamp()
    {
        var cve = VulnerabilityCacheRows.Cve("CVE-2025-0016", 8.8, WidgetCpe, "2.0");
        cve.KevAdded = "2025-03-01";
        cve.EpssScore = 0.42;

        var entry = await ReadAsync(VulnerabilityCacheRows.Filled(WidgetCpe, new[] { cve }, CachedAt, nvdTotal: 12));

        Assert.NotNull(entry);
        Assert.Equal(CachedAt, entry!.CachedAt);
        Assert.Equal(DateTimeKind.Utc, entry.CachedAt.Kind);
        var read = Assert.Single(entry.Cves);
        Assert.Equal("CVE-2025-0016", read.CveId);
        Assert.Equal("2025-03-01", read.KevAdded);
        Assert.Equal(0.42, read.EpssScore);
        Assert.Equal("2.0", Assert.Single(read.AffectedVersions).VersionEndExcluding);
    }

    [Fact]
    public async Task Read_RowOfTheOlderShape_StillServes()
    {
        var row = VulnerabilityCacheRows.OlderShape(
            WidgetCpe, new[] { VulnerabilityCacheRows.Cve("CVE-2025-0017", 7.5, WidgetCpe, "2.0") }, CachedAt);

        var entry = await ReadAsync(row);

        Assert.Equal("CVE-2025-0017", Assert.Single(entry!.Cves).CveId);
    }

    [Fact]
    public async Task Read_PayloadSplitOverSeveralColumns_IsReassembled()
    {
        var cves = Enumerable.Range(1, 9)
            .Select(n => VulnerabilityCacheRows.Cve($"CVE-2025-2{n:000}", 7.5, WidgetCpe, "2.0"))
            .ToList();
        var json = JsonSerializer.Serialize(cves, CveCacheFiller.JsonOptions);
        var third = json.Length / 3;
        var row = new Dictionary<string, object>
        {
            ["CachedAt"] = CachedAt.ToString("o"),
            ["CpeUri"] = WidgetCpe,
            ["State"] = "filled",
            ["CveDataJson_0"] = json[..third],
            ["CveDataJson_1"] = json[third..(2 * third)],
            ["CveDataJson_2"] = json[(2 * third)..],
            ["CveDataJson_ChunkCount"] = "3",
        };

        var entry = await ReadAsync(row);

        Assert.Equal(cves.Select(c => c.CveId), entry!.Cves.Select(c => c.CveId));
    }

    [Fact]
    public async Task Read_PendingMarker_MissingRow_OrUnreadableStamp_IsNoData()
    {
        var unreadable = VulnerabilityCacheRows.Filled(WidgetCpe, Array.Empty<CachedCve>(), CachedAt);
        unreadable["CachedAt"] = "not a date";

        Assert.Null(await ReadAsync(VulnerabilityCacheRows.Pending(WidgetCpe, CachedAt)));
        Assert.Null(await ReadAsync(null));
        Assert.Null(await ReadAsync(unreadable));
    }

    [Fact]
    public async Task Read_StorageFailure_IsNoData()
    {
        var repo = new Mock<IVulnerabilityRepository>();
        repo.Setup(r => r.GetCveCacheEntryAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Azure.RequestFailedException(503, "busy"));

        Assert.Null(await Filler(repo, _ => NvdFixture.Page()).ReadAsync(WidgetCpe));
    }

    // -----------------------------------------------------------------------
    // BuildRow
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildRow_HandsBackFreshnessTotalAndShapeAsGiven()
    {
        var cves = new List<CachedCve> { VulnerabilityCacheRows.Cve("CVE-2025-0018", 7.5, WidgetCpe, "2.0") };

        // What an EPSS-only rewrite of an older row passes: its own stamp, total and shape.
        var rewritten = CveCacheFiller.BuildRow(WidgetCpe, cves, CachedAt, nvdTotal: 4711, schemaVersion: 1);
        var fresh = CveCacheFiller.BuildRow(WidgetCpe, cves, CachedAt, nvdTotal: 1);

        Assert.Equal(CachedAt.ToString("o"), rewritten["CachedAt"]);
        Assert.Equal(4711, rewritten["NvdTotal"]);
        Assert.Equal(1, rewritten["SchemaVersion"]);
        Assert.Equal("filled", rewritten["State"]);
        Assert.Equal(1, rewritten["TotalCves"]);
        Assert.Equal(CveCacheFiller.SchemaVersion, fresh["SchemaVersion"]);
    }

    /// <summary>The keys existing rows are stored under; the refresh walk compares them with the mappings.</summary>
    [Theory]
    [InlineData("cpe:2.3:a:contoso:widget", "cve_cpe:2.3:a:contoso:widget")]
    [InlineData(@"cpe:2.3:a:microsoft:visual_c\+\+", "cve_cpe:2.3:a:microsoft:visual_c_+_+")] // a backslash cannot be part of a table key
    public void CacheKey_IsThePartitionKeyOfTheProductsRow(string cpeUri, string expected)
    {
        Assert.Equal(expected, CveCacheFiller.CacheKey(cpeUri));
        Assert.Equal("cache_result", CveCacheFiller.RowKey);
    }

    private sealed class LambdaHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public LambdaHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }
}
