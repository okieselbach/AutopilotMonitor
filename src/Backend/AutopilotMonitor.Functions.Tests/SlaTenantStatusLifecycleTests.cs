using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Lifecycle tests for <see cref="SlaBreachEvaluationService"/> + <see cref="SlaTenantStatus"/>.
/// Locks the persistent-state semantics: one row per tenant; per-breach-type fields drive
/// throttle + resolve detection; cooldown comes from AdminConfiguration.SlaNotificationCooldownHours.
/// </summary>
public class SlaTenantStatusLifecycleTests
{
    private const string TenantId = "tenant-aaa-bbb-ccc";

    // ── Fakes / helpers ─────────────────────────────────────────────────────

    private sealed class InMemorySlaStatusRepository : ISlaTenantStatusRepository
    {
        private readonly Dictionary<string, (SlaTenantStatus Row, long Version)> _rows = new(StringComparer.OrdinalIgnoreCase);
        private long _nextVersion = 1;
        public int ConflictsToInject { get; set; } = 0;

        public Task<SlaTenantStatus?> GetAsync(string tenantId)
        {
            return Task.FromResult(_rows.TryGetValue(tenantId, out var entry) ? Clone(entry.Row) : null);
        }

        public Task<(SlaTenantStatus? Status, string? ETag)> GetWithETagAsync(string tenantId)
        {
            if (_rows.TryGetValue(tenantId, out var entry))
                return Task.FromResult<(SlaTenantStatus?, string?)>((Clone(entry.Row), entry.Version.ToString()));
            return Task.FromResult<(SlaTenantStatus?, string?)>((null, null));
        }

        public Task<bool> UpsertAsync(SlaTenantStatus status)
        {
            _rows[status.TenantId] = (Clone(status), _nextVersion++);
            return Task.FromResult(true);
        }

        public Task<bool> TryUpsertAsync(SlaTenantStatus status, string? ifMatchETag)
        {
            if (ConflictsToInject > 0)
            {
                ConflictsToInject--;
                // Bump the underlying version to invalidate the caller's ETag, simulating a parallel writer.
                if (_rows.TryGetValue(status.TenantId, out var existing))
                    _rows[status.TenantId] = (existing.Row, _nextVersion++);
                return Task.FromResult(false);
            }

            if (ifMatchETag is null)
            {
                if (_rows.ContainsKey(status.TenantId)) return Task.FromResult(false);
                _rows[status.TenantId] = (Clone(status), _nextVersion++);
                return Task.FromResult(true);
            }

            if (!_rows.TryGetValue(status.TenantId, out var entry)) return Task.FromResult(false);
            if (entry.Version.ToString() != ifMatchETag) return Task.FromResult(false);

            _rows[status.TenantId] = (Clone(status), _nextVersion++);
            return Task.FromResult(true);
        }

        public Task<List<SlaTenantStatus>> ListAllActiveAsync()
            => Task.FromResult(_rows.Values.Where(e => e.Row.IsAnyTypeActive()).Select(e => Clone(e.Row)).ToList());

        public Task<List<SlaTenantStatus>> ListAllAsync()
            => Task.FromResult(_rows.Values.Select(e => Clone(e.Row)).ToList());

        private static SlaTenantStatus Clone(SlaTenantStatus s) => new()
        {
            TenantId = s.TenantId,
            LastEvaluatedAt = s.LastEvaluatedAt,
            SuccessRate_IsActive = s.SuccessRate_IsActive,
            SuccessRate_CurrentValue = s.SuccessRate_CurrentValue,
            SuccessRate_TargetValue = s.SuccessRate_TargetValue,
            SuccessRate_ThresholdValue = s.SuccessRate_ThresholdValue,
            SuccessRate_TotalSessions = s.SuccessRate_TotalSessions,
            SuccessRate_FailedSessions = s.SuccessRate_FailedSessions,
            SuccessRate_FirstBreachAt = s.SuccessRate_FirstBreachAt,
            SuccessRate_LastBreachAt = s.SuccessRate_LastBreachAt,
            SuccessRate_LastNotifiedAt = s.SuccessRate_LastNotifiedAt,
            SuccessRate_ResolvedAt = s.SuccessRate_ResolvedAt,
            Duration_IsActive = s.Duration_IsActive,
            Duration_CurrentP95Minutes = s.Duration_CurrentP95Minutes,
            Duration_TargetMinutes = s.Duration_TargetMinutes,
            Duration_TotalSessions = s.Duration_TotalSessions,
            Duration_FirstBreachAt = s.Duration_FirstBreachAt,
            Duration_LastBreachAt = s.Duration_LastBreachAt,
            Duration_LastNotifiedAt = s.Duration_LastNotifiedAt,
            Duration_ResolvedAt = s.Duration_ResolvedAt,
            AppInstall_IsActive = s.AppInstall_IsActive,
            AppInstall_CurrentRate = s.AppInstall_CurrentRate,
            AppInstall_TargetRate = s.AppInstall_TargetRate,
            AppInstall_TopFailingApp = s.AppInstall_TopFailingApp,
            AppInstall_FirstBreachAt = s.AppInstall_FirstBreachAt,
            AppInstall_LastBreachAt = s.AppInstall_LastBreachAt,
            AppInstall_LastNotifiedAt = s.AppInstall_LastNotifiedAt,
            AppInstall_ResolvedAt = s.AppInstall_ResolvedAt,
            ConsecutiveFailures_IsActive = s.ConsecutiveFailures_IsActive,
            ConsecutiveFailures_Count = s.ConsecutiveFailures_Count,
            ConsecutiveFailures_LastDevice = s.ConsecutiveFailures_LastDevice,
            ConsecutiveFailures_LastReason = s.ConsecutiveFailures_LastReason,
            ConsecutiveFailures_FirstAt = s.ConsecutiveFailures_FirstAt,
            ConsecutiveFailures_LastNotifiedAt = s.ConsecutiveFailures_LastNotifiedAt,
            ConsecutiveFailures_ResolvedAt = s.ConsecutiveFailures_ResolvedAt,
        };
    }

    private sealed class Harness
    {
        public InMemorySlaStatusRepository StatusRepo { get; } = new();
        public Mock<TenantNotificationService> NotificationService { get; }
        public SlaBreachEvaluationService Service { get; }
        public DateTime Now { get; }

        private readonly Mock<IConfigRepository> _configRepo = new();
        private readonly Mock<IMaintenanceRepository> _maintenanceRepo = new();
        private readonly Mock<ISessionRepository> _sessionRepo = new();
        private readonly Mock<IMetricsRepository> _metricsRepo = new();
        private readonly Mock<AdminConfigurationService> _adminConfig;

        public List<(string Type, string Title, string Message, string? Href)> NotificationsSent { get; } = new();

        public Harness(int cooldownHours = 24, DateTime? clock = null)
        {
            Now = clock ?? DateTime.UtcNow;
            var memCache = new MemoryCache(new MemoryCacheOptions());
            _adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, memCache);
            _adminConfig.Setup(a => a.GetConfigurationAsync())
                .ReturnsAsync(new AutopilotMonitor.Shared.Models.AdminConfiguration { SlaNotificationCooldownHours = cooldownHours });

            var tenantConfigService = new Mock<TenantConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, memCache);

            var notifRepo = new Mock<ITenantNotificationRepository>();
            notifRepo.Setup(r => r.AddNotificationAsync(It.IsAny<string>(), It.IsAny<GlobalNotification>()))
                .ReturnsAsync(true);
            NotificationService = new Mock<TenantNotificationService>(
                notifRepo.Object,
                new FakeSignalRNotificationService(),
                NullLogger<TenantNotificationService>.Instance);
            NotificationService
                .Setup(n => n.CreateNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
                .Returns<string, string, string, string, string?>((tenant, type, title, msg, href) =>
                {
                    NotificationsSent.Add((type, title, msg, href));
                    return Task.CompletedTask;
                });

            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>())).Returns(Task.CompletedTask);

            var webhook = new WebhookNotificationService(new HttpClient(), NullLogger<WebhookNotificationService>.Instance);
            var channelDispatcher = new NotificationChannelDispatcher(webhook, new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(), NullLogger<TelegramNotificationService>.Instance));
            var alertDispatch = TestNotifications.InertOpsAlertDispatch(_adminConfig.Object);
            var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

            var telemetry = new TelemetryClient(new TelemetryConfiguration());

            Service = new SlaBreachEvaluationService(
                tenantConfigService.Object,
                _configRepo.Object,
                _maintenanceRepo.Object,
                _sessionRepo.Object,
                _metricsRepo.Object,
                channelDispatcher,
                NotificationService.Object,
                StatusRepo,
                _adminConfig.Object,
                opsService,
                telemetry,
                NullLogger<SlaBreachEvaluationService>.Instance,
                () => Now);

            // Default: no consecutive failures (empty page)
            _sessionRepo.Setup(r => r.GetSessionsPageAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>()))
                .ReturnsAsync(new RawPage<SessionSummary>(new List<SessionSummary>(), null));
            // Default: no app installs
            _metricsRepo.Setup(r => r.GetAppInstallSummariesByTenantAsync(It.IsAny<string>(), It.IsAny<DateTime?>()))
                .ReturnsAsync(new List<AppInstallSummary>());
        }

        public void SetAppInstalls(string tenantId, List<AppInstallSummary> apps)
        {
            _metricsRepo.Setup(r => r.GetAppInstallSummariesByTenantAsync(tenantId, It.IsAny<DateTime?>())).ReturnsAsync(apps);
        }

        public void SetTenants(params TenantConfiguration[] tenants)
        {
            _configRepo.Setup(r => r.GetAllTenantConfigurationsAsync()).ReturnsAsync(tenants.ToList());
        }

        public void SetTerminalSessions(string tenantId, List<SessionSummary> sessions)
        {
            _maintenanceRepo.Setup(r => r.GetSessionsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), tenantId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(sessions);
        }

        public void SetRecentSessions(string tenantId, List<SessionSummary> sessions)
        {
            _sessionRepo.Setup(r => r.GetSessionsPageAsync(tenantId, It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>()))
                .ReturnsAsync(new RawPage<SessionSummary>(sessions, null));
        }
    }

    private static TenantConfiguration CreateConfig(
        bool successRate = false, bool duration = false, bool consecutive = false,
        decimal? targetSuccessRate = 95m, int? targetMaxDurationMinutes = 60, int consecThreshold = 3)
    {
        return new TenantConfiguration
        {
            TenantId = TenantId,
            SlaNotifyOnSuccessRateBreach = successRate,
            SlaTargetSuccessRate = successRate ? targetSuccessRate : null,
            SlaNotifyOnDurationBreach = duration,
            SlaTargetMaxDurationMinutes = duration ? targetMaxDurationMinutes : null,
            SlaNotifyOnConsecutiveFailures = consecutive,
            SlaConsecutiveFailureThreshold = consecThreshold,
        };
    }

    private static List<SessionSummary> Sessions(int succeeded, int failed, int? durationSec = 1800)
    {
        var list = new List<SessionSummary>();
        for (int i = 0; i < succeeded; i++)
            list.Add(new SessionSummary { TenantId = TenantId, SessionId = Guid.NewGuid().ToString(), Status = SessionStatus.Succeeded, DurationSeconds = durationSec });
        for (int i = 0; i < failed; i++)
            list.Add(new SessionSummary { TenantId = TenantId, SessionId = Guid.NewGuid().ToString(), Status = SessionStatus.Failed, DurationSeconds = durationSec });
        return list;
    }

    // ── Lifecycle scenarios ─────────────────────────────────────────────────

    [Fact]
    public async Task FirstBreach_SuccessRate_CreatesRowAndSendsNotification()
    {
        var h = new Harness();
        var config = CreateConfig(successRate: true);
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 8, failed: 4)); // 66.7% < 95%

        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.NotNull(row);
        Assert.True(row!.SuccessRate_IsActive);
        Assert.NotNull(row.SuccessRate_FirstBreachAt);
        Assert.NotNull(row.SuccessRate_LastNotifiedAt);
        Assert.Null(row.SuccessRate_ResolvedAt);
        Assert.Single(h.NotificationsSent, n => n.Type == "sla_breach");
    }

    [Fact]
    public async Task ReBreach_BeforeCooldown_DoesNotSendNotification()
    {
        var h = new Harness(cooldownHours: 24);
        var config = CreateConfig(successRate: true);
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 8, failed: 4));

        // First cycle: breach + notification
        await h.Service.EvaluateAllTenantsAsync();
        Assert.Single(h.NotificationsSent);
        var firstNotifiedAt = (await h.StatusRepo.GetAsync(TenantId))!.SuccessRate_LastNotifiedAt;

        // Second cycle, still breaching, well within 24h cooldown
        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.True(row!.SuccessRate_IsActive);
        Assert.Equal(firstNotifiedAt, row.SuccessRate_LastNotifiedAt); // NOT updated (no notification)
        Assert.NotNull(row.SuccessRate_LastBreachAt); // updated each cycle
        Assert.Single(h.NotificationsSent); // still only one notification
    }

    [Fact]
    public async Task ReBreach_AfterCooldown_SendsAnotherNotification()
    {
        var h = new Harness();
        var config = CreateConfig(successRate: true);
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 8, failed: 4));

        // First cycle
        await h.Service.EvaluateAllTenantsAsync();
        Assert.Single(h.NotificationsSent);

        // Backdate LastNotifiedAt to before the cooldown window
        var row = await h.StatusRepo.GetAsync(TenantId);
        row!.SuccessRate_LastNotifiedAt = DateTime.UtcNow.AddHours(-25);
        await h.StatusRepo.UpsertAsync(row);

        await h.Service.EvaluateAllTenantsAsync();

        Assert.Equal(2, h.NotificationsSent.Count(n => n.Type == "sla_breach"));
        var afterRow = await h.StatusRepo.GetAsync(TenantId);
        Assert.True((DateTime.UtcNow - afterRow!.SuccessRate_LastNotifiedAt!.Value).TotalMinutes < 1);
    }

    [Fact]
    public async Task BreachResolved_SetsIsActiveFalseAndSendsResolvedNotification()
    {
        var h = new Harness();
        var config = CreateConfig(successRate: true);
        h.SetTenants(config);

        // Cycle 1: breach
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 8, failed: 4));
        await h.Service.EvaluateAllTenantsAsync();
        Assert.Single(h.NotificationsSent, n => n.Type == "sla_breach");

        // Cycle 2: all green
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 12, failed: 0));
        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.False(row!.SuccessRate_IsActive);
        Assert.NotNull(row.SuccessRate_ResolvedAt);
        Assert.Single(h.NotificationsSent, n => n.Type == "sla_resolved");
    }

    [Fact]
    public async Task MixedBreach_SuccessRateAndDuration_PersistedTogether_TwoNotifications()
    {
        var h = new Harness();
        var config = CreateConfig(successRate: true, duration: true, targetMaxDurationMinutes: 30);
        h.SetTenants(config);
        // 8 succeeded + 4 failed at duration 1800s = 30min ; need P95 > 30min, so push some long ones
        var sessions = Sessions(succeeded: 8, failed: 4, durationSec: 1800);
        // Append some 60min sessions to push P95 over target
        for (int i = 0; i < 5; i++)
            sessions.Add(new SessionSummary { TenantId = TenantId, SessionId = Guid.NewGuid().ToString(), Status = SessionStatus.Succeeded, DurationSeconds = 3600 });
        h.SetTerminalSessions(TenantId, sessions);

        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.True(row!.SuccessRate_IsActive);
        Assert.True(row.Duration_IsActive);
        Assert.Equal(2, h.NotificationsSent.Count(n => n.Type == "sla_breach"));
    }

    [Fact]
    public async Task PartialResolve_OneTypeResolves_OtherStaysActive()
    {
        var h = new Harness();
        var config = CreateConfig(successRate: true, duration: true, targetMaxDurationMinutes: 30);
        h.SetTenants(config);

        // Cycle 1: both breach
        var initialSessions = Sessions(succeeded: 8, failed: 4, durationSec: 1800);
        for (int i = 0; i < 5; i++)
            initialSessions.Add(new SessionSummary { TenantId = TenantId, SessionId = Guid.NewGuid().ToString(), Status = SessionStatus.Succeeded, DurationSeconds = 3600 });
        h.SetTerminalSessions(TenantId, initialSessions);
        await h.Service.EvaluateAllTenantsAsync();
        Assert.Equal(2, h.NotificationsSent.Count(n => n.Type == "sla_breach"));
        h.NotificationsSent.Clear();

        // Cycle 2: SuccessRate recovers (all succeeded) but durations still high
        var recoveredSessions = new List<SessionSummary>();
        for (int i = 0; i < 8; i++)
            recoveredSessions.Add(new SessionSummary { TenantId = TenantId, SessionId = Guid.NewGuid().ToString(), Status = SessionStatus.Succeeded, DurationSeconds = 3600 });
        h.SetTerminalSessions(TenantId, recoveredSessions);

        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.False(row!.SuccessRate_IsActive);
        Assert.True(row.Duration_IsActive);
        // Exactly one resolved (SuccessRate) and zero new breach notifications (Duration is still throttled)
        Assert.Single(h.NotificationsSent, n => n.Type == "sla_resolved");
        Assert.DoesNotContain(h.NotificationsSent, n => n.Type == "sla_breach");
    }

    // Builds an inline-path SlaBreachEvaluationService wired to the shared StatusRepo, a
    // TenantConfigurationService backed by `config`, and a session page of `recentPage`.
    // Returns the service plus the captured in-app notification types for assertions.
    private static (SlaBreachEvaluationService Service, List<string> Captured) BuildInlineService(
        InMemorySlaStatusRepository statusRepo, TenantConfiguration config, List<SessionSummary> recentPage)
    {
        var memCache = new MemoryCache(new MemoryCacheOptions());
        var configRepoMock = new Mock<IConfigRepository>();
        configRepoMock.Setup(r => r.GetTenantConfigurationAsync(TenantId)).ReturnsAsync(config);
        var tenantConfigSvc = new TenantConfigurationService(configRepoMock.Object, NullLogger<TenantConfigurationService>.Instance, memCache);

        var notifRepo = new Mock<ITenantNotificationRepository>();
        notifRepo.Setup(r => r.AddNotificationAsync(It.IsAny<string>(), It.IsAny<GlobalNotification>())).ReturnsAsync(true);
        var notif = new Mock<TenantNotificationService>(notifRepo.Object, new FakeSignalRNotificationService(), NullLogger<TenantNotificationService>.Instance);
        var captured = new List<string>();
        notif.Setup(n => n.CreateNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string, string, string, string?>((_, type, _, _, _) => { captured.Add(type); return Task.CompletedTask; });

        var adminCfg = new Mock<AdminConfigurationService>(Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, memCache);
        adminCfg.Setup(a => a.GetConfigurationAsync()).ReturnsAsync(new AutopilotMonitor.Shared.Models.AdminConfiguration { SlaNotificationCooldownHours = 24 });

        var maintenanceRepo = new Mock<IMaintenanceRepository>();
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo.Setup(r => r.GetSessionsPageAsync(TenantId, It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>()))
            .ReturnsAsync(new RawPage<SessionSummary>(recentPage, null));

        var webhook = new WebhookNotificationService(new HttpClient(), NullLogger<WebhookNotificationService>.Instance);
        var channelDispatcher = new NotificationChannelDispatcher(webhook, new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(), NullLogger<TelegramNotificationService>.Instance));
        var alertDispatch = TestNotifications.InertOpsAlertDispatch(adminCfg.Object);
        var opsRepo = new Mock<IOpsEventRepository>();
        opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>())).Returns(Task.CompletedTask);
        var opsSvc = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

        var metricsRepo = new Mock<IMetricsRepository>();
        metricsRepo.Setup(r => r.GetAppInstallSummariesByTenantAsync(It.IsAny<string>(), It.IsAny<DateTime?>()))
            .ReturnsAsync(new List<AppInstallSummary>());
        var svc = new SlaBreachEvaluationService(
            tenantConfigSvc, configRepoMock.Object, maintenanceRepo.Object, sessionRepo.Object,
            metricsRepo.Object,
            channelDispatcher, notif.Object, statusRepo, adminCfg.Object, opsSvc,
            new TelemetryClient(new TelemetryConfiguration()),
            NullLogger<SlaBreachEvaluationService>.Instance);

        return (svc, captured);
    }

    private static List<SessionSummary> FailedPage(int count)
    {
        var list = new List<SessionSummary>();
        for (int i = 0; i < count; i++)
            list.Add(new SessionSummary { TenantId = TenantId, SessionId = $"s{i}", Status = SessionStatus.Failed });
        return list;
    }

    [Fact]
    public async Task ConsecutiveFailures_InlinePath_PersistsAndRespectsCooldown()
    {
        var h = new Harness();
        var config = CreateConfig(consecutive: true, consecThreshold: 3);
        var (svc, captured) = BuildInlineService(h.StatusRepo, config, FailedPage(3));

        var failedSession = new SessionSummary { TenantId = TenantId, SessionId = "s3", Status = SessionStatus.Failed, DeviceName = "DEV-01", FailureReason = "timeout" };

        // First failure burst → row created + notification
        await svc.EvaluateSessionCompletionAsync(TenantId, failedSession);
        var row1 = await h.StatusRepo.GetAsync(TenantId);
        Assert.True(row1!.ConsecutiveFailures_IsActive);
        Assert.Equal(3, row1.ConsecutiveFailures_Count);
        Assert.Equal("DEV-01", row1.ConsecutiveFailures_LastDevice);
        Assert.Single(captured, t => t == "sla_consecutive_failures");

        // Immediate second invocation → throttled
        await svc.EvaluateSessionCompletionAsync(TenantId, failedSession);
        Assert.Single(captured); // still one
    }

    [Fact]
    public async Task ConsecutiveFailures_InlinePath_SubMinimumThreshold_FallsBackToDefaultOfFive()
    {
        var h = new Harness();
        // Mis-set / unset threshold (< 2) must fall back to the canonical default of 5,
        // not be clamped to 2 — keeping the inline raise window in lock-step with the timer.
        var config = CreateConfig(consecutive: true, consecThreshold: 1);
        var failedSession = new SessionSummary { TenantId = TenantId, SessionId = "x", Status = SessionStatus.Failed, DeviceName = "DEV-01", FailureReason = "timeout" };

        // Only 3 consecutive failures available → below the effective threshold of 5 → no breach.
        var (svc3, captured3) = BuildInlineService(h.StatusRepo, config, FailedPage(3));
        await svc3.EvaluateSessionCompletionAsync(TenantId, failedSession);
        Assert.Null(await h.StatusRepo.GetAsync(TenantId));
        Assert.Empty(captured3);

        // 5 consecutive failures → breach fires and records the effective threshold (5).
        var (svc5, captured5) = BuildInlineService(h.StatusRepo, config, FailedPage(5));
        await svc5.EvaluateSessionCompletionAsync(TenantId, failedSession);
        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.True(row!.ConsecutiveFailures_IsActive);
        Assert.Equal(5, row.ConsecutiveFailures_Count);
        Assert.Single(captured5, t => t == "sla_consecutive_failures");
    }

    [Fact]
    public async Task ToggleDisabled_AfterPriorBreach_SilentlyClears()
    {
        var h = new Harness();

        // Pre-seed status as active
        await h.StatusRepo.UpsertAsync(new SlaTenantStatus
        {
            TenantId = TenantId,
            SuccessRate_IsActive = true,
            SuccessRate_FirstBreachAt = DateTime.UtcNow.AddHours(-12),
            SuccessRate_LastBreachAt = DateTime.UtcNow.AddHours(-2),
            SuccessRate_LastNotifiedAt = DateTime.UtcNow.AddHours(-2),
            SuccessRate_CurrentValue = 88,
            SuccessRate_TargetValue = 95,
        });

        // Toggle off; tenant still qualifies because Consecutive is on
        var config = CreateConfig(successRate: false, consecutive: true);
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 12, failed: 0));

        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.False(row!.SuccessRate_IsActive);
        Assert.NotNull(row.SuccessRate_ResolvedAt);
        Assert.DoesNotContain(h.NotificationsSent, n => n.Type == "sla_resolved");
        Assert.DoesNotContain(h.NotificationsSent, n => n.Type == "sla_breach");
    }

    [Fact]
    public async Task RaceProbe_InlineThenTimer_DoesNotLoseConsecutiveFailureFields()
    {
        var h = new Harness();
        var config = CreateConfig(successRate: true, consecutive: true, consecThreshold: 3);
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 8, failed: 4));

        // Pre-seed status as ConsecutiveFailures-active (simulating an inline-path write)
        var preSeed = new SlaTenantStatus
        {
            TenantId = TenantId,
            ConsecutiveFailures_IsActive = true,
            ConsecutiveFailures_FirstAt = DateTime.UtcNow.AddMinutes(-10),
            ConsecutiveFailures_Count = 3,
            ConsecutiveFailures_LastDevice = "DEV-A",
            ConsecutiveFailures_LastReason = "timeout",
            ConsecutiveFailures_LastNotifiedAt = DateTime.UtcNow.AddMinutes(-10),
        };
        await h.StatusRepo.UpsertAsync(preSeed);

        // Now the timer fires. Inline-seeded fields must survive the upsert.
        // Also session repo returns failing sessions so ConsecutiveFailures stays active.
        h.SetRecentSessions(TenantId, new List<SessionSummary>
        {
            new() { TenantId = TenantId, SessionId = "s1", Status = SessionStatus.Failed },
            new() { TenantId = TenantId, SessionId = "s2", Status = SessionStatus.Failed },
            new() { TenantId = TenantId, SessionId = "s3", Status = SessionStatus.Failed },
        });

        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.True(row!.ConsecutiveFailures_IsActive);
        Assert.Equal(3, row.ConsecutiveFailures_Count);
        Assert.Equal("DEV-A", row.ConsecutiveFailures_LastDevice);
        Assert.Equal("timeout", row.ConsecutiveFailures_LastReason);
        // The new SuccessRate breach must also be recorded
        Assert.True(row.SuccessRate_IsActive);
    }

    // ── Regression: review findings ─────────────────────────────────────────

    /// <summary>
    /// Review-finding regression: confirms the notification carries the configured warn-threshold
    /// (the line we breached), not the higher SLA target. Without this guard, a 94% rate
    /// against target=99% / threshold=95% would emit "below 99%" — misleading the operator.
    /// </summary>
    [Fact]
    public async Task BreachNotification_CarriesThresholdNotTarget()
    {
        var h = new Harness();
        var config = CreateConfig(successRate: true, targetSuccessRate: 99m);
        config.SlaSuccessRateNotifyThreshold = 95m;
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 47, failed: 3)); // 94% < 95%

        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.True(row!.SuccessRate_IsActive);
        // Row carries both target and threshold separately for display.
        Assert.Equal(95.0, row.SuccessRate_ThresholdValue);
        Assert.Equal(99.0, row.SuccessRate_TargetValue);
        // The dispatched notification message must reference the threshold (95), not the target (99).
        var notif = h.NotificationsSent.Single(n => n.Type == "sla_breach");
        Assert.Contains("95", notif.Message);
        Assert.DoesNotContain("99.0%", notif.Message);
    }

    /// <summary>
    /// Review-finding regression: when a tenant's SLA toggles get turned off while a breach is
    /// active, EvaluateTenantAsync no longer runs for them — so the timer's orphan sweep must
    /// silently clear the row. Without this the GA cross-tenant overview would show a zombie.
    /// </summary>
    [Fact]
    public async Task OrphanedActiveRow_ClearedSilently_WhenTenantHasNoTogglesEnabled()
    {
        var h = new Harness();
        await h.StatusRepo.UpsertAsync(new SlaTenantStatus
        {
            TenantId = TenantId,
            SuccessRate_IsActive = true,
            SuccessRate_FirstBreachAt = DateTime.UtcNow,
            SuccessRate_LastBreachAt = DateTime.UtcNow.AddHours(-2),
            SuccessRate_LastNotifiedAt = DateTime.UtcNow.AddHours(-2),
            Duration_IsActive = true,
            Duration_FirstBreachAt = DateTime.UtcNow,
            Duration_CurrentP95Minutes = 90,
        });

        // Tenant exists but ALL toggles are off → falls outside the qualifying loop entirely.
        h.SetTenants(CreateConfig(successRate: false, duration: false, consecutive: false));

        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.False(row!.SuccessRate_IsActive);
        Assert.False(row.Duration_IsActive);
        Assert.NotNull(row.SuccessRate_ResolvedAt);
        Assert.NotNull(row.Duration_ResolvedAt);
        // Crucially: no resolve notification — the tenant disabled their SLA, they don't
        // care about the state transition.
        Assert.DoesNotContain(h.NotificationsSent, n => n.Type == "sla_resolved");
        Assert.DoesNotContain(h.NotificationsSent, n => n.Type == "sla_breach");
    }

    /// <summary>
    /// Review-finding regression: throttle decision + LastNotifiedAt update must be atomic.
    /// Inject CAS conflicts; the retry loop should refetch and converge on a single notification.
    /// </summary>
    [Fact]
    public async Task TryUpsert_CasConflict_RetriesAndStillNotifiesOnce()
    {
        var h = new Harness();
        var config = CreateConfig(successRate: true);
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 8, failed: 4));

        // Pre-seed an empty row so the second-iteration retry has a non-null ETag path.
        await h.StatusRepo.UpsertAsync(SlaTenantStatus.CreateEmpty(TenantId));
        // Two conflicts: forces the timer-path to retry twice before succeeding.
        h.StatusRepo.ConflictsToInject = 2;

        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.True(row!.SuccessRate_IsActive);
        Assert.Single(h.NotificationsSent, n => n.Type == "sla_breach");
    }

    /// <summary>
    /// Review-finding regression: when CAS retries are exhausted (sustained contention),
    /// the call returns without dispatching to avoid double-fires when state is uncertain.
    /// </summary>
    [Fact]
    public async Task TryUpsert_CasConflict_ExhaustedRetries_DropsNotification()
    {
        var h = new Harness();
        var config = CreateConfig(successRate: true);
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 8, failed: 4));
        await h.StatusRepo.UpsertAsync(SlaTenantStatus.CreateEmpty(TenantId));
        h.StatusRepo.ConflictsToInject = 10; // way more than MaxConflictRetries

        await h.Service.EvaluateAllTenantsAsync();

        Assert.DoesNotContain(h.NotificationsSent, n => n.Type == "sla_breach");
    }

    /// <summary>
    /// Review-finding regression: AppInstall breach is now actually evaluated, not just toggled.
    /// </summary>
    [Fact]
    public async Task AppInstallBreach_BelowTarget_FiresBreachNotificationAndPopulatesRow()
    {
        var h = new Harness();
        var config = CreateConfig();
        config.SlaNotifyOnAppInstallBreach = true;
        config.SlaTargetAppInstallSuccessRate = 95m;
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 5, failed: 0));

        var apps = new List<AppInstallSummary>();
        for (int i = 0; i < 8; i++)
            apps.Add(new AppInstallSummary { TenantId = TenantId, SessionId = $"s-{i}", AppName = $"Office {i}", Status = "Succeeded", StartedAt = DateTime.UtcNow });
        for (int i = 0; i < 4; i++)
            apps.Add(new AppInstallSummary { TenantId = TenantId, SessionId = $"f-{i}", AppName = "Adobe Reader", Status = "Failed", StartedAt = DateTime.UtcNow });
        h.SetAppInstalls(TenantId, apps);

        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.True(row!.AppInstall_IsActive);
        Assert.Equal("Adobe Reader", row.AppInstall_TopFailingApp);
        Assert.NotNull(row.AppInstall_FirstBreachAt);
        Assert.NotNull(row.AppInstall_LastNotifiedAt);
        Assert.Single(h.NotificationsSent, n => n.Type == "sla_breach");
    }

    /// <summary>
    /// Review-finding regression: the breach notification for AppInstall must be rendered with
    /// its own template (title/summary/facts/period), not silently mapped to the Duration template
    /// because the builder's old branch only checked SuccessRate-vs-not-SuccessRate.
    /// </summary>
    [Fact]
    public async Task AppInstallBreach_Notification_UsesAppInstallTemplate_NotDuration()
    {
        var h = new Harness();
        var config = CreateConfig();
        config.SlaNotifyOnAppInstallBreach = true;
        config.SlaTargetAppInstallSuccessRate = 95m;
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 5, failed: 0));

        var apps = new List<AppInstallSummary>();
        for (int i = 0; i < 6; i++)
            apps.Add(new AppInstallSummary { TenantId = TenantId, SessionId = $"ok-{i}", AppName = "Excel", Status = "Succeeded", StartedAt = DateTime.UtcNow });
        for (int i = 0; i < 4; i++)
            apps.Add(new AppInstallSummary { TenantId = TenantId, SessionId = $"bad-{i}", AppName = "Adobe Reader", Status = "Failed", StartedAt = DateTime.UtcNow });
        h.SetAppInstalls(TenantId, apps);

        await h.Service.EvaluateAllTenantsAsync();

        var notif = h.NotificationsSent.Single(n => n.Type == "sla_breach");
        Assert.Contains("App Install", notif.Title);
        Assert.Contains("app install success rate", notif.Message);
        // Must NOT render as Duration
        Assert.DoesNotContain("P95", notif.Title);
        Assert.DoesNotContain("Duration", notif.Title);
        Assert.DoesNotContain("min", notif.Message);
    }

    /// <summary>
    /// Review-finding regression: AppInstall evaluation must use the same ISO-week window as the
    /// dashboard. Uses a fixed clock so the discriminating date (in the same month, but a
    /// previous ISO week) is deterministic on every test run — no silent date-dependent skips.
    ///
    /// Fixed clock: 2026-05-15 (Friday). ISO week 20. May 4 (Monday) is in the same month (May)
    /// but in ISO week 19 — would be inside a month-aligned window, but outside the current
    /// week-aligned one. Month-aligned eval would breach; week-aligned must not.
    /// </summary>
    [Fact]
    public async Task AppInstallBreach_RespectsCurrentIsoWeekWindow()
    {
        var clock = new DateTime(2026, 5, 15, 12, 0, 0, DateTimeKind.Utc);
        var previousWeekSameMonth = new DateTime(2026, 5, 4, 12, 0, 0, DateTimeKind.Utc);

        // Sanity-check the test premise: previous date is same month but a different ISO week.
        Assert.Equal(clock.Month, previousWeekSameMonth.Month);
        Assert.NotEqual(SlaMetricsService.GetIsoWeekKey(clock), SlaMetricsService.GetIsoWeekKey(previousWeekSameMonth));

        var h = new Harness(clock: clock);
        var config = CreateConfig();
        config.SlaNotifyOnAppInstallBreach = true;
        config.SlaTargetAppInstallSuccessRate = 95m;
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 5, failed: 0));

        var oldFailures = new List<AppInstallSummary>();
        for (int i = 0; i < 10; i++)
            oldFailures.Add(new AppInstallSummary { TenantId = TenantId, SessionId = $"old-{i}", AppName = "Old Failing App", Status = "Failed", StartedAt = previousWeekSameMonth });
        h.SetAppInstalls(TenantId, oldFailures);

        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        // With week-aligned evaluation, the previous-week failures are out of scope — no breach.
        // (Month-aligned eval would still see them and would have raised a breach.)
        Assert.False(row!.AppInstall_IsActive);
        Assert.DoesNotContain(h.NotificationsSent, n => n.Type == "sla_breach");
    }

    /// <summary>
    /// Review-finding regression: AppInstall notification + ops event must carry the actual
    /// installer totals (not 0/0). Without this, Application Insights / Ops dashboards see
    /// every AppInstall breach as "0 sessions, 0 failed" — meaningless context.
    /// </summary>
    [Fact]
    public async Task AppInstallBreach_OpsEvent_ReceivesActualTotals()
    {
        var totals = new List<(int Total, int Failed)>();
        var h = new HarnessWithOpsCapture(totals);
        var config = CreateConfig();
        config.SlaNotifyOnAppInstallBreach = true;
        config.SlaTargetAppInstallSuccessRate = 95m;
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 5, failed: 0));

        var apps = new List<AppInstallSummary>();
        for (int i = 0; i < 6; i++)
            apps.Add(new AppInstallSummary { TenantId = TenantId, SessionId = $"ok-{i}", AppName = "Excel", Status = "Succeeded", StartedAt = DateTime.UtcNow });
        for (int i = 0; i < 4; i++)
            apps.Add(new AppInstallSummary { TenantId = TenantId, SessionId = $"bad-{i}", AppName = "Adobe Reader", Status = "Failed", StartedAt = DateTime.UtcNow });
        h.SetAppInstalls(TenantId, apps);

        await h.Service.EvaluateAllTenantsAsync();

        var captured = totals.Single();
        Assert.Equal(10, captured.Total);   // 6 succeeded + 4 failed = 10 apps evaluated
        Assert.Equal(4, captured.Failed);
    }

    /// <summary>
    /// Harness variant that captures the totals passed to OpsEventService.RecordSlaBreachNotificationAsync
    /// so we can prove the totals (not zeros) are threaded all the way through.
    /// </summary>
    private sealed class HarnessWithOpsCapture
    {
        public InMemorySlaStatusRepository StatusRepo { get; } = new();
        public SlaBreachEvaluationService Service { get; }
        private readonly Mock<IConfigRepository> _configRepo = new();
        private readonly Mock<IMaintenanceRepository> _maintenanceRepo = new();
        private readonly Mock<ISessionRepository> _sessionRepo = new();
        private readonly Mock<IMetricsRepository> _metricsRepo = new();

        public HarnessWithOpsCapture(List<(int Total, int Failed)> totalsCapture)
        {
            var memCache = new MemoryCache(new MemoryCacheOptions());
            var adminConfig = new Mock<AdminConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance, memCache);
            adminConfig.Setup(a => a.GetConfigurationAsync())
                .ReturnsAsync(new AutopilotMonitor.Shared.Models.AdminConfiguration { SlaNotificationCooldownHours = 24 });

            var tenantConfigService = new Mock<TenantConfigurationService>(
                Mock.Of<IConfigRepository>(), NullLogger<TenantConfigurationService>.Instance, memCache);

            var notifRepo = new Mock<ITenantNotificationRepository>();
            notifRepo.Setup(r => r.AddNotificationAsync(It.IsAny<string>(), It.IsAny<GlobalNotification>())).ReturnsAsync(true);
            var notifService = new Mock<TenantNotificationService>(notifRepo.Object, new FakeSignalRNotificationService(), NullLogger<TenantNotificationService>.Instance);
            notifService.Setup(n => n.CreateNotificationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>())).Returns(Task.CompletedTask);

            // Capture the ops-event details JSON; assert totals against it.
            var opsRepo = new Mock<IOpsEventRepository>();
            opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
                .Callback<OpsEventEntry>(e =>
                {
                    if (e.EventType != "SlaBreachNotification") return;
                    using var doc = System.Text.Json.JsonDocument.Parse(e.Details!);
                    var total = doc.RootElement.GetProperty("totalSessions").GetInt32();
                    var failed = doc.RootElement.GetProperty("failedSessions").GetInt32();
                    totalsCapture.Add((total, failed));
                })
                .Returns(Task.CompletedTask);

            var webhook = new WebhookNotificationService(new HttpClient(), NullLogger<WebhookNotificationService>.Instance);
            var channelDispatcher = new NotificationChannelDispatcher(webhook, new TelegramNotificationService(new HttpClient(), Mock.Of<IConfigRepository>(), NullLogger<TelegramNotificationService>.Instance));
            var alertDispatch = TestNotifications.InertOpsAlertDispatch(adminConfig.Object);
            var opsService = new OpsEventService(opsRepo.Object, NullLogger<OpsEventService>.Instance, alertDispatch);

            Service = new SlaBreachEvaluationService(
                tenantConfigService.Object, _configRepo.Object, _maintenanceRepo.Object, _sessionRepo.Object,
                _metricsRepo.Object, channelDispatcher, notifService.Object, StatusRepo, adminConfig.Object, opsService,
                new TelemetryClient(new TelemetryConfiguration()), NullLogger<SlaBreachEvaluationService>.Instance);

            _sessionRepo.Setup(r => r.GetSessionsPageAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>()))
                .ReturnsAsync(new RawPage<SessionSummary>(new List<SessionSummary>(), null));
            _metricsRepo.Setup(r => r.GetAppInstallSummariesByTenantAsync(It.IsAny<string>(), It.IsAny<DateTime?>()))
                .ReturnsAsync(new List<AppInstallSummary>());
        }

        public void SetTenants(params TenantConfiguration[] tenants)
            => _configRepo.Setup(r => r.GetAllTenantConfigurationsAsync()).ReturnsAsync(tenants.ToList());

        public void SetTerminalSessions(string tenantId, List<SessionSummary> sessions)
            => _maintenanceRepo.Setup(r => r.GetSessionsByDateRangeAsync(It.IsAny<DateTime>(), It.IsAny<DateTime>(), tenantId, It.IsAny<CancellationToken>())).ReturnsAsync(sessions);

        public void SetAppInstalls(string tenantId, List<AppInstallSummary> apps)
            => _metricsRepo.Setup(r => r.GetAppInstallSummariesByTenantAsync(tenantId, It.IsAny<DateTime?>())).ReturnsAsync(apps);
    }

    /// <summary>
    /// Review-finding regression: telemetry Period tag must reflect the actual evaluation window.
    /// Without this, AppInstall breaches show up as "CurrentMonth" in Application Insights /
    /// ops dashboards even though they were evaluated on the current ISO week.
    /// </summary>
    [Theory]
    [InlineData("SuccessRate", "CurrentMonth")]
    [InlineData("Duration", "CurrentMonth")]
    [InlineData("AppInstall", "CurrentWeek")]
    public void PeriodForBreachType_MatchesEvaluationWindow(string breachType, string expectedPeriod)
    {
        Assert.Equal(expectedPeriod, SlaBreachEvaluationService.PeriodForBreachType(breachType));
    }

    /// <summary>
    /// Review-finding regression: AppInstall resolve fires a resolved notification once.
    /// </summary>
    [Fact]
    public async Task AppInstallBreach_BackAboveTarget_FiresResolved()
    {
        var h = new Harness();
        var config = CreateConfig();
        config.SlaNotifyOnAppInstallBreach = true;
        config.SlaTargetAppInstallSuccessRate = 95m;
        h.SetTenants(config);
        h.SetTerminalSessions(TenantId, Sessions(succeeded: 5, failed: 0));

        // Cycle 1: failing apps push rate below 95
        var failingApps = new List<AppInstallSummary>();
        for (int i = 0; i < 6; i++)
            failingApps.Add(new AppInstallSummary { TenantId = TenantId, SessionId = $"ok-{i}", AppName = "Excel", Status = "Succeeded", StartedAt = DateTime.UtcNow });
        for (int i = 0; i < 4; i++)
            failingApps.Add(new AppInstallSummary { TenantId = TenantId, SessionId = $"bad-{i}", AppName = "Adobe Reader", Status = "Failed", StartedAt = DateTime.UtcNow });
        h.SetAppInstalls(TenantId, failingApps);

        await h.Service.EvaluateAllTenantsAsync();
        h.NotificationsSent.Clear();

        // Cycle 2: all green
        var greenApps = new List<AppInstallSummary>();
        for (int i = 0; i < 10; i++)
            greenApps.Add(new AppInstallSummary { TenantId = TenantId, SessionId = $"green-{i}", AppName = "Excel", Status = "Succeeded", StartedAt = DateTime.UtcNow });
        h.SetAppInstalls(TenantId, greenApps);

        await h.Service.EvaluateAllTenantsAsync();

        var row = await h.StatusRepo.GetAsync(TenantId);
        Assert.False(row!.AppInstall_IsActive);
        Assert.NotNull(row.AppInstall_ResolvedAt);
        Assert.Single(h.NotificationsSent, n => n.Type == "sla_resolved");
    }
}
