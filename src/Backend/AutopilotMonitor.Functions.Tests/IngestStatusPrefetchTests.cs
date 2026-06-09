using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Shared.Models;
using Azure.Data.Tables;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the wire-format contract of the ingest status prefetch (Codex finding): Sessions
/// stores <c>Status</c> as a STRING (<c>status.ToString()</c>), so the prefetch must parse
/// strings — an int-only pattern match never fired and silently disabled the point-read
/// saving for the stall-heal probe.
/// </summary>
public class IngestStatusPrefetchTests
{
    private static TableEntity Row(object? status = null)
    {
        var row = new TableEntity("11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222");
        if (status != null) row["Status"] = status;
        return row;
    }

    [Theory]
    [InlineData("InProgress", SessionStatus.InProgress)]
    [InlineData("Stalled", SessionStatus.Stalled)]
    [InlineData("succeeded", SessionStatus.Succeeded)] // mapper parses case-insensitively — mirror it
    public void StringStatus_TheActualWireFormat_IsParsed(string stored, SessionStatus expected)
    {
        Assert.Equal(expected, IngestTelemetryFunction.TryReadSessionStatus(Row(stored)));
    }

    [Fact]
    public void IntStatus_LegacyDefensiveFallback_IsCast()
    {
        var defined = (int)SessionStatus.Stalled;
        Assert.Equal(SessionStatus.Stalled, IngestTelemetryFunction.TryReadSessionStatus(Row(defined)));
    }

    [Theory]
    [InlineData("NotARealStatus")]
    [InlineData("")]
    public void UnparseableStatus_ReturnsNull_SoCallerFallsBackToOwnRead(string stored)
    {
        Assert.Null(IngestTelemetryFunction.TryReadSessionStatus(Row(stored)));
    }

    [Fact]
    public void MissingStatusOrRow_ReturnsNull()
    {
        Assert.Null(IngestTelemetryFunction.TryReadSessionStatus(Row(status: null)));
        Assert.Null(IngestTelemetryFunction.TryReadSessionStatus(null));
    }
}
