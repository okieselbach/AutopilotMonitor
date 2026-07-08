using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins <see cref="ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent"/> — the backend-materialized
/// timeline event synthesized from the agent's best-effort 48h emergency-break report
/// (docs/design/enrollment-status-reclassification.md). The load-bearing bits are the Sequence assignment
/// (must sort AFTER the session's last event) and that it is a Warning, non-terminal marker — the timeout
/// classifier, not this event, decides the real verdict.
/// </summary>
public class ReportAgentErrorFunctionTests
{
    private const string TenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    private static AgentErrorReport Report(DateTime ts, string? message = null) => new()
    {
        SessionId = SessionId,
        TenantId = TenantId,
        ErrorType = AgentErrorType.SessionAgeEmergencyBreak,
        Message = message!,
        AgentVersion = "2.0.1236",
        Timestamp = ts,
    };

    private static readonly DateTime Now = new(2026, 7, 8, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Sequence_is_one_past_the_session_max()
    {
        var existing = new List<EnrollmentEvent>
        {
            new() { Sequence = 5 }, new() { Sequence = 88 }, new() { Sequence = 12 },
        };
        var evt = ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(
            Report(Now.AddMinutes(-1)), TenantId, existing, Now);
        Assert.Equal(89, evt.Sequence);
    }

    [Fact]
    public void Sequence_is_one_when_no_prior_events()
    {
        Assert.Equal(1, ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(
            Report(Now), TenantId, new List<EnrollmentEvent>(), Now).Sequence);
    }

    [Fact]
    public void Shape_is_warning_non_terminal_agent_break()
    {
        var evt = ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(Report(Now), TenantId, null!, Now);

        Assert.Equal("agent_emergency_break", evt.EventType);
        Assert.Equal(AutopilotMonitor.Shared.Constants.EventTypes.AgentEmergencyBreak, evt.EventType);
        Assert.Equal(EventSeverity.Warning, evt.Severity);
        Assert.Equal(EnrollmentPhase.Unknown, evt.Phase);
        Assert.Equal(TenantId, evt.TenantId);
        Assert.Equal(SessionId, evt.SessionId);
        Assert.Equal("emergency_channel", evt.Data["source"]);
    }

    [Fact]
    public void Uses_agent_break_timestamp_when_valid_else_receipt_time()
    {
        var breakTime = Now.AddHours(-2);
        Assert.Equal(breakTime, ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(
            Report(breakTime), TenantId, null!, Now).Timestamp);

        // Bogus/default timestamp → fall back to receipt time (now).
        Assert.Equal(Now, ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(
            Report(default), TenantId, null!, Now).Timestamp);
    }

    [Fact]
    public void Falls_back_to_default_message_when_report_message_blank()
    {
        var evt = ReportAgentErrorFunction.BuildAgentEmergencyBreakEvent(Report(Now, message: ""), TenantId, null!, Now);
        Assert.Contains("emergency break", evt.Message, StringComparison.OrdinalIgnoreCase);
    }
}
