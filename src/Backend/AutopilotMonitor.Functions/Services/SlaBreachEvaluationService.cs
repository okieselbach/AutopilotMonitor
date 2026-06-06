using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Notifications;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Config;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.ApplicationInsights;
using Microsoft.Extensions.Logging;
using AutopilotMonitor.Functions.Helpers;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Evaluates SLA compliance for all tenants and dispatches breach + resolved notifications.
    ///
    /// State and throttling are persisted on a single <c>SlaTenantStatus</c> row per tenant.
    /// Concurrent writers (timer + ingest inline path, or two parallel ingests) are kept atomic
    /// via an ETag-based compare-and-swap retry loop — the throttle decision is made and the
    /// <c>*_LastNotifiedAt</c> watermark is committed in the same write, so two parallel calls
    /// can never both succeed at sending the same notification.
    ///
    /// Entry points:
    ///   <see cref="EvaluateAllTenantsAsync"/>       — timer trigger (every 2 hours)
    ///   <see cref="EvaluateSessionCompletionAsync"/> — inline from IngestEventsFunction (fire-and-forget)
    /// </summary>
    public class SlaBreachEvaluationService
    {
        private const string DashboardUrl = "https://www.autopilotmonitor.com/sla";
        private const int MaxConflictRetries = 4;
        private const int MinAppInstallSampleSize = 5;

        private readonly TenantConfigurationService _configService;
        private readonly IConfigRepository _configRepo;
        private readonly IMaintenanceRepository _maintenanceRepo;
        private readonly ISessionRepository _sessionRepo;
        private readonly IMetricsRepository _metricsRepo;
        private readonly WebhookNotificationService _webhookService;
        private readonly TenantNotificationService _tenantNotificationService;
        private readonly ISlaTenantStatusRepository _statusRepo;
        private readonly AdminConfigurationService _adminConfigService;
        private readonly OpsEventService _opsEventService;
        private readonly TelemetryClient _telemetryClient;
        private readonly ILogger<SlaBreachEvaluationService> _logger;
        private readonly Func<DateTime> _nowProvider;

        public SlaBreachEvaluationService(
            TenantConfigurationService configService,
            IConfigRepository configRepo,
            IMaintenanceRepository maintenanceRepo,
            ISessionRepository sessionRepo,
            IMetricsRepository metricsRepo,
            WebhookNotificationService webhookService,
            TenantNotificationService tenantNotificationService,
            ISlaTenantStatusRepository statusRepo,
            AdminConfigurationService adminConfigService,
            OpsEventService opsEventService,
            TelemetryClient telemetryClient,
            ILogger<SlaBreachEvaluationService> logger,
            Func<DateTime>? nowProvider = null)
        {
            _configService = configService;
            _configRepo = configRepo;
            _maintenanceRepo = maintenanceRepo;
            _sessionRepo = sessionRepo;
            _metricsRepo = metricsRepo;
            _webhookService = webhookService;
            _tenantNotificationService = tenantNotificationService;
            _statusRepo = statusRepo;
            _adminConfigService = adminConfigService;
            _opsEventService = opsEventService;
            _telemetryClient = telemetryClient;
            _logger = logger;
            _nowProvider = nowProvider ?? (() => DateTime.UtcNow);
        }

        // ── Timer trigger entry point ─────────────────────────────────────────

        /// <summary>
        /// Evaluates SLA compliance for all tenants with at least one SlaNotifyOn* toggle enabled,
        /// plus any tenant with a stale active status row (so disabled-toggle zombies get cleared).
        /// </summary>
        public async Task EvaluateAllTenantsAsync()
        {
            var sw = Stopwatch.StartNew();
            int tenantsEvaluated = 0;
            int breachesDetected = 0;
            int notificationsSent = 0;

            try
            {
                var cooldown = await GetCooldownAsync();
                var allConfigs = await _configRepo.GetAllTenantConfigurationsAsync();
                var configByTenant = allConfigs.ToDictionary(
                    c => c.TenantId, StringComparer.OrdinalIgnoreCase);

                var qualifying = allConfigs.Where(c =>
                    c.SlaNotifyOnSuccessRateBreach ||
                    c.SlaNotifyOnDurationBreach ||
                    c.SlaNotifyOnAppInstallBreach ||
                    c.SlaNotifyOnConsecutiveFailures).ToList();

                _logger.LogInformation("SLA evaluation: {Total} tenants, {Qualifying} with SLA notifications enabled, cooldown={CooldownHours}h",
                    allConfigs.Count, qualifying.Count, cooldown.TotalHours);

                foreach (var config in qualifying)
                {
                    try
                    {
                        var result = await EvaluateTenantAsync(config, cooldown);
                        tenantsEvaluated++;
                        breachesDetected += result.BreachesDetected;
                        notificationsSent += result.NotificationsSent;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "SLA evaluation failed for tenant {TenantId}", config.TenantId);
                    }
                }

                // Sweep orphaned active rows — tenants whose toggles were turned off while a
                // breach was active. Without this they would linger as zombies on the GA overview.
                var allStatus = await _statusRepo.ListAllAsync();
                foreach (var orphan in allStatus)
                {
                    if (!orphan.IsAnyTypeActive()) continue;
                    if (configByTenant.TryGetValue(orphan.TenantId, out var cfg) &&
                        (cfg.SlaNotifyOnSuccessRateBreach || cfg.SlaNotifyOnDurationBreach
                         || cfg.SlaNotifyOnAppInstallBreach || cfg.SlaNotifyOnConsecutiveFailures))
                    {
                        continue; // tenant is still qualifying; handled by EvaluateTenantAsync
                    }

                    try
                    {
                        await ClearOrphanedRowAsync(orphan.TenantId);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clear orphaned SLA status row for tenant {TenantId}", orphan.TenantId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SLA evaluation failed");
            }

            sw.Stop();

            _telemetryClient.TrackEvent("SlaEvaluationCompleted", new Dictionary<string, string>
            {
                { "TenantsEvaluated", tenantsEvaluated.ToString() },
                { "BreachesFound", breachesDetected.ToString() },
                { "NotificationsSent", notificationsSent.ToString() },
                { "DurationMs", sw.ElapsedMilliseconds.ToString() },
            });

            _ = _opsEventService.RecordSlaEvaluationCompletedAsync(
                tenantsEvaluated, breachesDetected, notificationsSent, (int)sw.ElapsedMilliseconds);

            _logger.LogInformation("SLA evaluation completed: {Tenants} tenants, {Breaches} breaches, {Notifications} notifications in {Ms}ms",
                tenantsEvaluated, breachesDetected, notificationsSent, sw.ElapsedMilliseconds);
        }

        // ── Per-tenant evaluation (ETag-CAS retry loop) ───────────────────────

        private async Task<(int BreachesDetected, int NotificationsSent)> EvaluateTenantAsync(
            TenantConfiguration config, TimeSpan cooldown)
        {
            // Compute observables once outside the retry loop — they don't change across retries.
            var now = _nowProvider();
            var monthStart = new DateTime(now.Year, now.Month, 1);
            var sessions = await _maintenanceRepo.GetSessionsByDateRangeAsync(monthStart, now.AddDays(1), config.TenantId);
            var terminal = sessions
                .Where(s => s.Status == SessionStatus.Succeeded || s.Status == SessionStatus.Failed)
                .ToList();

            // AppInstall is evaluated on the same current-ISO-week scope as the SLA dashboard
            // (SlaMetricsService.cs:170, SectionSlaTargets.tsx). Without this alignment a
            // notification could fire while the UI shows green for the week (or vice versa).
            List<AppInstallSummary>? appInstalls = null;
            if (config.SlaNotifyOnAppInstallBreach && config.SlaTargetAppInstallSuccessRate.HasValue)
            {
                var currentWeekKey = SlaMetricsService.GetIsoWeekKey(now);
                var raw = await _metricsRepo.GetAppInstallSummariesByTenantAsync(config.TenantId);
                appInstalls = raw
                    .Where(a => (a.Status == "Succeeded" || a.Status == "Failed")
                                && SlaMetricsService.GetIsoWeekKey(a.StartedAt) == currentWeekKey)
                    .ToList();
            }

            // Recent-page query is also stable for the cycle (timer view of "is breach still going").
            RawPage<SessionSummary>? recentSessions = null;
            if (config.SlaNotifyOnConsecutiveFailures)
            {
                var threshold = EffectiveConsecutiveFailureThreshold(config);
                recentSessions = await _sessionRepo.GetSessionsPageAsync(
                    config.TenantId, days: null, pageSize: threshold, continuation: null);
            }

            for (int attempt = 0; attempt < MaxConflictRetries; attempt++)
            {
                var (statusFromRepo, etag) = await _statusRepo.GetWithETagAsync(config.TenantId);
                var status = statusFromRepo ?? SlaTenantStatus.CreateEmpty(config.TenantId);

                var pending = new List<Func<Task>>();
                int breaches = 0;

                // --- SuccessRate ---
                if (config.SlaNotifyOnSuccessRateBreach && config.SlaTargetSuccessRate.HasValue && terminal.Count > 0)
                {
                    var succeeded = terminal.Count(s => s.Status == SessionStatus.Succeeded);
                    var failed = terminal.Count(s => s.Status == SessionStatus.Failed);
                    var successRate = succeeded / (double)terminal.Count * 100;
                    var target = (double)config.SlaTargetSuccessRate.Value;
                    var threshold = config.SlaSuccessRateNotifyThreshold.HasValue
                        ? (double)config.SlaSuccessRateNotifyThreshold.Value
                        : target;
                    var isBreaching = successRate < threshold;
                    if (isBreaching) breaches++;

                    ApplySuccessRateState(status, now, isBreaching, successRate, target, threshold,
                        terminal.Count, failed, cooldown, pending, config);
                }
                else if (status.SuccessRate_IsActive)
                {
                    ClearSuccessRateSilent(status, now);
                }

                // --- Duration ---
                if (config.SlaNotifyOnDurationBreach && config.SlaTargetMaxDurationMinutes.HasValue)
                {
                    var completed = terminal
                        .Where(s => s.DurationSeconds.HasValue && s.DurationSeconds.Value > 0)
                        .Select(s => s.DurationSeconds!.Value / 60.0)
                        .OrderBy(d => d)
                        .ToList();

                    if (completed.Count > 0)
                    {
                        var p95 = MetricsMath.Percentile(completed, 95);
                        var target = config.SlaTargetMaxDurationMinutes.Value;
                        var isBreaching = p95 > target;
                        if (isBreaching) breaches++;

                        ApplyDurationState(status, now, isBreaching, p95, target, completed.Count,
                            cooldown, pending, config);
                    }
                    else if (status.Duration_IsActive)
                    {
                        ClearDurationSilent(status, now);
                    }
                }
                else if (status.Duration_IsActive)
                {
                    ClearDurationSilent(status, now);
                }

                // --- AppInstall ---
                if (config.SlaNotifyOnAppInstallBreach && config.SlaTargetAppInstallSuccessRate.HasValue && appInstalls != null)
                {
                    if (appInstalls.Count >= MinAppInstallSampleSize)
                    {
                        var succeeded = appInstalls.Count(a => a.Status == "Succeeded");
                        var failed = appInstalls.Count(a => a.Status == "Failed");
                        var rate = succeeded / (double)appInstalls.Count * 100;
                        var target = (double)config.SlaTargetAppInstallSuccessRate.Value;
                        var topFailing = appInstalls
                            .Where(a => a.Status == "Failed")
                            .GroupBy(a => a.AppName)
                            .OrderByDescending(g => g.Count())
                            .Select(g => g.Key)
                            .FirstOrDefault();
                        var isBreaching = rate < target;
                        if (isBreaching) breaches++;

                        ApplyAppInstallState(status, now, isBreaching, rate, target, topFailing,
                            appInstalls.Count, failed, cooldown, pending, config);
                    }
                    else if (status.AppInstall_IsActive)
                    {
                        ClearAppInstallSilent(status, now);
                    }
                }
                else if (status.AppInstall_IsActive)
                {
                    ClearAppInstallSilent(status, now);
                }

                // --- ConsecutiveFailures (timer detects resolve only) ---
                if (status.ConsecutiveFailures_IsActive && config.SlaNotifyOnConsecutiveFailures && recentSessions != null)
                {
                    var threshold = EffectiveConsecutiveFailureThreshold(config);
                    var allFailed = recentSessions.Items.Count >= threshold
                        && recentSessions.Items.Take(threshold).All(s => s.Status == SessionStatus.Failed);

                    if (!allFailed)
                    {
                        ResolveConsecutiveFailures(status, now, pending, config);
                    }
                }
                else if (status.ConsecutiveFailures_IsActive && !config.SlaNotifyOnConsecutiveFailures)
                {
                    ClearConsecutiveFailuresSilent(status, now);
                }

                status.LastEvaluatedAt = now;

                var committed = await _statusRepo.TryUpsertAsync(status, etag);
                if (!committed)
                {
                    if (attempt + 1 < MaxConflictRetries)
                    {
                        _logger.LogInformation("SLA status CAS conflict for tenant {TenantId}, retry {Attempt}",
                            config.TenantId, attempt + 1);
                        continue;
                    }
                    _logger.LogWarning("SLA status CAS conflict exhausted retries for tenant {TenantId} — skipping notifications",
                        config.TenantId);
                    return (breaches, 0);
                }

                int notifications = 0;
                foreach (var fn in pending)
                {
                    try { await fn(); notifications++; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "SLA notification dispatch failed for tenant {TenantId}", config.TenantId);
                    }
                }
                return (breaches, notifications);
            }

            return (0, 0);
        }

        private async Task ClearOrphanedRowAsync(string tenantId)
        {
            for (int attempt = 0; attempt < MaxConflictRetries; attempt++)
            {
                var (status, etag) = await _statusRepo.GetWithETagAsync(tenantId);
                if (status == null || !status.IsAnyTypeActive()) return;

                var now = _nowProvider();
                if (status.SuccessRate_IsActive) ClearSuccessRateSilent(status, now);
                if (status.Duration_IsActive) ClearDurationSilent(status, now);
                if (status.AppInstall_IsActive) ClearAppInstallSilent(status, now);
                if (status.ConsecutiveFailures_IsActive) ClearConsecutiveFailuresSilent(status, now);
                status.LastEvaluatedAt = now;

                if (await _statusRepo.TryUpsertAsync(status, etag))
                {
                    _logger.LogInformation("Cleared orphaned SLA status row for tenant {TenantId}", tenantId);
                    return;
                }
            }
            _logger.LogWarning("Failed to clear orphaned SLA status row for tenant {TenantId} after retries", tenantId);
        }

        // ── Per-type state transitions ─────────────────────────────────────────

        private void ApplySuccessRateState(SlaTenantStatus s, DateTime now, bool isBreaching,
            double current, double target, double threshold, int total, int failed,
            TimeSpan cooldown, List<Func<Task>> pending, TenantConfiguration config)
        {
            if (isBreaching)
            {
                var firstBreach = !s.SuccessRate_IsActive;
                if (firstBreach) s.SuccessRate_FirstBreachAt = now;

                s.SuccessRate_IsActive = true;
                s.SuccessRate_CurrentValue = current;
                s.SuccessRate_TargetValue = target;
                s.SuccessRate_ThresholdValue = threshold;
                s.SuccessRate_TotalSessions = total;
                s.SuccessRate_FailedSessions = failed;
                s.SuccessRate_LastBreachAt = now;
                s.SuccessRate_ResolvedAt = null;

                var shouldNotify = !s.SuccessRate_LastNotifiedAt.HasValue
                    || (now - s.SuccessRate_LastNotifiedAt.Value) >= cooldown;

                if (shouldNotify)
                {
                    s.SuccessRate_LastNotifiedAt = now;
                    // Pass the notify threshold (the line we breached) — not the SLA target —
                    // so the notification reads "below 95%" instead of "below 99%".
                    pending.Add(() => SendBreachNotificationAsync(config, SlaBreachType.SuccessRate, current, threshold, total, failed));
                }
            }
            else if (s.SuccessRate_IsActive)
            {
                var firstBreachAt = s.SuccessRate_FirstBreachAt;
                s.SuccessRate_IsActive = false;
                s.SuccessRate_CurrentValue = current;
                s.SuccessRate_TotalSessions = total;
                s.SuccessRate_FailedSessions = failed;
                s.SuccessRate_ResolvedAt = now;

                pending.Add(() => SendResolvedNotificationAsync(config, SlaBreachType.SuccessRate, current, threshold, firstBreachAt, now));
            }
            else
            {
                s.SuccessRate_CurrentValue = current;
                s.SuccessRate_TargetValue = target;
                s.SuccessRate_ThresholdValue = threshold;
                s.SuccessRate_TotalSessions = total;
                s.SuccessRate_FailedSessions = failed;
            }
        }

        private void ApplyDurationState(SlaTenantStatus s, DateTime now, bool isBreaching,
            double p95, int targetMinutes, int totalSessions,
            TimeSpan cooldown, List<Func<Task>> pending, TenantConfiguration config)
        {
            if (isBreaching)
            {
                var firstBreach = !s.Duration_IsActive;
                if (firstBreach) s.Duration_FirstBreachAt = now;

                s.Duration_IsActive = true;
                s.Duration_CurrentP95Minutes = p95;
                s.Duration_TargetMinutes = targetMinutes;
                s.Duration_TotalSessions = totalSessions;
                s.Duration_LastBreachAt = now;
                s.Duration_ResolvedAt = null;

                var shouldNotify = !s.Duration_LastNotifiedAt.HasValue
                    || (now - s.Duration_LastNotifiedAt.Value) >= cooldown;

                if (shouldNotify)
                {
                    s.Duration_LastNotifiedAt = now;
                    pending.Add(() => SendBreachNotificationAsync(config, SlaBreachType.Duration, p95, targetMinutes, totalSessions, 0));
                }
            }
            else if (s.Duration_IsActive)
            {
                var firstBreachAt = s.Duration_FirstBreachAt;
                s.Duration_IsActive = false;
                s.Duration_CurrentP95Minutes = p95;
                s.Duration_TotalSessions = totalSessions;
                s.Duration_ResolvedAt = now;

                pending.Add(() => SendResolvedNotificationAsync(config, SlaBreachType.Duration, p95, targetMinutes, firstBreachAt, now));
            }
            else
            {
                s.Duration_CurrentP95Minutes = p95;
                s.Duration_TargetMinutes = targetMinutes;
                s.Duration_TotalSessions = totalSessions;
            }
        }

        private void ApplyAppInstallState(SlaTenantStatus s, DateTime now, bool isBreaching,
            double currentRate, double targetRate, string? topFailingApp,
            int totalInstalls, int failedInstalls,
            TimeSpan cooldown, List<Func<Task>> pending, TenantConfiguration config)
        {
            if (isBreaching)
            {
                var firstBreach = !s.AppInstall_IsActive;
                if (firstBreach) s.AppInstall_FirstBreachAt = now;

                s.AppInstall_IsActive = true;
                s.AppInstall_CurrentRate = currentRate;
                s.AppInstall_TargetRate = targetRate;
                s.AppInstall_TopFailingApp = topFailingApp;
                s.AppInstall_LastBreachAt = now;
                s.AppInstall_ResolvedAt = null;

                var shouldNotify = !s.AppInstall_LastNotifiedAt.HasValue
                    || (now - s.AppInstall_LastNotifiedAt.Value) >= cooldown;

                if (shouldNotify)
                {
                    s.AppInstall_LastNotifiedAt = now;
                    var captureTopFailing = topFailingApp;
                    pending.Add(() => SendBreachNotificationAsync(
                        config, SlaBreachType.AppInstall, currentRate, targetRate,
                        totalInstalls, failedInstalls, captureTopFailing));
                }
            }
            else if (s.AppInstall_IsActive)
            {
                var firstBreachAt = s.AppInstall_FirstBreachAt;
                s.AppInstall_IsActive = false;
                s.AppInstall_CurrentRate = currentRate;
                s.AppInstall_TargetRate = targetRate;
                s.AppInstall_TopFailingApp = topFailingApp;
                s.AppInstall_ResolvedAt = now;

                pending.Add(() => SendResolvedNotificationAsync(config, SlaBreachType.AppInstall, currentRate, targetRate, firstBreachAt, now));
            }
            else
            {
                s.AppInstall_CurrentRate = currentRate;
                s.AppInstall_TargetRate = targetRate;
                s.AppInstall_TopFailingApp = topFailingApp;
            }
        }

        private void ResolveConsecutiveFailures(SlaTenantStatus s, DateTime now,
            List<Func<Task>> pending, TenantConfiguration config)
        {
            var firstBreachAt = s.ConsecutiveFailures_FirstAt;
            s.ConsecutiveFailures_IsActive = false;
            s.ConsecutiveFailures_ResolvedAt = now;
            pending.Add(() => SendResolvedNotificationAsync(config, SlaBreachType.ConsecutiveFailures, null, null, firstBreachAt, now));
        }

        private static void ClearSuccessRateSilent(SlaTenantStatus s, DateTime now)
        {
            s.SuccessRate_IsActive = false;
            s.SuccessRate_ResolvedAt = now;
        }

        private static void ClearDurationSilent(SlaTenantStatus s, DateTime now)
        {
            s.Duration_IsActive = false;
            s.Duration_ResolvedAt = now;
        }

        private static void ClearAppInstallSilent(SlaTenantStatus s, DateTime now)
        {
            s.AppInstall_IsActive = false;
            s.AppInstall_ResolvedAt = now;
        }

        private static void ClearConsecutiveFailuresSilent(SlaTenantStatus s, DateTime now)
        {
            s.ConsecutiveFailures_IsActive = false;
            s.ConsecutiveFailures_ResolvedAt = now;
        }

        // ── Notification dispatch ─────────────────────────────────────────────

        private async Task SendBreachNotificationAsync(TenantConfiguration config, string breachType,
            double currentValue, double targetValue, int totalSessions, int failedSessions,
            string? extraContext = null)
        {
            var alert = NotificationAlertBuilder.BuildSlaBreachAlert(
                config.TenantId, currentValue, targetValue,
                totalSessions, failedSessions, breachType, DashboardUrl, extraContext);

            var (webhookUrl, providerType) = config.GetEffectiveWebhookConfig();
            if (!string.IsNullOrEmpty(webhookUrl) && providerType != 0)
            {
                await _webhookService.SendNotificationAsync(webhookUrl, (Shared.Models.Notifications.WebhookProviderType)providerType, alert);
            }

            await _tenantNotificationService.CreateNotificationAsync(
                config.TenantId,
                "sla_breach",
                alert.Title,
                alert.Summary ?? "",
                "/sla");

            _telemetryClient.TrackEvent("SlaBreachDetected", new Dictionary<string, string>
            {
                { "TenantId", config.TenantId },
                { "BreachType", breachType },
                { "CurrentValue", currentValue.ToString("F1") },
                { "TargetValue", targetValue.ToString("F1") },
                { "TotalSessions", totalSessions.ToString() },
                { "FailedSessions", failedSessions.ToString() },
                { "Period", PeriodForBreachType(breachType) },
            });

            _telemetryClient.TrackEvent("SlaNotificationSent", new Dictionary<string, string>
            {
                { "TenantId", config.TenantId },
                { "Channel", !string.IsNullOrEmpty(webhookUrl) ? "Webhook+InApp" : "InApp" },
                { "NotificationType", "sla_breach" },
                { "BreachType", breachType },
            });

            _ = _opsEventService.RecordSlaBreachNotificationAsync(
                config.TenantId, breachType, currentValue, targetValue, totalSessions, failedSessions);
        }

        private async Task SendResolvedNotificationAsync(TenantConfiguration config, string breachType,
            double? currentValue, double? targetValue, DateTime? firstBreachAt, DateTime resolvedAt)
        {
            var alert = NotificationAlertBuilder.BuildSlaResolvedAlert(
                config.TenantId, breachType, currentValue, targetValue,
                firstBreachAt, resolvedAt, DashboardUrl);

            var (webhookUrl, providerType) = config.GetEffectiveWebhookConfig();
            if (!string.IsNullOrEmpty(webhookUrl) && providerType != 0)
            {
                await _webhookService.SendNotificationAsync(webhookUrl, (Shared.Models.Notifications.WebhookProviderType)providerType, alert);
            }

            await _tenantNotificationService.CreateNotificationAsync(
                config.TenantId,
                "sla_resolved",
                alert.Title,
                alert.Summary ?? "",
                "/sla");

            _telemetryClient.TrackEvent("SlaBreachResolved", new Dictionary<string, string>
            {
                { "TenantId", config.TenantId },
                { "BreachType", breachType },
                { "ResolvedAt", resolvedAt.ToString("O") },
            });
        }

        // ── Inline entry point (from IngestEventsFunction) ────────────────────

        /// <summary>
        /// Checks for consecutive enrollment failures after a session completes with failure.
        /// Designed to be called fire-and-forget — never throws.
        /// </summary>
        public async Task EvaluateSessionCompletionAsync(string tenantId, SessionSummary failedSession)
        {
            try
            {
                var config = await _configService.GetConfigurationAsync(tenantId);
                if (config == null || !config.SlaNotifyOnConsecutiveFailures)
                    return;

                var threshold = EffectiveConsecutiveFailureThreshold(config);

                var page = await _sessionRepo.GetSessionsPageAsync(tenantId, days: null, pageSize: threshold, continuation: null);
                if (page.Items.Count < threshold)
                    return;

                var allFailed = page.Items
                    .Take(threshold)
                    .All(s => s.Status == SessionStatus.Failed);
                if (!allFailed) return;

                var cooldown = await GetCooldownAsync();

                // CAS retry loop so two parallel inline calls (or inline ↔ timer) can't both
                // bypass the throttle. We commit *_LastNotifiedAt in the same write that decides
                // to notify, so a losing thread will refetch and see the watermark.
                for (int attempt = 0; attempt < MaxConflictRetries; attempt++)
                {
                    var now = _nowProvider();
                    var (statusFromRepo, etag) = await _statusRepo.GetWithETagAsync(tenantId);
                    var status = statusFromRepo ?? SlaTenantStatus.CreateEmpty(tenantId);

                    var firstBreach = !status.ConsecutiveFailures_IsActive;
                    if (firstBreach) status.ConsecutiveFailures_FirstAt = now;

                    status.ConsecutiveFailures_IsActive = true;
                    status.ConsecutiveFailures_Count = threshold;
                    status.ConsecutiveFailures_LastDevice = failedSession.DeviceName;
                    status.ConsecutiveFailures_LastReason = failedSession.FailureReason;
                    status.ConsecutiveFailures_ResolvedAt = null;

                    var shouldNotify = !status.ConsecutiveFailures_LastNotifiedAt.HasValue
                        || (now - status.ConsecutiveFailures_LastNotifiedAt.Value) >= cooldown;
                    if (shouldNotify)
                        status.ConsecutiveFailures_LastNotifiedAt = now;

                    status.LastEvaluatedAt = now;

                    if (!await _statusRepo.TryUpsertAsync(status, etag))
                    {
                        if (attempt + 1 < MaxConflictRetries) continue;
                        _logger.LogWarning("Consecutive-failures CAS conflict exhausted retries for tenant {TenantId}", tenantId);
                        return;
                    }

                    if (!shouldNotify) return;

                    var lastDevice = failedSession.DeviceName;
                    var lastReason = failedSession.FailureReason;

                    var alert = NotificationAlertBuilder.BuildConsecutiveFailuresAlert(
                        tenantId, threshold, lastDevice, lastReason, DashboardUrl);

                    var (webhookUrl, providerType) = config.GetEffectiveWebhookConfig();
                    if (!string.IsNullOrEmpty(webhookUrl) && providerType != 0)
                    {
                        await _webhookService.SendNotificationAsync(webhookUrl, (Shared.Models.Notifications.WebhookProviderType)providerType, alert);
                    }

                    await _tenantNotificationService.CreateNotificationAsync(
                        tenantId,
                        "sla_consecutive_failures",
                        alert.Title,
                        alert.Summary ?? "",
                        "/sla");

                    _telemetryClient.TrackEvent("SlaConsecutiveFailures", new Dictionary<string, string>
                    {
                        { "TenantId", tenantId },
                        { "ConsecutiveCount", threshold.ToString() },
                        { "LastDevice", lastDevice ?? "" },
                        { "LastFailureReason", lastReason ?? "" },
                    });

                    _telemetryClient.TrackEvent("SlaNotificationSent", new Dictionary<string, string>
                    {
                        { "TenantId", tenantId },
                        { "Channel", !string.IsNullOrEmpty(webhookUrl) ? "Webhook+InApp" : "InApp" },
                        { "NotificationType", "consecutive_failures" },
                    });

                    _ = _opsEventService.RecordSlaConsecutiveFailuresAsync(tenantId, threshold, lastDevice, lastReason);

                    _logger.LogWarning("Consecutive failure notification sent for tenant {TenantId}: {Count} failures",
                        tenantId, threshold);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to evaluate consecutive failures for tenant {TenantId}", tenantId);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        // Canonical effective consecutive-failure threshold. A configured value below the
        // minimum sensible window (< 2, i.e. unset or mis-set) falls back to the default of 5
        // — the same default used by the model, DB read, and the web UI. Using a single helper
        // for both the inline raise path and the timer resolve path guarantees the raise and
        // resolve windows stay symmetric (otherwise a breach raised at 5-in-a-row could be
        // resolved by the timer as soon as the top 2 weren't both failed).
        private static int EffectiveConsecutiveFailureThreshold(TenantConfiguration config)
            => config.SlaConsecutiveFailureThreshold < 2 ? 5 : config.SlaConsecutiveFailureThreshold;

        private async Task<TimeSpan> GetCooldownAsync()
        {
            var admin = await _adminConfigService.GetConfigurationAsync();
            var hours = Math.Clamp(admin.SlaNotificationCooldownHours, 1, 168);
            return TimeSpan.FromHours(hours);
        }

        // Telemetry tag — must match the actual evaluation window so Application Insights /
        // ops dashboards don't group AppInstall breaches under "CurrentMonth".
        internal static string PeriodForBreachType(string breachType) => breachType switch
        {
            SlaBreachType.AppInstall => "CurrentWeek",
            SlaBreachType.SuccessRate => "CurrentMonth",
            SlaBreachType.Duration => "CurrentMonth",
            _ => "CurrentPeriod",
        };
    }
}
