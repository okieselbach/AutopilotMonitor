using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The SessionsIndex filters behind the TPM panel's "connected later" status. Table Storage
/// rejects a $filter with more than 15 comparisons, so the serials are split across queries.
/// </summary>
public class CertificateIngestLookupFilterTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private static readonly DateTime Since = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    private static int Comparisons(string filter) =>
        Regex.Matches(filter, @" (eq|ne|lt|le|gt|ge) ").Count;

    [Fact]
    public void EveryFilter_StaysWithinTheTableStorageComparisonLimit()
    {
        // Mixed case doubles each serial's comparisons (as stored + upper-case).
        var serials = Enumerable.Range(1, 40).Select(i => $"sn{i:D3}").ToList();

        var filters = TableStorageService.BuildCertificateIngestFilters(TenantId, serials, Since);

        Assert.Equal(7, filters.Count); // 40 serials, 6 per query
        Assert.All(filters, f => Assert.True(Comparisons(f) <= 15, $"{Comparisons(f)} comparisons: {f}"));
    }

    [Fact]
    public void Filter_BoundsPartitionAndStartWindow()
    {
        var filter = Assert.Single(TableStorageService.BuildCertificateIngestFilters(TenantId, ["SN1"], Since));

        Assert.StartsWith($"PartitionKey eq '{TenantId}' and RowKey lt '{RowKeyCodec.InvertedTicks(Since)}' and (", filter);
        Assert.Contains("SerialNumber eq 'SN1'", filter);
    }

    [Fact]
    public void Serials_TrimmedDeduplicatedAndBlankDropped()
    {
        var filters = TableStorageService.BuildCertificateIngestFilters(
            TenantId, [" SN1 ", "sn1", "SN1", "", "   ", "SN2"], Since);

        var filter = Assert.Single(filters);
        Assert.Single(Regex.Matches(filter, "SerialNumber eq 'SN1'"));
        Assert.Contains("SerialNumber eq 'SN2'", filter);
        Assert.DoesNotContain("SerialNumber eq ''", filter);
    }

    [Fact]
    public void NoUsableSerial_NoQuery()
    {
        Assert.Empty(TableStorageService.BuildCertificateIngestFilters(TenantId, ["", " "], Since));
    }

    [Fact]
    public void Quote_InSerial_IsEscaped()
    {
        var filter = Assert.Single(TableStorageService.BuildCertificateIngestFilters(TenantId, ["AB'C"], Since));

        Assert.Contains("SerialNumber eq 'AB''C'", filter);
    }
}
