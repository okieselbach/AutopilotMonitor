using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for timestamp validation and sanitization in the ingest pipeline.
///
/// REGRESSION GUARD: Invalid agent-side timestamps (DateTime.MinValue, far-future dates,
/// clock-skewed values) previously flowed through to RowKey generation, duration calculations,
/// and Azure Table Storage writes without any validation — causing production issues.
///
/// EventTimestampValidator clamps out-of-range timestamps to a safe range while preserving
/// the original value in OriginalTimestamp for troubleshooting.
/// </summary>
public class EventTimestampValidationTests
{
    // Fixed reference time for deterministic tests
    private static readonly DateTime FixedUtcNow = new(2026, 3, 30, 12, 0, 0, DateTimeKind.Utc);

    private static readonly string ValidTenantId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private static readonly string ValidSessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    // =========================================================================
    // SanitizeTimestamp
    // =========================================================================

    [Fact]
    public void SanitizeTimestamp_ValidRecentUtcTimestamp_ReturnsSameValue()
    {
        var valid = new DateTime(2026, 3, 25, 10, 30, 0, DateTimeKind.Utc);

        var result = EventTimestampValidator.SanitizeTimestamp(valid, FixedUtcNow);

        Assert.Equal(valid, result);
    }

    [Fact]
    public void SanitizeTimestamp_DateTimeMinValue_ClampsToUtcNow()
    {
        // Catastrophic value — falls through past-drift clamp to the safe fallback (receive time).
        var result = EventTimestampValidator.SanitizeTimestamp(DateTime.MinValue, FixedUtcNow);

        Assert.Equal(FixedUtcNow, result);
    }

    [Fact]
    public void SanitizeTimestamp_DateTimeMaxValue_ClampsToUtcNow()
    {
        var result = EventTimestampValidator.SanitizeTimestamp(DateTime.MaxValue, FixedUtcNow);

        Assert.Equal(FixedUtcNow, result);
    }

    [Fact]
    public void SanitizeTimestamp_FarPast1999_ClampsToUtcNow()
    {
        var farPast = new DateTime(1999, 6, 15, 0, 0, 0, DateTimeKind.Utc);

        var result = EventTimestampValidator.SanitizeTimestamp(farPast, FixedUtcNow);

        Assert.Equal(FixedUtcNow, result);
    }

    [Fact]
    public void SanitizeTimestamp_FarFuture2099_ClampsToUtcNow()
    {
        var farFuture = new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        var result = EventTimestampValidator.SanitizeTimestamp(farFuture, FixedUtcNow);

        Assert.Equal(FixedUtcNow, result);
    }

    [Fact]
    public void SanitizeTimestamp_SlightFutureWithin24h_PassesThrough()
    {
        // Agent clock is 2 hours ahead — within tolerance
        var slightFuture = FixedUtcNow.AddHours(2);

        var result = EventTimestampValidator.SanitizeTimestamp(slightFuture, FixedUtcNow);

        Assert.Equal(slightFuture, result);
    }

    [Fact]
    public void SanitizeTimestamp_ExactlyAt24hBoundary_PassesThrough()
    {
        var boundary = FixedUtcNow.AddHours(24);

        var result = EventTimestampValidator.SanitizeTimestamp(boundary, FixedUtcNow);

        Assert.Equal(boundary, result);
    }

    [Fact]
    public void SanitizeTimestamp_25HoursInFuture_ClampsToUtcNow()
    {
        var tooFar = FixedUtcNow.AddHours(25);

        var result = EventTimestampValidator.SanitizeTimestamp(tooFar, FixedUtcNow);

        Assert.Equal(FixedUtcNow, result);
    }

    [Fact]
    public void SanitizeTimestamp_LocalKind_ConvertsToUtcThenValidates()
    {
        // A recent Local kind timestamp — should be converted to UTC, then pass validation
        var local = new DateTime(2026, 3, 25, 10, 0, 0, DateTimeKind.Local);
        var expectedUtc = local.ToUniversalTime();

        var result = EventTimestampValidator.SanitizeTimestamp(local, FixedUtcNow);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(expectedUtc, result);
    }

    [Fact]
    public void SanitizeTimestamp_UnspecifiedKind_TreatedAsUtc()
    {
        var unspecified = new DateTime(2026, 3, 25, 10, 0, 0, DateTimeKind.Unspecified);

        var result = EventTimestampValidator.SanitizeTimestamp(unspecified, FixedUtcNow);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(2026, result.Year);
        Assert.Equal(3, result.Month);
        Assert.Equal(25, result.Day);
    }

    // =========================================================================
    // IsReasonableTimestamp
    // =========================================================================

    [Fact]
    public void IsReasonableTimestamp_ValidDate_ReturnsTrue()
    {
        var valid = new DateTime(2026, 3, 25, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(EventTimestampValidator.IsReasonableTimestamp(valid, FixedUtcNow));
    }

    [Fact]
    public void IsReasonableTimestamp_MinValue_ReturnsFalse()
    {
        Assert.False(EventTimestampValidator.IsReasonableTimestamp(DateTime.MinValue, FixedUtcNow));
    }

    [Fact]
    public void IsReasonableTimestamp_MaxValue_ReturnsFalse()
    {
        Assert.False(EventTimestampValidator.IsReasonableTimestamp(DateTime.MaxValue, FixedUtcNow));
    }

    [Fact]
    public void IsReasonableTimestamp_FarFuture_ReturnsFalse()
    {
        var farFuture = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(EventTimestampValidator.IsReasonableTimestamp(farFuture, FixedUtcNow));
    }

    [Fact]
    public void IsReasonableTimestamp_Year2019_ReturnsFalse()
    {
        // Beyond 7-day past-tolerance — not reasonable relative to FixedUtcNow (2026).
        var justBefore = new DateTime(2019, 12, 31, 23, 59, 59, DateTimeKind.Utc);

        Assert.False(EventTimestampValidator.IsReasonableTimestamp(justBefore, FixedUtcNow));
    }

    // =========================================================================
    // SafeDurationSeconds
    // =========================================================================

    [Fact]
    public void SafeDurationSeconds_NormalRange_ReturnsCorrectDuration()
    {
        var start = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddSeconds(300);

        Assert.Equal(300, EventTimestampValidator.SafeDurationSeconds(start, end));
    }

    [Fact]
    public void SafeDurationSeconds_EndBeforeStart_ReturnsZero()
    {
        var start = new DateTime(2026, 3, 30, 10, 5, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, EventTimestampValidator.SafeDurationSeconds(start, end));
    }

    [Fact]
    public void SafeDurationSeconds_MinValueToMaxValue_ClampsToMax()
    {
        // This would overflow int if not clamped: ~315 billion seconds
        var result = EventTimestampValidator.SafeDurationSeconds(DateTime.MinValue, DateTime.MaxValue);

        Assert.Equal(EventTimestampValidator.DefaultMaxDurationSeconds, result);
    }

    [Fact]
    public void SafeDurationSeconds_SameTimestamp_ReturnsZero()
    {
        var ts = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);

        Assert.Equal(0, EventTimestampValidator.SafeDurationSeconds(ts, ts));
    }

    [Fact]
    public void SafeDurationSeconds_ExceedsMaxDuration_ClampsToMax()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc); // 31 days apart

        var result = EventTimestampValidator.SafeDurationSeconds(start, end);

        Assert.Equal(EventTimestampValidator.DefaultMaxDurationSeconds, result); // 7 days max
    }

    [Fact]
    public void SafeDurationSeconds_CustomMaxDuration_Respected()
    {
        var start = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddSeconds(500);

        var result = EventTimestampValidator.SafeDurationSeconds(start, end, maxDurationSeconds: 100);

        Assert.Equal(100, result);
    }

    // =========================================================================
    // SafeRowKeyTimestamp
    // =========================================================================

    [Fact]
    public void SafeRowKeyTimestamp_ValidDate_FormatsCorrectly()
    {
        var ts = new DateTime(2026, 3, 30, 14, 25, 30, 123, DateTimeKind.Utc);

        var result = EventTimestampValidator.SafeRowKeyTimestamp(ts, FixedUtcNow);

        Assert.Equal("20260330142530123", result);
    }

    [Fact]
    public void SafeRowKeyTimestamp_MinValue_ClampsToUtcNow()
    {
        // DateTime.MinValue must be clamped — never produce "00010101000000000"
        var result = EventTimestampValidator.SafeRowKeyTimestamp(DateTime.MinValue, FixedUtcNow);

        Assert.DoesNotContain("0001", result);
        Assert.StartsWith("2026", result); // Clamped to FixedUtcNow (past-drift fallback)
    }

    [Fact]
    public void SafeRowKeyTimestamp_MaxValue_ProducesReasonableKey()
    {
        // DateTime.MaxValue must be clamped — never produce "99991231235959999"
        var result = EventTimestampValidator.SafeRowKeyTimestamp(DateTime.MaxValue, FixedUtcNow);

        Assert.DoesNotContain("9999", result);
        Assert.StartsWith("2026", result); // Clamped to FixedUtcNow year
    }

    // =========================================================================
    // SanitizeEventTimestamps (pipeline integration)
    // =========================================================================

    [Fact]
    public void SanitizeEventTimestamps_ValidTimestamp_NoFlagsSet()
    {
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = new DateTime(2026, 3, 25, 10, 0, 0, DateTimeKind.Utc) }
        };

        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow);

        Assert.Null(events[0].OriginalTimestamp);
        Assert.False(events[0].TimestampClamped);
    }

    [Fact]
    public void SanitizeEventTimestamps_InvalidTimestamp_PreservesOriginalAndSetsFlag()
    {
        var badTimestamp = DateTime.MinValue;
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = badTimestamp }
        };

        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow);

        Assert.True(events[0].TimestampClamped);
        Assert.Equal(badTimestamp, events[0].OriginalTimestamp);
        Assert.Equal(FixedUtcNow, events[0].Timestamp);
    }

    [Fact]
    public void SanitizeEventTimestamps_MixedValidAndInvalid_OnlyInvalidGetFlag()
    {
        var validTs = new DateTime(2026, 3, 25, 10, 0, 0, DateTimeKind.Utc);
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = validTs, EventType = "valid_event" },
            new() { Timestamp = DateTime.MinValue, EventType = "bad_past_event" },
            new() { Timestamp = new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), EventType = "bad_future_event" },
        };

        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow);

        // Valid event: unchanged
        Assert.False(events[0].TimestampClamped);
        Assert.Null(events[0].OriginalTimestamp);
        Assert.Equal(validTs, events[0].Timestamp);

        // Bad past: clamped to utcNow, original preserved
        Assert.True(events[1].TimestampClamped);
        Assert.Equal(DateTime.MinValue, events[1].OriginalTimestamp);
        Assert.Equal(FixedUtcNow, events[1].Timestamp);

        // Bad future: clamped to utcNow, original preserved
        Assert.True(events[2].TimestampClamped);
        Assert.Equal(new DateTime(2099, 1, 1, 0, 0, 0, DateTimeKind.Utc), events[2].OriginalTimestamp);
        Assert.Equal(FixedUtcNow, events[2].Timestamp);
    }

    [Fact]
    public void SanitizeEventTimestamps_NoEventsDropped()
    {
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = DateTime.MinValue },
            new() { Timestamp = DateTime.MaxValue },
            new() { Timestamp = new DateTime(2026, 3, 25, 10, 0, 0, DateTimeKind.Utc) },
        };

        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow);

        Assert.Equal(3, events.Count); // All events preserved
    }

    [Fact]
    public void SanitizeEventTimestamps_EmptyList_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            EventIngestProcessor.SanitizeEventTimestamps(new List<EnrollmentEvent>(), FixedUtcNow));
        Assert.Null(ex);
    }

    // =========================================================================
    // RecalculateAppDurations (exposed as internal static for testing)
    // =========================================================================

    [Fact]
    public void RecalculateAppDurations_NormalInstall_CorrectDuration()
    {
        var start = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);
        var end = start.AddSeconds(60);

        var state = new AppInstallAggregationState
        {
            Summary = new AppInstallSummary
            {
                AppName = "TestApp",
                StartedAt = start,
                CompletedAt = end,
                Status = "Succeeded"
            }
        };

        EventIngestProcessor.RecalculateAppDurations(state);

        Assert.Equal(60, state.Summary.DurationSeconds);
    }

    [Fact]
    public void RecalculateAppDurations_DownloadThenInstall_CorrectDownloadDuration()
    {
        var downloadStart = new DateTime(2026, 3, 30, 10, 0, 0, DateTimeKind.Utc);
        var installStart = downloadStart.AddSeconds(30);
        var completed = installStart.AddSeconds(45);

        var state = new AppInstallAggregationState
        {
            DownloadStartedAt = downloadStart,
            InstallStartedAt = installStart,
            Summary = new AppInstallSummary
            {
                AppName = "TestApp",
                StartedAt = downloadStart,
                CompletedAt = completed,
                Status = "Succeeded"
            }
        };

        EventIngestProcessor.RecalculateAppDurations(state);

        Assert.Equal(30, state.Summary.DownloadDurationSeconds); // download→install gap
        Assert.Equal(75, state.Summary.DurationSeconds);         // full duration
    }

    [Fact]
    public void RecalculateAppDurations_ExtremeTimestampGap_DurationClamped()
    {
        // Even if timestamps somehow bypass sanitization, SafeDurationSeconds prevents overflow
        var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 12, 31, 23, 59, 59, DateTimeKind.Utc); // ~7 years apart

        var state = new AppInstallAggregationState
        {
            Summary = new AppInstallSummary
            {
                AppName = "TestApp",
                StartedAt = start,
                CompletedAt = end,
                Status = "Succeeded"
            }
        };

        EventIngestProcessor.RecalculateAppDurations(state);

        // Should be clamped to max 7 days (604800 seconds), not ~220 million seconds
        Assert.Equal(EventTimestampValidator.DefaultMaxDurationSeconds, state.Summary.DurationSeconds);
    }

    // =========================================================================
    // End-to-End Pipeline: stamp → sanitize → safe for storage
    // =========================================================================

    [Fact]
    public void FullPipeline_MinValueTimestamp_ClampedWithOriginalPreserved()
    {
        var events = new List<EnrollmentEvent>
        {
            new()
            {
                EventType = "bad_event",
                Timestamp = DateTime.MinValue,
                TenantId = ValidTenantId,
                SessionId = ValidSessionId
            }
        };

        EventIngestProcessor.StampServerFields(events, ValidTenantId, ValidSessionId, FixedUtcNow);
        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow);

        var evt = events[0];

        // Clamped timestamp produces valid RowKey format
        var rowKey = $"{evt.Timestamp:yyyyMMddHHmmssfff}_{evt.Sequence:D10}";
        Assert.StartsWith("2026", rowKey); // Clamped to FixedUtcNow (past-drift fallback)
        Assert.DoesNotContain("0001", rowKey);

        // Original preserved for troubleshooting
        Assert.True(evt.TimestampClamped);
        Assert.Equal(DateTime.MinValue, evt.OriginalTimestamp);
    }

    [Fact]
    public void FullPipeline_MixedTimestamps_AllEventsPreservedFlagsCorrect()
    {
        var validTs = new DateTime(2026, 3, 25, 8, 0, 0, DateTimeKind.Utc);
        var events = new List<EnrollmentEvent>
        {
            new() { EventType = "valid", Timestamp = validTs },
            new() { EventType = "past", Timestamp = new DateTime(1990, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { EventType = "future", Timestamp = new DateTime(2099, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { EventType = "also_valid", Timestamp = FixedUtcNow.AddHours(1) },
        };

        EventIngestProcessor.StampServerFields(events, ValidTenantId, ValidSessionId, FixedUtcNow);
        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow);

        // All 4 events preserved
        Assert.Equal(4, events.Count);

        // Valid: no flags
        Assert.False(events[0].TimestampClamped);
        Assert.False(events[3].TimestampClamped);

        // Invalid: flags set, originals preserved
        Assert.True(events[1].TimestampClamped);
        Assert.True(events[2].TimestampClamped);
        Assert.NotNull(events[1].OriginalTimestamp);
        Assert.NotNull(events[2].OriginalTimestamp);
    }

    // =========================================================================
    // Past-drift clamping (MaxPastToleranceHours = 168h / 7 days)
    //
    // Regression guard for production incident: devices with bad hardware clocks
    // submitted events with agent-side timestamps ~18 days in the past. Since the
    // old validator only clamped to 2020-01-01, those timestamps flowed through
    // untouched, pushed Sessions.StartedAt back by 18 days, and triggered repeated
    // false-positive "ExcessiveDataSender" blocks.
    // =========================================================================

    [Fact]
    public void SanitizeTimestamp_PastDrift6Days_PassesThrough()
    {
        // 6 days back, within 7d tolerance
        var sixDaysBack = FixedUtcNow.AddDays(-6);

        var result = EventTimestampValidator.SanitizeTimestamp(sixDaysBack, FixedUtcNow);

        Assert.Equal(sixDaysBack, result);
    }

    [Fact]
    public void SanitizeTimestamp_PastDriftExactly7Days_PassesThrough()
    {
        // Exactly at the 7d boundary — inclusive
        var boundary = FixedUtcNow.AddHours(-EventTimestampValidator.MaxPastToleranceHours);

        var result = EventTimestampValidator.SanitizeTimestamp(boundary, FixedUtcNow);

        Assert.Equal(boundary, result);
    }

    [Fact]
    public void SanitizeTimestamp_PastDrift7DaysPlus1Second_ClampsToUtcNow()
    {
        // 1 second beyond the 7d boundary — should clamp
        var beyond = FixedUtcNow.AddHours(-EventTimestampValidator.MaxPastToleranceHours).AddSeconds(-1);

        var result = EventTimestampValidator.SanitizeTimestamp(beyond, FixedUtcNow);

        Assert.Equal(FixedUtcNow, result);
    }

    [Fact]
    public void SanitizeTimestamp_PastDrift18Days_ClampsToUtcNow()
    {
        // The smoking-gun production case: device clock 18 days behind.
        var smokingGun = FixedUtcNow.AddDays(-18);

        var result = EventTimestampValidator.SanitizeTimestamp(smokingGun, FixedUtcNow);

        Assert.Equal(FixedUtcNow, result);
    }

    [Fact]
    public void SanitizeTimestamp_PastDrift18DaysLocalKind_ClampsAndReturnsUtc()
    {
        // Local kind with past-drift: must convert to UTC, then clamp, then return UTC kind.
        var local = DateTime.SpecifyKind(FixedUtcNow.AddDays(-18), DateTimeKind.Local);

        var result = EventTimestampValidator.SanitizeTimestamp(local, FixedUtcNow);

        Assert.Equal(DateTimeKind.Utc, result.Kind);
        Assert.Equal(FixedUtcNow, result);
    }

    [Fact]
    public void IsReasonableTimestamp_PastDrift6Days_ReturnsTrue()
    {
        var valid = FixedUtcNow.AddDays(-6);
        Assert.True(EventTimestampValidator.IsReasonableTimestamp(valid, FixedUtcNow));
    }

    [Fact]
    public void IsReasonableTimestamp_PastDrift8Days_ReturnsFalse()
    {
        var tooOld = FixedUtcNow.AddDays(-8);
        Assert.False(EventTimestampValidator.IsReasonableTimestamp(tooOld, FixedUtcNow));
    }

    [Fact]
    public void SanitizeEventTimestamps_PastDrift18Days_PreservesOriginalAndSetsFlag()
    {
        var badTs = FixedUtcNow.AddDays(-18);
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = badTs, EventType = "bad_clock_event" }
        };

        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow);

        Assert.True(events[0].TimestampClamped);
        Assert.Equal(badTs, events[0].OriginalTimestamp);
        Assert.Equal(FixedUtcNow, events[0].Timestamp);
    }

    [Fact]
    public void SanitizeEventTimestamps_MixedPastFuture_AllClampedToUtcNow()
    {
        // All clamping cases (catastrophic, past-drift, future-drift) resolve to utcNow.
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = DateTime.MinValue, EventType = "catastrophic" },               // MinValue → past
            new() { Timestamp = FixedUtcNow.AddDays(-18), EventType = "past_drift" },          // 18d back
            new() { Timestamp = FixedUtcNow.AddHours(25), EventType = "future_drift" },        // 25h ahead
            new() { Timestamp = FixedUtcNow.AddDays(-2), EventType = "valid_past" },           // valid
        };

        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow);

        // Catastrophic: clamped to utcNow (falls into past-drift category)
        Assert.True(events[0].TimestampClamped);
        Assert.Equal(FixedUtcNow, events[0].Timestamp);

        // Past-drift: clamped to utcNow
        Assert.True(events[1].TimestampClamped);
        Assert.Equal(FixedUtcNow, events[1].Timestamp);

        // Future-drift: clamped to utcNow
        Assert.True(events[2].TimestampClamped);
        Assert.Equal(FixedUtcNow, events[2].Timestamp);

        // Valid: unchanged
        Assert.False(events[3].TimestampClamped);
        Assert.Equal(FixedUtcNow.AddDays(-2), events[3].Timestamp);
    }

    // =========================================================================
    // Clock-skew observability logging
    //
    // Verifies that SanitizeEventTimestamps emits a structured Warning when any
    // event in a batch was clamped — enables App Insights querying for bad-clock
    // devices via:  traces | where message startswith "Agent clock skew"
    // =========================================================================

    [Fact]
    public void SanitizeEventTimestamps_NoClamping_DoesNotLog()
    {
        var logger = new CapturingLogger();
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = FixedUtcNow.AddDays(-2), EventType = "valid" },
            new() { Timestamp = FixedUtcNow.AddMinutes(-30), EventType = "valid" },
        };

        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow, logger);

        Assert.Empty(logger.WarningMessages);
    }

    [Fact]
    public void SanitizeEventTimestamps_PastDrift_EmitsSingleWarning()
    {
        var logger = new CapturingLogger();
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = FixedUtcNow.AddDays(-18), EventType = "bad_1", TenantId = ValidTenantId, SessionId = ValidSessionId },
            new() { Timestamp = FixedUtcNow.AddDays(-15), EventType = "bad_2", TenantId = ValidTenantId, SessionId = ValidSessionId },
            new() { Timestamp = FixedUtcNow.AddHours(-2), EventType = "valid",  TenantId = ValidTenantId, SessionId = ValidSessionId },
        };

        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow, logger);

        // Exactly one aggregate warning per batch (not one per event)
        Assert.Single(logger.WarningMessages);
        var msg = logger.WarningMessages[0];
        Assert.StartsWith("Agent clock skew detected", msg);
    }

    [Fact]
    public void SanitizeEventTimestamps_MixedDirections_WarningCarriesStructuredProps()
    {
        var logger = new CapturingLogger();
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = FixedUtcNow.AddDays(-18),          EventType = "past1",  TenantId = ValidTenantId, SessionId = ValidSessionId },
            new() { Timestamp = FixedUtcNow.AddDays(-10),          EventType = "past2",  TenantId = ValidTenantId, SessionId = ValidSessionId },
            new() { Timestamp = FixedUtcNow.AddHours(30),          EventType = "future", TenantId = ValidTenantId, SessionId = ValidSessionId },
        };

        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow, logger);

        Assert.Single(logger.WarningMessages);
        var props = logger.WarningProperties[0];

        // Verify structured props are populated (values are logged in-order via message template)
        Assert.Equal(ValidTenantId, props["TenantId"]?.ToString());
        Assert.Equal(ValidSessionId, props["SessionId"]?.ToString());
        Assert.Equal(3, Convert.ToInt32(props["TotalEvents"]));
        Assert.Equal(2, Convert.ToInt32(props["ClampedPast"]));
        Assert.Equal(1, Convert.ToInt32(props["ClampedFuture"]));

        // Max past drift should be ~432h (18 days from the oldest event)
        var maxPast = Convert.ToDouble(props["MaxPastDriftHours"]);
        Assert.InRange(maxPast, 431.9, 432.1);

        // Max future drift should be ~30h
        var maxFuture = Convert.ToDouble(props["MaxFutureDriftHours"]);
        Assert.InRange(maxFuture, 29.9, 30.1);
    }

    [Fact]
    public void SanitizeEventTimestamps_DateTimeMinValue_FallsIntoPastCategory()
    {
        // Regression guard: DateTime.MinValue must be classified as "past-drift" (not a
        // separate floor category) and produce an absurdly large drift, making it trivial
        // to spot in logs.
        var logger = new CapturingLogger();
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = DateTime.MinValue, EventType = "catastrophic", TenantId = ValidTenantId, SessionId = ValidSessionId },
        };

        EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow, logger);

        Assert.Single(logger.WarningMessages);
        var props = logger.WarningProperties[0];
        Assert.Equal(1, Convert.ToInt32(props["ClampedPast"]));
        Assert.Equal(0, Convert.ToInt32(props["ClampedFuture"]));

        // ~17.7 million hours (2026 years). Anything > 1 million is clearly catastrophic.
        var maxPast = Convert.ToDouble(props["MaxPastDriftHours"]);
        Assert.True(maxPast > 1_000_000);
    }

    [Fact]
    public void SanitizeEventTimestamps_NullLogger_DoesNotThrow()
    {
        // Backward-compat: default call without a logger must still work.
        var events = new List<EnrollmentEvent>
        {
            new() { Timestamp = FixedUtcNow.AddDays(-18), EventType = "bad_clock" }
        };

        var ex = Record.Exception(() => EventIngestProcessor.SanitizeEventTimestamps(events, FixedUtcNow));

        Assert.Null(ex);
        Assert.True(events[0].TimestampClamped);
    }

    /// <summary>
    /// Minimal in-memory logger that captures Warning-level messages and their structured
    /// properties for assertion in tests. Implements ILogger directly — avoids pulling in
    /// a mocking library for a single use case.
    /// </summary>
    private sealed class CapturingLogger : ILogger
    {
        public List<string> WarningMessages { get; } = new();
        public List<IReadOnlyDictionary<string, object?>> WarningProperties { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
                return;

            WarningMessages.Add(formatter(state, exception));

            var props = new Dictionary<string, object?>();
            if (state is IReadOnlyList<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kv in kvps)
                    props[kv.Key] = kv.Value;
            }
            WarningProperties.Add(props);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
