using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
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

var builder = FunctionsApplication.CreateBuilder(args);

builder.ConfigureFunctionsWebApplication();

// Register middleware pipeline (Azure Functions .NET 8 isolated worker pattern)
// Order matters: request telemetry (wraps all) → correlation ID → global exception handler → JWT authentication (401) → policy enforcement (403)
builder.UseMiddleware<RequestTelemetryMiddleware>();
builder.UseMiddleware<CorrelationIdMiddleware>();
builder.UseMiddleware<TimingAllowOriginMiddleware>();
builder.UseMiddleware<GlobalExceptionMiddleware>();
builder.UseMiddleware<AuthenticationMiddleware>();
builder.UseMiddleware<PolicyEnforcementMiddleware>();
builder.UseMiddleware<UserRateLimitMiddleware>();

// Configure JSON serialization to use camelCase
builder.Services.Configure<JsonSerializerOptions>(options =>
{
    options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    // Serialize enums as strings for better readability and frontend compatibility
    options.Converters.Add(new JsonStringEnumConverter());
});

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

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
        options.Instance = "https://login.microsoftonline.com/";
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
                "https://login.microsoftonline.com/organizations/v2.0",
                "https://sts.windows.net/{tenantid}/"
            },
            // Temporarily accept Microsoft Graph tokens (used by frontend with User.Read scope)
            // TODO: Later expose custom API and use api://{clientId} scopes
            ValidAudiences = new[]
            {
                builder.Configuration["EntraId:ClientId"] // Our app's client ID
                //"https://graph.microsoft.com", // Microsoft Graph
                //"00000003-0000-0000-c000-000000000000" // Microsoft Graph App ID
            }
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
// Cascade-deletion read surface implemented by the TableStorageService partial (PR1).
// No production caller in PR1; producer (PR3) + worker (PR4) consume it later.
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.ISessionDeletionInventoryReader>(
    sp => sp.GetRequiredService<TableStorageService>());
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Deletion.DeletionManifestBuilder>();
builder.Services.AddHostedService<TableInitializerService>(); // Initialize all tables at startup

// Data Access Layer — repository interfaces backed by Table Storage.
// To switch to Cosmos DB: replace AddTableStorageDataAccess() with AddCosmosDataAccess().
// To add event streaming: chain .AddEventStreaming<EventHubPublisher>() after this call.
builder.Services.AddTableStorageDataAccess();
builder.Services.AddSingleton<TenantConfigurationService>();
builder.Services.AddSingleton<AdminConfigurationService>();
builder.Services.AddSingleton<ILatestVersionsService, LatestVersionsService>();
builder.Services.AddSingleton<RateLimitService>();
builder.Services.AddSingleton<DistressRateLimitService>();
builder.Services.AddSingleton<UsageMetricsService>();
builder.Services.AddSingleton<PlatformMetricsService>();
builder.Services.AddSingleton<SlaMetricsService>();
builder.Services.AddSingleton<SlaBreachEvaluationService>();
builder.Services.AddSingleton<GlobalAdminService>();
builder.Services.AddSingleton<McpUserService>();
builder.Services.AddSingleton<PreviewWhitelistService>();
builder.Services.AddSingleton<TenantAdminsService>();
builder.Services.AddSingleton<HealthCheckService>();
builder.Services.AddSingleton<BackendBuildInfo>();
builder.Services.AddSingleton<GatherRuleService>();
builder.Services.AddSingleton<AnalyzeRuleService>();
builder.Services.AddSingleton<ImeLogPatternService>();
builder.Services.AddHttpClient<GitHubRuleRepository>()
    .AddPolicyHandler((sp, _) => sp.GetRequiredService<ResiliencePolicies>().ExternalDataApi);
builder.Services.AddSingleton<MaintenanceService>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Monitoring.IAzureMonitorMetricsReader,
    AutopilotMonitor.Functions.Services.Monitoring.AzureMonitorMetricsReader>();
builder.Services.AddSingleton<
    AutopilotMonitor.Functions.Services.Monitoring.IPoisonQueueProbe,
    AutopilotMonitor.Functions.Services.Monitoring.AzurePoisonQueueProbe>();
builder.Services.AddSingleton<OpsAlertDispatchService>();
builder.Services.AddSingleton<OpsEventService>();
builder.Services.AddSingleton<BlockedDeviceService>();
builder.Services.AddSingleton<HardwareRejectionThrottleService>();
builder.Services.AddSingleton<BlockedVersionService>();
builder.Services.AddSingleton<BlobStorageService>();
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
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Vulnerability.MsrcCorrelationService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Services.Vulnerability.VulnerabilityCorrelationService>();
// Hydrate MSRC + KEV in-memory caches from blob snapshots at app startup (fire-and-forget;
// keeps cold-start fast for re-deploys, see VulnerabilityCacheWarmer for the contract).
builder.Services.AddHostedService<AutopilotMonitor.Functions.Services.Vulnerability.VulnerabilityCacheWarmer>();

// Register agent Function classes so bootstrap wrappers can inject them for code reuse
builder.Services.AddSingleton<IngestEventsFunction>();
builder.Services.AddSingleton<EventIngestProcessor>();
builder.Services.AddSingleton<RegisterSessionFunction>();
builder.Services.AddSingleton<GetAgentConfigFunction>();
builder.Services.AddSingleton<ReportAgentErrorFunction>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.GraphTokenService>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.AutopilotDeviceValidator>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.CorporateIdentifierValidator>();
builder.Services.AddSingleton<AutopilotMonitor.Functions.Security.DeviceAssociationValidator>();
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
builder.Services.AddSingleton<ResendEmailService>();
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
