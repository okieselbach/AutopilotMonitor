using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The agent counts short-lived console processes and reports them as one console_script_activity
/// per window. A script that keeps spawning them is not enrollment progress: the count never heals
/// a Stalled session. A console the agent reports as such stays real activity.
/// </summary>
public class ConsoleScriptActivityClassificationTests
{
    private static readonly DateTime At = new(2026, 11, 11, 14, 41, 35, DateTimeKind.Utc);

    [Fact]
    public void A_count_of_script_processes_alone_is_no_real_activity()
    {
        var classification = EventIngestProcessor.ClassifyEvents(new List<EnrollmentEvent>
        {
            Event(Constants.EventTypes.ConsoleScriptActivity),
        });

        Assert.False(classification.HasNonPeriodicRealEvent);
    }

    [Fact]
    public void A_reported_console_is_real_activity()
    {
        var classification = EventIngestProcessor.ClassifyEvents(new List<EnrollmentEvent>
        {
            Event(Constants.EventTypes.ConsoleScriptActivity),
            Event(Constants.EventTypes.OobeConsoleSpawned),
        });

        Assert.True(classification.HasNonPeriodicRealEvent);
    }

    private static EnrollmentEvent Event(string eventType) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        Timestamp = At,
        EventType = eventType,
        Source = "ConsoleBypassWatcher",
        Data = new Dictionary<string, object> { ["count"] = 3 },
    };
}
