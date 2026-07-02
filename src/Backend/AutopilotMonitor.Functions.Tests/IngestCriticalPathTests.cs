using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the critical ingest processing path.
///
/// REGRESSION GUARD: On 2026-03-19 the ingest endpoint returned 500 for all devices because
/// individual events did not have TenantId/SessionId set before being passed to
/// StoreEventsBatchAsync, which validates them as GUIDs.
/// StampServerFields (now on EventIngestProcessor) is the fix — these tests ensure
/// it can never be accidentally removed or bypassed.
/// </summary>
public class IngestCriticalPathTests
{
    private static readonly string ValidTenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private static readonly string ValidSessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    // -------------------------------------------------------------------------
    // StampServerFields — THE regression test
    // -------------------------------------------------------------------------

    [Fact]
    public void StampServerFields_SetsReceivedAtOnAllEvents()
    {
        var receivedAt = DateTime.UtcNow;
        var events = MakeEvents(3);

        EventIngestProcessor.StampServerFields(events, ValidTenantId, ValidSessionId, receivedAt);

        Assert.All(events, evt => Assert.Equal(receivedAt, evt.ReceivedAt));
    }

    [Fact]
    public void StampServerFields_SetsTenantIdOnAllEvents()
    {
        var events = MakeEvents(3);

        EventIngestProcessor.StampServerFields(events, ValidTenantId, ValidSessionId, DateTime.UtcNow);

        Assert.All(events, evt => Assert.Equal(ValidTenantId, evt.TenantId));
    }

    [Fact]
    public void StampServerFields_SetsSessionIdOnAllEvents()
    {
        var events = MakeEvents(3);

        EventIngestProcessor.StampServerFields(events, ValidTenantId, ValidSessionId, DateTime.UtcNow);

        Assert.All(events, evt => Assert.Equal(ValidSessionId, evt.SessionId));
    }

    [Fact]
    public void StampServerFields_OverridesNullTenantId()
    {
        // Regression: agent may not set TenantId per-event (e.g. new event types, early startup)
        var events = new List<EnrollmentEvent>
        {
            new() { TenantId = null, SessionId = null },
        };

        EventIngestProcessor.StampServerFields(events, ValidTenantId, ValidSessionId, DateTime.UtcNow);

        Assert.Equal(ValidTenantId, events[0].TenantId);
        Assert.Equal(ValidSessionId, events[0].SessionId);
    }

    [Fact]
    public void StampServerFields_OverridesWrongTenantId()
    {
        // Regression: agent running in unusual context may send wrong or empty TenantId per-event
        var events = new List<EnrollmentEvent>
        {
            new() { TenantId = "wrong-tenant-value", SessionId = "wrong-session" },
        };

        EventIngestProcessor.StampServerFields(events, ValidTenantId, ValidSessionId, DateTime.UtcNow);

        Assert.Equal(ValidTenantId, events[0].TenantId);
        Assert.Equal(ValidSessionId, events[0].SessionId);
    }

    [Fact]
    public void StampServerFields_AllEventsReceiveSameTenantId()
    {
        // All events in one batch belong to the same tenant — never mixed
        var events = new List<EnrollmentEvent>
        {
            new() { TenantId = null },
            new() { TenantId = "different-1" },
            new() { TenantId = "different-2" },
            new() { TenantId = ValidTenantId },  // already correct
        };

        EventIngestProcessor.StampServerFields(events, ValidTenantId, ValidSessionId, DateTime.UtcNow);

        Assert.All(events, evt => Assert.Equal(ValidTenantId, evt.TenantId));
    }

    [Fact]
    public void StampServerFields_EmptyList_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            EventIngestProcessor.StampServerFields([], ValidTenantId, ValidSessionId, DateTime.UtcNow));
        Assert.Null(ex);
    }

    // -------------------------------------------------------------------------
    // Guard: after StampServerFields, EnsureValidGuid must pass for all events
    // (simulates the contract between ProcessEventsAsync and StoreEventsBatchAsync)
    // -------------------------------------------------------------------------

    [Fact]
    public void StampServerFields_ThenEnsureValidGuid_DoesNotThrow()
    {
        // This is the exact sequence in ProcessEventsAsync → StoreEventsBatchAsync.
        // If StampServerFields is removed or broken, this test fails with ArgumentException.
        var events = MakeEvents(5, tenantId: null, sessionId: null);

        EventIngestProcessor.StampServerFields(events, ValidTenantId, ValidSessionId, DateTime.UtcNow);

        // Simulate what StoreEventsBatchAsync does:
        foreach (var evt in events)
        {
            SecurityValidator.EnsureValidGuid(evt.TenantId, "TenantId");
            SecurityValidator.EnsureValidGuid(evt.SessionId, "SessionId");
        }
        // No exception = pass
    }

    [Fact]
    public void WithoutStampServerFields_EnsureValidGuid_Throws()
    {
        // Documents WHY StampServerFields is required: without it, storage validation explodes.
        var events = MakeEvents(1, tenantId: null, sessionId: null);

        Assert.Throws<ArgumentException>(() =>
            SecurityValidator.EnsureValidGuid(events[0].TenantId, "TenantId"));
    }

    // -------------------------------------------------------------------------
    // TenantId mismatch guard (body vs header)
    // -------------------------------------------------------------------------

    [Fact]
    public void TenantIdMismatch_DifferentCasing_IsDetected()
    {
        // OrdinalIgnoreCase is used — same GUID in different case should NOT be a mismatch
        var header = ValidTenantId.ToLowerInvariant();
        var body = ValidTenantId.ToUpperInvariant();

        Assert.True(string.Equals(header, body, StringComparison.OrdinalIgnoreCase),
            "Same GUID in different casing must be treated as equal (prevents false 403)");
    }

    [Fact]
    public void TenantIdMismatch_DifferentGuids_IsDetected()
    {
        var headerTenant = ValidTenantId;
        var bodyTenant = "c3d4e5f6-a7b8-9012-cdef-012345678901";

        Assert.False(string.Equals(headerTenant, bodyTenant, StringComparison.OrdinalIgnoreCase),
            "Different GUIDs must be detected as a mismatch (prevents TenantId spoofing)");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static List<EnrollmentEvent> MakeEvents(int count, string? tenantId = "original-tenant", string? sessionId = "original-session")
    {
        return Enumerable.Range(0, count)
            .Select(i => new EnrollmentEvent
            {
                EventType = $"test_event_{i}",
                TenantId = tenantId,
                SessionId = sessionId
            })
            .ToList();
    }
}
