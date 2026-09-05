using System;
using Azure.Core;
using AutopilotMonitor.Functions.Helpers;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The storage SDK budgets are sized so one hot-path operation — every attempt into its
/// timeout, every backoff at its cap — finishes before the agent's 30 s upload timeout. A
/// change to either side that breaks that relation turns a storage brownout into zombie
/// requests plus agent replays (audit 2026-09-05 F09).
/// </summary>
public class StorageClientOptionsTests
{
    [Fact]
    public void Table_worst_case_stays_under_the_agent_upload_timeout()
    {
        var worst = StorageClientOptions.WorstCase(StorageClientOptions.Table().Retry);
        Assert.True(worst < StorageClientOptions.AgentUploadTimeout, $"worst case {worst} must stay under {StorageClientOptions.AgentUploadTimeout}");
    }

    [Fact]
    public void Queue_worst_case_stays_under_the_agent_upload_timeout()
    {
        var worst = StorageClientOptions.WorstCase(StorageClientOptions.Queue().Retry);
        Assert.True(worst < StorageClientOptions.AgentUploadTimeout, $"worst case {worst} must stay under {StorageClientOptions.AgentUploadTimeout}");
    }

    [Fact]
    public void Blob_worst_case_is_bounded()
    {
        var worst = StorageClientOptions.WorstCase(StorageClientOptions.Blob().Retry);
        Assert.True(worst < TimeSpan.FromMinutes(5), $"worst case {worst} must stay under five minutes");
    }

    [Fact]
    public void Worst_case_counts_every_attempt_and_every_capped_backoff()
    {
        // 3 attempts × 8 s + backoffs 0.5 s·1.2 and 1 s·1.2 (both under the 4 s cap) = 25.8 s.
        var retry = StorageClientOptions.Table().Retry;
        Assert.Equal(RetryMode.Exponential, retry.Mode);
        Assert.Equal(StorageClientOptions.HotPathMaxRetries, retry.MaxRetries);
        Assert.Equal(StorageClientOptions.HotPathNetworkTimeout, retry.NetworkTimeout);

        var expected = TimeSpan.FromTicks(StorageClientOptions.HotPathNetworkTimeout.Ticks * 3)
            + TimeSpan.FromTicks((long)(StorageClientOptions.HotPathRetryDelay.Ticks * 1.2))
            + TimeSpan.FromTicks((long)(StorageClientOptions.HotPathRetryDelay.Ticks * 2 * 1.2));

        Assert.Equal(expected, StorageClientOptions.WorstCase(retry));
    }

    [Fact]
    public void Blob_worst_case_counts_four_attempts_and_three_backoffs()
    {
        var blob = StorageClientOptions.Blob().Retry;
        // 4 attempts × 60 s + backoffs 0.6 s, 1.2 s, 2.4 s (all under the 4 s cap).
        var expected = TimeSpan.FromTicks(StorageClientOptions.BlobNetworkTimeout.Ticks * 4)
            + TimeSpan.FromMilliseconds(600) + TimeSpan.FromMilliseconds(1200) + TimeSpan.FromMilliseconds(2400);
        Assert.Equal(expected, StorageClientOptions.WorstCase(blob));
    }

    [Fact]
    public void Queue_options_carry_the_requested_encoding()
    {
        Assert.Equal(Azure.Storage.Queues.QueueMessageEncoding.Base64,
            StorageClientOptions.Queue(Azure.Storage.Queues.QueueMessageEncoding.Base64).MessageEncoding);
        Assert.Equal(Azure.Storage.Queues.QueueMessageEncoding.None, StorageClientOptions.Queue().MessageEncoding);
    }
}
