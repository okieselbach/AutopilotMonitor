using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the third-state stats bucketing (docs/design/enrollment-status-reclassification.md):
/// AwaitingUser and Incomplete get their own buckets instead of hiding in Other, and the buckets
/// always reconcile to Total by construction — so a session in either state can never be silently
/// miscounted as a failure.
/// </summary>
public class SessionStatusBucketsTests
{
    private static SessionStatusBuckets Tally(params string[] statuses)
    {
        var b = default(SessionStatusBuckets);
        foreach (var s in statuses) b = b.Add(s);
        return b;
    }

    [Fact]
    public void Incomplete_and_AwaitingUser_get_their_own_buckets_not_Other()
    {
        var b = Tally("Incomplete", "Incomplete", "AwaitingUser");
        Assert.Equal(2, b.Incomplete);
        Assert.Equal(1, b.AwaitingUser);
        Assert.Equal(0, b.Other);
    }

    [Fact]
    public void Unknown_status_still_lands_in_Other()
    {
        var b = Tally("Succeeded", "SomethingWeird");
        Assert.Equal(1, b.Succeeded);
        Assert.Equal(1, b.Other);
    }

    [Fact]
    public void Buckets_always_reconcile_to_total()
    {
        var b = Tally("Succeeded", "Failed", "Incomplete", "AwaitingUser", "InProgress", "Pending", "Stalled", "weird");
        Assert.Equal(8, b.Total);
        Assert.Equal(b.Total,
            b.Succeeded + b.Failed + b.Incomplete + b.AwaitingUser + b.InProgress + b.Pending + b.Stalled + b.Other);
    }

    [Fact]
    public void Failure_rate_denominator_is_terminal_only_excluding_incomplete()
    {
        // 1 succeeded, 1 failed, 8 incomplete (the crcins shape). Honest rate = 1/(1+1) = 50%,
        // NOT 1/10 = 10% (Incomplete must not dilute) and NOT counting Incomplete as failures.
        var b = Tally("Succeeded", "Failed", "Incomplete", "Incomplete", "Incomplete", "Incomplete",
                      "Incomplete", "Incomplete", "Incomplete", "Incomplete");
        var terminal = b.Succeeded + b.Failed;
        var failureRate = terminal > 0 ? (double)b.Failed / terminal * 100 : 0;
        Assert.Equal(50.0, failureRate);
        Assert.Equal(8, b.Incomplete);
    }
}
