using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Queueing
{
    /// <summary>
    /// Shared poll-loop skeleton for the project's Storage-Queue <see cref="BackgroundService"/>
    /// workers (code-quality audit 2026-05-29, finding D2). Replaces the QueueTrigger binding —
    /// which needs a Functions-host-specific <c>&lt;Connection&gt;__queueServiceUri</c> app-setting
    /// the rest of the project's storage access does not use — with a pure DI poll loop that
    /// reproduces the platform's retry semantics:
    /// <list type="bullet">
    ///   <item>Receive up to <see cref="BatchSize"/> messages with <see cref="VisibilityTimeout"/>.</item>
    ///   <item>Handler success → <c>DeleteMessageAsync</c>.</item>
    ///   <item>Handler failure → no delete; the message reappears after the visibility timeout and
    ///     <c>DequeueCount</c> increments (identical to platform retry).</item>
    ///   <item>After <see cref="MaxDequeueCount"/> failed attempts → move to the <c>-poison</c> queue.</item>
    ///   <item>Empty receive → idle for <see cref="PollInterval"/>.</item>
    ///   <item>Unhandled poll-loop exception → log + back off for <see cref="ErrorBackoff"/>.</item>
    /// </list>
    /// <para>
    /// This non-generic base owns everything that does not depend on the envelope type: queue
    /// bootstrap (via <see cref="QueueClientFactory"/>), the poll loop, poison-move, safe-delete,
    /// and queue creation. The typical worker derives from
    /// <see cref="QueuePollingWorker{TEnvelope}"/> (deserialize → validate → handler). A worker
    /// with a bespoke per-message lifecycle (e.g. lease + CAS state machine) derives from this
    /// base directly and implements <see cref="ProcessOneAsync"/>.
    /// </para>
    /// </summary>
    public abstract class QueuePollingWorkerBase : BackgroundService
    {
        private readonly TimeSpan? _pollIntervalOverride;

        /// <summary>Main work queue. Protected so bespoke <see cref="ProcessOneAsync"/> overrides can drive it directly.</summary>
        protected QueueClient MainQueue { get; }

        /// <summary>Poison sibling of <see cref="MainQueue"/>.</summary>
        protected QueueClient PoisonQueue { get; }

        protected ILogger Logger { get; }

        /// <summary>Production bootstrap: build the main + poison client pair from the shared factory.</summary>
        protected QueuePollingWorkerBase(
            QueueClientFactory queueFactory,
            string queueName,
            ILogger logger,
            string? poisonQueueName = null)
        {
            if (queueFactory is null) throw new ArgumentNullException(nameof(queueFactory));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            (MainQueue, PoisonQueue) = queueFactory.CreatePair(queueName, poisonQueueName);
        }

        /// <summary>
        /// Test seam: inject pre-built (possibly mocked) queues so the poll loop can run against an
        /// in-memory queue without Azurite. <paramref name="pollIntervalOverride"/> lets tests use a
        /// short idle interval instead of the production default.
        /// </summary>
        protected QueuePollingWorkerBase(
            QueueClient mainQueue,
            QueueClient poisonQueue,
            ILogger logger,
            TimeSpan? pollIntervalOverride = null)
        {
            MainQueue = mainQueue ?? throw new ArgumentNullException(nameof(mainQueue));
            PoisonQueue = poisonQueue ?? throw new ArgumentNullException(nameof(poisonQueue));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _pollIntervalOverride = pollIntervalOverride;
        }

        // ── Tunables (override per worker) ───────────────────────────────────────

        /// <summary>Max messages received per poll. Storage Queue caps batch-receive at 32.</summary>
        protected virtual int BatchSize => 32;

        /// <summary>Visibility timeout per received message — must cover the slowest handler path.</summary>
        protected virtual TimeSpan VisibilityTimeout => TimeSpan.FromMinutes(5);

        /// <summary>Idle sleep between empty receives (and pause cycles).</summary>
        protected virtual TimeSpan PollInterval => TimeSpan.FromSeconds(10);

        /// <summary>Cool-down after an unhandled poll-loop exception.</summary>
        protected virtual TimeSpan ErrorBackoff => TimeSpan.FromSeconds(30);

        /// <summary>
        /// Canonical max-attempts value, matching the QueueTrigger platform default. Exposed as a
        /// const so tests can drive a message past the threshold without hard-coding the number.
        /// </summary>
        public const int DefaultMaxDequeueCount = 5;

        /// <summary>Match the QueueTrigger platform default: 5 attempts, then move to poison.</summary>
        protected virtual int MaxDequeueCount => DefaultMaxDequeueCount;

        /// <summary>Effective poll interval — the test override wins over the virtual default.</summary>
        protected TimeSpan ResolvedPollInterval => _pollIntervalOverride ?? PollInterval;

        /// <summary>Log-friendly worker name. Defaults to the concrete type name.</summary>
        protected virtual string WorkerName => GetType().Name;

        // ── Loop ─────────────────────────────────────────────────────────────────

        protected sealed override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Ensure both queues exist before the first receive — the producer creates the main
            // queue on first send too, but having the poison queue ready avoids losing the first
            // poison-move attempt.
            await TryCreateQueueAsync(MainQueue, "main", stoppingToken).ConfigureAwait(false);
            await TryCreateQueueAsync(PoisonQueue, "poison", stoppingToken).ConfigureAwait(false);

            Logger.LogInformation("{Worker}: poll loop started (queue {Name})", WorkerName, MainQueue.Name);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Optional entry guard (e.g. kill-switch). When paused the worker does not
                    // dequeue — messages stay visible without burning dequeue budget.
                    if (await ShouldPauseAsync(stoppingToken).ConfigureAwait(false))
                    {
                        await Task.Delay(ResolvedPollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    var batch = await MainQueue
                        .ReceiveMessagesAsync(BatchSize, VisibilityTimeout, stoppingToken)
                        .ConfigureAwait(false);

                    if (batch?.Value is null || batch.Value.Length == 0)
                    {
                        await Task.Delay(ResolvedPollInterval, stoppingToken).ConfigureAwait(false);
                        continue;
                    }

                    foreach (var msg in batch.Value)
                    {
                        if (stoppingToken.IsCancellationRequested) break;
                        await ProcessOneAsync(msg, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "{Worker}: poll-loop error — backing off {Backoff}", WorkerName, ErrorBackoff);
                    try { await Task.Delay(ErrorBackoff, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            Logger.LogInformation("{Worker}: poll loop stopped", WorkerName);
        }

        /// <summary>
        /// Optional entry guard evaluated once per poll cycle before receiving. Default never pauses.
        /// Override to honor a kill-switch.
        /// </summary>
        protected virtual ValueTask<bool> ShouldPauseAsync(CancellationToken ct) => new(false);

        /// <summary>Process a single received message. Implemented by <see cref="QueuePollingWorker{TEnvelope}"/> or a bespoke worker.</summary>
        protected abstract Task ProcessOneAsync(QueueMessage msg, CancellationToken ct);

        // ── Shared helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// Moves a message that exhausted its dequeue budget to the poison queue and deletes it
        /// from the main queue. <see cref="BeforePoisonMoveAsync"/> can veto the move (leaving the
        /// message visible for retry); <see cref="AfterPoisonMoveAsync"/> runs after a successful move.
        /// </summary>
        protected async Task MoveToPoisonAsync(QueueMessage msg, CancellationToken ct)
        {
            if (!await BeforePoisonMoveAsync(msg, ct).ConfigureAwait(false))
            {
                // Hook vetoed the move (e.g. a durable terminal-state transition failed and must
                // be retried before we lose the message). Leave it visible for the next dequeue.
                return;
            }

            try
            {
                await PoisonQueue.SendMessageAsync(msg.Body.ToString(), ct).ConfigureAwait(false);
                await SafeDeleteAsync(msg.MessageId, msg.PopReceipt, ct).ConfigureAwait(false);
                Logger.LogWarning(
                    "{Worker}: moved message {Id} to poison queue after {N} failed attempts",
                    WorkerName, msg.MessageId, msg.DequeueCount - 1);
            }
            catch (Exception ex)
            {
                // If poison-enqueue itself fails, leave the message — next visibility-timeout
                // retries the move. Avoids losing data on transient outages.
                Logger.LogError(ex, "{Worker}: poison move failed for message {Id} (will retry)", WorkerName, msg.MessageId);
                return;
            }

            await AfterPoisonMoveAsync(msg, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs before the poison-queue send. Return <c>false</c> to abort the move and leave the
        /// message visible (used when a durable terminal-state transition must succeed first).
        /// Default proceeds with the move. The message body is available for envelope re-parsing.
        /// </summary>
        protected virtual Task<bool> BeforePoisonMoveAsync(QueueMessage msg, CancellationToken ct) => Task.FromResult(true);

        /// <summary>Runs after a successful poison move (e.g. emit an OpsEvent). Default no-op.</summary>
        protected virtual Task AfterPoisonMoveAsync(QueueMessage msg, CancellationToken ct) => Task.CompletedTask;

        protected Task SafeDeleteAsync(QueueMessage msg, CancellationToken ct)
            => SafeDeleteAsync(msg.MessageId, msg.PopReceipt, ct);

        protected async Task SafeDeleteAsync(string messageId, string popReceipt, CancellationToken ct)
        {
            try
            {
                await MainQueue.DeleteMessageAsync(messageId, popReceipt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex,
                    "{Worker}: delete failed for message {Id} (will reappear after visibility-timeout)",
                    WorkerName, messageId);
            }
        }

        protected async Task TryCreateQueueAsync(QueueClient queue, string label, CancellationToken ct)
        {
            try
            {
                await queue.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex,
                    "{Worker}: CreateIfNotExists failed for {Label} queue — will continue, send/receive will retry",
                    WorkerName, label);
            }
        }
    }
}
