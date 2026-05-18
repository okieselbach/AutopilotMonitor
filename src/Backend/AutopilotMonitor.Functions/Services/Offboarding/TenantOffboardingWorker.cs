using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Offboarding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// Background poll-loop for the <c>tenant-offboarding</c> queue (Plan §7.3). Modeled on
    /// <see cref="Deletion.SessionDeletionWorker"/>:
    /// <list type="bullet">
    ///   <item><see cref="BatchSize"/> = <b>1</b> — one tenant cascade per receive bounds memory
    ///       (per-tenant enumerate + per-session enqueue is long-running) and limits the
    ///       blast radius of a poison.</item>
    ///   <item>Heartbeat extends visibility every <see cref="DefaultHeartbeatInterval"/> while
    ///       the handler runs so concurrent re-delivery can't spawn a parallel offboarding
    ///       for the same tenant.</item>
    ///   <item>Max-dequeue <see cref="MaxDequeueCount"/> → poison queue + the handler's own
    ///       Failed-state transition (the handler emits <c>TenantOffboardingFailed</c> when
    ///       it fails closed; the worker emits one too on dequeue exhaustion so an envelope
    ///       that never reaches the handler still surfaces).</item>
    /// </list>
    /// </summary>
    public sealed class TenantOffboardingWorker : BackgroundService
    {
        internal const int BatchSize = 1;

        internal static readonly TimeSpan VisibilityTimeout = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(60);
        internal static readonly TimeSpan HeartbeatExtendBy = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(10);
        internal static readonly TimeSpan ErrorBackoff = TimeSpan.FromSeconds(30);

        internal const int MaxDequeueCount = 5;
        private const string PoisonQueueSuffix = "-poison";

        private readonly QueueClient _mainQueue;
        private readonly QueueClient _poisonQueue;
        private readonly TenantOffboardingHandler _handler;
        private readonly OpsEventService _opsEvents;
        private readonly ILogger<TenantOffboardingWorker> _logger;

        private readonly TimeSpan _heartbeatInterval;
        private readonly TimeSpan _pollInterval;

        public TenantOffboardingWorker(
            IConfiguration configuration,
            TenantOffboardingHandler handler,
            OpsEventService opsEvents,
            ILogger<TenantOffboardingWorker> logger)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _opsEvents = opsEvents ?? throw new ArgumentNullException(nameof(opsEvents));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _heartbeatInterval = DefaultHeartbeatInterval;
            _pollInterval = DefaultPollInterval;

            var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
            var storageAccountName = configuration["AzureStorageAccountName"];
            var connectionString = configuration["AzureTableStorageConnectionString"];

            if (!string.IsNullOrEmpty(storageAccountName))
            {
                var mainUri = new Uri(
                    $"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.TenantOffboarding}");
                var poisonUri = new Uri(
                    $"https://{storageAccountName}.queue.core.windows.net/{Constants.QueueNames.TenantOffboardingPoison}");
                var credential = new DefaultAzureCredential();
                _mainQueue = new QueueClient(mainUri, credential, options);
                _poisonQueue = new QueueClient(poisonUri, credential, options);
                _logger.LogInformation(
                    "TenantOffboardingWorker initialized with Managed Identity (account: {Account})",
                    storageAccountName);
            }
            else if (!string.IsNullOrEmpty(connectionString))
            {
                _mainQueue = new QueueClient(connectionString, Constants.QueueNames.TenantOffboarding, options);
                _poisonQueue = new QueueClient(connectionString, Constants.QueueNames.TenantOffboardingPoison, options);
                _logger.LogInformation("TenantOffboardingWorker initialized with connection string");
            }
            else
            {
                throw new InvalidOperationException(
                    "Queue Storage not configured. Set either 'AzureStorageAccountName' (for Managed Identity) or 'AzureTableStorageConnectionString'.");
            }
        }

        /// <summary>Test seam — inject mocked queues + shorter intervals for unit tests.</summary>
        internal TenantOffboardingWorker(
            QueueClient mainQueue,
            QueueClient poisonQueue,
            TenantOffboardingHandler handler,
            OpsEventService opsEvents,
            ILogger<TenantOffboardingWorker> logger,
            TimeSpan? heartbeatInterval = null,
            TimeSpan? pollInterval = null)
        {
            _mainQueue = mainQueue ?? throw new ArgumentNullException(nameof(mainQueue));
            _poisonQueue = poisonQueue ?? throw new ArgumentNullException(nameof(poisonQueue));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _opsEvents = opsEvents ?? throw new ArgumentNullException(nameof(opsEvents));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _heartbeatInterval = heartbeatInterval ?? DefaultHeartbeatInterval;
            _pollInterval = pollInterval ?? DefaultPollInterval;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await TryCreateQueueAsync(_mainQueue, "main", stoppingToken).ConfigureAwait(false);
            await TryCreateQueueAsync(_poisonQueue, "poison", stoppingToken).ConfigureAwait(false);

            _logger.LogInformation("TenantOffboardingWorker: poll loop started (queue {Name})", _mainQueue.Name);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var batch = await _mainQueue
                        .ReceiveMessagesAsync(BatchSize, VisibilityTimeout, stoppingToken)
                        .ConfigureAwait(false);

                    if (batch?.Value is null || batch.Value.Length == 0)
                    {
                        await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
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
                    _logger.LogError(ex,
                        "TenantOffboardingWorker: poll-loop error — backing off {Backoff}",
                        ErrorBackoff);
                    try { await Task.Delay(ErrorBackoff, stoppingToken).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            _logger.LogInformation("TenantOffboardingWorker: poll loop stopped");
        }

        private async Task ProcessOneAsync(QueueMessage msg, CancellationToken ct)
        {
            if (msg.DequeueCount > MaxDequeueCount)
            {
                await MoveToPoisonAsync(msg, ct).ConfigureAwait(false);
                return;
            }

            TenantOffboardingEnvelope? envelope;
            try
            {
                envelope = JsonConvert.DeserializeObject<TenantOffboardingEnvelope>(msg.Body.ToString());
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex,
                    "TenantOffboardingWorker: malformed envelope JSON — dropping (msg {Id})",
                    msg.MessageId);
                await SafeDeleteAsync(msg.MessageId, msg.PopReceipt, ct).ConfigureAwait(false);
                return;
            }

            if (envelope is null
                || string.IsNullOrEmpty(envelope.TenantId)
                || string.IsNullOrEmpty(envelope.HistoryRowKey))
            {
                _logger.LogWarning(
                    "TenantOffboardingWorker: envelope missing required fields — dropping (msg {Id})",
                    msg.MessageId);
                await SafeDeleteAsync(msg.MessageId, msg.PopReceipt, ct).ConfigureAwait(false);
                return;
            }

            using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeatState = new HeartbeatState(msg.MessageId, msg.PopReceipt);
            var heartbeatTask = Task.Run(() => HeartbeatLoopAsync(heartbeatState, heartbeatCts.Token), heartbeatCts.Token);

            try
            {
                await _handler.HandleAsync(envelope, ct).ConfigureAwait(false);

                heartbeatCts.Cancel();
                try { await heartbeatTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

                await SafeDeleteAsync(msg.MessageId, heartbeatState.PopReceipt, ct).ConfigureAwait(false);
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

                _logger.LogWarning(ex,
                    "TenantOffboardingWorker: handler failed (tenant={Tenant} history={History} dequeue={N}) — visibility-timeout retry",
                    envelope.TenantId, envelope.HistoryRowKey, msg.DequeueCount);
            }
        }

        private async Task HeartbeatLoopAsync(HeartbeatState state, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(_heartbeatInterval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }

                try
                {
                    var updated = await _mainQueue.UpdateMessageAsync(
                        state.MessageId, state.PopReceipt,
                        visibilityTimeout: HeartbeatExtendBy,
                        cancellationToken: ct).ConfigureAwait(false);
                    state.PopReceipt = updated.Value.PopReceipt;
                    _logger.LogDebug(
                        "TenantOffboardingWorker: heartbeat extended visibility for message {Id} by {Extend}",
                        state.MessageId, HeartbeatExtendBy);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "TenantOffboardingWorker: heartbeat UpdateMessageAsync failed for message {Id} — exiting heartbeat",
                        state.MessageId);
                    return;
                }
            }
        }

        private async Task MoveToPoisonAsync(QueueMessage msg, CancellationToken ct)
        {
            TenantOffboardingEnvelope? envelope = null;
            try { envelope = JsonConvert.DeserializeObject<TenantOffboardingEnvelope>(msg.Body.ToString()); }
            catch (JsonException) { /* still poison the malformed envelope below */ }

            // Review-Fix (second-pass) Finding 1: the durable Failed-state transition MUST land
            // BEFORE the poison-queue send + main-queue delete. Otherwise a transient storage /
            // OpsEvent / pointer-CAS failure on the transition would leave the message in poison
            // but the Tenant stuck at InProgress. By doing the transition first we keep the
            // message visible on the main queue when the transition fails — the worker will
            // retry on the next dequeue. The transition itself is idempotent
            // (MarkEnvelopeFailedFromPoisonAsync no-ops on terminal History.Status) so retries
            // are safe.
            if (envelope != null
                && !string.IsNullOrEmpty(envelope.TenantId)
                && !string.IsNullOrEmpty(envelope.HistoryRowKey))
            {
                try
                {
                    await _handler.MarkEnvelopeFailedFromPoisonAsync(
                        envelope, (int)(msg.DequeueCount - 1), ct);
                }
                catch (Exception ex)
                {
                    // Critical: leave the message visible so the next dequeue can retry the
                    // transition. We do NOT proceed to the poison send / SafeDelete here —
                    // otherwise the operator would see a queue with nothing in it and a tenant
                    // hanging on InProgress.
                    _logger.LogError(ex,
                        "TenantOffboardingWorker: MarkEnvelopeFailedFromPoisonAsync failed for tenant={Tenant} history={History} — message left visible for retry (durable Failed state has higher priority than poison move)",
                        envelope.TenantId, envelope.HistoryRowKey);
                    return;
                }
            }

            // Failed state is now durable (or envelope was malformed and couldn't transition).
            // Best-effort poison move + SafeDelete; transient failure here leaves the message
            // visible but the next pickup will see History.Status=Failed and SafeDelete cleanly.
            try
            {
                await _poisonQueue.SendMessageAsync(msg.Body.ToString(), ct).ConfigureAwait(false);
                await SafeDeleteAsync(msg.MessageId, msg.PopReceipt, ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "TenantOffboardingWorker: moved message {Id} to poison queue after {N} failed attempts",
                    msg.MessageId, msg.DequeueCount - 1);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "TenantOffboardingWorker: poison move failed for message {Id} (will retry; durable Failed state already written)",
                    msg.MessageId);
            }
        }

        private async Task SafeDeleteAsync(string messageId, string popReceipt, CancellationToken ct)
        {
            try
            {
                await _mainQueue.DeleteMessageAsync(messageId, popReceipt, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "TenantOffboardingWorker: DeleteMessageAsync failed for message {Id} — message will reappear after visibility expires",
                    messageId);
            }
        }

        private async Task TryCreateQueueAsync(QueueClient queue, string label, CancellationToken ct)
        {
            try
            {
                await queue.CreateIfNotExistsAsync(cancellationToken: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "TenantOffboardingWorker: CreateIfNotExists failed on {Label} queue {Name}",
                    label, queue.Name);
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
