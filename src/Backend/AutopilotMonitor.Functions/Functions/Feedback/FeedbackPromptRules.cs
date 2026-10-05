using System;
using AutopilotMonitor.Shared.DataAccess;

namespace AutopilotMonitor.Functions.Functions.Feedback;

/// <summary>
/// When the rating bubble may ask a person, and what one answer does to that person's row. A person is
/// asked at most twice: an X click counts a dismissal and the second ask waits for the configured pause;
/// a rating ends it for good and is never replaced. Only clicks count — a bubble that is ignored records
/// nothing.
/// </summary>
internal static class FeedbackPromptRules
{
    /// <summary>The second dismissal is the last ask.</summary>
    internal const int MaxDismissals = 2;

    /// <summary>Whether the bubble may ask the person whose row this is (null = never answered).</summary>
    internal static bool IsEligible(FeedbackEntry? entry, int pauseDays, DateTime nowUtc)
    {
        if (entry == null) return true;
        if (entry.Submitted) return false;
        if (entry.DismissCount <= 0) return true;
        if (entry.DismissCount >= MaxDismissals || pauseDays <= 0) return false;
        return entry.InteractedAt is DateTime answeredAt && (nowUtc - answeredAt).TotalDays >= pauseDays;
    }

    /// <summary>
    /// The person's row after one answer, or null when the answer changes nothing: a rated row is final
    /// (a second tab neither overwrites it nor pings again), and a dismissal beyond the second is not counted.
    /// </summary>
    internal static FeedbackEntry? Apply(FeedbackEntry? current, FeedbackAnswer answer, DateTime nowUtc)
    {
        if (current is { Submitted: true }) return null;

        var dismissals = current?.DismissCount ?? 0;
        if (answer.Dismissed && dismissals >= MaxDismissals) return null;

        return new FeedbackEntry
        {
            Upn = answer.Upn,
            TenantId = answer.TenantId,
            DisplayName = answer.DisplayName,
            Rating = answer.Dismissed ? null : answer.Rating,
            Comment = answer.Dismissed ? null : answer.Comment,
            Dismissed = answer.Dismissed,
            Submitted = !answer.Dismissed,
            DismissCount = answer.Dismissed ? dismissals + 1 : dismissals,
            InteractedAt = nowUtc,
        };
    }
}

/// <summary>One click on the rating bubble: a dismissal, or a rating with an optional comment.</summary>
internal sealed record FeedbackAnswer(
    string Upn, string TenantId, string DisplayName, bool Dismissed, int? Rating, string? Comment);
