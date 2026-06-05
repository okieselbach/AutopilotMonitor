using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Queueing
{
    /// <summary>
    /// The common-case queue worker: deserialize each message into a <typeparamref name="TEnvelope"/>,
    /// validate it, hand it to <see cref="HandleAsync"/>, and delete on success. Adds the optional
    /// in-flight <b>heartbeat</b> (visibility extension) used by long-running cascade workers.
    /// <para>
    /// Subclasses override <see cref="HandleAsync"/> (required) and optionally
    /// <see cref="QueuePollingWorkerBase.BatchSize"/> / <see cref="QueuePollingWorkerBase.VisibilityTimeout"/>,
    /// <see cref="TryValidate"/>, <see cref="QueuePollingWorkerBase.ShouldPauseAsync"/>,
    /// <see cref="UseHeartbeat"/>, and the poison-move hooks.
    /// </para>
    /// </summary>
    /// <typeparam name="TEnvelope">The JSON message contract for this queue.</typeparam>
    public abstract class QueuePollingWorker<TEnvelope> : QueuePollingWorkerBase
        where TEnvelope : class
    {
        private readonly TimeSpan? _heartbeatIntervalOverride;

        protected QueuePollingWorker(
            QueueClientFactory queueFactory,
            string queueName,
            ILogger logger,
            string? poisonQueueName = null)
            : base(queueFactory, queueName, logger, poisonQueueName)
        {
        }

        protected QueuePollingWorker(
            QueueClient mainQueue,
            QueueClient poisonQueue,
            ILogger logger,
            TimeSpan? pollIntervalOverride = null,
            TimeSpan? heartbeatIntervalOverride = null)
            : base(mainQueue, poisonQueue, logger, pollIntervalOverride)
        {
            _heartbeatIntervalOverride = heartbeatIntervalOverride;
        }

        // ── Heartbeat tunables ─────────────────────────────────────────────────────

        /// <summary>
        /// When true, a sidecar task extends the in-flight message's visibility every
        /// <see cref="HeartbeatInterval"/> while <see cref="HandleAsync"/> runs — preventing queue
        /// re-delivery from spawning a parallel worker on the same long-running item.
        /// </summary>
        protected virtual bool UseHeartbeat => false;

        /// <summary>How often the heartbeat task extends the in-flight message's visibility.</summary>
        protected virtual TimeSpan HeartbeatInterval => TimeSpan.FromSeconds(60);

        /// <summary>How much the heartbeat task adds to visibility on each tick.</summary>
        protected virtual TimeSpan HeartbeatExtendBy => TimeSpan.FromMinutes(5);

        private TimeSpan ResolvedHeartbeatInterval => _heartbeatIntervalOverride ?? HeartbeatInterval;

        // ── Required / optional overrides ───────────────────────────────────────────

        /// <summary>Process a validated envelope. Throw to trigger visibility-timeout retry.</summary>
        protected abstract Task HandleAsync(TEnvelope envelope, CancellationToken ct);

        /// <summary>
        /// Validate a deserialized envelope before dispatch. Return <c>false</c> to drop the message
        /// (permanently malformed). Default accepts any non-null envelope.
        /// </summary>
        protected virtual bool TryValidate(TEnvelope envelope) => true;

        /// <summary>Optional log context appended to handler-failure messages (e.g. tenant/session ids).</summary>
        protected virtual string DescribeForLog(TEnvelope envelope) => string.Empty;

        // ── Per-message flow ────────────────────────────────────────────────────────

        protected sealed override async Task ProcessOneAsync(QueueMessage msg, CancellationToken ct)
        {
            // Platform parity: after MaxDequeueCount attempts, move to poison. DequeueCount > max
            // means the prior attempts are exhausted; this receive is not handed to the handler.
            if (msg.DequeueCount > MaxDequeueCount)
            {
                await MoveToPoisonAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            TEnvelope? envelope;
            try
            {
                envelope = JsonConvert.DeserializeObject<TEnvelope>(msg.Body.ToString());
            }
            catch (JsonException ex)
            {
                // Malformed JSON is permanent — drop directly so it doesn't burn dequeue attempts.
                Logger.LogWarning(ex, "{Worker}: malformed envelope JSON — dropping (msg {Id})", WorkerName, msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            if (envelope is null)
            {
                Logger.LogWarning("{Worker}: null envelope after deserialization — dropping (msg {Id})", WorkerName, msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            if (!TryValidate(envelope))
            {
                Logger.LogWarning("{Worker}: envelope failed validation (missing required fields) — dropping (msg {Id})", WorkerName, msg.MessageId);
                await SafeDeleteAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            if (UseHeartbeat)
                await DispatchWithHeartbeatAsync(msg, envelope, ct).ConfigureAwait(false);
            else
                await DispatchAsync(msg, msg.PopReceipt, envelope, ct).ConfigureAwait(false);
        }

        private async Task DispatchAsync(QueueMessage msg, string popReceipt, TEnvelope envelope, CancellationToken ct)
        {
            try
            {
                await HandleAsync(envelope, ct).ConfigureAwait(false);
                await SafeDeleteAsync(msg.MessageId, popReceipt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Shutdown — leave the message visible for the next worker run.
                throw;
            }
            catch (Exception ex)
            {
                // Don't delete: visibility-timeout makes the message visible again, DequeueCount
                // increments, eventual move-to-poison. Identical retry shape to the platform.
                LogHandlerFailure(ex, msg, envelope);
            }
        }

        private async Task DispatchWithHeartbeatAsync(QueueMessage msg, TEnvelope envelope, CancellationToken ct)
        {
            // Capture the latest PopReceipt locally — each UpdateMessageAsync returns a fresh one;
            // the SafeDelete at the end uses whichever receipt was issued last.
            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var state = new HeartbeatState(msg.MessageId, msg.PopReceipt);
            var heartbeatTask = Task.Run(() => HeartbeatLoopAsync(state, heartbeatCts.Token), heartbeatCts.Token);

            try
            {
                await HandleAsync(envelope, ct).ConfigureAwait(false);

                heartbeatCts.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

                await SafeDeleteAsync(state.MessageId, state.PopReceipt, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                heartbeatCts.Cancel();
                throw;
            }
            catch (Exception ex)
            {
                heartbeatCts.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

                LogHandlerFailure(ex, msg, envelope);
            }
        }

        private async Task HeartbeatLoopAsync(HeartbeatState state, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(ResolvedHeartbeatInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try
                {
                    var updated = await MainQueue.UpdateMessageAsync(
                        state.MessageId, state.PopReceipt,
                        visibilityTimeout: HeartbeatExtendBy,
                        cancellationToken: ct).ConfigureAwait(false);
                    state.PopReceipt = updated.Value.PopReceipt;
                    Logger.LogDebug(
                        "{Worker}: heartbeat extended visibility for message {Id} by {Extend}",
                        WorkerName, state.MessageId, HeartbeatExtendBy);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    // Heartbeat failure (e.g. PopReceipt expired) is logged and the loop exits. The
                    // handler keeps running; on its return, SafeDelete may 404 and the message will
                    // reappear after its visibility expires — handlers guard against parallel pickup
                    // with their own idempotency (ETag-CAS on progress state).
                    Logger.LogWarning(ex,
                        "{Worker}: heartbeat UpdateMessageAsync failed for message {Id} — heartbeat exiting",
                        WorkerName, state.MessageId);
                    return;
                }
            }
        }

        private void LogHandlerFailure(Exception ex, QueueMessage msg, TEnvelope envelope)
        {
            var detail = DescribeForLog(envelope);
            if (string.IsNullOrEmpty(detail))
            {
                Logger.LogWarning(ex,
                    "{Worker}: handler failed (msg {Id}, dequeue {N}) — visibility-timeout retry",
                    WorkerName, msg.MessageId, msg.DequeueCount);
            }
            else
            {
                Logger.LogWarning(ex,
                    "{Worker}: handler failed (msg {Id}, dequeue {N}, {Detail}) — visibility-timeout retry",
                    WorkerName, msg.MessageId, msg.DequeueCount, detail);
            }
        }

        private sealed class HeartbeatState
        {
            public string MessageId { get; }
            public string PopReceipt { get; set; }
            public HeartbeatState(string messageId, string popReceipt)
            {
                MessageId = messageId;
                PopReceipt = popReceipt;
            }
        }
    }
}
