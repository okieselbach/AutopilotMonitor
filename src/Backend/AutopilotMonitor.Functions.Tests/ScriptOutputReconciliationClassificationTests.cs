using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// D-316: the agent corrects an already reported platform run from IME's saved result with a
/// script_output_reconciliation. It is observation of an earlier run: it never heals a Stalled session and never
/// counts as another script run.
/// </summary>
public class ScriptOutputReconciliationClassificationTests
{
    private static readonly DateTime At = new(2026, 10, 3, 20, 59, 8, DateTimeKind.Utc);

    [Fact]
    public void A_correction_alone_is_no_real_activity_and_no_script_run()
    {
        var classification = EventIngestProcessor.ClassifyEvents(new List<EnrollmentEvent>
        {
            Event(Constants.EventTypes.ScriptOutputReconciliation, new Dictionary<string, object> { ["scriptType"] = "platform", ["runId"] = "run-1", ["outcome"] = "foreign" }),
        });

        Assert.False(classification.HasNonPeriodicRealEvent);
        Assert.Equal(0, classification.PlatformScriptCount);
    }

    [Fact]
    public void The_corrected_run_counts_once_as_a_platform_script()
    {
        var classification = EventIngestProcessor.ClassifyEvents(new List<EnrollmentEvent>
        {
            Event("script_failed", new Dictionary<string, object> { ["scriptType"] = "platform", ["runId"] = "run-1", ["exitCode"] = "1" }),
            Event(Constants.EventTypes.ScriptOutputReconciliation, new Dictionary<string, object> { ["scriptType"] = "platform", ["runId"] = "run-1", ["outcome"] = "foreign" }),
        });

        Assert.True(classification.HasNonPeriodicRealEvent);
        Assert.Equal(1, classification.PlatformScriptCount);
    }

    private static EnrollmentEvent Event(string eventType, Dictionary<string, object> data) => new()
    {
        EventId = Guid.NewGuid().ToString(),
        Timestamp = At,
        EventType = eventType,
        Source = "RegistryScriptResult",
        Data = data,
    };
}
