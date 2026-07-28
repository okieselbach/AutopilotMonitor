using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the per-session connection-type projection (2026-07-28): the agent stamps
/// <c>connectionType</c> ("WiFi"/"Ethernet") into every <c>network_interface_info</c>
/// emission (initial collect + re-collect on change); ingest takes the LAST matching
/// event of the batch and persists it via <c>UpdateSessionConnectionTypeAsync</c>
/// (last-write-wins → a device that switches media mid-enrollment is relabeled).
/// The no-NIC payload ({"status":"no_active_interface"}) carries no connectionType
/// and must never match.
/// </summary>
public class IngestConnectionTypeProjectionTests
{
    private static EnrollmentEvent NicInfo(string? connectionType)
    {
        var evt = new EnrollmentEvent
        {
            EventType = "network_interface_info",
            Data = new Dictionary<string, object>
            {
                ["adapterName"] = "Ethernet 2",
                ["linkSpeedMbps"] = 1000L,
            },
        };
        if (connectionType != null) evt.Data["connectionType"] = connectionType;
        return evt;
    }

    [Fact]
    public void EthernetEmission_IsExtracted()
    {
        var events = new List<EnrollmentEvent> { NicInfo("Ethernet") };

        Assert.True(EventIngestProcessor.TryExtractConnectionType(events, out var type));
        Assert.Equal("Ethernet", type);
    }

    [Fact]
    public void LastEmissionOfBatch_Wins()
    {
        // Media switch mid-batch (dock removed → WiFi) — the most recent state wins.
        var events = new List<EnrollmentEvent>
        {
            NicInfo("Ethernet"),
            new EnrollmentEvent { EventType = "download_progress" },
            NicInfo("WiFi"),
        };

        Assert.True(EventIngestProcessor.TryExtractConnectionType(events, out var type));
        Assert.Equal("WiFi", type);
    }

    [Fact]
    public void NoActiveInterfacePayload_DoesNotMatch()
    {
        // The no-NIC emission carries only {"status":"no_active_interface"}.
        var events = new List<EnrollmentEvent>
        {
            new EnrollmentEvent
            {
                EventType = "network_interface_info",
                Data = new Dictionary<string, object> { ["status"] = "no_active_interface" },
            },
        };

        Assert.False(EventIngestProcessor.TryExtractConnectionType(events, out _));
    }

    [Fact]
    public void NoActiveInterfaceAfterValidEmission_DoesNotShadowIt()
    {
        // The predicate selects the last event that CARRIES connectionType.
        var events = new List<EnrollmentEvent>
        {
            NicInfo("WiFi"),
            new EnrollmentEvent
            {
                EventType = "network_interface_info",
                Data = new Dictionary<string, object> { ["status"] = "no_active_interface" },
            },
        };

        Assert.True(EventIngestProcessor.TryExtractConnectionType(events, out var type));
        Assert.Equal("WiFi", type);
    }

    [Theory]
    [InlineData("Cellular")]
    [InlineData("wifi")]      // agent emits exact casing — anything else is unexpected
    [InlineData("")]
    public void UnexpectedValues_AreDroppedDefensively(string raw)
    {
        var events = new List<EnrollmentEvent> { NicInfo(raw) };

        Assert.False(EventIngestProcessor.TryExtractConnectionType(events, out _));
    }

    [Fact]
    public void BatchWithoutNicEvents_DoesNotMatch()
    {
        var events = new List<EnrollmentEvent>
        {
            new EnrollmentEvent { EventType = "esp_phase_changed" },
            new EnrollmentEvent { EventType = "agent_metrics_snapshot" },
        };

        Assert.False(EventIngestProcessor.TryExtractConnectionType(events, out _));
    }

    [Fact]
    public void NullData_DoesNotMatch()
    {
        var events = new List<EnrollmentEvent>
        {
            new EnrollmentEvent { EventType = "network_interface_info", Data = null! },
        };

        Assert.False(EventIngestProcessor.TryExtractConnectionType(events, out _));
    }
}
