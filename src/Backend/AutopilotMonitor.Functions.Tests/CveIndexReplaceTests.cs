using System.Collections.Concurrent;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// A session's CveIndex rows follow its report: <see cref="TableStorageService.ReplaceCveIndexEntriesAsync"/>
/// writes the rows of the new findings and removes those only the replaced report had. A row that
/// stays keeps its detection date, so re-evaluating an old session does not move it into the
/// fleet summary's window, which <see cref="TableStorageService.ScanCveIndexAsync"/> applies in
/// the query. The index takes the five most urgent CVEs of a software entry plus every
/// known-exploited one.
/// </summary>
public class CveIndexReplaceTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Session = "22222222-2222-2222-2222-222222222222";

    private static Dictionary<string, object> Vuln(string cveId, bool kev = false) => new()
    {
        ["cveId"] = cveId,
        ["cvssScore"] = 7.5,
        ["cvssSeverity"] = "HIGH",
        ["isKev"] = kev,
    };

    private static Dictionary<string, object> Finding(string software, params Dictionary<string, object>[] vulns) => new()
    {
        ["softwareName"] = software,
        ["cpeUri"] = "cpe:2.3:a:contoso:widget:3.2.0",
        ["riskLevel"] = "high",
        ["vulnerabilities"] = vulns.ToList(),
    };

    private static List<Dictionary<string, object>> Findings(params string[] cveIds) =>
        new() { Finding("Contoso Widget", cveIds.Select(id => Vuln(id)).ToArray()) };

    private sealed record Write(string Call, string CveId, bool HasDetectedAt, ETag IfMatch, TableUpdateMode Mode);

    private sealed class Harness
    {
        public Mock<TableClient> Table { get; } = new();
        public Mock<TableServiceClient> Service { get; } = new();
        public ConcurrentQueue<Write> Writes { get; } = new();
        public ConcurrentQueue<(string PartitionKey, string RowKey)> Deletes { get; } = new();
        public List<(string? Filter, List<string> Select)> Queries { get; } = new();
        public List<TableEntity> Rows { get; } = new();

        /// <summary>Status the table answers with, per call and CVE id; none means success.</summary>
        public Func<string, string, int?> Fail { get; set; } = (_, _) => null;

        public TableStorageService Sut { get; }

        public Harness()
        {
            Table.Setup(t => t.UpdateEntityAsync(It.IsAny<TableEntity>(), It.IsAny<ETag>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((entity, ifMatch, mode, _) =>
                    Record(new Write("update", entity.GetString("CveId"), entity.ContainsKey("DetectedAt"), ifMatch, mode)));
            Table.Setup(t => t.UpsertEntityAsync(It.IsAny<TableEntity>(), It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, TableUpdateMode, CancellationToken>((entity, mode, _) =>
                    Record(new Write("upsert", entity.GetString("CveId"), entity.ContainsKey("DetectedAt"), default, mode)));
            Table.Setup(t => t.DeleteEntityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ETag>(), It.IsAny<CancellationToken>()))
                .Returns<string, string, ETag, CancellationToken>((partitionKey, rowKey, _, _) =>
                {
                    Deletes.Enqueue((partitionKey, rowKey));
                    return Answer(Fail("delete", partitionKey.Substring(Tenant.Length + 1)));
                });
            Table.Setup(t => t.QueryAsync<TableEntity>(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                .Returns((string? filter, int? _, IEnumerable<string> select, CancellationToken _) =>
                {
                    Queries.Add((filter, select.ToList()));
                    return AsyncPageable<TableEntity>.FromPages(new[] { Page<TableEntity>.FromValues(Rows, null, Mock.Of<Response>()) });
                });

            Service.Setup(s => s.GetTableClient(Constants.TableNames.CveIndex)).Returns(Table.Object);
            Sut = new TableStorageService(Service.Object, NullLogger<TableStorageService>.Instance);
        }

        private Task<Response> Record(Write write)
        {
            Writes.Enqueue(write);
            return Answer(Fail(write.Call, write.CveId));
        }

        private static Task<Response> Answer(int? failure) => failure.HasValue
            ? Task.FromException<Response>(new RequestFailedException(failure.Value, "synthetic failure"))
            : Task.FromResult(Mock.Of<Response>());

        public List<Write> WritesInOrder() => Writes.OrderBy(w => w.CveId, StringComparer.Ordinal).ThenBy(w => w.Call, StringComparer.Ordinal).ToList();
    }

    // -----------------------------------------------------------------------
    // Which CVEs of a report get a row
    // -----------------------------------------------------------------------

    [Fact]
    public void BuildCveIndexEntities_TakesTheFirstFiveOfAnEntry_AndEveryKnownExploitedOneBeyond()
    {
        var findings = new List<Dictionary<string, object>>
        {
            Finding("Contoso Widget",
                Vuln("CVE-01"), Vuln("CVE-02"), Vuln("CVE-03"), Vuln("CVE-04"), Vuln("CVE-05"),
                Vuln("CVE-06"), Vuln("CVE-07", kev: true), Vuln("CVE-08"), Vuln("CVE-09", kev: true)),
            // The count runs per entry: this one lists CVE-08 among its first five.
            Finding("Fabrikam Gadget",
                Vuln("CVE-11"), Vuln("CVE-08"), Vuln("CVE-13"), Vuln("CVE-14"), Vuln("CVE-15"), Vuln("CVE-16")),
        };

        var indexed = TableStorageService.BuildCveIndexEntities(Tenant, Session, findings).Select(e => e.GetString("CveId"));
        var all = TableStorageService.BuildCveIndexEntities(Tenant, Session, findings, mostUrgentOnly: false).Select(e => e.GetString("CveId"));

        Assert.Equal(
            new[] { "CVE-01", "CVE-02", "CVE-03", "CVE-04", "CVE-05", "CVE-07", "CVE-08", "CVE-09", "CVE-11", "CVE-13", "CVE-14", "CVE-15" },
            indexed.OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(14, all.Count());
    }

    // -----------------------------------------------------------------------
    // ReplaceCveIndexEntriesAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Replace_KeptRowIsMergedWithoutItsDate_NewRowIsWrittenInFull_DroppedRowIsDeleted()
    {
        var harness = new Harness();

        await harness.Sut.ReplaceCveIndexEntriesAsync(
            Tenant, Session, Findings("CVE-KEPT", "CVE-NEW"), previousFindings: Findings("CVE-KEPT", "CVE-GONE"));

        Assert.Equal(new[]
        {
            // A merge that does not name DetectedAt leaves the stored date alone.
            new Write("update", "CVE-KEPT", HasDetectedAt: false, ETag.All, TableUpdateMode.Merge),
            new Write("upsert", "CVE-NEW", HasDetectedAt: true, default, TableUpdateMode.Replace),
        }, harness.WritesInOrder());
        Assert.Equal(new[] { ($"{Tenant}_CVE-GONE", Session) }, harness.Deletes);
    }

    [Fact]
    public async Task Replace_KeptRowThatNeverLanded_IsWrittenInFullWithADate()
    {
        var harness = new Harness { Fail = (call, _) => call == "update" ? 404 : null };

        await harness.Sut.ReplaceCveIndexEntriesAsync(Tenant, Session, Findings("CVE-KEPT"), previousFindings: Findings("CVE-KEPT"));

        Assert.Equal(new[]
        {
            new Write("update", "CVE-KEPT", HasDetectedAt: false, ETag.All, TableUpdateMode.Merge),
            new Write("upsert", "CVE-KEPT", HasDetectedAt: true, default, TableUpdateMode.Replace),
        }, harness.WritesInOrder());
        Assert.Empty(harness.Deletes);
    }

    [Fact]
    public async Task Replace_NoPreviousReport_WritesEveryRowInFull()
    {
        var harness = new Harness();

        await harness.Sut.ReplaceCveIndexEntriesAsync(Tenant, Session, Findings("CVE-A", "CVE-B"), previousFindings: null);

        Assert.Equal(new[]
        {
            new Write("upsert", "CVE-A", HasDetectedAt: true, default, TableUpdateMode.Replace),
            new Write("upsert", "CVE-B", HasDetectedAt: true, default, TableUpdateMode.Replace),
        }, harness.WritesInOrder());
        Assert.Empty(harness.Deletes);
    }

    [Fact]
    public async Task Replace_ReportRemoved_DeletesEveryRowItHad()
    {
        var harness = new Harness();

        await harness.Sut.ReplaceCveIndexEntriesAsync(Tenant, Session, findings: null, previousFindings: Findings("CVE-A", "CVE-B"));

        Assert.Empty(harness.Writes);
        Assert.Equal(
            new[] { ($"{Tenant}_CVE-A", Session), ($"{Tenant}_CVE-B", Session) },
            harness.Deletes.OrderBy(d => d.PartitionKey, StringComparer.Ordinal));
    }

    /// <summary>
    /// A report stored before the index was limited has a row for every CVE it lists. All of its
    /// CVEs count as existing rows, so the ones the new selection leaves out are removed.
    /// </summary>
    [Fact]
    public async Task Replace_PreviousReportIsReadInFull_SoRowsBeyondTheSelectionAreRemoved()
    {
        var harness = new Harness();
        var sevenCves = new[] { "CVE-1", "CVE-2", "CVE-3", "CVE-4", "CVE-5", "CVE-6", "CVE-7" };

        await harness.Sut.ReplaceCveIndexEntriesAsync(Tenant, Session, Findings(sevenCves), previousFindings: Findings(sevenCves));

        Assert.Equal(new[] { "CVE-1", "CVE-2", "CVE-3", "CVE-4", "CVE-5" }, harness.WritesInOrder().Select(w => w.CveId));
        Assert.All(harness.Writes, w => Assert.Equal("update", w.Call));
        Assert.Equal(
            new[] { $"{Tenant}_CVE-6", $"{Tenant}_CVE-7" },
            harness.Deletes.Select(d => d.PartitionKey).OrderBy(pk => pk, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Replace_PreviousFindingsAsReadBackFromStorage_StillNameTheRows()
    {
        var harness = new Harness();
        var stored = ReportFixture.AsStored(new Dictionary<string, object> { ["findings"] = Findings("CVE-KEPT", "CVE-GONE") });

        await harness.Sut.ReplaceCveIndexEntriesAsync(
            Tenant, Session, Findings("CVE-KEPT"), VulnerabilityCorrelationService.FindingsOf(stored));

        Assert.Equal("update", Assert.Single(harness.Writes).Call);
        Assert.Equal(new[] { ($"{Tenant}_CVE-GONE", Session) }, harness.Deletes);
    }

    [Fact]
    public async Task Replace_NothingToWriteOrRemove_DoesNotTouchTheTable()
    {
        var harness = new Harness();

        await harness.Sut.ReplaceCveIndexEntriesAsync(Tenant, Session, null, null);
        await harness.Sut.ReplaceCveIndexEntriesAsync(
            Tenant, Session, new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>());

        harness.Service.Verify(s => s.GetTableClient(It.IsAny<string>()), Times.Never);
        harness.Table.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("delete", 404)] // already gone
    [InlineData("delete", 503)]
    [InlineData("upsert", 503)]
    [InlineData("update", 503)]
    public async Task Replace_FailingWrites_NeverThrow_AndTheOtherRowsAreStillWritten(string failingCall, int status)
    {
        var harness = new Harness { Fail = (call, _) => call == failingCall ? status : null };

        await harness.Sut.ReplaceCveIndexEntriesAsync(
            Tenant, Session, Findings("CVE-KEPT", "CVE-NEW"), previousFindings: Findings("CVE-KEPT", "CVE-GONE"));

        Assert.Contains(harness.Writes, w => w.Call == "update" && w.CveId == "CVE-KEPT");
        Assert.Contains(harness.Writes, w => w.Call == "upsert" && w.CveId == "CVE-NEW");
        Assert.Equal(new[] { ($"{Tenant}_CVE-GONE", Session) }, harness.Deletes);
        // Only a 404 means "the row is not there"; any other failed update is not retried as an insert.
        Assert.DoesNotContain(harness.Writes, w => w.Call == "upsert" && w.CveId == "CVE-KEPT");
    }

    [Fact]
    public async Task Replace_TableUnreachable_DoesNotThrow()
    {
        var service = new Mock<TableServiceClient>();
        service.Setup(s => s.GetTableClient(It.IsAny<string>())).Throws(new RequestFailedException(503, "busy"));
        var sut = new TableStorageService(service.Object, NullLogger<TableStorageService>.Instance);

        await sut.ReplaceCveIndexEntriesAsync(Tenant, Session, Findings("CVE-A"), previousFindings: null);
    }

    // -----------------------------------------------------------------------
    // ScanCveIndexAsync
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Scan_WithAWindow_FiltersByDetectionDateInTheQuery()
    {
        var harness = new Harness();
        var detectedAt = new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);
        harness.Rows.Add(new TableEntity($"{Tenant}_CVE-2026-0001", Session)
        {
            ["TenantId"] = Tenant, ["SessionId"] = Session, ["CveId"] = "CVE-2026-0001", ["DetectedAt"] = detectedAt,
        });
        var since = new DateTime(2026, 11, 5, 8, 30, 0, DateTimeKind.Utc);

        var (rows, truncated) = await harness.Sut.ScanCveIndexAsync(Tenant, maxRows: 100, detectedSinceUtc: since);
        await harness.Sut.ScanCveIndexAsync(null, maxRows: 100, detectedSinceUtc: since);
        await harness.Sut.ScanCveIndexAsync(Tenant, maxRows: 100);

        Assert.Equal(new[]
        {
            $"(PartitionKey ge '{Tenant}_' and PartitionKey lt '{Tenant}_~') and DetectedAt ge datetime'2026-11-05T08:30:00Z'",
            "DetectedAt ge datetime'2026-11-05T08:30:00Z'",
            $"PartitionKey ge '{Tenant}_' and PartitionKey lt '{Tenant}_~'",
        }, harness.Queries.Select(q => q.Filter));
        Assert.All(harness.Queries, q => Assert.Contains("DetectedAt", q.Select));

        Assert.False(truncated);
        Assert.Equal(detectedAt.UtcDateTime, Assert.Single(rows).DetectedAt);
    }
}
