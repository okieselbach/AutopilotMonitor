using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the hardware lists of the usage metrics: ranked and shared by sessions, with the distinct
/// devices behind them keyed like device journeys (normalized serial per tenant) and a session
/// without a usable serial counting as its own device.
/// </summary>
public class UsageMetricsHardwareTests
{
    private const string TenantA = "00000000-0000-0000-0000-00000000000a";
    private const string TenantB = "00000000-0000-0000-0000-00000000000b";

    private static SessionSummary Session(string id, string serial, string manufacturer = "Contoso",
        string model = "Book 14", string tenantId = TenantA) => new()
    {
        SessionId = id,
        TenantId = tenantId,
        SerialNumber = serial,
        Manufacturer = manufacturer,
        Model = model,
        StartedAt = DateTime.UtcNow.AddHours(-1),
        Status = SessionStatus.Succeeded,
    };

    [Fact]
    public void A_reinstalled_device_counts_once_per_session_and_once_as_a_device()
    {
        var m = UsageMetricsService.BuildHardwareMetrics(new List<SessionSummary>
        {
            Session("s1", "SN-1001"),
            Session("s2", "SN-1001"),
            Session("s3", "SN-1001"),
        });

        var row = Assert.Single(m.TopModels);
        Assert.Equal(3, row.Count);
        Assert.Equal(1, row.DeviceCount);
        Assert.Equal(100, row.Percentage);
        Assert.Equal(1, Assert.Single(m.TopManufacturers).DeviceCount);
    }

    [Fact]
    public void Serials_differing_in_case_or_whitespace_are_one_device()
    {
        var m = UsageMetricsService.BuildHardwareMetrics(new List<SessionSummary>
        {
            Session("s1", "sn-1001"),
            Session("s2", " SN-1001 "),
        });

        Assert.Equal(1, Assert.Single(m.TopModels).DeviceCount);
    }

    [Fact]
    public void Sessions_without_a_usable_serial_count_as_one_device_each()
    {
        var m = UsageMetricsService.BuildHardwareMetrics(new List<SessionSummary>
        {
            Session("s1", "To be filled by O.E.M."),
            Session("s2", "To be filled by O.E.M."),
            Session("s3", ""),
            Session("s4", "SN-1001"),
        });

        var row = Assert.Single(m.TopModels);
        Assert.Equal(4, row.Count);
        Assert.Equal(4, row.DeviceCount);
    }

    [Fact]
    public void The_same_serial_in_two_tenants_is_two_devices()
    {
        var m = UsageMetricsService.BuildHardwareMetrics(new List<SessionSummary>
        {
            Session("s1", "SN-1001", tenantId: TenantA),
            Session("s2", "SN-1001", tenantId: TenantB),
        });

        Assert.Equal(2, Assert.Single(m.TopModels).DeviceCount);
    }

    [Fact]
    public void Lists_rank_by_sessions_not_devices()
    {
        var m = UsageMetricsService.BuildHardwareMetrics(new List<SessionSummary>
        {
            // One device enrolled three times outranks two devices enrolled once each.
            Session("s1", "SN-1001", "Fabrikam", "Desk 5"),
            Session("s2", "SN-1001", "Fabrikam", "Desk 5"),
            Session("s3", "SN-1001", "Fabrikam", "Desk 5"),
            Session("s4", "SN-2001"),
            Session("s5", "SN-2002"),
        });

        Assert.Equal(new[] { "Desk 5", "Book 14" }, m.TopModels.Select(r => r.Name));
        Assert.Equal(new[] { 3, 2 }, m.TopModels.Select(r => r.Count));
        Assert.Equal(new[] { 1, 2 }, m.TopModels.Select(r => r.DeviceCount));
        Assert.Equal(new[] { 60.0, 40.0 }, m.TopModels.Select(r => r.Percentage));
    }

    [Fact]
    public void Empty_window_yields_empty_lists()
    {
        var m = UsageMetricsService.BuildHardwareMetrics(new List<SessionSummary>());

        Assert.Empty(m.TopManufacturers);
        Assert.Empty(m.TopModels);
    }
}
