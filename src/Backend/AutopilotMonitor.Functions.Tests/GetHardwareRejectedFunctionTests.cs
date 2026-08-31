using AutopilotMonitor.Functions.Functions.Reports;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the hardware rejection aggregation logic extracted from GetHardwareRejectedFunction.
/// All tests target the pure static BuildAggregatedResult method — no HTTP or DI needed.
/// </summary>
public class GetHardwareRejectedFunctionTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    // =========================================================================
    // Empty / no matching reports
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_EmptyList_ReturnsEmpty()
    {
        var (aggregated, totalRawReports) = GetHardwareRejectedFunction.BuildAggregatedResult([]);

        Assert.Empty(aggregated);
        Assert.Equal(0, totalRawReports);
    }

    [Fact]
    public void BuildAggregatedResult_FiltersHardwareNotAllowedOnly()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN1", T0),
            MakeEntry("CertificateExpired", "Dell", "Latitude", "SN2", T0.AddMinutes(1)),
            MakeEntry("Timeout", "HP", "EliteBook", "SN3", T0.AddMinutes(2)),
        };

        var (aggregated, totalRawReports) = GetHardwareRejectedFunction.BuildAggregatedResult(reports);

        Assert.Single(aggregated);
        Assert.Equal(1, totalRawReports);
    }

    // =========================================================================
    // Aggregation logic
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_AggregatesCaseInsensitive()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude 5520", "SN1", T0),
            MakeEntry("HardwareNotAllowed", "DELL", "LATITUDE 5520", "SN2", T0.AddMinutes(1)),
            MakeEntry("HardwareNotAllowed", "dell", "latitude 5520", "SN3", T0.AddMinutes(2)),
        };

        var (aggregated, _) = GetHardwareRejectedFunction.BuildAggregatedResult(reports);

        Assert.Single(aggregated);
        var item = aggregated[0];
        Assert.Equal(3, (int)item.AttemptCount);
    }

    [Fact]
    public void BuildAggregatedResult_CountsAttemptsCorrectly()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN1", T0),
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN2", T0.AddMinutes(1)),
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN3", T0.AddMinutes(2)),
            MakeEntry("HardwareNotAllowed", "HP", "EliteBook", "SN4", T0.AddMinutes(3)),
        };

        var (aggregated, totalRawReports) = GetHardwareRejectedFunction.BuildAggregatedResult(reports);

        Assert.Equal(4, totalRawReports);
        Assert.Equal(2, aggregated.Count);

        var dell = aggregated.First(a => a.Manufacturer.Equals("Dell", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(3, (int)dell.AttemptCount);
    }

    [Fact]
    public void BuildAggregatedResult_CountsUniqueSerials()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN1", T0),
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN1", T0.AddMinutes(1)), // duplicate
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "sn1", T0.AddMinutes(2)), // duplicate (case)
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN2", T0.AddMinutes(3)),
        };

        var (aggregated, _) = GetHardwareRejectedFunction.BuildAggregatedResult(reports);

        var item = aggregated[0];
        Assert.Equal(2, (int)item.UniqueSerials);
        Assert.Equal(4, (int)item.AttemptCount);
    }

    [Fact]
    public void BuildAggregatedResult_LimitsSampleSerialsToFive()
    {
        var reports = Enumerable.Range(1, 8)
            .Select(i => MakeEntry("HardwareNotAllowed", "Dell", "Latitude", $"SN{i}", T0.AddMinutes(i)))
            .ToList();

        var (aggregated, _) = GetHardwareRejectedFunction.BuildAggregatedResult(reports);

        var item = aggregated[0];
        var samples = (IEnumerable<object>)item.SampleSerialNumbers;
        Assert.Equal(5, samples.Count());
        Assert.Equal(8, (int)item.UniqueSerials);
    }

    // =========================================================================
    // Timestamps
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_FirstSeenLastSeen_Correct()
    {
        var t1 = T0;
        var t2 = T0.AddHours(1);
        var t3 = T0.AddHours(2);

        var reports = new List<DistressReportEntry>
        {
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN1", t1),
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN2", t2),
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN3", t3),
        };

        var (aggregated, _) = GetHardwareRejectedFunction.BuildAggregatedResult(reports);

        var item = aggregated[0];
        Assert.Equal(t1, (DateTime)item.FirstSeen);
        Assert.Equal(t3, (DateTime)item.LastSeen);
    }

    [Fact]
    public void BuildAggregatedResult_OrderedByLastSeenDescending()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN1", T0),
            MakeEntry("HardwareNotAllowed", "HP", "EliteBook", "SN2", T0.AddHours(2)),
            MakeEntry("HardwareNotAllowed", "Lenovo", "ThinkPad", "SN3", T0.AddHours(1)),
        };

        var (aggregated, _) = GetHardwareRejectedFunction.BuildAggregatedResult(reports);

        Assert.Equal(3, aggregated.Count);
        var first = aggregated[0];
        var second = aggregated[1];
        var third = aggregated[2];

        Assert.True((DateTime)first.LastSeen > (DateTime)second.LastSeen);
        Assert.True((DateTime)second.LastSeen > (DateTime)third.LastSeen);
    }

    // =========================================================================
    // Null manufacturer/model
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_NullManufacturerModel_GroupedAsEmpty()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("HardwareNotAllowed", null, null, "SN1", T0),
            MakeEntry("HardwareNotAllowed", null, null, "SN2", T0.AddMinutes(1)),
        };

        var (aggregated, _) = GetHardwareRejectedFunction.BuildAggregatedResult(reports);

        Assert.Single(aggregated);
        var item = aggregated[0];
        Assert.Equal("", (string)item.Manufacturer);
        Assert.Equal("", (string)item.Model);
        Assert.Equal(2, (int)item.AttemptCount);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static DistressReportEntry MakeEntry(
        string errorType, string? manufacturer, string? model, string? serial, DateTime ingestedAt) =>
        new()
        {
            TenantId = "test-tenant",
            ErrorType = errorType,
            Manufacturer = manufacturer,
            Model = model,
            SerialNumber = serial,
            IngestedAt = ingestedAt,
            AgentTimestamp = ingestedAt,
        };

}
