using AutopilotMonitor.Functions.Services;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins that the caller-influenced tenantId interpolated into the metrics-summary (SessionsIndex)
/// and device-snapshot search (DeviceSnapshot) OData filters is escaped, so a quote in the value
/// cannot rewrite the filter grammar (e.g. <c>x' or PartitionKey ne '</c> dissolving the
/// single-tenant scope and the RowKey date-window bound).
/// </summary>
public class TenantFilterEscapingTests
{
    private const string Injection = "x' or PartitionKey ne '";

    [Fact]
    public void MetricsSummaryFilter_EscapesQuotesInTenantId()
    {
        var filter = TableStorageService.BuildMetricsSummaryFilter(Injection, "cutoff");

        Assert.Equal("RowKey lt 'cutoff' and PartitionKey eq 'x'' or PartitionKey ne '''", filter);
        Assert.DoesNotContain(" or ", filter.Replace("'x'' or PartitionKey ne '''", ""));
    }

    [Fact]
    public void MetricsSummaryFilter_NoTenant_KeepsWindowBoundOnly()
    {
        Assert.Equal("RowKey lt 'cutoff'", TableStorageService.BuildMetricsSummaryFilter(null, "cutoff"));
        Assert.Equal("RowKey lt 'cutoff'", TableStorageService.BuildMetricsSummaryFilter("", "cutoff"));
    }

    [Fact]
    public void DeviceSnapshotTenantFilter_EscapesQuotesInTenantId()
    {
        Assert.Equal("PartitionKey eq 'x'' or PartitionKey ne '''",
            TableStorageService.BuildDeviceSnapshotTenantFilter(Injection));
        Assert.Null(TableStorageService.BuildDeviceSnapshotTenantFilter(null));
    }
}
