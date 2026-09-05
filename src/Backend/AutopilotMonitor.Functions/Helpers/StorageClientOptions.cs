using System;
using Azure.Core;
using Azure.Data.Tables;
using Azure.Storage.Blobs;
using Azure.Storage.Queues;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// One place that sizes the Azure Storage SDK retry and timeout budgets. The SDK defaults
/// (100 s network timeout, 3 retries, exponential backoff from 0.8 s) let one hanging Table call
/// hold a request for minutes, while the agent gives up on its upload after
/// <see cref="AgentUploadTimeout"/> and replays the batch — under a storage brownout that fills
/// every worker slot with requests nobody is waiting for and doubles the load (audit 2026-09-05
/// F09). The hot-path budget therefore keeps the WORST CASE of one SDK operation (every attempt
/// running into its timeout, every backoff at its cap) under the agent's timeout, so a request
/// that cannot finish fails as 503 + Retry-After while the agent is still listening and its own
/// retry ladder takes over. Pinned by StorageClientOptionsTests.
/// <para>
/// Table and Queue share the hot-path budget: the ingest writes tables and enqueues follow-up
/// work on every batch. Blob is not on the ingest path (diagnostics uploads go straight to the
/// SAS) and carries single-shot uploads of tens of MB (IME MSI archive, report ZIPs), so it gets
/// a bounded but generous budget. No extra retry layer (Polly) on top of the SDK: that would
/// multiply retries instead of bounding them.
/// </para>
/// <para>
/// Known gap: a service <c>Retry-After</c> header on a 429/503 can stretch one backoff beyond
/// the computed cap — Azure.Core honours the larger of the two. Storage answers with small
/// values; the bound below is the code-side guarantee, not a hard ceiling on service hints.
/// </para>
/// </summary>
public static class StorageClientOptions
{
    /// <summary>
    /// The agent's upload HttpClient timeout (<c>MtlsHttpClientFactory.DefaultTimeout</c> on the
    /// agent side). Every hot-path storage call chain must stay under it.
    /// </summary>
    public static readonly TimeSpan AgentUploadTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Per-attempt network timeout for Table and Queue calls.</summary>
    public static readonly TimeSpan HotPathNetworkTimeout = TimeSpan.FromSeconds(8);

    /// <summary>Retries after the first attempt for Table and Queue calls.</summary>
    public const int HotPathMaxRetries = 2;

    public static readonly TimeSpan HotPathRetryDelay = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan HotPathMaxRetryDelay = TimeSpan.FromSeconds(4);

    /// <summary>Per-attempt network timeout for Blob calls (single-shot uploads, streamed reads).</summary>
    public static readonly TimeSpan BlobNetworkTimeout = TimeSpan.FromSeconds(60);
    public const int BlobMaxRetries = 3;

    /// <summary>
    /// Azure.Core adds up to 20 % jitter on top of the exponential delay; the worst-case bound
    /// accounts for it.
    /// </summary>
    internal const double BackoffJitterFactor = 1.2;

    public static TableClientOptions Table()
    {
        var options = new TableClientOptions();
        ApplyHotPath(options.Retry);
        return options;
    }

    public static QueueClientOptions Queue(QueueMessageEncoding encoding = QueueMessageEncoding.None)
    {
        var options = new QueueClientOptions { MessageEncoding = encoding };
        ApplyHotPath(options.Retry);
        return options;
    }

    public static BlobClientOptions Blob()
    {
        var options = new BlobClientOptions();
        options.Retry.Mode = RetryMode.Exponential;
        options.Retry.MaxRetries = BlobMaxRetries;
        options.Retry.Delay = HotPathRetryDelay;
        options.Retry.MaxDelay = HotPathMaxRetryDelay;
        options.Retry.NetworkTimeout = BlobNetworkTimeout;
        return options;
    }

    private static void ApplyHotPath(RetryOptions retry)
    {
        retry.Mode = RetryMode.Exponential;
        retry.MaxRetries = HotPathMaxRetries;
        retry.Delay = HotPathRetryDelay;
        retry.MaxDelay = HotPathMaxRetryDelay;
        retry.NetworkTimeout = HotPathNetworkTimeout;
    }

    /// <summary>
    /// Upper bound on the wall-clock one SDK operation can take under these options: every
    /// attempt runs into <see cref="RetryOptions.NetworkTimeout"/>, every backoff is the
    /// exponential delay (with jitter) capped at <see cref="RetryOptions.MaxDelay"/>.
    /// </summary>
    public static TimeSpan WorstCase(RetryOptions retry)
    {
        if (retry == null) throw new ArgumentNullException(nameof(retry));

        var attempts = retry.MaxRetries + 1;
        var total = TimeSpan.FromTicks(retry.NetworkTimeout.Ticks * attempts);

        for (var attempt = 0; attempt < retry.MaxRetries; attempt++)
        {
            var backoff = retry.Mode == RetryMode.Exponential
                ? TimeSpan.FromTicks((long)(retry.Delay.Ticks * Math.Pow(2, attempt) * BackoffJitterFactor))
                : retry.Delay;
            if (backoff > retry.MaxDelay) backoff = retry.MaxDelay;
            total += backoff;
        }

        return total;
    }
}
