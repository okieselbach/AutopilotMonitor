using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Push;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests.Push;

/// <summary>
/// The ingest gate that decides whether a batch fires the session watches: the terminal status on
/// the enrollment alert's gate, else the first genuine WhiteGlove seal on the pre-provisioning
/// alert's gate. A duplicate seal or a status that did not change fires nothing.
/// </summary>
public class SessionWatchOutcomeGateTests
{
    private static EnrollmentEvent Event(string type) => new() { EventType = type };

    [Fact]
    public void A_completion_that_changed_the_status_fires_succeeded()
    {
        var c = new EventClassification { CompletionEvent = Event("enrollment_complete") };

        Assert.Equal(SessionWatchOutcome.Succeeded, EventIngestProcessor.WatchOutcomeFor(true, false, c, null));
    }

    [Fact]
    public void A_failure_with_a_failure_verdict_fires_failed()
    {
        var c = new EventClassification { FailureEvent = Event("enrollment_failed") };

        Assert.Equal(SessionWatchOutcome.Failed, EventIngestProcessor.WatchOutcomeFor(true, false, c, "ESP timeout (0x800705b4)"));
    }

    [Fact]
    public void An_agent_timeout_that_classified_without_a_failure_reason_fires_nothing()
    {
        var c = new EventClassification { FailureEvent = Event("enrollment_failed") };

        Assert.Null(EventIngestProcessor.WatchOutcomeFor(true, false, c, null));
    }

    [Fact]
    public void A_terminal_event_without_a_status_change_fires_nothing()
    {
        var c = new EventClassification { CompletionEvent = Event("enrollment_complete") };

        Assert.Null(EventIngestProcessor.WatchOutcomeFor(false, false, c, null));
    }

    [Fact]
    public void The_first_genuine_seal_fires_sealed()
    {
        var c = new EventClassification { WhiteGloveEvent = Event("whiteglove_complete") };

        Assert.Equal(SessionWatchOutcome.Sealed, EventIngestProcessor.WatchOutcomeFor(false, true, c, null));
    }

    [Fact]
    public void The_duplicate_whiteglove_complete_fires_nothing()
    {
        // The agent emits whiteglove_complete twice; the second batch finds the session already
        // sealed, so whiteGloveStatusTransitioned stays false and the watch is not consumed again.
        var c = new EventClassification { WhiteGloveEvent = Event("whiteglove_complete") };

        Assert.Null(EventIngestProcessor.WatchOutcomeFor(false, false, c, null));
    }

    [Fact]
    public void A_batch_without_terminal_or_seal_events_fires_nothing()
    {
        Assert.Null(EventIngestProcessor.WatchOutcomeFor(true, true, new EventClassification(), null));
    }
}
