using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Ime;
using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Azure.Storage.Queues.Models;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Queue
{
    /// <summary>
    /// Host-managed consumer of the <c>ime-msi-archive</c> queue: downloads a newly sighted IME
    /// installer into the permanent <c>ime-archive</c> container and merges the outcome onto the
    /// ImeVersionHistory row. Volume is ~one message per Microsoft IME release.
    /// <para>
    /// Retry semantics: the archiver never throws — it classifies. Permanent rejections (bad
    /// version, size cap) complete the message with their <c>Failed:*</c> status persisted;
    /// transient failures (download/timeout) throw so the host's retry → poison ladder applies,
    /// with the last status visible on the row. The host renews the message lease for the
    /// up-to-10-minute download window. The status row-merge happens on EVERY attempt so
    /// operators see a stuck download as <c>Failed:Download</c> instead of nothing.
    /// </para>
    /// <para>
    /// <b>Pause</b> (<see cref="AdminConfiguration.ImeMsiArchivingEnabled"/> off): a trigger
    /// cannot leave a message untouched, so the envelope is re-enqueued with
    /// <see cref="PauseRequeueDelay"/> and the current message completes. The message parks in
    /// the queue until the flag returns; its dequeue count starts over, which is right for a
    /// pause — it is not a failure. The re-enqueue is fail-soft like every other archive step;
    /// operator-side blob backfill remains the fallback.
    /// </para>
    /// </summary>
    public sealed class ImeMsiArchiveQueueFunction
    {
        /// <summary>How long a parked message stays invisible while archiving is switched off.</summary>
        internal static readonly TimeSpan PauseRequeueDelay = TimeSpan.FromMinutes(10);

        private readonly ImeMsiArchiver _archiver;
        private readonly ISessionRepository _sessionRepo;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly IImeMsiArchiveProducer _producer;
        private readonly ILogger<ImeMsiArchiveQueueFunction> _logger;

        public ImeMsiArchiveQueueFunction(
            ImeMsiArchiver archiver,
            ISessionRepository sessionRepo,
            AdminConfigurationService adminConfigService,
            IImeMsiArchiveProducer producer,
            ILogger<ImeMsiArchiveQueueFunction> logger)
        {
            _archiver = archiver ?? throw new ArgumentNullException(nameof(archiver));
            _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
            _adminConfigService = adminConfigService ?? throw new ArgumentNullException(nameof(adminConfigService));
            _producer = producer ?? throw new ArgumentNullException(nameof(producer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [Function("ImeMsiArchiveQueue")]
        public Task Run(
            [QueueTrigger(Constants.QueueNames.ImeMsiArchive, Connection = Constants.QueueNames.TriggerConnection)]
            QueueMessage message,
            CancellationToken cancellationToken)
            => ProcessAsync(message, cancellationToken);

        /// <summary>Testable core — the trigger entry above only forwards.</summary>
        internal async Task ProcessAsync(QueueMessage message, CancellationToken cancellationToken)
        {
            if (!QueueEnvelopeReader.TryRead<ImeMsiArchiveEnvelope>(
                    message, _logger, out var envelope,
                    validate: e => !string.IsNullOrEmpty(e.Version)))
                return;

            if (!await IsArchivingEnabledAsync().ConfigureAwait(false))
            {
                _logger.LogWarning(
                    "ImeMsiArchive paused (ImeMsiArchivingEnabled=false) — re-enqueuing version {Version} with {Delay} delay",
                    envelope.Version, PauseRequeueDelay);
                await _producer.EnqueueAsync(envelope, PauseRequeueDelay, cancellationToken).ConfigureAwait(false);
                return;
            }

            var result = await _archiver.ArchiveAsync(envelope, cancellationToken).ConfigureAwait(false);

            // Merge the outcome first — also for failures, so the row always tells the truth.
            await _sessionRepo.UpdateImeVersionArchiveInfoAsync(
                envelope.Version, result.Status, result.BlobPath, result.Sha256, result.SizeBytes, result.SourceUrl)
                .ConfigureAwait(false);

            if (!result.Success && result.Retryable)
            {
                // Host retry → poison after maxDequeueCount. The thrown message lands in the
                // function's failure log line.
                throw new InvalidOperationException(
                    $"IME MSI archive attempt failed with {result.Status} for version {envelope.Version} (dequeue {message.DequeueCount})");
            }
        }

        private async Task<bool> IsArchivingEnabledAsync()
        {
            try
            {
                var config = await _adminConfigService.GetConfigurationAsync().ConfigureAwait(false);
                return config.ImeMsiArchivingEnabled;
            }
            catch
            {
                return true; // config unreadable → keep working (archive is fail-soft anyway)
            }
        }
    }
}
