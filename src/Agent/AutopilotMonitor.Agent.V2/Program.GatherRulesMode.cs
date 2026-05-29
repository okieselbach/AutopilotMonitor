using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Transport.Telemetry;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2
{
    /// <summary>
    /// <c>--run-gather-rules</c> standalone diagnostic mode. Plan §4.x M4.6.δ.
    /// <para>
    /// Fetches remote config, executes all startup gather rules against a freshly-registered
    /// ephemeral session, uploads the collected events, and exits. Parity with Legacy
    /// <c>Program.GatherRulesMode.cs</c> — the V2 differences are minimal (V2 BackendApiClient
    /// ctor; both rails now share the same TenantIdResolver shape).
    /// </para>
    /// </summary>
    public static partial class Program
    {
        private const int GatherRulesMaxWaitSeconds = 120;

        internal static int RunGatherRulesMode(string[] args)
        {
            var consoleMode = args.Contains("--console") || Environment.UserInteractive;
            var logDir = Environment.ExpandEnvironmentVariables(Constants.LogDirectory);
            var logger = new AgentLogger(logDir) { EnableConsoleOutput = consoleMode };
            var agentVersion = GetAgentVersion();

            if (consoleMode)
            {
                Console.WriteLine("Autopilot Monitor Agent V2 — Run Gather Rules");
                Console.WriteLine("=============================================");
                Console.WriteLine();
            }

            try
            {
                var tenantId = TenantIdResolver.Resolve(logger);
                if (string.IsNullOrEmpty(tenantId))
                {
                    logger.Error("--run-gather-rules: TenantId could not be resolved — device is not MDM-enrolled.");
                    if (consoleMode) Console.Error.WriteLine("ERROR: device is not MDM-enrolled (no TenantId).");
                    return 2;
                }

                // Always use a fresh ephemeral session id — keeps the gather-rules run out of
                // any active enrollment session's spool / journal.
                var sessionId = Guid.NewGuid().ToString();
                var apiBaseUrl = GetArgValue(args, "--api-url", "--backend-api") ?? Constants.ApiBaseUrl;

                var config = new AgentConfiguration
                {
                    ApiBaseUrl = apiBaseUrl,
                    SessionId = sessionId,
                    TenantId = tenantId,
                    LogDirectory = logDir,
                    SpoolDirectory = Environment.ExpandEnvironmentVariables(Constants.SpoolDirectory),
                    UseClientCertAuth = true,
                    ImeLogPathOverride = GetArgValue(args, "--ime-log-path"),
                    CommandLineArgs = FormatArgsForGatherLog(args),
                };

                logger.Info("======================= --run-gather-rules mode =======================");

                if (consoleMode)
                {
                    Console.WriteLine($"Session ID: {sessionId}  (new, ephemeral)");
                    Console.WriteLine($"Tenant ID:  {tenantId}");
                    Console.WriteLine($"API URL:    {apiBaseUrl}");
                    Console.WriteLine();
                }

                // gather-rules-mode runs single-shot on a fully-provisioned VM; cert is expected
                // to be present. We mirror BackendClientFactory's cert-optional path: resolve
                // the cert, log a warning if absent, and continue — the eventual 401 surfaces
                // through register-session's catch block (line below) instead of crashing here.
                var hardwareInfo = HardwareInfo.GetHardwareInfo(logger);
                var clientCert = new DefaultCertificateResolver().FindClientCertificate(logger);
                if (clientCert == null)
                {
                    logger.Warning("--run-gather-rules: no MDM client certificate found — backend calls will likely fail security validation.");
                }
                // BackendApiClient takes ownership of the HttpClient and disposes it via its
                // own Dispose() — no outer using needed.
                using (var apiClient = new BackendApiClient(
                    httpClient: Runtime.BackendClientFactory.BuildBackendApiHttpClient(new NetworkMetrics(), clientCert, agentVersion),
                    baseUrl: apiBaseUrl,
                    manufacturer: hardwareInfo.Manufacturer,
                    model: hardwareInfo.Model,
                    serialNumber: hardwareInfo.SerialNumber,
                    useBootstrapTokenAuth: false,
                    bootstrapToken: null,
                    agentVersion: agentVersion,
                    logger: logger))
                using (var remoteConfigService = new RemoteConfigService(apiClient, tenantId, logger))
                {
                    // Step 1 — fetch remote config. FetchConfigAsync swallows transport errors
                    // internally and falls back to cached/default config, so the only way
                    // CurrentConfig stays null after Wait is a timeout (task still in flight).
                    AgentConfigResponse remoteConfig = null;
                    try
                    {
                        var configTask = remoteConfigService.FetchConfigAsync();
                        var completed = configTask.Wait(TimeSpan.FromSeconds(15));
                        if (!completed)
                        {
                            logger.Warning("Remote config fetch timed out after 15s — continuing without it.");
                            if (consoleMode) Console.WriteLine("WARNING: Remote config fetch timed out — continuing without it.");
                            // The Task keeps running against the soon-to-be-disposed HttpClient.
                            // FetchConfigAsync catches ObjectDisposedException internally so
                            // there is no escape, but we still observe the eventual outcome
                            // explicitly to satisfy the unobserved-task contract.
                            _ = configTask.ContinueWith(
                                t => { var _ignored = t.Exception; },
                                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
                                | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously);
                        }
                        else
                        {
                            remoteConfig = remoteConfigService.CurrentConfig;
                            if (remoteConfig != null)
                            {
                                logger.Info("Remote config fetched.");
                                if (consoleMode) Console.WriteLine("Remote config fetched.");
                            }
                            else
                            {
                                logger.Warning("Remote config fetch completed but CurrentConfig is null — continuing without it.");
                                if (consoleMode) Console.WriteLine("WARNING: Remote config fetch returned null — continuing without it.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Remote config fetch failed (continuing): {ex.Message}");
                        if (consoleMode) Console.WriteLine($"WARNING: Remote config fetch failed: {ex.Message}");
                    }

                    var rules = remoteConfig?.GatherRules;
                    var startupRules = rules?
                        .Where(r => r.Enabled && r.Trigger == "startup")
                        .ToList() ?? new List<GatherRule>();

                    if (startupRules.Count == 0)
                    {
                        logger.Info("No enabled startup gather rules found — nothing to execute.");
                        if (consoleMode) Console.WriteLine("No startup gather rules found. Nothing to execute.");
                        return 0;
                    }

                    // Step 2 — register the ephemeral session.
                    try
                    {
                        var registration = new SessionRegistration
                        {
                            SessionId = sessionId,
                            TenantId = tenantId,
                            SerialNumber = hardwareInfo.SerialNumber,
                            Manufacturer = hardwareInfo.Manufacturer,
                            Model = hardwareInfo.Model,
                            DeviceName = Environment.MachineName,
                            OsName = DeviceInfoProvider.GetOsName(),
                            OsBuild = DeviceInfoProvider.GetOsBuild(),
                            OsDisplayVersion = DeviceInfoProvider.GetOsDisplayVersion(),
                            OsEdition = DeviceInfoProvider.GetOsEdition(),
                            OsLanguage = System.Globalization.CultureInfo.CurrentCulture.Name,
                            StartedAt = DateTime.UtcNow,
                            AgentVersion = agentVersion,
                            EnrollmentType = "gather_rules",
                        };

                        var regResponse = apiClient.RegisterSessionAsync(registration).GetAwaiter().GetResult();
                        if (regResponse != null && regResponse.Success)
                            logger.Info($"Session registered: {regResponse.SessionId}");
                        else
                            logger.Warning($"Session registration: {regResponse?.Message ?? "null response"}");
                    }
                    catch (Exception ex)
                    {
                        logger.Warning($"Session registration failed (continuing): {ex.Message}");
                    }

                    // Step 3 — collect events in-memory, never touch the live agent's spool.
                    var collectedEvents = new List<EnrollmentEvent>();
                    var evtLock = new object();
                    long sequence = 0;

                    Action<EnrollmentEvent> emitEvent = evt =>
                    {
                        evt.Sequence = Interlocked.Increment(ref sequence);
                        lock (evtLock) collectedEvents.Add(evt);
                        logger.Debug($"Gather event: {evt.EventType} — {evt.Message}");
                        if (consoleMode)
                            Console.WriteLine($"  [{evt.Timestamp:HH:mm:ss}] [{evt.EventType}] {evt.Message}");
                    };

                    emitEvent(new EnrollmentEvent
                    {
                        SessionId = sessionId,
                        TenantId = tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = Constants.EventTypes.GatherRulesCollectionStarted,
                        Severity = EventSeverity.Info,
                        Source = "Agent",
                        Phase = EnrollmentPhase.Start,
                        Message = $"Gather rules collection started ({startupRules.Count} rule(s))",
                        Data = new Dictionary<string, object>
                        {
                            { "ruleCount", startupRules.Count },
                            { "agentVersion", agentVersion },
                        },
                    });

                    // Step 4 — run the executor and wait.
                    using (var executor = new GatherRuleExecutor(sessionId, tenantId, emitEvent, logger, config.ImeLogPathOverride))
                    {
                        executor.UpdateRules(rules);

                        if (consoleMode) Console.WriteLine($"Running {startupRules.Count} rule(s)...");
                        var allCompleted = executor.WaitForStartupRules(GatherRulesMaxWaitSeconds);
                        if (!allCompleted)
                        {
                            logger.Warning($"Some gather rules did not complete within {GatherRulesMaxWaitSeconds}s.");
                            if (consoleMode) Console.WriteLine($"WARNING: Some rules timed out after {GatherRulesMaxWaitSeconds}s.");
                        }
                        else
                        {
                            logger.Info("All startup rules completed.");
                            if (consoleMode) Console.WriteLine("All rules completed.");
                        }
                    }

                    // Step 5 — emit completion event, then upload.
                    List<EnrollmentEvent> eventsToUpload;
                    lock (evtLock) eventsToUpload = new List<EnrollmentEvent>(collectedEvents);

                    var totalEventCount = eventsToUpload.Count + 1;
                    emitEvent(new EnrollmentEvent
                    {
                        SessionId = sessionId,
                        TenantId = tenantId,
                        Timestamp = DateTime.UtcNow,
                        EventType = Constants.EventTypes.GatherRulesCollectionCompleted,
                        Severity = EventSeverity.Info,
                        Source = "Agent",
                        Phase = EnrollmentPhase.Start,
                        Message = $"Gather rules collection completed ({totalEventCount} event(s) collected)",
                        Data = new Dictionary<string, object> { { "totalEvents", totalEventCount } },
                    });
                    lock (evtLock) eventsToUpload = new List<EnrollmentEvent>(collectedEvents);

                    // Step 6 — upload via the V2 telemetry endpoint (POST /api/agent/telemetry).
                    // Single-shot, no spool, no orchestrator: gather-rules collects everything in
                    // memory above and posts one batch at the end.
                    if (eventsToUpload.Count > 0)
                    {
                        UploadGatheredEvents(
                            apiBaseUrl: apiBaseUrl,
                            tenantId: tenantId,
                            sessionId: sessionId,
                            agentVersion: agentVersion,
                            events: eventsToUpload,
                            logger: logger,
                            consoleMode: consoleMode);
                    }
                    else
                    {
                        logger.Info("No events were collected.");
                        if (consoleMode) Console.WriteLine("No events were collected.");
                    }
                }

                if (consoleMode) Console.WriteLine("\nGather rules run complete.");
                return 0;
            }
            catch (Exception ex)
            {
                logger.Error("--run-gather-rules failed.", ex);
                if (consoleMode) Console.Error.WriteLine($"ERROR: {ex.Message}");
                return 1;
            }
        }

        private static string FormatArgsForGatherLog(string[] args) => FormatArgsForLog(args);

        /// <summary>
        /// Wraps the in-memory <see cref="EnrollmentEvent"/> batch as <see cref="TelemetryItem"/>s
        /// (Kind=Event) and POSTs once to <c>/api/agent/telemetry</c> via
        /// <see cref="BackendTelemetryUploader"/>. The mTLS pipeline is built fresh for this run —
        /// gather-rules-mode is a standalone diagnostic on a fully-provisioned VM, not concurrent
        /// with a live enrollment, so a private <see cref="NetworkMetrics"/> instance is fine
        /// (we do not want to pollute the live agent's pipeline counter, which does not exist
        /// in this process anyway).
        /// </summary>
        private static void UploadGatheredEvents(
            string apiBaseUrl,
            string tenantId,
            string sessionId,
            string agentVersion,
            List<EnrollmentEvent> events,
            AgentLogger logger,
            bool consoleMode)
        {
            try
            {
                var hardware = HardwareInfo.GetHardwareInfo(logger);
                var partitionKey = $"{tenantId}_{sessionId}";
                var enqueuedAt = DateTime.UtcNow;

                var items = new List<TelemetryItem>(events.Count);
                for (int i = 0; i < events.Count; i++)
                {
                    var evt = events[i];
                    if (string.IsNullOrEmpty(evt.SessionId)) evt.SessionId = sessionId;
                    if (string.IsNullOrEmpty(evt.TenantId)) evt.TenantId = tenantId;

                    var rowKey = $"{evt.Timestamp:yyyyMMddHHmmssfff}_{evt.Sequence:D10}";
                    evt.RowKey = rowKey;

                    var payloadJson = JsonConvert.SerializeObject(evt, Formatting.None);

                    items.Add(new TelemetryItem(
                        kind: TelemetryItemKind.Event,
                        partitionKey: partitionKey,
                        rowKey: rowKey,
                        telemetryItemId: i,
                        sessionTraceOrdinal: null,
                        payloadJson: payloadJson,
                        requiresImmediateFlush: false,
                        enqueuedAtUtc: enqueuedAt,
                        retryCount: 0));
                }

                using (var httpClient = MtlsHttpClientFactory.Create(
                    resolver: new DefaultCertificateResolver(),
                    logger: logger,
                    metrics: new NetworkMetrics()))
                {
                    var uploader = new BackendTelemetryUploader(
                        httpClient: httpClient,
                        baseUrl: apiBaseUrl,
                        tenantId: tenantId,
                        manufacturer: hardware.Manufacturer,
                        model: hardware.Model,
                        serialNumber: hardware.SerialNumber,
                        bootstrapToken: null,
                        agentVersion: agentVersion,
                        authFailureTracker: null,
                        logger: logger);

                    var result = uploader.UploadBatchAsync(items, CancellationToken.None).GetAwaiter().GetResult();
                    if (result.Success)
                    {
                        logger.Info($"Uploaded {items.Count} event(s) successfully via /api/agent/telemetry.");
                        if (consoleMode) Console.WriteLine($"Uploaded {items.Count} event(s) successfully.");
                    }
                    else
                    {
                        logger.Warning($"Upload failed ({(result.IsTransient ? "transient" : "permanent")}): {result.ErrorReason}");
                        if (consoleMode) Console.WriteLine($"Upload failed: {result.ErrorReason}");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Error("Failed to upload events.", ex);
                if (consoleMode) Console.Error.WriteLine($"ERROR uploading events: {ex.Message}");
            }
        }
    }
}
