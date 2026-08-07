using AutopilotMonitor.Functions.Functions.Reports;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the device-not-registered aggregation logic extracted from
/// GetDeviceNotRegisteredFunction. All tests target the pure static BuildAggregatedResult
/// method — no HTTP or DI needed. Aggregation is by serial number (Autopilot registers
/// individual devices, not models).
/// </summary>
public class GetDeviceNotRegisteredFunctionTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    // =========================================================================
    // Empty / no matching reports
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_EmptyList_ReturnsEmpty()
    {
        var (aggregated, totalRawReports) = GetDeviceNotRegisteredFunction.BuildAggregatedResult([]);

        Assert.Empty(aggregated);
        Assert.Equal(0, totalRawReports);
    }

    [Fact]
    public void BuildAggregatedResult_FiltersDeviceNotRegisteredOnly()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "SN1", T0),
            MakeEntry("HardwareNotAllowed", "Dell", "Latitude", "SN2", T0.AddMinutes(1)),
            MakeEntry("AuthCertificateRejected", "HP", "EliteBook", "SN3", T0.AddMinutes(2)),
        };

        var (aggregated, totalRawReports) = GetDeviceNotRegisteredFunction.BuildAggregatedResult(reports);

        Assert.Single(aggregated);
        Assert.Equal(1, totalRawReports);
    }

    // =========================================================================
    // Aggregation by serial number
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_AggregatesBySerialCaseInsensitive()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "GM18NHV3", T0),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "gm18nhv3", T0.AddMinutes(1)),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", " GM18NHV3 ", T0.AddMinutes(2)),
        };

        var (aggregated, totalRawReports) = GetDeviceNotRegisteredFunction.BuildAggregatedResult(reports);

        Assert.Single(aggregated);
        Assert.Equal(3, totalRawReports);
        var item = ToDynamic(aggregated[0]);
        Assert.Equal(3, (int)item.attemptCount);
    }

    [Fact]
    public void BuildAggregatedResult_DistinctSerials_ProduceDistinctRows()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "SN1", T0),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "SN2", T0.AddMinutes(1)),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkCentre M70q", "SN3", T0.AddMinutes(2)),
        };

        var (aggregated, totalRawReports) = GetDeviceNotRegisteredFunction.BuildAggregatedResult(reports);

        Assert.Equal(3, aggregated.Count);
        Assert.Equal(3, totalRawReports);
    }

    [Fact]
    public void BuildAggregatedResult_UsesMostRecentManufacturerModel()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("DeviceNotRegistered", "OldMfr", "OldModel", "SN1", T0),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "SN1", T0.AddMinutes(5)),
        };

        var (aggregated, _) = GetDeviceNotRegisteredFunction.BuildAggregatedResult(reports);

        var item = ToDynamic(aggregated[0]);
        Assert.Equal("Lenovo", (string)item.manufacturer);
        Assert.Equal("ThinkPad T14", (string)item.model);
        Assert.Equal("SN1", (string)item.serialNumber);
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
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "SN1", t2),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "SN1", t1),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "SN1", t3),
        };

        var (aggregated, _) = GetDeviceNotRegisteredFunction.BuildAggregatedResult(reports);

        var item = ToDynamic(aggregated[0]);
        Assert.Equal(t1, (DateTime)item.firstSeen);
        Assert.Equal(t3, (DateTime)item.lastSeen);
    }

    [Fact]
    public void BuildAggregatedResult_OrderedByLastSeenDescending()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "SN1", T0),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad X13", "SN2", T0.AddHours(2)),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkCentre", "SN3", T0.AddHours(1)),
        };

        var (aggregated, _) = GetDeviceNotRegisteredFunction.BuildAggregatedResult(reports);

        Assert.Equal(3, aggregated.Count);
        var first = ToDynamic(aggregated[0]);
        var second = ToDynamic(aggregated[1]);
        var third = ToDynamic(aggregated[2]);

        Assert.True((DateTime)first.lastSeen > (DateTime)second.lastSeen);
        Assert.True((DateTime)second.lastSeen > (DateTime)third.lastSeen);
    }

    // =========================================================================
    // Null / empty serial number
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_NullOrEmptySerials_GroupedIntoSingleBucket()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad", null, T0),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad", "", T0.AddMinutes(1)),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad", "   ", T0.AddMinutes(2)),
        };

        var (aggregated, _) = GetDeviceNotRegisteredFunction.BuildAggregatedResult(reports);

        Assert.Single(aggregated);
        var item = ToDynamic(aggregated[0]);
        Assert.Equal(3, (int)item.attemptCount);
    }

    // =========================================================================
    // Cloud PC marker aggregation
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_IsCloudPc_StickyTrueAcrossGroup()
    {
        // One old agent (no field → false) + one new agent reporting the W365 marker:
        // the aggregate must say Cloud PC even though the MOST RECENT report is false —
        // same OR semantics as the Sessions-table IsCloudPc merge.
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("DeviceNotRegistered", "Microsoft Corporation", "Virtual Machine", "SN1", T0, isCloudPc: true),
            MakeEntry("DeviceNotRegistered", "Microsoft Corporation", "Virtual Machine", "SN1", T0.AddMinutes(5), isCloudPc: false),
        };

        var (aggregated, _) = GetDeviceNotRegisteredFunction.BuildAggregatedResult(reports);

        var item = ToDynamic(aggregated[0]);
        Assert.True((bool)item.isCloudPc);
    }

    [Fact]
    public void BuildAggregatedResult_IsCloudPc_FalseWhenNoReportCarriesMarker()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "SN1", T0),
        };

        var (aggregated, _) = GetDeviceNotRegisteredFunction.BuildAggregatedResult(reports);

        var item = ToDynamic(aggregated[0]);
        Assert.False((bool)item.isCloudPc);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static DistressReportEntry MakeEntry(
        string errorType, string? manufacturer, string? model, string? serial, DateTime ingestedAt,
        bool isCloudPc = false) =>
        new()
        {
            TenantId = "test-tenant",
            ErrorType = errorType,
            Manufacturer = manufacturer,
            Model = model,
            SerialNumber = serial,
            IngestedAt = ingestedAt,
            AgentTimestamp = ingestedAt,
            IsCloudPc = isCloudPc,
        };

    private static dynamic ToDynamic(object obj)
    {
        var type = obj.GetType();
        var dict = new System.Dynamic.ExpandoObject() as IDictionary<string, object?>;
        foreach (var prop in type.GetProperties())
        {
            dict[prop.Name] = prop.GetValue(obj);
        }
        return dict;
    }
}
