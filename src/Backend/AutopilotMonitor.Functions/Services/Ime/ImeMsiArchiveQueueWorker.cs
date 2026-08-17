using AutopilotMonitor.Functions.Services.Queueing;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Ime
{
    /// <summary>
    /// Background worker for the <c>ime-msi-archive</c> queue: downloads a newly sighted IME
    /// installer into the permanent <c>ime-archive</c> container and merges the outcome onto
    /// the ImeVersionHistory row. Volume is ~one message per Microsoft IME release.
    /// <para>
    /// Retry semantics: the archiver never throws — it classifies. Permanent rejections
    /// (bad version, size cap) complete the message with their <c>Failed:*</c> status
    /// persisted; transient failures (download/timeout) rethrow so the visibility-timeout
    /// retry → poison ladder applies, with the last status visible on the row.
    /// <see cref="UseHeartbeat"/> keeps the message invisible across the up-to-10-minute
    /// download window. The status row-merge happens on EVERY attempt so operators can see
    /// a stuck download as <c>Failed:Download</c> instead of nothing.
    /// </para>
    /// </summary>
    public sealed class ImeMsiArchiveQueueWorker : QueuePollingWorker<ImeMsiArchiveEnvelope>
    {
        private readonly ImeMsiArchiver _archiver;
        private readonly ISessionRepository _sessionRepo;
        private readonly AdminConfigurationService _adminConfigService;

        public ImeMsiArchiveQueueWorker(
            QueueClientFactory queueFactory,
            ImeMsiArchiver archiver,
            ISessionRepository sessionRepo,
            AdminConfigurationService adminConfigService,
            ILogger<ImeMsiArchiveQueueWorker> logger)
            : base(queueFactory, Constants.QueueNames.ImeMsiArchive, logger)
        {
            _archiver = archiver ?? throw new ArgumentNullException(nameof(archiver));
            _sessionRepo = sessionRepo ?? throw new ArgumentNullException(nameof(sessionRepo));
            _adminConfigService = adminConfigService ?? throw new ArgumentNullException(nameof(adminConfigService));
        }

        /// <summary>Download can legitimately take minutes — extend visibility while working.</summary>
        protected override bool UseHeartbeat => true;

        /// <summary>
        /// Paused while <see cref="AdminConfiguration.ImeMsiArchivingEnabled"/> is off —
        /// messages stay parked (not dropped) and are processed when the flag returns.
        /// Uses the cached config read: this is a pause, not a kill-switch, and the worker
        /// only wakes for rare messages anyway.
        /// </summary>
        protected override async ValueTask<bool> ShouldPauseAsync(CancellationToken ct)
        {
            try
            {
                var config = await _adminConfigService.GetConfigurationAsync();
                return !config.ImeMsiArchivingEnabled;
            }
            catch
            {
                return false; // config unreadable → keep working (archive is fail-soft anyway)
            }
        }

        protected override bool TryValidate(ImeMsiArchiveEnvelope envelope)
            => !string.IsNullOrEmpty(envelope.Version);

        protected override string DescribeForLog(ImeMsiArchiveEnvelope envelope)
            => $"version {envelope.Version}";

        protected override async Task HandleAsync(ImeMsiArchiveEnvelope envelope, CancellationToken ct)
        {
            var result = await _archiver.ArchiveAsync(envelope, ct);

            // Merge the outcome first — also for failures, so the row always tells the truth.
            await _sessionRepo.UpdateImeVersionArchiveInfoAsync(
                envelope.Version, result.Status, result.BlobPath, result.Sha256, result.SizeBytes, result.SourceUrl);

            if (!result.Success && result.Retryable)
            {
                // Visibility-timeout retry → poison after MaxDequeueCount. The thrown message
                // lands in the worker's handler-failure log line.
                throw new InvalidOperationException(
                    $"IME MSI archive attempt failed with {result.Status} for version {envelope.Version}");
            }
        }
    }
}
