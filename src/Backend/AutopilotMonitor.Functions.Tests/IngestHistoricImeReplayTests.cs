using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Session eaf3d8c4 (2026-07-23): a previous enrollment's IME log surviving on disk made the
/// agent replay a week-old script history — 156 replayed <c>script_completed</c> events
/// inflated the session's RemediationScriptCount. Newer agents suppress the replay at the
/// source; this predicate covers the fleet of not-yet-updated agents by skipping the script
/// counter increment for events whose <c>rejectedSourceTimestamp</c> (the source log line's
/// own time, preserved by the agent's staleness clamp) is more than 24 h older than the
/// event's clock-clamped stamp.
/// </summary>
public class IngestHistoricImeReplayTests
{
    private static readonly DateTime EventStamp = new(2026, 7, 23, 15, 42, 0, DateTimeKind.Utc);

    private static EnrollmentEvent ScriptEvent(object? rejectedSourceTimestamp, bool includeKey = true)
    {
        var evt = new EnrollmentEvent
        {
            EventType = "script_completed",
            Timestamp = EventStamp,
            Data = new Dictionary<string, object> { ["scriptType"] = "remediation" },
        };
        if (includeKey)
            evt.Data["rejectedSourceTimestamp"] = rejectedSourceTimestamp!;
        return evt;
    }

    [Fact]
    public void StaleRejectedSourceTimestamp_IsHistoricReplay()
    {
        var evt = ScriptEvent(EventStamp.AddDays(-7).ToString("o"));
        Assert.True(EventIngestProcessor.IsHistoricImeReplay(evt));
    }

    [Fact]
    public void StaleAppInstallEvent_IsHistoricReplay()
    {
        // The predicate is event-type-agnostic — it also guards AggregateAppInstallEvent
        // (AppInstallSummaries would otherwise be overwritten with week-old runs).
        var evt = new EnrollmentEvent
        {
            EventType = "app_install_completed",
            Timestamp = EventStamp,
            Data = new Dictionary<string, object>
            {
                ["appName"] = "Contoso App",
                ["rejectedSourceTimestamp"] = EventStamp.AddDays(-7).ToString("o"),
            },
        };
        Assert.True(EventIngestProcessor.IsHistoricImeReplay(evt));
    }

    [Fact]
    public void JustOver24h_IsHistoricReplay()
    {
        var evt = ScriptEvent(EventStamp.AddHours(-25).ToString("o"));
        Assert.True(EventIngestProcessor.IsHistoricImeReplay(evt));
    }

    [Fact]
    public void Within24h_IsNotHistoricReplay()
    {
        var evt = ScriptEvent(EventStamp.AddHours(-23).ToString("o"));
        Assert.False(EventIngestProcessor.IsHistoricImeReplay(evt));
    }

    [Fact]
    public void FutureSkewRejection_IsNotHistoricReplay()
    {
        // A rejected source timestamp AHEAD of the event stamp is a mid-enrollment clock
        // jump (WhiteGlove +1h CMTrace skew), not replayed history.
        var evt = ScriptEvent(EventStamp.AddHours(2).ToString("o"));
        Assert.False(EventIngestProcessor.IsHistoricImeReplay(evt));
    }

    [Fact]
    public void MissingKey_IsNotHistoricReplay()
    {
        Assert.False(EventIngestProcessor.IsHistoricImeReplay(ScriptEvent(null, includeKey: false)));
    }

    [Fact]
    public void NullData_IsNotHistoricReplay()
    {
        var evt = new EnrollmentEvent { EventType = "script_completed", Timestamp = EventStamp, Data = null! };
        Assert.False(EventIngestProcessor.IsHistoricImeReplay(evt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-date")]
    public void UnparseableValue_IsNotHistoricReplay(string raw)
    {
        Assert.False(EventIngestProcessor.IsHistoricImeReplay(ScriptEvent(raw)));
    }

    [Fact]
    public void NullEvent_IsNotHistoricReplay()
    {
        Assert.False(EventIngestProcessor.IsHistoricImeReplay(null));
    }
}
