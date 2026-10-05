using System;
using AutopilotMonitor.Functions.Functions.Feedback;
using AutopilotMonitor.Shared.DataAccess;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The rating bubble asks a person at most twice: an X click counts, the second ask waits for the pause,
/// a rating ends it for good and is never replaced. Only clicks count.
/// </summary>
public sealed class FeedbackPromptRulesTests
{
    private const int Pause = 60;
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);

    private static FeedbackEntry Row(bool submitted, int dismissals, int daysAgo) => new()
    {
        Upn = "alice@contoso.invalid",
        TenantId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
        DisplayName = "Alice",
        Submitted = submitted,
        Dismissed = !submitted,
        Rating = submitted ? 5 : null,
        Comment = submitted ? "Great" : null,
        DismissCount = dismissals,
        InteractedAt = Now.AddDays(-daysAgo),
    };

    private static FeedbackAnswer Dismiss() =>
        new("alice@contoso.invalid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "Alice", true, null, null);

    private static FeedbackAnswer Rate(int stars, string? comment) =>
        new("alice@contoso.invalid", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "Alice", false, stars, comment);

    // ── When the bubble may ask ──────────────────────────────────────────────

    [Fact]
    public void NeverAnswered_IsAsked()
        => Assert.True(FeedbackPromptRules.IsEligible(null, Pause, Now));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 400)]
    [InlineData(1, 400)]
    public void AfterARating_NeverAgain(int dismissals, int daysAgo)
        => Assert.False(FeedbackPromptRules.IsEligible(Row(submitted: true, dismissals, daysAgo), Pause, Now));

    [Theory]
    [InlineData(1, false)]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(400, true)]
    public void AfterTheFirstDismissal_OnceMore_AfterThePause(int daysAgo, bool expected)
        => Assert.Equal(expected, FeedbackPromptRules.IsEligible(Row(submitted: false, 1, daysAgo), Pause, Now));

    [Theory]
    [InlineData(1)]
    [InlineData(400)]
    public void AfterTheSecondDismissal_NeverAgain(int daysAgo)
        => Assert.False(FeedbackPromptRules.IsEligible(Row(submitted: false, 2, daysAgo), Pause, Now));

    [Fact]
    public void PauseZero_MeansOneAskOnly()
        => Assert.False(FeedbackPromptRules.IsEligible(Row(submitted: false, 1, 400), 0, Now));

    // ── What one answer does ─────────────────────────────────────────────────

    [Fact]
    public void FirstDismissal_CountsOne()
    {
        var row = FeedbackPromptRules.Apply(null, Dismiss(), Now)!;

        Assert.True(row.Dismissed);
        Assert.False(row.Submitted);
        Assert.Equal(1, row.DismissCount);
        Assert.Equal(Now, row.InteractedAt);
        Assert.Null(row.Rating);
    }

    [Fact]
    public void SecondDismissal_CountsTwo_AndEndsTheAsking()
    {
        var row = FeedbackPromptRules.Apply(Row(submitted: false, 1, 61), Dismiss(), Now)!;

        Assert.Equal(2, row.DismissCount);
        Assert.False(FeedbackPromptRules.IsEligible(row, Pause, Now.AddDays(400)));
    }

    [Fact]
    public void DismissalBeyondTheSecond_ChangesNothing()
        => Assert.Null(FeedbackPromptRules.Apply(Row(submitted: false, 2, 1), Dismiss(), Now));

    [Fact]
    public void RatingAfterADismissal_IsStored_AndKeepsTheCount()
    {
        var row = FeedbackPromptRules.Apply(Row(submitted: false, 1, 61), Rate(4, "Mostly good"), Now)!;

        Assert.True(row.Submitted);
        Assert.False(row.Dismissed);
        Assert.Equal(4, row.Rating);
        Assert.Equal("Mostly good", row.Comment);
        Assert.Equal(1, row.DismissCount);
        Assert.Equal("alice@contoso.invalid", row.Upn);
        Assert.Equal("Alice", row.DisplayName);
    }

    [Fact]
    public void ARatedRow_IsFinal()
    {
        // A second tab's dismissal or second rating must neither replace the rating nor ping again.
        var rated = Row(submitted: true, 0, 1);

        Assert.Null(FeedbackPromptRules.Apply(rated, Dismiss(), Now));
        Assert.Null(FeedbackPromptRules.Apply(rated, Rate(1, "changed my mind"), Now));
    }
}
