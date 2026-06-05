using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Storage.Queues;
using Azure.Storage.Queues.Models;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models.Offboarding;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services.Offboarding
{
    /// <summary>
    /// Background poll-loop for the <c>tenant-offboarding</c> queue (Plan §7.3). Built on the
    /// shared <see cref="QueuePollingWorker{TEnvelope}"/>:
    /// <list type="bullet">
    ///   <item><see cref="BatchSize"/> = <b>1</b> — one tenant cascade per receive bounds memory
    ///       (per-tenant enumerate + per-session enqueue is long-running) and limits the blast
    ///       radius of a poison.</item>
    ///   <item>Heartbeat (<see cref="UseHeartbeat"/>) extends visibility while the handler runs so
    ///       concurrent re-delivery can't spawn a parallel offboarding for the same tenant.</item>
    ///   <item>Max-dequeue → poison queue, but <see cref="BeforePoisonMoveAsync"/> persists the
    ///       handler's Failed-state transition FIRST and vetoes the move if that fails — otherwise a
    ///       transient failure would leave the message in poison while the Tenant hangs at
    ///       InProgress.</item>
    /// </list>
    /// </summary>
    public sealed class TenantOffboardingWorker : QueuePollingWorker<TenantOffboardingEnvelope>
    {
        private readonly TenantOffboardingHandler _handler;
        private readonly OpsEventService _opsEvents;

        public TenantOffboardingWorker(
            QueueClientFactory queueFactory,
            TenantOffboardingHandler handler,
            OpsEventService opsEvents,
            ILogger<TenantOffboardingWorker> logger)
            : base(queueFactory, Constants.QueueNames.TenantOffboarding, logger, Constants.QueueNames.TenantOffboardingPoison)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _opsEvents = opsEvents ?? throw new ArgumentNullException(nameof(opsEvents));
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
            : base(mainQueue, poisonQueue, logger, pollInterval, heartbeatInterval)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _opsEvents = opsEvents ?? throw new ArgumentNullException(nameof(opsEvents));
        }

        protected override int BatchSize => 1;

        protected override bool UseHeartbeat => true;

        protected override bool TryValidate(TenantOffboardingEnvelope envelope)
            => !string.IsNullOrEmpty(envelope.TenantId)
            && !string.IsNullOrEmpty(envelope.HistoryRowKey);

        protected override string DescribeForLog(TenantOffboardingEnvelope envelope)
            => $"tenant={envelope.TenantId} history={envelope.HistoryRowKey}";

        protected override Task HandleAsync(TenantOffboardingEnvelope envelope, CancellationToken ct)
            => _handler.HandleAsync(envelope, ct);

        protected override async Task<bool> BeforePoisonMoveAsync(QueueMessage msg, CancellationToken ct)
        {
            TenantOffboardingEnvelope? envelope = null;
            try { envelope = JsonConvert.DeserializeObject<TenantOffboardingEnvelope>(msg.Body.ToString()); }
            catch (JsonException) { /* still poison the malformed envelope */ }

            // Review-Fix (second-pass) Finding 1: the durable Failed-state transition MUST land
            // BEFORE the poison-queue send + main-queue delete. Otherwise a transient storage /
            // OpsEvent / pointer-CAS failure on the transition would leave the message in poison
            // but the Tenant stuck at InProgress. By vetoing the poison move on transition failure
            // we keep the message visible on the main queue — the worker retries on the next
            // dequeue. The transition is idempotent (MarkEnvelopeFailedFromPoisonAsync no-ops on a
            // terminal History.Status), so retries are safe.
            if (envelope != null
                && !string.IsNullOrEmpty(envelope.TenantId)
                && !string.IsNullOrEmpty(envelope.HistoryRowKey))
            {
                try
                {
                    await _handler.MarkEnvelopeFailedFromPoisonAsync(
                        envelope, (int)(msg.DequeueCount - 1), ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Critical: leave the message visible so the next dequeue can retry the
                    // transition. We do NOT proceed to the poison send / SafeDelete — otherwise the
                    // operator would see an empty queue and a tenant hanging on InProgress.
                    Logger.LogError(ex,
                        "{Worker}: MarkEnvelopeFailedFromPoisonAsync failed for tenant={Tenant} history={History} — message left visible for retry (durable Failed state has higher priority than poison move)",
                        WorkerName, envelope.TenantId, envelope.HistoryRowKey);
                    return false;
                }
            }

            // Failed state is now durable (or envelope was malformed and couldn't transition).
            // The base proceeds with a best-effort poison move + SafeDelete; transient failure
            // there leaves the message visible but the next pickup sees History.Status=Failed and
            // SafeDeletes cleanly.
            return true;
        }
    }
}
