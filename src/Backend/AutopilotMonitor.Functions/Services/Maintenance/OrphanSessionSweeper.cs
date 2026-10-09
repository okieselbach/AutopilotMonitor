using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Backup;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using Azure.Storage.Blobs.Specialized;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services.Maintenance
{
    /// <summary>
    /// The cascade for sessions that have no Sessions row. A cascade needs the row (CAS lock,
    /// manifest, tombstone); rows a writer leaves behind for a session the backend does not know
    /// are therefore reachable only through the discovery handle every events write creates
    /// (EventSessionIndex, PK tenantId / RK sessionId). The sweep diffs those handles against the
    /// Sessions keys, re-checks every candidate moments before touching it, clears every session
    /// table the cascade manifest knows (ratchet in <c>TableLifecycleBucketTests</c>), and deletes
    /// the handle LAST — a failure before that point leaves the session discoverable for the next
    /// run; every delete is idempotent.
    /// <para>
    /// Once a week (Sunday morning UTC, or on any manual maintenance run) it additionally
    /// reconciles EventTypeIndex against the Sessions keys, read-only: the proof that nothing
    /// without a handle is left behind.
    /// </para>
    /// </summary>
    public sealed class OrphanSessionSweeper
    {
        /// <summary>A handle younger than this may belong to a session whose registration is still in flight.</summary>
        public static readonly TimeSpan IngestGrace = TimeSpan.FromHours(24);

        /// <summary>
        /// A restore re-inserts the handle (old LastIngestAt, fresh write time) and the Sessions row
        /// LAST; a handle written this recently is left alone until the restore has either finished
        /// or failed.
        /// </summary>
        public static readonly TimeSpan HandleWriteGrace = TimeSpan.FromHours(2);

        public const int MaxCandidatesPerRun = 50;
        public static readonly TimeSpan RunBudget = TimeSpan.FromMinutes(5);

        /// <summary>Sunday at or after this hour (UTC) is the weekly reconciliation window — the quiet part of the week.</summary>
        public const int DeepCheckSundayHourUtc = 8;
        public static readonly TimeSpan DeepCheckInterval = TimeSpan.FromDays(6);
        /// <summary>A missed Sunday (deploy, outage) does not postpone the reconciliation past this age.</summary>
        public static readonly TimeSpan DeepCheckOverdue = TimeSpan.FromDays(8);
        public const int DeepCheckSampleSize = 20;

        /// <summary>LastIngestAt of a handle written by hand for a legacy orphan: no ingest ever happened at that time.</summary>
        public static readonly DateTime LegacyHandleLastIngestAt = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public const string Actor = "System.OrphanSweep";
        public const string TimerTrigger = "Timer";

        /// <summary>
        /// Every table the sweep clears: the filter tables, the inventory pair (side row + the
        /// SoftwareInventory decrements) and the handle. Pinned against the cascade manifest.
        /// </summary>
        public static readonly IReadOnlyList<string> SweptTables = OrphanSessionRowFilters.Tables
            .Concat(new[]
            {
                Constants.TableNames.SessionInventoryContributions,
                Constants.TableNames.SoftwareInventory,
                Constants.TableNames.EventSessionIndex,
            })
            .ToArray();

        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly IVulnerabilityRepository _vulnRepo;
        private readonly ISessionDeletionInventoryReader _reader;
        private readonly AdminConfigurationService _adminConfig;
        private readonly OrphanSweepLockStore _lockStore;
        private readonly OpsEventService _opsEvents;
        private readonly ILogger<OrphanSessionSweeper> _logger;
        private readonly Func<DateTime> _clock;
        private readonly TimeSpan _runBudget;

        public OrphanSessionSweeper(
            IMaintenanceRepository maintenanceRepo,
            IVulnerabilityRepository vulnRepo,
            ISessionDeletionInventoryReader reader,
            AdminConfigurationService adminConfig,
            OrphanSweepLockStore lockStore,
            OpsEventService opsEvents,
            ILogger<OrphanSessionSweeper> logger)
            : this(maintenanceRepo, vulnRepo, reader, adminConfig, lockStore, opsEvents, logger, () => DateTime.UtcNow, RunBudget)
        {
        }

        /// <summary>Test seam: injectable clock and run budget.</summary>
        internal OrphanSessionSweeper(
            IMaintenanceRepository maintenanceRepo,
            IVulnerabilityRepository vulnRepo,
            ISessionDeletionInventoryReader reader,
            AdminConfigurationService adminConfig,
            OrphanSweepLockStore lockStore,
            OpsEventService opsEvents,
            ILogger<OrphanSessionSweeper> logger,
            Func<DateTime> clock,
            TimeSpan runBudget)
        {
            _maintenanceRepo = maintenanceRepo;
            _vulnRepo = vulnRepo;
            _reader = reader;
            _adminConfig = adminConfig;
            _lockStore = lockStore;
            _opsEvents = opsEvents;
            _logger = logger;
            _clock = clock;
            _runBudget = runBudget;
        }

        /// <summary>
        /// One run under the sweep lease: timer and manual maintenance enter here. Returns false
        /// when another run held the lease. A manual run always includes the reconciliation.
        /// Every outcome is an ops event; nothing propagates to the caller.
        /// </summary>
        public async Task<bool> RunAsync(string triggeredBy, CancellationToken cancellationToken)
        {
            BlobLeaseClient lease;
            try
            {
                lease = await _lockStore.AcquireLeaseAsync(ct: cancellationToken).ConfigureAwait(false);
            }
            catch (LeaseHeldException)
            {
                _logger.LogWarning("OrphanSweep: lease held by another run — skipping (triggeredBy={TriggeredBy})", triggeredBy);
                await _opsEvents.RecordOrphanSweepSkippedLockedAsync(triggeredBy).ConfigureAwait(false);
                return false;
            }

            using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var leaseHolder = new MaintenanceLeaseHolder(lease, runCts, _logger);
            try
            {
                OrphanSweepRunResult result;
                try
                {
                    result = await RunCoreAsync(forceDeepCheck: triggeredBy != TimerTrigger, runCts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "OrphanSweep: run failed (triggeredBy={TriggeredBy})", triggeredBy);
                    await _opsEvents.RecordOrphanSweepFailedAsync(ex.Message, triggeredBy).ConfigureAwait(false);
                    return true;
                }

                await _opsEvents.RecordOrphanSweepCompletedAsync(result, triggeredBy).ConfigureAwait(false);
                if (result.Cleaned.Count > 0)
                    await _opsEvents.RecordOrphanEventsCleanedAsync(result).ConfigureAwait(false);
                return true;
            }
            finally
            {
                if (leaseHolder.RenewalFailureReason != null)
                    _logger.LogWarning("OrphanSweep: the lease could not be renewed during the run ({Reason})", leaseHolder.RenewalFailureReason);
                await leaseHolder.DisposeAsync().ConfigureAwait(false);
            }
        }

        /// <summary>The run without lease and ops events (unit-testable).</summary>
        internal async Task<OrphanSweepRunResult> RunCoreAsync(bool forceDeepCheck, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var now = _clock();
            var result = new OrphanSweepRunResult();

            // Sessions first: a cascade that finishes between the two drains removes the handle
            // (step 6) before the row (step 18), so this order cannot produce a candidate the
            // cascade is still working on.
            var sessionKeys = await _maintenanceRepo.GetSessionKeysAsync(ct).ConfigureAwait(false);
            var handles = await _maintenanceRepo.GetEventSessionIndexHandlesAsync(ct).ConfigureAwait(false);
            result.SessionKeys = sessionKeys.Count;
            result.HandlesScanned = handles.Count;

            var candidates = handles
                .Where(h => !sessionKeys.Contains((h.TenantId, h.SessionId)))
                .Where(h => h.LastIngestAt <= now - IngestGrace && h.WrittenAt <= now - HandleWriteGrace)
                .OrderBy(h => h.LastIngestAt)
                .ToList();
            result.Candidates = candidates.Count;

            var processed = 0;
            foreach (var candidate in candidates)
            {
                if (processed >= MaxCandidatesPerRun) break;
                if (sw.Elapsed >= _runBudget)
                {
                    result.BudgetExhausted = true;
                    break;
                }
                ct.ThrowIfCancellationRequested();
                processed++;
                try
                {
                    await CleanCandidateAsync(candidate, result, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result.FailedSessions++;
                    _logger.LogError(ex,
                        "OrphanSweep: cleaning tenant={TenantId} session={SessionId} failed — the handle stays for the next run",
                        candidate.TenantId, candidate.SessionId);
                }
            }
            result.Remaining = candidates.Count - processed;

            if (forceDeepCheck || IsDeepCheckDue(await ReadDeepCheckLastRunAsync().ConfigureAwait(false), now))
                result.Reconcile = await ReconcileAsync(sessionKeys, handles, now, ct).ConfigureAwait(false);

            result.DurationMs = sw.ElapsedMilliseconds;
            return result;
        }

        private async Task CleanCandidateAsync(OrphanSessionHandle candidate, OrphanSweepRunResult result, CancellationToken ct)
        {
            var tenantId = candidate.TenantId;
            var sessionId = candidate.SessionId;

            // Re-checks moments before the deletes — the scan above is minutes old.
            var handle = await _maintenanceRepo.GetEventSessionIndexHandleAsync(tenantId, sessionId, ct).ConfigureAwait(false);
            if (handle == null)
            {
                result.SkippedHandleGone++; // a cascade got there first
                return;
            }
            if (!string.Equals(handle.ETag, candidate.ETag, StringComparison.Ordinal))
            {
                result.SkippedHandleChanged++; // a later write; its own grace starts over
                return;
            }
            var row = await _reader.GetSessionRowAsync(tenantId, sessionId, SessionDeletionGuard.LockColumns, ct).ConfigureAwait(false);
            if (row != null)
            {
                result.SkippedRowReturned++; // registered or restored meanwhile
                return;
            }
            var afterCascade = await _reader.GetActiveSessionTombstoneAsync(tenantId, sessionId, ct).ConfigureAwait(false) != null;
            if (afterCascade) result.AfterCascade++;

            var cleaned = new OrphanSweepCleanedSession
            {
                TenantId = tenantId,
                SessionId = sessionId,
                Legacy = candidate.LastIngestAt <= LegacyHandleLastIngestAt,
                AfterCascade = afterCascade,
            };
            var etag = handle.ETag;

            // Inventory first, with the handle as the progress record. The stamp is the claim: a
            // crash after it loses at most this session's decrements (the counts stay high, as they
            // did before the sweep existed), and a rerun can never decrement twice.
            if (!handle.InventoryDecremented)
            {
                var stamped = await _maintenanceRepo.StampHandleInventoryDecrementedAsync(tenantId, sessionId, etag, ct).ConfigureAwait(false);
                if (stamped == null)
                {
                    result.SkippedHandleChanged++;
                    return;
                }
                etag = stamped;

                var keys = await _vulnRepo.GetSessionInventoryContributionsAsync(tenantId, sessionId, ct).ConfigureAwait(false);
                if (keys != null)
                {
                    cleaned.HadInventorySideRow = true;
                    foreach (var key in keys)
                    {
                        try
                        {
                            await _vulnRepo.DecrementSoftwareInventoryEntryAsync(tenantId, key.Vendor, key.Name, key.Version, ct).ConfigureAwait(false);
                            cleaned.InventoryKeysDecremented++;
                        }
                        catch (Exception ex)
                        {
                            result.InventoryDecrementsFailed++;
                            _logger.LogWarning(ex,
                                "OrphanSweep: inventory decrement failed tenant={TenantId} session={SessionId} key={Vendor}/{Name}/{Version}",
                                tenantId, sessionId, key.Vendor, key.Name, key.Version);
                        }
                    }
                }
            }
            await _vulnRepo.DeleteSessionInventoryContributionsAsync(tenantId, sessionId, ct).ConfigureAwait(false);

            foreach (var table in OrphanSessionRowFilters.Tables)
            {
                var rows = await _maintenanceRepo.DeleteOrphanSessionRowsAsync(table, tenantId, sessionId, ct).ConfigureAwait(false);
                cleaned.RowsByTable[table] = rows;
            }

            // The handle last, and only while unchanged: a batch that re-wrote it meanwhile has
            // rows the next run must still find.
            var handleDeleted = await _maintenanceRepo.DeleteEventSessionIndexHandleAsync(tenantId, sessionId, etag, ct).ConfigureAwait(false);
            cleaned.HandleKept = !handleDeleted;

            result.Add(cleaned);
            _logger.LogInformation(
                "OrphanSweep: cleaned tenant={TenantId} session={SessionId} rows={Rows} inventoryKeys={Keys} legacy={Legacy} afterCascade={AfterCascade} handleKept={HandleKept}",
                tenantId, sessionId, cleaned.TotalRows, cleaned.InventoryKeysDecremented, cleaned.Legacy, cleaned.AfterCascade, cleaned.HandleKept);
        }

        /// <summary>
        /// Sunday at/after 08:00 UTC once the last run is at least six days old; a run older than
        /// eight days is due at the next tick regardless. Never run before: the first Sunday.
        /// </summary>
        internal static bool IsDeepCheckDue(DateTime? lastRunUtc, DateTime nowUtc)
        {
            var inSundayWindow = nowUtc.DayOfWeek == DayOfWeek.Sunday && nowUtc.Hour >= DeepCheckSundayHourUtc;
            if (lastRunUtc == null) return inSundayWindow;
            var age = nowUtc - lastRunUtc.Value;
            if (age >= DeepCheckOverdue) return true;
            return inSundayWindow && age >= DeepCheckInterval;
        }

        private async Task<DateTime?> ReadDeepCheckLastRunAsync()
        {
            var config = await _adminConfig.GetConfigurationAsync().ConfigureAwait(false);
            return DateTime.TryParse(config?.OrphanDeepCheckLastRunUtc, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : (DateTime?)null;
        }

        /// <summary>
        /// Read-only: every (tenant, session) in EventTypeIndex that has neither a Sessions row nor
        /// a handle (handles are the sweep's own pipeline) is residue — confirmed against the live
        /// Sessions row, because the drains are minutes old. Residue is reported, never cleaned
        /// here: the operator gives it a handle, and the normal path with all its checks does the rest.
        /// </summary>
        private async Task<OrphanReconcileResult> ReconcileAsync(
            HashSet<(string TenantId, string SessionId)> sessionKeys,
            IReadOnlyList<OrphanSessionHandle> handles,
            DateTime now,
            CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            var indexKeys = await _maintenanceRepo.GetEventTypeIndexSessionKeysAsync(ct).ConfigureAwait(false);
            var handleKeys = new HashSet<(string TenantId, string SessionId)>(handles.Select(h => (h.TenantId, h.SessionId)));

            var residue = new List<(string TenantId, string SessionId)>();
            foreach (var key in indexKeys)
            {
                if (sessionKeys.Contains(key) || handleKeys.Contains(key)) continue;
                ct.ThrowIfCancellationRequested();
                var row = await _reader.GetSessionRowAsync(key.TenantId, key.SessionId, SessionDeletionGuard.LockColumns, ct).ConfigureAwait(false);
                if (row != null) continue;
                residue.Add(key);
            }

            var result = new OrphanReconcileResult
            {
                IndexSessions = indexKeys.Count,
                ResidueSessions = residue.Count,
                Sample = residue.Take(DeepCheckSampleSize).ToList(),
                DurationMs = sw.ElapsedMilliseconds,
            };
            await _opsEvents.RecordOrphanReconcileCompletedAsync(result).ConfigureAwait(false);

            var stamp = now.ToString("o", CultureInfo.InvariantCulture);
            await _adminConfig.UpdateAsync(c => { c.OrphanDeepCheckLastRunUtc = stamp; return null; }, Actor, "orphan-sweep").ConfigureAwait(false);
            return result;
        }
    }

    /// <summary>One session the sweep cleaned in a run.</summary>
    public sealed class OrphanSweepCleanedSession
    {
        public string TenantId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        /// <summary>The handle was written by hand for a legacy orphan (LastIngestAt 2000-01-01).</summary>
        public bool Legacy { get; set; }
        /// <summary>A tombstone marker existed: a cascade ran, rows were written after it.</summary>
        public bool AfterCascade { get; set; }
        /// <summary>The handle changed between the deletes and the final delete; the next run sees the session again.</summary>
        public bool HandleKept { get; set; }
        public bool HadInventorySideRow { get; set; }
        public int InventoryKeysDecremented { get; set; }
        public Dictionary<string, int> RowsByTable { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public int TotalRows => RowsByTable.Values.Sum();
    }

    /// <summary>Outcome of the weekly reconciliation.</summary>
    public sealed class OrphanReconcileResult
    {
        public int IndexSessions { get; set; }
        public int ResidueSessions { get; set; }
        public List<(string TenantId, string SessionId)> Sample { get; set; } = new List<(string TenantId, string SessionId)>();
        public long DurationMs { get; set; }
    }

    /// <summary>Counters of one sweep run; the Completed ops event carries them verbatim.</summary>
    public sealed class OrphanSweepRunResult
    {
        public int HandlesScanned { get; set; }
        public int SessionKeys { get; set; }
        public int Candidates { get; set; }
        public List<OrphanSweepCleanedSession> Cleaned { get; } = new List<OrphanSweepCleanedSession>();
        public int SkippedRowReturned { get; set; }
        public int SkippedHandleGone { get; set; }
        public int SkippedHandleChanged { get; set; }
        public int AfterCascade { get; set; }
        public int Remaining { get; set; }
        public int FailedSessions { get; set; }
        public int InventoryDecrementsFailed { get; set; }
        public bool BudgetExhausted { get; set; }
        public long DurationMs { get; set; }
        public Dictionary<string, int> TotalsByTable { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public OrphanReconcileResult? Reconcile { get; set; }

        public void Add(OrphanSweepCleanedSession cleaned)
        {
            Cleaned.Add(cleaned);
            foreach (var kv in cleaned.RowsByTable)
                TotalsByTable[kv.Key] = TotalsByTable.TryGetValue(kv.Key, out var n) ? n + kv.Value : kv.Value;
            Bump(Constants.TableNames.SessionInventoryContributions, cleaned.HadInventorySideRow ? 1 : 0);
            Bump(Constants.TableNames.SoftwareInventory, cleaned.InventoryKeysDecremented);
            Bump(Constants.TableNames.EventSessionIndex, cleaned.HandleKept ? 0 : 1);
        }

        private void Bump(string table, int by)
            => TotalsByTable[table] = TotalsByTable.TryGetValue(table, out var n) ? n + by : by;
    }
}
