using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Backup;

namespace AutopilotMonitor.Functions.Services.Backup
{
    /// <summary>
    /// Backup orchestrator contract. The lease is ALWAYS owned by the caller (worker
    /// or timer); the service is intentionally lease-unaware so it can be tested in
    /// isolation. <see cref="GenerateBackupId"/> is split out so the caller can stamp
    /// the id onto <c>BackupJobStatus</c> before the heavy work begins (UI deep-link).
    /// </summary>
    public interface ICriticalTableBackupService
    {
        /// <summary>Pure id generator — no side effects. Format <c>yyyyMMddTHHmmssZ_{guid8}</c>.</summary>
        string GenerateBackupId();

        /// <summary>
        /// Runs the full per-table backup loop under an externally-held maintenance lease.
        /// Returns a populated <see cref="BackupRunResult"/>; per-table errors are folded
        /// into the result with <see cref="BackupOutcome.Partial"/>. Fatal failures
        /// (manifest write throws, stream infra dies) propagate to the caller.
        /// </summary>
        Task<BackupRunResult> RunBackupUnderLeaseAsync(string backupId, string triggeredBy, CancellationToken ct = default);
    }

    /// <summary>
    /// Outcome of a single <c>RunBackupUnderLeaseAsync</c> call. The worker maps
    /// <see cref="BackupOutcome.Success"/>/<see cref="BackupOutcome.Partial"/> onto the
    /// matching JobStatus + OpsEvent; a thrown exception maps to JobState=Failed without
    /// a result object.
    /// </summary>
    public sealed class BackupRunResult
    {
        public BackupOutcome Outcome { get; set; }
        public CriticalTableBackupManifest Manifest { get; set; } = new();
        public string ManifestBlobName { get; set; } = string.Empty;
    }
}
