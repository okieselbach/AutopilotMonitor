using System;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Maintenance;

/// <summary>
/// Daily informational sweep over the tenant plan lifecycle. NOT load-bearing — trial expiry
/// degrades a tenant to Community at READ time (FeatureEntitlementCatalog.ResolveEdition),
/// and the retention grace is likewise resolved read-time by the retention fanout; this timer
/// only surfaces the transitions as ops events so the operator has visibility:
/// <list type="bullet">
///   <item><c>TenantTrialExpired</c> (Warning) — the trial ended within the last sweep window
///         (24h look-back matching the daily cadence).</item>
///   <item><c>TenantTrialExpiring</c> (Info) — the trial ends within the next
///         <see cref="ExpiringHeadsUpDays"/> days (re-emitted each daily run until expiry —
///         acceptable for an Info-tier heads-up, no dedupe state to maintain).</item>
///   <item><c>TenantRetentionGraceExpiring</c> / <c>TenantRetentionGraceEnded</c> (Warning) —
///         same window mechanics for the retention downgrade grace
///         (TenantEntitlementService.GetRetentionGraceEndUtc). Emitted only when data is
///         actually at risk: stored retention above the Community cap (0 = infinite ⇒ never
///         at risk, the fanout skips those tenants).</item>
/// </list>
/// Tenants whose stored <c>PlanTier</c> is already a permanent Pro tier ("pro"/legacy "enterprise") are skipped —
/// their trial timestamps are inert leftovers and expiry changes nothing.
/// Failure never poisons the timer; the next run re-sweeps.
/// </summary>
public class TrialExpirySweepFunction
{
    /// <summary>Heads-up window for TenantTrialExpiring.</summary>
    public static readonly TimeSpan ExpiringHeadsUp = TimeSpan.FromDays(3);
    private const int ExpiringHeadsUpDays = 3;

    /// <summary>Look-back for TenantTrialExpired — matches the daily cadence so no expiry is skipped.</summary>
    public static readonly TimeSpan ExpiredLookBack = TimeSpan.FromHours(24);

    /// <summary>03:30 UTC daily — off the busy maintenance window, after ScriptNameCacheCleanup (03:00).</summary>
    private const string Cron = "0 30 3 * * *";

    private readonly IConfigRepository _configRepo;
    private readonly OpsEventService _opsEvents;
    private readonly ILogger<TrialExpirySweepFunction> _logger;
    private readonly TimeProvider _time;

    public TrialExpirySweepFunction(
        IConfigRepository configRepo,
        OpsEventService opsEvents,
        ILogger<TrialExpirySweepFunction> logger)
        : this(configRepo, opsEvents, logger, TimeProvider.System)
    {
    }

    /// <summary>Test seam — inject a fake <see cref="TimeProvider"/> for deterministic window math.</summary>
    internal TrialExpirySweepFunction(
        IConfigRepository configRepo,
        OpsEventService opsEvents,
        ILogger<TrialExpirySweepFunction> logger,
        TimeProvider time)
    {
        _configRepo = configRepo;
        _opsEvents = opsEvents;
        _logger = logger;
        _time = time;
    }

    [Function("TrialExpirySweep")]
    public async Task Run([TimerTrigger(Cron)] object timer, CancellationToken cancellationToken)
    {
        await RunCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Testable core — bypasses TimerInfo / FunctionContext.</summary>
    internal async Task<SweepResult> RunCoreAsync(CancellationToken ct)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var result = new SweepResult();

        System.Collections.Generic.List<AutopilotMonitor.Shared.Models.TenantConfiguration> configs;
        try
        {
            configs = await _configRepo.GetAllTenantConfigurationsAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Informational sweep — never poison the timer; the next daily run re-sweeps.
            _logger.LogError(ex, "TrialExpirySweep: failed to load tenant configurations — skipping this run");
            return result;
        }

        foreach (var config in configs)
        {
            ct.ThrowIfCancellationRequested();

            await SweepTrialAsync(config, now, result).ConfigureAwait(false);
            await SweepRetentionGraceAsync(config, now, result).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "TrialExpirySweep completed: trialsSeen={TrialsSeen} expiredEmitted={ExpiredEmitted} expiringEmitted={ExpiringEmitted} " +
            "graceExpiringEmitted={GraceExpiringEmitted} graceEndedEmitted={GraceEndedEmitted}",
            result.TrialsSeen, result.ExpiredEmitted, result.ExpiringEmitted,
            result.GraceExpiringEmitted, result.GraceEndedEmitted);
        return result;
    }

    private async Task SweepTrialAsync(
        AutopilotMonitor.Shared.Models.TenantConfiguration config, DateTime now, SweepResult result)
    {
        if (config.TrialExpiresUtc is not DateTime expiry)
            return;

        // Permanent Pro: the trial timestamps are inert leftovers — expiry changes nothing.
        if (FeatureEntitlementCatalog.IsPermanentProTier(config.PlanTier))
            return;

        result.TrialsSeen++;

        if (expiry <= now)
        {
            if (expiry > now - ExpiredLookBack)
            {
                await _opsEvents.RecordTenantTrialExpiredAsync(config.TenantId, config.DomainName, expiry)
                    .ConfigureAwait(false);
                result.ExpiredEmitted++;
            }
            // Older expiries were reported by a previous run — stay silent.
        }
        else if (expiry <= now + ExpiringHeadsUp)
        {
            var daysLeft = Math.Max(1, (int)Math.Ceiling((expiry - now).TotalDays));
            await _opsEvents.RecordTenantTrialExpiringAsync(config.TenantId, config.DomainName, expiry, daysLeft)
                .ConfigureAwait(false);
            result.ExpiringEmitted++;
        }
    }

    /// <summary>
    /// Retention downgrade grace visibility: warn shortly before and once after the grace ends,
    /// but only when data is actually at risk — stored retention above the Community cap.
    /// (0 = the GA-only infinite escape hatch: the fanout skips those tenants, nothing is ever
    /// deleted, so the comparison excludes it naturally.) GetRetentionGraceEndUtc returns null
    /// for effectively-Pro tenants, so re-upgraded tenants go silent automatically.
    /// </summary>
    private async Task SweepRetentionGraceAsync(
        AutopilotMonitor.Shared.Models.TenantConfiguration config, DateTime now, SweepResult result)
    {
        var communityCap = FeatureEntitlementCatalog.Get(TenantEdition.Community).RetentionCapDays;
        if (config.DataRetentionDays <= communityCap)
            return;

        if (TenantEntitlementService.GetRetentionGraceEndUtc(config, now) is not DateTime graceEnd)
            return;

        if (graceEnd <= now)
        {
            if (graceEnd > now - ExpiredLookBack)
            {
                await _opsEvents.RecordTenantRetentionGraceEndedAsync(
                        config.TenantId, config.DomainName, graceEnd, config.DataRetentionDays)
                    .ConfigureAwait(false);
                result.GraceEndedEmitted++;
            }
        }
        else if (graceEnd <= now + ExpiringHeadsUp)
        {
            var daysLeft = Math.Max(1, (int)Math.Ceiling((graceEnd - now).TotalDays));
            await _opsEvents.RecordTenantRetentionGraceExpiringAsync(
                    config.TenantId, config.DomainName, graceEnd, daysLeft, config.DataRetentionDays)
                .ConfigureAwait(false);
            result.GraceExpiringEmitted++;
        }
    }

    /// <summary>Summary counters returned from <see cref="RunCoreAsync"/> so tests can assert outcomes.</summary>
    internal sealed class SweepResult
    {
        public int TrialsSeen { get; set; }
        public int ExpiredEmitted { get; set; }
        public int ExpiringEmitted { get; set; }
        public int GraceExpiringEmitted { get; set; }
        public int GraceEndedEmitted { get; set; }
    }
}
