using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the Progress Portal success responses
/// (Functions/Progress/ProgressPortalFunction.cs). Each case serializes the OLD anonymous
/// literal (copied verbatim from the pre-migration code) against the NEW typed DTO with the
/// same values via <see cref="ApiResponseWireParityTests.AssertWireIdentical"/>.
/// </summary>
public class ProgressWireParityTests
{
    private static SessionSummary MakeSessionSummary() => new SessionSummary
    {
        SessionId = "7f3a1c9e-2b4d-4e8f-9a10-5c6d7e8f9a0b",
        TenantId = "11111111-2222-3333-4444-555555555555",
        SerialNumber = "SN-CONTOSO-0042",
        DeviceName = "CONTOSO-LT-0042",
        Manufacturer = "Fabrikam Inc.",
        Model = "Latitude 5440",
        StartedAt = new DateTime(2026, 8, 30, 9, 15, 0, DateTimeKind.Utc),
        CompletedAt = null,
        CurrentPhase = 3,
        CurrentPhaseDetail = "Device setup",
        Status = SessionStatus.InProgress,
        FailureReason = string.Empty
    };

    private static List<EnrollmentEvent> MakeEvents() => new List<EnrollmentEvent>
    {
        new EnrollmentEvent
        {
            EventId = "a1b2c3d4-e5f6-4a7b-8c9d-0e1f2a3b4c5d",
            SessionId = "7f3a1c9e-2b4d-4e8f-9a10-5c6d7e8f9a0b",
            TenantId = "11111111-2222-3333-4444-555555555555",
            Timestamp = new DateTime(2026, 8, 30, 9, 16, 30, DateTimeKind.Utc),
            EventType = "phase_transition"
        },
        new EnrollmentEvent
        {
            EventId = "b2c3d4e5-f6a7-4b8c-9d0e-1f2a3b4c5d6e",
            SessionId = "7f3a1c9e-2b4d-4e8f-9a10-5c6d7e8f9a0b",
            TenantId = "11111111-2222-3333-4444-555555555555",
            Timestamp = new DateTime(2026, 8, 30, 9, 18, 5, DateTimeKind.Utc),
            EventType = "app_install_start"
        }
    };

    // GET /api/progress/sessions/lookup — { success, found, session } with a matched session.
    [Fact]
    public void ProgressLookupSession_match_found()
    {
        var match = MakeSessionSummary();

        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                found = match != null,
                session = match
            },
            new ProgressLookupSessionResponse
            {
                Success = true,
                Found = match != null,
                Session = match
            });
    }

    // GET /api/progress/sessions/lookup — no match: session is null and the key must vanish
    // identically on both sides (WhenWritingNull).
    [Fact]
    public void ProgressLookupSession_no_match_omits_session_key()
    {
        SessionSummary? match = null;

        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                found = match != null,
                session = match
            },
            new ProgressLookupSessionResponse
            {
                Success = true,
                Found = match != null,
                Session = match
            });
    }

    // GET /api/progress/sessions/{sessionId}/events — { success, sessionId, count, events }.
    [Fact]
    public void ProgressGetSessionEvents_success_with_events()
    {
        var sessionId = "7f3a1c9e-2b4d-4e8f-9a10-5c6d7e8f9a0b";
        var events = MakeEvents();

        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                sessionId = sessionId,
                count = events.Count,
                events = events
            },
            new ProgressGetSessionEventsResponse
            {
                Success = true,
                SessionId = sessionId,
                Count = events.Count,
                Events = events
            });
    }

    // Same shape with an empty event list — count 0 and an empty array on both sides.
    [Fact]
    public void ProgressGetSessionEvents_success_with_empty_list()
    {
        var sessionId = "7f3a1c9e-2b4d-4e8f-9a10-5c6d7e8f9a0b";
        var events = new List<EnrollmentEvent>();

        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                sessionId = sessionId,
                count = events.Count,
                events = events
            },
            new ProgressGetSessionEventsResponse
            {
                Success = true,
                SessionId = sessionId,
                Count = events.Count,
                Events = events
            });
    }
}
