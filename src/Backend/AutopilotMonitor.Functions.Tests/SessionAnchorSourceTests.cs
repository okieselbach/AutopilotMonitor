using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Backfilled environment observations never set the session start (D-315). Since agent 2.0.1473
/// the update and servicing watchers deliver rows with their original event time up to 60 min
/// before the agent started; anchoring on them stretched StartedAt by 22 min at the median
/// (p90 57 min). Source strings are the persisted wire values, spelled out on purpose.
/// </summary>
public class SessionAnchorSourceTests
{
    private static readonly DateTime AgentStart = new(2026, 10, 3, 9, 6, 46, DateTimeKind.Utc);

    [Theory]
    [InlineData("SystemTimelineWatcher")]
    [InlineData("WindowsUpdateWatcher")]
    [InlineData("ServicingWatcher")]
    [InlineData("ShellCoreTracker")]   // the update page, read back an hour on an agent's first run
    public void Backfill_sources_are_not_anchor_eligible(string source)
    {
        Assert.False(Constants.EventSources.IsSessionAnchorEligible(source));
    }

    [Theory]
    [InlineData("ImeLogTracker")]
    [InlineData("Agent")]
    [InlineData("MdmRebootPolicyWatcher")]
    [InlineData("EspAndHelloTracker")]   // the ESP and Hello signals from the same Shell-Core log
    [InlineData("windowsupdatewatcher")] // ordinal: a differently cased source is not the watcher
    [InlineData(null)]                    // legacy row without a source
    public void Activity_sources_are_anchor_eligible(string? source)
    {
        Assert.True(Constants.EventSources.IsSessionAnchorEligible(source));
    }

    [Fact]
    public void Ingest_batch_anchors_on_the_first_activity_not_on_backfilled_update_rows()
    {
        // Shape of session 0f1d1f58: update scans and CBS servicing backfilled from before the
        // IME replay, a clock step even earlier, then IME replay, agent start, a live update row.
        var firstImeLine = AgentStart.AddMinutes(-10);
        var liveUpdate = AgentStart.AddMinutes(25);
        var events = new List<EnrollmentEvent>
        {
            Event(AgentStart.AddMinutes(-16), "system_clock_changed", "SystemTimelineWatcher"),
            Event(AgentStart.AddMinutes(-15), "windows_update_started", "WindowsUpdateWatcher"),
            Event(AgentStart.AddMinutes(-14), "windows_update_servicing", "ServicingWatcher"),
            Event(firstImeLine, "ime_agent_version", "ImeLogTracker"),
            Event(AgentStart, "agent_started", "Agent"),
            Event(liveUpdate, "windows_update_started", "WindowsUpdateWatcher"),
        };

        var classification = EventIngestProcessor.ClassifyEvents(events);

        Assert.Equal(firstImeLine, classification.EarliestEventTimestamp);
        // The latest timestamp still counts every row: a live update row is a real agent report.
        Assert.Equal(liveUpdate, classification.LatestEventTimestamp);
    }

    [Fact]
    public void Ingest_batch_of_only_backfilled_rows_carries_no_anchor()
    {
        var events = new List<EnrollmentEvent>
        {
            Event(AgentStart.AddMinutes(-50), "windows_update_started", "WindowsUpdateWatcher"),
            Event(AgentStart.AddMinutes(-40), "windows_update_servicing", "ServicingWatcher"),
        };

        var classification = EventIngestProcessor.ClassifyEvents(events);

        Assert.Null(classification.EarliestEventTimestamp);
        Assert.Equal(AgentStart.AddMinutes(-40), classification.LatestEventTimestamp);
    }

    private static EnrollmentEvent Event(DateTime timestamp, string eventType, string source) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        Timestamp = timestamp,
        EventType = eventType,
        Source = source,
    };
}
