using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Per-tenant retention dispatch + rate-limit tests for <see cref="SessionRetentionFanoutService"/>.
/// </summary>
public class SessionRetentionFanoutServiceTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public async Task RunAsync_enqueues_v2_cascade_for_old_sessions()
    {
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30,
            sessions: new[] { Old("s1", 60), Old("s2", 45) });

        var result = await harness.RunAsync();

        Assert.Equal(2, result.SessionsEnqueued);
        harness.Enqueuer.Verify(
            e => e.EnqueueAsync(TenantA, "s1", "retention_cutoff", It.Is<DeletionActor>(a => a.Type == "maintenance"), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        harness.Enqueuer.Verify(
            e => e.EnqueueAsync(TenantA, "s2", "retention_cutoff", It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_handles_two_tenants_with_different_retentions()
    {
        var harness = new Harness();
        // Tenant A: 30d retention → eligible at 45d
        harness.WithTenant(TenantA, retentionDays: 30, sessions: new[] { Old("a1", 45) });
        // Tenant B: 120d retention — above the Community cap (90), so B must be Enterprise for
        // the 120d cutoff to remain effective (Community would be clamped to 90).
        harness.WithTenant(TenantB, retentionDays: 120, sessions: new[] { Old("b1", 45), Old("b2", 200) }, planTier: "enterprise");

        // The repo only returns sessions older than each cutoff. Wire each tenant's mock to honor that.
        harness.MaintenanceRepo.Setup(m => m.GetSessionsOlderThanAsync(TenantA, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<SessionSummary> { Summary(TenantA, "a1") });
        harness.MaintenanceRepo.Setup(m => m.GetSessionsOlderThanAsync(TenantB, It.Is<DateTime>(d => d <= DateTime.UtcNow.AddDays(-120)), It.IsAny<int>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<SessionSummary> { Summary(TenantB, "b2") });

        var result = await harness.RunAsync();

        Assert.Equal(2, result.TenantsProcessed);
        Assert.Equal(2, result.SessionsEnqueued);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "a1", "retention_cutoff", It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantB, "b2", "retention_cutoff", It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        // b1 must NOT be enqueued — its session age is below the 120d cutoff for Tenant B.
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantB, "b1", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_enforces_rate_limit_per_tenant_per_run()
    {
        var harness = new Harness();
        var many = new List<SessionSummary>();
        for (int i = 0; i < SessionRetentionFanoutService.MaxEnqueuesPerTenantPerRun + 25; i++)
            many.Add(Summary(TenantA, $"s{i:000}"));
        harness.WithTenantOverride(TenantA, retentionDays: 30, sessions: many);

        var result = await harness.RunAsync();

        Assert.Equal(SessionRetentionFanoutService.MaxEnqueuesPerTenantPerRun, result.SessionsEnqueued);
        Assert.Equal(1, result.RateLimitedTenants);
        // The 101st session onward must NOT have been touched.
        harness.Enqueuer.Verify(
            e => e.EnqueueAsync(TenantA, "s100", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RunAsync_reads_session_backlog_server_bounded_to_cap_plus_one()
    {
        // Codex HIGH fix: the fanout must NOT materialize the whole backlog. It reads at most
        // MaxEnqueuesPerTenantPerRun+1 (the +1 is the "more remaining" probe) so a large backlog
        // is no longer reread+rematerialized every run while only 100 sessions advance.
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30, sessions: new[] { Old("s1", 60) });

        await harness.RunAsync();

        harness.MaintenanceRepo.Verify(
            m => m.GetSessionsOlderThanAsync(
                TenantA,
                It.IsAny<DateTime>(),
                SessionRetentionFanoutService.MaxEnqueuesPerTenantPerRun + 1,
                // In-flight deletions must be excluded from the capped head, otherwise ≥cap
                // stuck sessions (Poisoned / stranded Queued) starve the tail on every run.
                true),
            Times.Once);
    }

    [Fact]
    public async Task RunAsync_skips_tenant_with_DataRetentionDays_zero()
    {
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 0, sessions: new[] { Old("s1", 365) });

        var result = await harness.RunAsync();

        Assert.Equal(0, result.SessionsEnqueued);
        // GetSessionsOlderThanAsync must not have been called for that tenant.
        harness.MaintenanceRepo.Verify(m => m.GetSessionsOlderThanAsync(TenantA, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_clamps_community_retention_to_90_days()
    {
        // Edition retention cap (fail-closed backstop): a Community tenant with a stored value
        // above the cap (e.g. legacy 180d, or a trial that expired) is enforced at 90 — the
        // cutoff passed to storage must be ~90 days, not the stored 180.
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 180, sessions: new[] { Old("s1", 100) });

        await harness.RunAsync();

        harness.MaintenanceRepo.Verify(m => m.GetSessionsOlderThanAsync(
            TenantA,
            It.Is<DateTime>(d => d > DateTime.UtcNow.AddDays(-91) && d <= DateTime.UtcNow.AddDays(-89)),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_does_not_clamp_enterprise_retention_within_365()
    {
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 180, sessions: new[] { Old("s1", 200) }, planTier: "enterprise");

        await harness.RunAsync();

        harness.MaintenanceRepo.Verify(m => m.GetSessionsOlderThanAsync(
            TenantA,
            It.Is<DateTime>(d => d > DateTime.UtcNow.AddDays(-181) && d <= DateTime.UtcNow.AddDays(-179)),
            It.IsAny<int>(), It.IsAny<bool>()), Times.Once);
    }

    // ────────────────────────────────────────────────────────────────────────── PR6 follow-up F2 ─

    [Fact]
    public async Task RunAsync_aborts_mid_loop_when_kill_switch_flips_on()
    {
        // PR6 follow-up F2: per-session kill-switch check halts the fanout immediately when the
        // emergency switch flips, instead of finishing the rest of this tenant's backlog.
        var harness = new Harness();
        var manySessions = new List<SessionSummary>();
        for (int i = 0; i < 5; i++) manySessions.Add(Summary(TenantA, $"s{i}"));
        harness.WithTenantOverride(TenantA, retentionDays: 30, sessions: manySessions);

        // First 2 calls return false (kill-switch off), then true → fanout aborts at the 3rd iteration.
        var calls = 0;
        harness.AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync())
            .Returns(() =>
            {
                calls++;
                // Sequence: tenant-entry probe (call 1, false), then per-session probes:
                // session 0 (call 2, false), session 1 (call 3, false), session 2 (call 4, TRUE — abort).
                return Task.FromResult(calls >= 4);
            });

        var result = await harness.RunAsync();

        Assert.True(result.AbortedByKillSwitch);
        Assert.Equal(2, result.SessionsEnqueued); // only sessions 0 and 1 made it through
        // Confirm only the first two enqueues fired.
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "s0", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "s1", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "s2", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "s4", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_aborts_at_tenant_boundary_when_kill_switch_flips_between_tenants()
    {
        // Kill-switch flips on between tenant A and tenant B → tenant B is never started.
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30, sessions: new[] { Old("a1", 60) });
        harness.WithTenant(TenantB, retentionDays: 30, sessions: new[] { Old("b1", 60) });

        var calls = 0;
        harness.AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync())
            .Returns(() =>
            {
                calls++;
                // Tenant A: entry probe (call 1, false), session probe (call 2, false), enqueue runs.
                // Tenant B: entry probe (call 3, TRUE — abort).
                return Task.FromResult(calls >= 3);
            });

        var result = await harness.RunAsync();

        Assert.True(result.AbortedByKillSwitch);
        Assert.Equal(1, result.TenantsProcessed);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "a1", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Once);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantB, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_aborts_when_producer_reports_KillSwitchActive_outcome()
    {
        // Race scenario: pre-check (per-tenant + per-session) returns false because the
        // operator hadn't flipped yet, the producer's own Step 0 check returns true because
        // the flip landed in between. The fanout must NOT keep pushing the rest of the
        // tenant's backlog through a producer that's already 503'ing every call.
        var harness = new Harness();
        var sessions = new List<SessionSummary>();
        for (int i = 0; i < 4; i++) sessions.Add(Summary(TenantA, $"s{i}"));
        harness.WithTenantOverride(TenantA, retentionDays: 30, sessions: sessions);

        // First enqueue returns Enqueued, second returns KillSwitchActive (producer flipped).
        var enqueueCalls = 0;
        harness.Enqueuer.Setup(e => e.EnqueueAsync(TenantA, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                enqueueCalls++;
                return enqueueCalls == 1
                    ? new SessionDeletionEnqueueResult { Outcome = SessionDeletionEnqueueOutcome.Enqueued, ManifestId = "M" }
                    : new SessionDeletionEnqueueResult { Outcome = SessionDeletionEnqueueOutcome.KillSwitchActive };
            });

        var result = await harness.RunAsync();

        Assert.True(result.AbortedByKillSwitch);
        Assert.Equal(1, result.SessionsEnqueued);
        Assert.Equal(1, result.SessionsSkipped); // the KillSwitchActive session counts as skipped
        // s2 and s3 must NOT be enqueued — inner loop broke at s1.
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "s2", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "s3", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_aborts_outer_loop_when_producer_reports_KillSwitchActive_for_tenant_A()
    {
        // Two-tenant regression: tenant A's second enqueue returns KillSwitchActive. The
        // outer loop must NOT advance to tenant B — that would push at least one more
        // request through the producer despite the authoritative fail-closed outcome.
        var harness = new Harness();
        harness.WithTenantOverride(TenantA, retentionDays: 30, sessions: new List<SessionSummary>
        {
            Summary(TenantA, "a0"),
            Summary(TenantA, "a1"),
        });
        harness.WithTenant(TenantB, retentionDays: 30, sessions: new[] { Old("b0", 60) });

        // Tenant A: first enqueue Enqueued, second KillSwitchActive.
        // Tenant B: would-be Enqueued (default) — must NEVER be called.
        var aCalls = 0;
        harness.Enqueuer.Setup(e => e.EnqueueAsync(TenantA, It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                aCalls++;
                return aCalls == 1
                    ? new SessionDeletionEnqueueResult { Outcome = SessionDeletionEnqueueOutcome.Enqueued, ManifestId = "M" }
                    : new SessionDeletionEnqueueResult { Outcome = SessionDeletionEnqueueOutcome.KillSwitchActive };
            });

        var result = await harness.RunAsync();

        Assert.True(result.AbortedByKillSwitch);
        Assert.Equal(1, result.TenantsProcessed); // tenant A completed its RunForTenantAsync; B never started
        Assert.Equal(1, result.SessionsEnqueued);

        // Tenant B must NEVER have been touched — neither config-read nor enqueue.
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantB, It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        harness.MaintenanceRepo.Verify(m => m.GetSessionsOlderThanAsync(TenantB, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    // ───────────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_counts_AlreadyInFlight_outcome_as_skip()
    {
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30, sessions: new[] { Old("s1", 60) });
        // Another producer already claimed this session — fanout must not double-count.
        harness.Enqueuer.Setup(e => e.EnqueueAsync(TenantA, "s1", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionDeletionEnqueueResult
            {
                Outcome = SessionDeletionEnqueueOutcome.AlreadyInFlight,
                ManifestId = "OTHER-MANIFEST",
                ExistingState = SessionDeletionState.Running,
            });

        var result = await harness.RunAsync();

        Assert.Equal(0, result.SessionsEnqueued);
        Assert.Equal(1, result.SessionsSkipped);
    }

    // ────────────────────────────────────────────────────────────────────────── Run budget ─

    [Fact]
    public async Task RunAsync_aborts_immediately_when_deadline_already_passed()
    {
        var harness = new Harness();
        harness.WithTenant(TenantA, retentionDays: 30, sessions: new[] { Old("s1", 60) });

        var result = await harness.RunAsync(deadlineUtc: DateTime.UtcNow.AddMinutes(-1));

        Assert.True(result.AbortedByBudget);
        Assert.Equal(0, result.TenantsProcessed);
        Assert.Equal(0, result.SessionsEnqueued);
        harness.MaintenanceRepo.Verify(m => m.GetSessionsOlderThanAsync(TenantA, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_aborts_mid_tenant_when_budget_deadline_crosses()
    {
        // Scripted clock: the deadline crosses between session 1 and session 2 of tenant A —
        // the remaining sessions of A and all of tenant B must be left untouched.
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var deadline = baseTime.AddMinutes(50);
        var clockCalls = 0;
        // Call sequence: tenant-A boundary (1), session s0 (2), session s1 (3), session s2 (4 → past deadline).
        var harness = new Harness(utcNow: () => ++clockCalls >= 4 ? deadline.AddMinutes(1) : baseTime);

        var sessions = new List<SessionSummary>();
        for (int i = 0; i < 4; i++) sessions.Add(Summary(TenantA, $"s{i}"));
        harness.WithTenantOverride(TenantA, retentionDays: 30, sessions: sessions);
        harness.WithTenant(TenantB, retentionDays: 30, sessions: new[] { Old("b1", 60) });

        var result = await harness.RunAsync(deadlineUtc: deadline);

        Assert.True(result.AbortedByBudget);
        Assert.Equal(2, result.SessionsEnqueued); // only s0 and s1 made it through
        Assert.Equal(1, result.TenantsProcessed); // tenant A returned normally, B never started
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantA, "s2", It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.Enqueuer.Verify(e => e.EnqueueAsync(TenantB, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()), Times.Never);
        harness.MaintenanceRepo.Verify(m => m.GetSessionsOlderThanAsync(TenantB, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<bool>()), Times.Never);
    }

    [Fact]
    public async Task RunAsync_mutates_the_shared_result_incrementally_during_the_run()
    {
        // Watchdog-progress contract: the caller-supplied FanoutResult must be bumped per
        // session (not once at the end), so mid-run snapshots show real progress. The injected
        // throttle runs between sessions — snapshot the counter there.
        var snapshots = new List<int>();
        var result = new SessionRetentionFanoutService.FanoutResult();
        var harness = new Harness(throttle: (_, _) =>
        {
            snapshots.Add(result.SessionsEnqueued);
            return Task.CompletedTask;
        });
        var sessions = new List<SessionSummary>();
        for (int i = 0; i < 3; i++) sessions.Add(Summary(TenantA, $"s{i}"));
        harness.WithTenantOverride(TenantA, retentionDays: 30, sessions: sessions);

        await harness.Sut.RunAsync(result, DateTime.UtcNow.AddMinutes(50), CancellationToken.None);

        Assert.Equal(3, result.SessionsEnqueued);
        // Throttle fires after s0 and s1 (not after the last session) — each snapshot must
        // already reflect the enqueues so far.
        Assert.Equal(new[] { 1, 2 }, snapshots);
    }

    // ============================================================ Helpers ====

    private static SessionSummary Summary(string tenantId, string sessionId) =>
        new() { TenantId = tenantId, SessionId = sessionId };

    private static SessionSummary Old(string sessionId, int ageDays)
    {
        return new SessionSummary
        {
            SessionId = sessionId,
            StartedAt = DateTime.UtcNow.AddDays(-ageDays),
        };
    }

    // ============================================================ Harness ====

    private sealed class Harness
    {
        public Mock<IMaintenanceRepository> MaintenanceRepo { get; }
        public Mock<TenantConfigurationService> TenantConfig { get; }
        public Mock<ISessionDeletionEnqueuer> Enqueuer { get; }
        public Mock<AdminConfigurationService> AdminConfig { get; }
        public SessionRetentionFanoutService Sut { get; }

        private readonly List<string> _tenantIds = new();

        public Harness(Func<TimeSpan, CancellationToken, Task>? throttle = null, Func<DateTime>? utcNow = null)
        {
            MaintenanceRepo = new Mock<IMaintenanceRepository>();
            var memCache = new MemoryCache(new MemoryCacheOptions());
            TenantConfig = new Mock<TenantConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, memCache);
            Enqueuer = new Mock<ISessionDeletionEnqueuer>();

            // PR6 follow-up F2: per-session kill-switch check inside the fanout loop.
            AdminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, memCache);
            AdminConfig.Setup(a => a.IsSessionDeletionKillSwitchActiveAsync()).ReturnsAsync(false);

            // Default: every enqueue succeeds.
            Enqueuer.Setup(e => e.EnqueueAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<DeletionActor>(), It.IsAny<DeletionRetentionContext?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SessionDeletionEnqueueResult
                {
                    Outcome = SessionDeletionEnqueueOutcome.Enqueued,
                    ManifestId = Guid.NewGuid().ToString("N"),
                });

            // Default: maintenance repo returns the registered tenants.
            MaintenanceRepo.Setup(m => m.GetAllTenantIdsAsync()).ReturnsAsync(() => new List<string>(_tenantIds));
            MaintenanceRepo.Setup(m => m.LogAuditEntryAsync(
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Dictionary<string, string>?>()))
                .ReturnsAsync(true);

            // Use internal ctor to inject a no-op throttle so the rate-limit test runs in real-time
            // (50ms × 100 = 5s otherwise) and an optional scripted clock for the budget tests.
            Sut = new SessionRetentionFanoutService(
                MaintenanceRepo.Object, TenantConfig.Object, Enqueuer.Object,
                AdminConfig.Object,
                NullLogger<SessionRetentionFanoutService>.Instance,
                throttle: throttle ?? ((_, _) => Task.CompletedTask),
                utcNow: utcNow);
        }

        /// <summary>
        /// Drives <see cref="SessionRetentionFanoutService.RunAsync"/> with a fresh result object
        /// and a far-away default deadline (the budget is exercised by dedicated tests).
        /// </summary>
        public async Task<SessionRetentionFanoutService.FanoutResult> RunAsync(DateTime? deadlineUtc = null)
        {
            var result = new SessionRetentionFanoutService.FanoutResult();
            await Sut.RunAsync(result, deadlineUtc ?? DateTime.UtcNow.AddMinutes(50), CancellationToken.None);
            return result;
        }

        public void WithTenant(string tenantId, int retentionDays, SessionSummary[] sessions, string planTier = "free")
        {
            _tenantIds.Add(tenantId);
            TenantConfig.Setup(t => t.GetConfigurationAsync(tenantId))
                .ReturnsAsync(new TenantConfiguration { TenantId = tenantId, DataRetentionDays = retentionDays, PlanTier = planTier });
            MaintenanceRepo.Setup(m => m.GetSessionsOlderThanAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<bool>()))
                .ReturnsAsync(new List<SessionSummary>(WithTenantId(tenantId, sessions)));
        }

        public void WithTenantOverride(string tenantId, int retentionDays, List<SessionSummary> sessions)
        {
            _tenantIds.Add(tenantId);
            TenantConfig.Setup(t => t.GetConfigurationAsync(tenantId))
                .ReturnsAsync(new TenantConfiguration { TenantId = tenantId, DataRetentionDays = retentionDays });
            foreach (var s in sessions) s.TenantId = tenantId;
            MaintenanceRepo.Setup(m => m.GetSessionsOlderThanAsync(tenantId, It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<bool>())).ReturnsAsync(sessions);
        }

        private static IEnumerable<SessionSummary> WithTenantId(string tenantId, IEnumerable<SessionSummary> sessions)
        {
            foreach (var s in sessions) { s.TenantId = tenantId; yield return s; }
        }
    }
}
