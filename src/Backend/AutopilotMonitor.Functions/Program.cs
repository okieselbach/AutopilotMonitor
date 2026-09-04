using System.IO.Compression;
using System.Text.Json;
using AutopilotMonitor.Functions.DataAccess;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Functions.Sessions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Middleware;
using AutopilotMonitor.Functions.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Identity.Web;

// First statement on purpose: BackendStartupMs measures from here (see StartupClock).
var startupClock = StartupClock.StartNow();

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Register middleware pipeline (Azure Functions .NET 8 isolated worker pattern)
// Order matters: request telemetry (wraps all) → correlation ID → global exception handler → JWT authentication (401) → policy enforcement (403)
builder.UseMiddleware<RequestTelemetryMiddleware>();
builder.UseMiddleware<CorrelationIdMiddleware>();
builder.UseMiddleware<TimingAllowOriginMiddleware>();
// Stamp static security-baseline headers (X-Content-Type-Options, Referrer-Policy,
// X-Frame-Options) on every response. Unconditional, runs early so even short-circuit
// 401/403/500 responses from downstream middleware inherit the headers.
builder.UseMiddleware<SecurityHeadersMiddleware>();
// Stamp Cache-Control: no-store on credential/identity endpoints before next() so
// short-circuit responses (401/403/500) from downstream middleware still inherit the
// header. Allowlist lives inside NoStoreCacheMiddleware.
builder.UseMiddleware<NoStoreCacheMiddleware>();
builder.UseMiddleware<GlobalExceptionMiddleware>();
builder.UseMiddleware<AuthenticationMiddleware>();
builder.UseMiddleware<PolicyEnforcementMiddleware>();
builder.UseMiddleware<UserRateLimitMiddleware>();
// MCP quota (daily/monthly budget on top of the per-minute rate limit): only requests marked
// X-Client-Source: mcp with an authenticated principal are checked; GA exempt; fail-open on
// counter errors, fail-closed on plan resolution. See McpQuotaEnforcementMiddleware.
builder.UseMiddleware<McpQuotaEnforcementMiddleware>();
// After policy enforcement (RequestContext resolved): record best-effort "last seen" presence for
// authenticated web users so a Global Admin can see who is active right now. Throttled + fail-open.
builder.UseMiddleware<UserPresenceMiddleware>();

// Configure JSON serialization to the wire settings (camelCase, absent-when-null, string enums).
// Single source: ApiJsonOptions — test harnesses build their serializer from the same settings.
builder.Services.Configure<JsonSerializerOptions>(ApiJsonOptions.Apply);

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// L4 ATTEMPT REVERTED 2026-06-09 (deploy-verified ineffective): tried to exclude Request;Event
// from worker-side adaptive sampling via EnableAdaptiveSampling=false + a manual
// Configure<TelemetryConfiguration> { DefaultTelemetrySink…UseAdaptiveSampling(excludedTypes) }.
// In the Functions isolated worker that classic-ASP.NET pattern does NOT take effect — live data
// showed the enriched worker request copy (Source=WorkerMiddleware) getting sampled to ~63% while
// the redundant host request item (Source="") stayed fully billed. Net worse (lossy enriched copy,
// unchanged host duplicate). The host request item is emitted by the HOST process and cannot be
// dropped from worker code or by host.json "Host.Results" level. Re-attempt only with a mechanism
// verified against the isolated-worker telemetry pipeline — do not re-add the manual chain.
// 2026-08-23: solved differently — RequestTelemetryMiddleware sets SamplingPercentage=100 on its
// own item (per-item sampling bypass), and the host duplicate is removed by a workspace DCR
// transformation on AppRequests (keeps rows with Properties.Source=='WorkerMiddleware' plus
// non-HTTP host rows with empty Url, i.e. timer/queue invocations). See internal/docs/backend/telemetry-ingest-shaping.md.

// Drop successful Azure Storage dependencies (Table/Queue/Blob) from the worker telemetry
// pipeline to curb AppDependencies ingestion cost — this backend is storage-I/O heavy and those
// rows are high-volume, low-value. Failed storage calls and all non-storage dependencies
// (HTTP/Graph/SQL/SignalR) are preserved. See StorageDependencyFilterProcessor for the contract.
builder.Services.AddApplicationInsightsTelemetryProcessor<AutopilotMonitor.Functions.Telemetry.StorageDependencyFilterProcessor>();

// Configure JWT Authentication for Multi-Tenant Azure AD
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(options =>
    {
        // Configure JWT Bearer options if needed
        options.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Auth");
                logger.LogWarning("Authentication failed: {Error}", context.Exception?.Message);
                return Task.CompletedTask;
            },
            OnTokenValidated = context =>
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Auth");
                var claims = context.Principal?.Claims;
                var tenantId = claims?.FirstOrDefault(c => c.Type == "tid")?.Value;
                logger.LogDebug("Token validated for tenant: {TenantId}", tenantId);
                return Task.CompletedTask;
            }
        };
    },
    options =>
    {
        // Multi-Tenant Configuration
        // Fully qualified: Microsoft.Identity.Web also exports a 'Constants' type.
        options.Instance = AutopilotMonitor.Shared.Constants.EntraLoginBaseUrl + "/";
        options.TenantId = "organizations"; // Accept tokens from any Azure AD tenant
        options.ClientId = builder.Configuration["EntraId:ClientId"];

        // Token validation parameters
        options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuers = new[]
            {
                AutopilotMonitor.Shared.Constants.EntraLoginBaseUrl + "/organizations/v2.0",
                "https://sts.windows.net/{tenantid}/"
            },
            // Full audience trust set — MUST match AuthenticationMiddleware's per-request set
            // (primary + legacy + additional client ids, each in bare and api:// form). Built via
            // the SAME shared helpers so the two lists cannot drift; a single-audience list here
            // would 401 legacy-app tokens on [Authorize]-gated paths during the dual app-reg window.
            ValidAudiences = AutopilotMonitor.Functions.Middleware.AuthenticationMiddleware.BuildValidAudiences(
                AutopilotMonitor.Functions.Middleware.AuthenticationMiddleware.ResolveConfiguredClientIds(
                    builder.Configuration["EntraId:ClientId"],
                    AutopilotMonitor.Functions.Middleware.AuthenticationMiddleware.CombineAdditionalClientIdSources(
                        builder.Configuration["EntraId:LegacyClientId"],
                        builder.Configuration["EntraId:AdditionalClientIds"]),
                    out _))
        };
    });

builder.Services.AddAuthorization();

// Enable ASP.NET Core integration for authentication
builder.Services.AddHttpContextAccessor();

// HTTP compression — bidirectional gzip for bandwidth-sensitive agent links.
// UseResponseCompression: backend → agent (config, ingest ack, ...). Triggered by Accept-Encoding.
// UseRequestDecompression: agent → backend (ingest NDJSON). Triggered by Content-Encoding.
// Registered via IStartupFilter because FunctionsApplication.CreateBuilder's Build() returns IHost,
// not WebApplication — we can't call app.UseXxx() directly.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes
        .Concat(new[] { "application/json", "application/x-ndjson" });
});
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.AddRequestDecompression();
builder.Services.AddTransient<IStartupFilter, HttpCompressionStartupFilter>();

// Register our services
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ResiliencePolicies>();
builder.Services.AddSingleton<TableStorageService>();
// Shared QueueClient bootstrap (code-quality audit D2): single Managed-Identity/connection-string
// resolution consumed by every queue producer / worker / probe.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Queueing.QueueClientFactory>();
// Cascade-deletion read surface implemented by the TableStorageService partial (PR1).
// No production caller in PR1; producer (PR3) + worker (PR4) consume it later.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.ISessionDeletionInventoryReader>(
    sp => sp.GetRequiredService<TableStorageService>());
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.DeletionManifestBuilder>();
// PR3: cascade-delete guard (writer-block invariant) + producer (CAS state-machine + queue enqueue).
// PR5 wires the producer into DeleteSessionFunction (admin-delete dispatcher) via the
// ISessionDeletionEnqueuer interface; PR6 will wire it into the dedicated
// SessionDeletionMaintenanceFunction (retention fanout).
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.SessionDeletionGuard>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.SessionDeletionProducer>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.ISessionDeletionEnqueuer>(
    sp => sp.GetRequiredService<AutopilotMonitor.Functions.Services.Deletion.SessionDeletionProducer>());
// PR4b: cascade-delete restore endpoint (full + partial-poisoned-recovery). Consumed by
// RestoreSessionFunction; GA-only via EndpointAccessPolicyCatalog. No hosted-service registration
// needed — the function is HTTP-triggered.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.SessionRestoreService>();
// PR4: cascade-delete worker pipeline. Verifier + Handler are pure DI plumbing; Worker is the
// queue poll-loop. Registered as HostedService in PR5 (the producer-wiring PR) so the queue
// drains as soon as the flag-gated DeleteSessionFunction starts producing envelopes; without
// this the session-deletion queue would back up.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.CascadeVerificationService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.SessionDeletionHandler>();
builder.Services.AddHostedService<AutopilotMonitor.Functions.Services.Deletion.SessionDeletionWorker>();

// PR6: cascade-delete maintenance subsystem — dedicated 12h timer with watchdog OpsEvents,
// retention fanout (V2 + legacy), manifest-blob TTL sweep, stale-Preparing GC, and stranded-
// Queued detection. Independent cadence + kill-switch from the generic 2h Maintenance timer.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.SessionRetentionFanoutService>();
// Manual-trigger path: blob-lease lock store (serializes timer vs manual runs), fail-hard
// queue producer for POST /api/global/session-deletions/maintenance/trigger, and the
// BackgroundService worker that consumes trigger envelopes. The maintenance Function class is
// registered explicitly so the worker can invoke the same RunCoreAsync body the timer uses.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.SessionDeletionMaintenanceLockStore>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Deletion.ISessionDeletionMaintenanceTriggerProducer,
    AutopilotMonitor.Functions.Services.Deletion.AzureQueueSessionDeletionMaintenanceTriggerProducer>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Functions.Maintenance.SessionDeletionMaintenanceFunction>();
builder.Services.AddHostedService<
    AutopilotMonitor.Functions.Services.Deletion.SessionDeletionMaintenanceQueueWorker>();

// Tenant-offboarding cascade worker (Plan Rev 9). Consumes per-tenant envelopes off
// `tenant-offboarding`, drives the §3 Phase 2 sequence (cascade enqueue → drain → SafeWipe →
// blob wipe → tenant config delete), and lands the audit row under AuditGlobalTenantId.
// OffboardingFilters is a static helper — no DI registration needed.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Offboarding.SafeWipeService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Offboarding.OffboardingSessionEnumerator>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Offboarding.IOffboardingExpectationsStore,
    AutopilotMonitor.Functions.Services.Offboarding.BlobOffboardingExpectationsStore>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Offboarding.IDeletionProgressDrainProbe,
    AutopilotMonitor.Functions.Services.Offboarding.DeletionProgressDrainProbe>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Offboarding.ITenantOffboardingEnqueuer,
    AutopilotMonitor.Functions.Services.Offboarding.AzureQueueTenantOffboardingEnqueuer>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Offboarding.TenantOffboardingHandler>();
builder.Services.AddHostedService<AutopilotMonitor.Functions.Services.Offboarding.TenantOffboardingWorker>();
// OffboardingMarkerCleanupFunction is TimerTrigger — auto-registered via the Functions host;
// no AddHostedService call needed. DI for its dependencies comes from the singletons above.

builder.Services.AddHostedService<TableInitializerService>(); // Initialize all tables at startup
builder.Services.AddSingleton(startupClock);
builder.Services.AddHostedService<StartupTelemetryService>();  // BackendStartupMs / BackendTableInitMs metrics on ApplicationStarted

// Data Access Layer — repository interfaces backed by Table Storage.
// To switch to Cosmos DB: replace AddTableStorageDataAccess() with AddCosmosDataAccess().
// To add event streaming: chain .AddEventStreaming<EventHubPublisher>() after this call.
builder.Services.AddTableStorageDataAccess();
builder.Services.AddSingleton<TenantConfigurationService>();
builder.Services.AddSingleton<TenantConfigPatchService>();
builder.Services.AddSingleton<AdminConfigurationService>();
builder.Services.AddSingleton<AppHomingService>();
builder.Services.AddSingleton<TenantEntitlementService>();
builder.Services.AddSingleton<McpQuotaService>();
builder.Services.AddSingleton<ILatestVersionsService, LatestVersionsService>();
builder.Services.AddSingleton<RateLimitService>();
builder.Services.AddSingleton<DistressRateLimitService>();
builder.Services.AddSingleton<UsageMetricsService>();
builder.Services.AddSingleton<PlatformMetricsService>();
builder.Services.AddSingleton<AgentEfficiencyMetricsService>();
builder.Services.AddSingleton<SlaMetricsService>();
builder.Services.AddSingleton<SlaBreachEvaluationService>();
// Identity binding (tid + oid behind every cross-tenant-role UPN) — consulted by both role services.
builder.Services.AddSingleton<AdminIdentityBindingService>();
builder.Services.AddSingleton<AdminIdentityResolver>();
builder.Services.AddSingleton<GlobalAdminService>();
builder.Services.AddSingleton<DelegatedAdminService>();
builder.Services.AddSingleton<DelegatedSlotService>();
builder.Services.AddSingleton<DelegationSelfService>();
builder.Services.AddSingleton<McpUserService>();
builder.Services.AddSingleton<PreviewWhitelistService>();
// Shared activation path (whitelist add + auto-promote + welcome email) — used by the
// Global Admin approve endpoint and the tenant auto-approve queue worker.
builder.Services.AddSingleton<TenantApprovalService>();
builder.Services.AddSingleton<TenantAdminsService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.TenantMemberRoleResolver>();
builder.Services.AddSingleton<HealthCheckService>();
builder.Services.AddSingleton<BackendBuildInfo>();
builder.Services.AddSingleton<GatherRuleService>();
builder.Services.AddSingleton<AnalyzeRuleService>();
// Cached per-tenant evaluateOn trigger sets for the ingest-side interim analyze enqueue
// (internal/docs/rules/analyze-rule-triggers.md). Singleton so the 5-min TTL cache is shared.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Analyze.InterimTriggerRegistry>();
builder.Services.AddSingleton<ImeLogPatternService>();
builder.Services.AddHttpClient<GitHubRuleRepository>()
    .AddPolicyHandler((sp, _) => sp.GetRequiredService<ResiliencePolicies>().ExternalDataApi);
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddSingleton<LegacyReclassificationService>();
builder.Services.AddSingleton<OccurredUtcBackfillService>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Monitoring.IAzureMonitorMetricsReader,
    AutopilotMonitor.Functions.Services.Monitoring.AzureMonitorMetricsReader>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Monitoring.IPoisonQueueProbe,
    AutopilotMonitor.Functions.Services.Monitoring.AzurePoisonQueueProbe>();
builder.Services.AddSingleton<OpsAlertDispatchService>();
builder.Services.AddSingleton<OpsEventService>();
// Assume-breach observer on the policy middleware's deny path (GlobalAdminOnly 403 → Critical ops event).
builder.Services.AddSingleton<IPrivilegedDenialReporter, PrivilegedDenialReporter>();
// Operator KQL proxy (global/raw/logs): the three telemetry stores it can reach, the managed-identity
// credential as an injectable seam, and a client WITHOUT its own timeout — the per-request budget
// (CancellationTokenSource + Prefer: wait) is the only clock.
builder.Services.AddSingleton<LogQuerySourceCatalog>();
builder.Services.AddSingleton<Azure.Core.TokenCredential>(_ => new Azure.Identity.DefaultAzureCredential());
builder.Services.AddHttpClient(LogQuerySourceCatalog.HttpClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
// IME pattern-drift loop: folds ime_pattern_hits into ImePatternStats and raises
// ImePatternDriftSuspected; singleton because it caches the stats snapshot for evaluation.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Ime.ImePatternHealthService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.SessionOwnerBindingObserver>();
builder.Services.AddSingleton<BlockedDeviceService>();
builder.Services.AddSingleton<HardwareRejectionThrottleService>();
builder.Services.AddSingleton<BlockedVersionService>();
builder.Services.AddSingleton<KillSwitchEvaluator>();
builder.Services.AddSingleton<BlobStorageService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Diagnostics.HostedDiagnosticsBlobService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Diagnostics.DiagnosticsBlobStreamer>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Diagnostics.DiagnosticsBlobCascadeDeleter>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Diagnostics.SessionReportDiagnosticsArchiveCopier>();
builder.Services.AddSingleton<SessionReportService>();
builder.Services.AddSingleton<BootstrapSessionService>();

// V2 Decision Engine index-table dual-write producer (Plan §2.8, §M5.d). Gated by
// AdminConfiguration.EnableIndexDualWrite (default false) inside the implementation.
builder.Services.AddSingleton<
    AutopilotMonitor.Shared.DataAccess.IIndexReconcileProducer,
    AutopilotMonitor.Functions.Services.Indexing.AzureQueueIndexReconcileProducer>();

// V2 Decision Engine index-table reconcile consumer (Plan §M5.d.3). Plain class, not
// interface-abstracted — Cosmos swap would reshape around IIndexTableRepository, not here.
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Indexing.IndexReconcileHandler>();

// Background poll-loop for the telemetry-index-reconcile queue (Plan §M5.d.3). Replaces the
// earlier QueueTrigger function, which required a Functions-host-specific
// `<Connection>__queueServiceUri` app-setting that diverged from the rest of the project's
// AzureStorageAccountName + DefaultAzureCredential pattern. This worker uses the same
// resolution as AzureQueueIndexReconcileProducer — Managed Identity by account name, with
// connection-string fallback — and provides full QueueTrigger parity (visibility-timeout
// retries, poison-queue move after 5 failed attempts).
builder.Services.AddHostedService<
    AutopilotMonitor.Functions.Services.Indexing.IndexReconcileQueueWorker>();

// Auto-analyze fan-out at session end. Replaces the previous in-function fire-and-forget
// Task.Run that ran the rule engine after enrollment_complete / enrollment_failed / async
// vulnerability correlation — Functions scale-in could kill the Task.Run mid-flight, leaving
// rule results un-persisted (manual "Analyze Now" was the only recovery). Same producer +
// worker pattern as IndexReconcile above.
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Analyze.IAnalyzeOnEnrollmentEndProducer,
    AutopilotMonitor.Functions.Services.Analyze.AzureQueueAnalyzeOnEnrollmentEndProducer>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Analyze.AnalyzeOnEnrollmentEndHandler>();
builder.Services.AddHostedService<
    AutopilotMonitor.Functions.Services.Analyze.AnalyzeOnEnrollmentEndQueueWorker>();

// Vulnerability-correlate fan-out triggered by the shutdown software_inventory_analysis
// event. Replaces the in-function fire-and-forget Task.Run inside EventIngestProcessor.
// Same producer + worker pattern as the analyze queue. Inventory loader is DI-shared with
// the manual rescan endpoint (GetVulnerabilityReportFunction ?rescan=true).
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Vulnerability.IVulnerabilityCorrelateProducer,
    AutopilotMonitor.Functions.Services.Vulnerability.AzureQueueVulnerabilityCorrelateProducer>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Vulnerability.IVulnerabilityInventoryLoader,
    AutopilotMonitor.Functions.Services.Vulnerability.VulnerabilityInventoryLoader>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Vulnerability.VulnerabilityCorrelateHandler>();
builder.Services.AddHostedService<
    AutopilotMonitor.Functions.Services.Vulnerability.VulnerabilityCorrelateQueueWorker>();

// IME-installer archiving on first fleet-wide sighting of a new IME version (~monthly).
// Producer sits in EventIngestProcessor's new-version continuation; the worker downloads
// the MSI from the CSP-reported (allowlisted) URL into the permanent ime-archive container
// and merges the outcome onto the ImeVersionHistory row. Typed HttpClient: no redirects
// (SSRF posture — the allowlisted host must serve directly), long timeout owned by the
// archiver's own CTS, transient retry via the shared external-data policy.
builder.Services.AddHttpClient<AutopilotMonitor.Functions.Services.Ime.ImeMsiArchiver>()
    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
    .AddPolicyHandler((sp, _) => sp.GetRequiredService<ResiliencePolicies>().ExternalDataApi);
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Ime.IImeMsiArchiveProducer,
    AutopilotMonitor.Functions.Services.Ime.AzureQueueImeMsiArchiveProducer>();
builder.Services.AddHostedService<
    AutopilotMonitor.Functions.Services.Ime.ImeMsiArchiveQueueWorker>();

// Delayed tenant auto-approve (public availability). Producer fires at first-login signup
// with a ~1-minute visibility delay; the worker activates the tenant via the shared
// TenantApprovalService only if AdminConfiguration.AutoApproveNewTenants is enabled at
// processing time. Same producer + worker pattern as the analyze queue above.
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Activation.ITenantAutoApproveEnqueuer,
    AutopilotMonitor.Functions.Services.Activation.AzureQueueTenantAutoApproveEnqueuer>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Activation.TenantAutoApproveHandler>();
builder.Services.AddHostedService<
    AutopilotMonitor.Functions.Services.Activation.TenantAutoApproveQueueWorker>();

// ── Critical-Table Backup (plan §PR1) ─────────────────────────────────────────
// Lease-aware service registered separately from the timer/worker so the lease
// boundary is testable in isolation. JobsRepository is the canonical persistence
// for the 6-state machine.
builder.Services.AddSingleton<AutopilotMonitor.Functions.DataAccess.TableStorage.BackupJobsRepository>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Backup.BlobBackupStore>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Backup.ICriticalTableBackupService,
    AutopilotMonitor.Functions.Services.Backup.CriticalTableBackupService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Backup.BackupJobWatchdog>();
// Producer = fail-hard SendMessage (HTTP trigger handles the rollback to Failed).
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Backup.Queue.ICriticalTableBackupProducer,
    AutopilotMonitor.Functions.Services.Backup.Queue.AzureQueueCriticalTableBackupProducer>();
// Worker = BackgroundService, not QueueTrigger (matches AnalyzeOnEnrollmentEndQueueWorker
// pattern per plan §Wave4 #1 — avoids the QueueTrigger-specific connection app-setting).
builder.Services.AddHostedService<
    AutopilotMonitor.Functions.Services.Backup.Queue.CriticalTableBackupQueueWorker>();
// PR2: single-row restore. Both validators are pure / I/O-only; the restore
// service is stateless (lease lifecycle is owned per-call). Singleton is safe.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Backup.RestoreTablePreflightValidator>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Backup.BackupRestoreInputValidator>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Backup.CriticalTableRestoreService>();

// Programmatic SignalR push for background tasks (rule engine, vulnerability correlation)
builder.Services.AddSingleton<SignalRNotificationService>();
builder.Services.AddSingleton<ISignalRNotificationService>(sp => sp.GetRequiredService<SignalRNotificationService>());

// Vulnerability correlation services
builder.Services.AddHttpClient<AutopilotMonitor.Functions.Services.Vulnerability.NvdApiClient>()
    .AddPolicyHandler((sp, _) => sp.GetRequiredService<ResiliencePolicies>().ExternalDataApi);
builder.Services.AddHttpClient<AutopilotMonitor.Functions.Services.Vulnerability.KevDataService>()
    .AddPolicyHandler((sp, _) => sp.GetRequiredService<ResiliencePolicies>().ExternalDataApi);
builder.Services.AddHttpClient<AutopilotMonitor.Functions.Services.Vulnerability.MsrcApiClient>()
    .AddPolicyHandler((sp, _) => sp.GetRequiredService<ResiliencePolicies>().ExternalDataApi);
builder.Services.AddHttpClient<AutopilotMonitor.Functions.Services.Vulnerability.EpssApiClient>()
    .AddPolicyHandler((sp, _) => sp.GetRequiredService<ResiliencePolicies>().ExternalDataApi);
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Vulnerability.MsrcCorrelationService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Vulnerability.VulnerabilityCacheRefreshService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Vulnerability.VulnerabilityCorrelationService>();
// Hydrate MSRC + KEV in-memory caches from blob snapshots at app startup (fire-and-forget;
// keeps cold-start fast for re-deploys, see VulnerabilityCacheWarmer for the contract).
builder.Services.AddHostedService<AutopilotMonitor.Functions.Services.Vulnerability.VulnerabilityCacheWarmer>();

// Register agent Function classes so bootstrap wrappers can inject them for code reuse
builder.Services.AddSingleton<EventIngestProcessor>();
builder.Services.AddSingleton<RegisterSessionFunction>();
builder.Services.AddSingleton<GetAgentConfigFunction>();
builder.Services.AddSingleton<ReportAgentErrorFunction>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.EntraAppRegistry>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.GraphTokenService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.AutopilotDeviceValidator>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.CorporateIdentifierValidator>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.DeviceAssociationValidator>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.CloudPcDeviceValidator>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.IntuneDeviceBindingValidator>();

// Graph add-on permission detection + script display-name resolution.
// Detector parses the `roles` claim of the SP access token; Resolver fetches
// Intune script display names from Graph beta, cached per tenant.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.GraphResolution.IGraphFeatureDetector,
    AutopilotMonitor.Functions.Services.GraphResolution.GraphFeatureDetector>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.GraphResolution.IScriptDisplayNameResolver,
    AutopilotMonitor.Functions.Services.GraphResolution.ScriptDisplayNameResolver>();
builder.Services.AddHttpClient<AutopilotMonitor.Functions.Services.Notifications.WebhookNotificationService>()
    .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(10),
    })
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(15))
    .AddPolicyHandler((sp, _) => sp.GetRequiredService<ResiliencePolicies>().Notification);
builder.Services.AddHttpClient<TelegramNotificationService>()
    .AddPolicyHandler((sp, _) => sp.GetRequiredService<ResiliencePolicies>().Notification);
// Channel-level send API — routes each NotificationChannel to its transport (webhook renderer
// vs. the platform Telegram bot). Transient: both transports are typed HttpClients.
builder.Services.AddTransient<AutopilotMonitor.Functions.Services.Notifications.NotificationChannelDispatcher>();
// Transactional email (welcome / farewell). Typed client like the other outbound notifiers;
// the provider is an implementation detail of EmailService + the Email:* settings.
builder.Services.AddSingleton<EmailTemplateService>();
builder.Services.AddSingleton<IEmailTemplateProvider>(sp => sp.GetRequiredService<EmailTemplateService>());
builder.Services.AddHttpClient<EmailService>()
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(15))
    .AddPolicyHandler((sp, _) => sp.GetRequiredService<ResiliencePolicies>().Notification);
builder.Services.AddTransient<IEmailService>(sp => sp.GetRequiredService<EmailService>());
builder.Services.AddTransient<IOffboardFarewellEmailSender>(sp => sp.GetRequiredService<EmailService>());
builder.Services.AddSingleton<GlobalNotificationService>();
builder.Services.AddSingleton<TenantNotificationService>();

var app = builder.Build();

// Validate critical security configuration at startup
var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
var entraClientId = builder.Configuration["EntraId:ClientId"];
var entraClientSecret = builder.Configuration["EntraId:ClientSecret"];
if (string.IsNullOrEmpty(entraClientId))
    startupLogger.LogWarning("EntraId:ClientId is not configured — JWT audience validation and Graph API calls will fail");
if (string.IsNullOrEmpty(entraClientSecret))
    startupLogger.LogWarning("EntraId:ClientSecret is not configured — device validation via Graph API will fail at runtime");
// Dual app-registration window: the legacy pair must be set together — an id without a secret
// breaks Graph for every legacy-homed tenant, a secret without an id is dead config.
var entraLegacyClientId = builder.Configuration["EntraId:LegacyClientId"];
var entraLegacyClientSecret = builder.Configuration["EntraId:LegacyClientSecret"];
if (string.IsNullOrEmpty(entraLegacyClientId) != string.IsNullOrEmpty(entraLegacyClientSecret))
    startupLogger.LogWarning(
        "EntraId:LegacyClientId and EntraId:LegacyClientSecret must be configured together — only one is set, legacy-homed tenants will fail Graph token acquisition");

// Log CORS configuration at startup so misconfigured origins are immediately visible
// in the log stream. CORS is enforced by Azure infrastructure, not by function code,
// so a blocked preflight never reaches the function worker and leaves no trace.
var corsOrigins = builder.Configuration["Host:CORS"]                   // local.settings.json
    ?? builder.Configuration["WEBSITE_CORS_ALLOWED_ORIGINS"]           // Azure App Settings
    ?? "(not configured - all cross-origin requests will be blocked!)";
var corsCredentials = builder.Configuration["Host:CORSCredentials"]
    ?? builder.Configuration["WEBSITE_CORS_SUPPORT_CREDENTIALS"]
    ?? "unknown";
startupLogger.LogInformation(
    "=== CORS CONFIG: AllowedOrigins={CorsOrigins} | SupportCredentials={CorsCredentials} ===",
    corsOrigins, corsCredentials);

app.Run();
