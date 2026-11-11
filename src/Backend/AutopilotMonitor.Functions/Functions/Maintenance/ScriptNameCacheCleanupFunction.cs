using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Maintenance;

/// <summary>
/// Daily housekeeping for the <c>ScriptNameCache</c> table that backs Intune script
/// display-name resolution (Graph Add-On feature).
/// <list type="bullet">
///   <item>Positive (found) rows older than <see cref="PositiveTtl"/> are deleted so a
///         renamed Intune script eventually surfaces under its new name.</item>
///   <item>Negative (NotFound) rows live just as long (<see cref="NotFoundTtl"/>): they
///         suppress repeated 404 fetches for an ID Graph does not know. A list pull that
///         contains the ID after all overwrites the row.</item>
///   <item>Per-tenant meta rows (<c>"{Kind}_$meta"</c>) live forever; they're tiny and
///         steer the resolver's "do a full pull vs. per-ID fallback" decision.</item>
/// </list>
/// Failure on a single row is logged and skipped — the timer never poisons. Next-day
/// run picks up whatever was missed.
/// </summary>
public class ScriptNameCacheCleanupFunction
{
    /// <summary>Positive entries are usable for 7 days; covers all reasonable troubleshooting timeframes.</summary>
    public static readonly TimeSpan PositiveTtl = TimeSpan.FromDays(7);

    /// <summary>
    /// Same lifetime as a name. A shorter one asks Graph again for every unknown ID on the
    /// first view after each cleanup run.
    /// </summary>
    public static readonly TimeSpan NotFoundTtl = PositiveTtl;

    /// <summary>03:00 UTC daily — outside European working hours, off the busy maintenance window.</summary>
    private const string Cron = "0 0 3 * * *";

    private readonly IScriptNameCacheRepository _repo;
    private readonly ILogger<ScriptNameCacheCleanupFunction> _logger;
    private readonly TimeProvider _time;

    public ScriptNameCacheCleanupFunction(
        IScriptNameCacheRepository repo,
        ILogger<ScriptNameCacheCleanupFunction> logger)
        : this(repo, logger, TimeProvider.System)
    {
    }

    /// <summary>Test seam — inject a fake <see cref="TimeProvider"/> for deterministic TTL math.</summary>
    internal ScriptNameCacheCleanupFunction(
        IScriptNameCacheRepository repo,
        ILogger<ScriptNameCacheCleanupFunction> logger,
        TimeProvider time)
    {
        _repo = repo;
        _logger = logger;
        _time = time;
    }

    [Function("ScriptNameCacheCleanup")]
    public async Task Run([TimerTrigger(Cron)] object timer, CancellationToken cancellationToken)
    {
        await RunCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Testable core — bypasses TimerInfo / FunctionContext.</summary>
    internal async Task<CleanupResult> RunCoreAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var positiveCutoff = now - PositiveTtl;
        var notFoundCutoff = now - NotFoundTtl;
        var result = new CleanupResult();

        await foreach (var entry in _repo.QueryExpiredAsync(positiveCutoff, notFoundCutoff, ct))
        {
            ct.ThrowIfCancellationRequested();
            result.Scanned++;

            try
            {
                await _repo.DeleteAsync(entry.TenantId, entry.Kind, entry.ScriptId, ct).ConfigureAwait(false);
                if (entry.IsNotFound) result.NotFoundDeleted++;
                else result.PositiveDeleted++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ScriptNameCacheCleanup: delete failed for tenant={TenantId} kind={Kind} id={ScriptId} — will retry next run",
                    entry.TenantId, entry.Kind, entry.ScriptId);
                result.Failures++;
            }
        }

        _logger.LogInformation(
            "ScriptNameCacheCleanup completed: scanned={Scanned} positiveDeleted={PositiveDeleted} notFoundDeleted={NotFoundDeleted} failures={Failures}",
            result.Scanned, result.PositiveDeleted, result.NotFoundDeleted, result.Failures);
        return result;
    }

    /// <summary>Summary counters returned from <see cref="RunCoreAsync"/> so tests can assert outcomes.</summary>
    internal sealed class CleanupResult
    {
        public int Scanned { get; set; }
        public int PositiveDeleted { get; set; }
        public int NotFoundDeleted { get; set; }
        public int Failures { get; set; }
    }
}
