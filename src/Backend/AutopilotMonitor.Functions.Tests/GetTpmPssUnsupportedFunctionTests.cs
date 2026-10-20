using AutopilotMonitor.Functions.Functions.Reports;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the TPM-PSS-unsupported aggregation logic extracted from
/// GetTpmPssUnsupportedFunction. All tests target the pure static BuildAggregatedResult
/// method — no HTTP or DI needed. Aggregation is by serial number (the TPM incompatibility
/// is a per-device condition, not a per-model one — firmware levels differ within a model).
/// </summary>
public class GetTpmPssUnsupportedFunctionTests
{
    private static readonly DateTime T0 = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    // =========================================================================
    // Empty / no matching reports
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_EmptyList_ReturnsEmpty()
    {
        var (aggregated, totalRawReports) = GetTpmPssUnsupportedFunction.BuildAggregatedResult([]);

        Assert.Empty(aggregated);
        Assert.Equal(0, totalRawReports);
    }

    [Fact]
    public void BuildAggregatedResult_FiltersTpmPssUnsupportedOnly()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "SN1", T0),
            MakeEntry("DeviceNotRegistered", "Lenovo", "ThinkPad T14", "SN2", T0.AddMinutes(1)),
            MakeEntry("AuthCertificateRejected", "HP", "EliteBook", "SN3", T0.AddMinutes(2)),
        };

        var (aggregated, totalRawReports) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports);

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
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "TPMCASE01", T0),
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "tpmcase01", T0.AddMinutes(1)),
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", " TPMCASE01 ", T0.AddMinutes(2)),
        };

        var (aggregated, totalRawReports) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports);

        Assert.Single(aggregated);
        Assert.Equal(3, totalRawReports);
        var item = aggregated[0];
        Assert.Equal(3, (int)item.AttemptCount);
    }

    [Fact]
    public void BuildAggregatedResult_DistinctSerials_ProduceDistinctRows()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "SN1", T0),
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "SN2", T0.AddMinutes(1)),
            MakeEntry("TpmPssUnsupported", "Microsoft", "Surface Book", "SN3", T0.AddMinutes(2)),
        };

        var (aggregated, totalRawReports) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports);

        Assert.Equal(3, aggregated.Count);
        Assert.Equal(3, totalRawReports);
    }

    [Fact]
    public void BuildAggregatedResult_UsesMostRecentManufacturerModel()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("TpmPssUnsupported", "OldMfr", "OldModel", "SN1", T0),
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "SN1", T0.AddMinutes(5)),
        };

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports);

        var item = aggregated[0];
        Assert.Equal("Lenovo", (string)item.Manufacturer);
        Assert.Equal("ThinkCentre M720q", (string)item.Model);
        Assert.Equal("SN1", (string)item.SerialNumber);
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
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "SN1", t2),
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "SN1", t1),
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "SN1", t3),
        };

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports);

        var item = aggregated[0];
        Assert.Equal(t1, (DateTime)item.FirstSeen);
        Assert.Equal(t3, (DateTime)item.LastSeen);
    }

    [Fact]
    public void BuildAggregatedResult_OrderedByLastSeenDescending()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "SN1", T0),
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkPad X13", "SN2", T0.AddHours(2)),
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720s", "SN3", T0.AddHours(1)),
        };

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports);

        Assert.Equal(3, aggregated.Count);
        var first = aggregated[0];
        var second = aggregated[1];
        var third = aggregated[2];

        Assert.True((DateTime)first.LastSeen > (DateTime)second.LastSeen);
        Assert.True((DateTime)second.LastSeen > (DateTime)third.LastSeen);
    }

    // =========================================================================
    // Null / empty serial number
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_NullOrEmptySerials_GroupedIntoSingleBucket()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", null, T0),
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "", T0.AddMinutes(1)),
            MakeEntry("TpmPssUnsupported", "Lenovo", "ThinkCentre M720q", "   ", T0.AddMinutes(2)),
        };

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports);

        Assert.Single(aggregated);
        var item = aggregated[0];
        Assert.Equal(3, (int)item.AttemptCount);
    }

    // =========================================================================
    // Device context from the reports
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_DeviceContext_FromNewestReportCarryingIt()
    {
        var older = MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN1", T0);
        older.TpmVersion = "IFX 7.63.3353.0";
        older.OsBuild = "26100.1000";
        older.OobeState = "in_progress";
        var newer = MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN1", T0.AddHours(1));
        newer.OsBuild = "26200.2000";
        newer.OobeState = "completed";
        // Newest report from an agent that predates the fields: carries none of them.
        var newestLegacy = MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN1", T0.AddHours(2));

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult([older, newestLegacy, newer]);

        var item = Assert.Single(aggregated);
        Assert.Equal("IFX 7.63.3353.0", item.TpmVersion);
        Assert.Equal("26200.2000", item.OsBuild);
        Assert.Equal("completed", item.OobeState);
    }

    [Fact]
    public void BuildAggregatedResult_LegacyReportsOnly_LeaveDeviceContextNull()
    {
        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(
            [MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN1", T0)]);

        var item = Assert.Single(aggregated);
        Assert.Null(item.TpmVersion);
        Assert.Null(item.OsBuild);
        Assert.Null(item.OobeState);
    }

    // =========================================================================
    // ConnectedAt — a certificate-authenticated upload after the last report
    // =========================================================================

    [Fact]
    public void BuildAggregatedResult_UploadAfterLastReport_SetsConnectedAt()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN1", T0),
            MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN1", T0.AddHours(1)),
        };
        var lastIngest = new Dictionary<string, DateTime> { ["sn1"] = T0.AddHours(2) };

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports, lastIngest);

        Assert.Equal(T0.AddHours(2), Assert.Single(aggregated).ConnectedAt);
    }

    [Fact]
    public void BuildAggregatedResult_UploadBeforeLastReport_LeavesDeviceUnresolved()
    {
        // The device connected once, then failed again: the newer report wins.
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN1", T0),
            MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN1", T0.AddHours(3)),
        };
        var lastIngest = new Dictionary<string, DateTime> { ["sn1"] = T0.AddHours(2) };

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports, lastIngest);

        Assert.Null(Assert.Single(aggregated).ConnectedAt);
    }

    [Fact]
    public void BuildAggregatedResult_UploadAtLastReportTime_LeavesDeviceUnresolved()
    {
        var reports = new List<DistressReportEntry> { MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN1", T0) };
        var lastIngest = new Dictionary<string, DateTime> { ["sn1"] = T0 };

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports, lastIngest);

        Assert.Null(Assert.Single(aggregated).ConnectedAt);
    }

    [Fact]
    public void BuildAggregatedResult_SerialCaseAndWhitespace_StillMatchesLookupKey()
    {
        var reports = new List<DistressReportEntry> { MakeEntry("TpmPssUnsupported", "HP", "EliteBook", " SnMixed1 ", T0) };
        var lastIngest = new Dictionary<string, DateTime> { ["snmixed1"] = T0.AddMinutes(30) };

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports, lastIngest);

        Assert.Equal(T0.AddMinutes(30), Assert.Single(aggregated).ConnectedAt);
    }

    [Fact]
    public void BuildAggregatedResult_NoLookup_LeavesEveryDeviceUnresolved()
    {
        var reports = new List<DistressReportEntry>
        {
            MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN1", T0),
            MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN2", T0),
        };

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports, lastCertificateIngestBySerial: null);

        Assert.All(aggregated, item => Assert.Null(item.ConnectedAt));
    }

    [Fact]
    public void BuildAggregatedResult_OtherSerialConnected_DoesNotResolveThisOne()
    {
        var reports = new List<DistressReportEntry> { MakeEntry("TpmPssUnsupported", "HP", "EliteBook", "SN1", T0) };
        var lastIngest = new Dictionary<string, DateTime> { ["sn2"] = T0.AddHours(1) };

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports, lastIngest);

        Assert.Null(Assert.Single(aggregated).ConnectedAt);
    }

    [Fact]
    public void BuildAggregatedResult_UnknownSerialBucket_NeverConnected()
    {
        // A lookup key "" must never resolve the bucket of reports without a serial.
        var reports = new List<DistressReportEntry> { MakeEntry("TpmPssUnsupported", "HP", "EliteBook", null, T0) };
        var lastIngest = new Dictionary<string, DateTime> { [""] = T0.AddHours(1) };

        var (aggregated, _) = GetTpmPssUnsupportedFunction.BuildAggregatedResult(reports, lastIngest);

        Assert.Null(Assert.Single(aggregated).ConnectedAt);
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
