using AutopilotMonitor.Functions.Services.Monitoring;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Service for performing health checks
/// </summary>
public class HealthCheckService
{
    private readonly ILogger<HealthCheckService> _logger;
    private readonly AdminConfigurationService _adminConfigService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IAzureMonitorMetricsReader _metricsReader;
    private readonly IPoisonQueueProbe _poisonQueueProbe;
    private readonly IConfiguration _configuration;

    public HealthCheckService(
        ILogger<HealthCheckService> logger,
        AdminConfigurationService adminConfigService,
        IHttpClientFactory httpClientFactory,
        IAzureMonitorMetricsReader metricsReader,
        IPoisonQueueProbe poisonQueueProbe,
        IConfiguration configuration)
    {
        _logger = logger;
        _adminConfigService = adminConfigService;
        _httpClientFactory = httpClientFactory;
        _metricsReader = metricsReader;
        _poisonQueueProbe = poisonQueueProbe;
        _configuration = configuration;
    }

    /// <summary>
    /// Performs all health checks and returns the results
    /// </summary>
    public async Task<HealthCheckResult> PerformAllChecksAsync()
    {
        var result = new HealthCheckResult
        {
            Timestamp = DateTime.UtcNow,
            Checks = new List<HealthCheck>()
        };

        var checks = await Task.WhenAll(
            CheckStorageBackendAsync(),
            CheckProcessingBackendAsync(),
            CheckAgentBinariesAsync(),
            CheckMcpServerAsync(),
            CheckSignalRQuotaAsync(),
            CheckPoisonQueuesAsync()
        );

        result.Checks.AddRange(checks);

        // Tri-state combiner: any unhealthy → unhealthy; any warning (no unhealthy) → warning;
        // else healthy. "unknown" checks (e.g. SignalR watcher not configured) don't affect overall.
        var rated = result.Checks.Where(c => c.Status != "unknown").ToList();
        result.OverallStatus =
            rated.Any(c => c.Status == "unhealthy") ? "unhealthy" :
            rated.Any(c => c.Status == "warning") ? "warning" :
            "healthy";

        return result;
    }

    /// <summary>
    /// Checks Azure Table Storage connectivity by querying the admin configuration table
    /// </summary>
    private async Task<HealthCheck> CheckStorageBackendAsync()
    {
        var check = new HealthCheck
        {
            Name = "Storage Backend",
            Description = "Data storage connectivity"
        };

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await _adminConfigService.GetConfigurationAsync();
            sw.Stop();

            check.Status = "healthy";
            check.Message = $"Storage reachable ({sw.ElapsedMilliseconds}ms)";
        }
        catch (Exception ex)
        {
            check.Status = "unhealthy";
            check.Message = $"Storage unreachable: {ex.Message}";
            _logger.LogError(ex, "Storage backend health check failed");
        }

        return check;
    }

    /// <summary>
    /// Checks that the Azure Functions host is running
    /// </summary>
    private static Task<HealthCheck> CheckProcessingBackendAsync()
    {
        var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

        return Task.FromResult(new HealthCheck
        {
            Name = "Processing Backend",
            Description = "Application host process",
            Status = "healthy",
            Message = $"Host running (uptime: {(int)uptime.TotalMinutes}m {uptime.Seconds}s)"
        });
    }

    /// <summary>
    /// Checks that the agent binaries (ZIP) and bootstrap script (PS1) are reachable in blob storage
    /// </summary>
    private async Task<HealthCheck> CheckAgentBinariesAsync()
    {
        var check = new HealthCheck
        {
            Name = "Agent Binaries",
            Description = "Agent download package availability"
        };

        var zipUrl = $"{Constants.AgentBlobBaseUrl}/{Constants.AgentZipFileName}";
        var ps1Url = $"{Constants.AgentBlobBaseUrl}/Install-AutopilotMonitor.ps1";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // HEAD requests to check availability without downloading
            var zipRequest = new HttpRequestMessage(HttpMethod.Head, zipUrl);
            var ps1Request = new HttpRequestMessage(HttpMethod.Head, ps1Url);

            var results = await Task.WhenAll(
                client.SendAsync(zipRequest),
                client.SendAsync(ps1Request)
            );

            sw.Stop();

            var zipResponse = results[0];
            var ps1Response = results[1];

            var zipOk = zipResponse.IsSuccessStatusCode;
            var ps1Ok = ps1Response.IsSuccessStatusCode;

            if (zipOk && ps1Ok)
            {
                check.Status = "healthy";
                check.Message = $"Agent package and bootstrap script available ({sw.ElapsedMilliseconds}ms)";
            }
            else
            {
                check.Status = "unhealthy";
                var issues = new List<string>();
                if (!zipOk) issues.Add($"Agent ZIP: {(int)zipResponse.StatusCode}");
                if (!ps1Ok) issues.Add($"Bootstrap script: {(int)ps1Response.StatusCode}");
                check.Message = $"Missing: {string.Join(", ", issues)}";
            }
        }
        catch (Exception ex)
        {
            check.Status = "unhealthy";
            check.Message = $"Blob storage unreachable: {ex.Message}";
            _logger.LogError(ex, "Agent binaries health check failed");
        }

        return check;
    }

    /// <summary>
    /// Probes the MCP server's public <c>/health</c> endpoint (which sits in front of the
    /// <c>/mcp</c> access guard, so no auth is required) and surfaces reachability + the
    /// deployed MCP build version. Visible to all authenticated users, so the server URL is
    /// deliberately NOT included in the message or details. The MCP Container App runs with
    /// minReplicas=0, so the first probe after idle may incur a cold start within the 10s
    /// timeout — a slow-but-successful probe still reports healthy.
    /// </summary>
    internal async Task<HealthCheck> CheckMcpServerAsync()
    {
        var check = new HealthCheck
        {
            Name = "MCP Server",
            Description = "AI query interface availability"
        };

        var baseUrl = (_configuration["McpServerUrl"] ?? Constants.McpServerBaseUrl).TrimEnd('/');
        var healthUrl = $"{baseUrl}/health";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var response = await client.GetAsync(healthUrl);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                check.Status = "unhealthy";
                check.Message = $"MCP server returned {(int)response.StatusCode}";
                return check;
            }

            var version = await TryReadMcpVersionAsync(response);

            check.Status = "healthy";
            check.Message = $"MCP server reachable ({sw.ElapsedMilliseconds}ms)";
            if (!string.IsNullOrWhiteSpace(version))
            {
                check.Details = new Dictionary<string, object> { ["Version"] = version };
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException or HttpRequestException)
        {
            // Timeout or connection failure. The Container App scales to zero on idle and
            // only starts serving /health after a multi-second cold start (knowledge-base +
            // embeddings load), so this is most often a warming instance, not an outage —
            // classify as a warning so an idle dashboard load does not raise a false alarm.
            // This very probe wakes the scaler, so a re-check shortly after should go green.
            check.Status = "warning";
            check.Message = "MCP server not responding — it may be cold-starting (scaled to zero on idle). Re-check in a moment.";
            _logger.LogWarning(ex, "MCP server health probe did not respond (possible cold start)");
        }
        catch (Exception ex)
        {
            check.Status = "unhealthy";
            check.Message = $"MCP server health probe failed: {ex.Message}";
            _logger.LogError(ex, "MCP server health check failed");
        }

        return check;
    }

    /// <summary>
    /// Best-effort parse of the MCP <c>/health</c> JSON body for its <c>version</c> field.
    /// A missing or malformed body is non-fatal — the probe already proved reachability.
    /// </summary>
    private async Task<string?> TryReadMcpVersionAsync(HttpResponseMessage response)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not parse MCP /health version field");
            return null;
        }
    }

    /// <summary>
    /// Reads the same SignalR ConnectionCount + MessageCount metrics that the Ops watcher
    /// (see <see cref="MaintenanceService"/> SignalR-quota partial) classifies, and surfaces
    /// the live state as a health-check entry. Live read on each call — no cache. The
    /// underlying <see cref="IAzureMonitorMetricsReader"/> is fail-soft (returns null on
    /// auth/throttle/missing-data), so this method classifies that case as "unhealthy" with
    /// a clear message rather than crashing the dashboard.
    /// </summary>
    private async Task<HealthCheck> CheckSignalRQuotaAsync()
    {
        var check = new HealthCheck
        {
            Name = "SignalR Quota",
            Description = "Free-tier connection + daily message usage"
        };

        var resourceId = _configuration["SignalRResourceId"];
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            check.Status = "unknown";
            check.Message = "SignalR quota watcher not configured (SignalRResourceId app setting unset)";
            return check;
        }

        var connectionLimit = ParsePositiveInt(
            "SignalRFreeTierConnectionLimit", MaintenanceService.DefaultSignalRConnectionLimit);
        var messageLimit = ParsePositiveLong(
            "SignalRFreeTierDailyMessageLimit", MaintenanceService.DefaultSignalRDailyMessageLimit);

        var nowUtc = DateTimeOffset.UtcNow;
        var startOfDayUtc = new DateTimeOffset(nowUtc.UtcDateTime.Date, TimeSpan.Zero);

        // Two parallel Azure Monitor reads — same shape as the watcher uses.
        var connectionsTask = _metricsReader.GetMaximumAsync(
            resourceId, "ConnectionCount", TimeSpan.FromHours(1), CancellationToken.None);
        var messagesTask = _metricsReader.GetTotalAsync(
            resourceId, "MessageCount", startOfDayUtc, nowUtc, CancellationToken.None);

        await Task.WhenAll(connectionsTask, messagesTask);
        var observedConnections = await connectionsTask;
        var observedMessages = await messagesTask;

        // Both reads failed → watcher is blind. Most likely cause: managed identity
        // missing the "Monitoring Reader" role on the SignalR resource.
        if (observedConnections is null && observedMessages is null)
        {
            check.Status = "unhealthy";
            check.Message = "Azure Monitor returned no data for ConnectionCount or MessageCount — check the Function App's managed identity has 'Monitoring Reader' on the SignalR resource";
            check.Details = new Dictionary<string, object>
            {
                ["Resource"] = resourceId
            };
            return check;
        }

        var details = new Dictionary<string, object>();
        var worstTier = MaintenanceService.SignalRQuotaTier.None;

        if (observedConnections is not null)
        {
            var observedInt = (int)Math.Ceiling(observedConnections.Value);
            var percent = MaintenanceService.CalculatePercent(observedInt, connectionLimit);
            var tier = MaintenanceService.ClassifySignalRQuotaTier(percent);
            if (tier > worstTier) worstTier = tier;
            details["Connections (max/1h)"] = $"{observedInt}/{connectionLimit} ({percent}%)";
        }
        else
        {
            details["Connections (max/1h)"] = "no data";
        }

        if (observedMessages is not null)
        {
            var observedLong = (long)Math.Ceiling(observedMessages.Value);
            var percent = MaintenanceService.CalculatePercent(observedLong, messageLimit);
            var tier = MaintenanceService.ClassifySignalRQuotaTier(percent);
            if (tier > worstTier) worstTier = tier;
            details["Messages (today, UTC)"] = $"{observedLong:N0}/{messageLimit:N0} ({percent}%)";
        }
        else
        {
            details["Messages (today, UTC)"] = "no data";
        }

        details["Resource"] = resourceId;

        check.Status = worstTier switch
        {
            MaintenanceService.SignalRQuotaTier.Critical => "unhealthy",
            MaintenanceService.SignalRQuotaTier.Warning => "warning",
            _ => "healthy"
        };

        check.Message = worstTier switch
        {
            MaintenanceService.SignalRQuotaTier.Critical => "Free-tier limit nearly saturated — scale to Standard before clients get 429'd",
            MaintenanceService.SignalRQuotaTier.Warning => "Approaching free-tier limit — consider scaling to Standard",
            _ => "Within free-tier limits"
        };
        check.Details = details;
        return check;
    }

    /// <summary>
    /// Polls the approximate message count of each monitored poison queue in parallel and
    /// surfaces the worst tier as a single health entry. A non-existent poison queue counts
    /// as zero (queues are created lazily on first poison-move). Individual probe failures
    /// are recorded in the details dictionary as <c>error: &lt;message&gt;</c> and force the
    /// overall status to at least <c>warning</c> so a flaky Storage call cannot silently
    /// mask a real backlog. Thresholds + monitored-queue list live in
    /// <see cref="MaintenanceService"/> so the live health card and the timer-driven
    /// Telegram alert classify identically.
    /// </summary>
    internal async Task<HealthCheck> CheckPoisonQueuesAsync()
    {
        var check = new HealthCheck
        {
            Name = "Poison Queues",
            Description = "Async-worker dead-letter backlog"
        };

        var warningThreshold = ParsePositiveInt(
            "PoisonQueueWarningThreshold", MaintenanceService.DefaultPoisonQueueWarningThreshold);
        var criticalThreshold = ParsePositiveInt(
            "PoisonQueueCriticalThreshold", MaintenanceService.DefaultPoisonQueueCriticalThreshold);

        var probes = MaintenanceService.MonitoredPoisonQueues
            .Select(name => (name, task: ProbeOneAsync(name)))
            .ToArray();

        await Task.WhenAll(probes.Select(p => p.task)).ConfigureAwait(false);

        var details = new Dictionary<string, object>();
        var worstTier = MaintenanceService.PoisonQueueTier.None;

        foreach (var (name, task) in probes)
        {
            var result = await task.ConfigureAwait(false);
            if (result.Error is not null)
            {
                details[name] = $"error: {result.Error}";
                if (worstTier < MaintenanceService.PoisonQueueTier.Warning)
                    worstTier = MaintenanceService.PoisonQueueTier.Warning;
                continue;
            }

            var count = result.Count!.Value;
            details[name] = count == 1 ? "1 message" : $"{count:N0} messages";
            var tier = MaintenanceService.ClassifyPoisonQueueTier(count, warningThreshold, criticalThreshold);
            if (tier > worstTier) worstTier = tier;
        }

        check.Status = worstTier switch
        {
            MaintenanceService.PoisonQueueTier.Critical => "unhealthy",
            MaintenanceService.PoisonQueueTier.Warning => "warning",
            _ => "healthy"
        };

        check.Message = worstTier switch
        {
            MaintenanceService.PoisonQueueTier.Critical => $"Poison backlog accumulating (≥{criticalThreshold} messages) — investigate failing handlers",
            MaintenanceService.PoisonQueueTier.Warning => "Operator review required — failed messages parked after 5 retries",
            _ => "All poison queues empty"
        };
        check.Details = details;
        return check;
    }

    private async Task<PoisonQueueProbeResult> ProbeOneAsync(string queueName)
    {
        try
        {
            var count = await _poisonQueueProbe
                .GetApproximateMessageCountAsync(queueName, CancellationToken.None)
                .ConfigureAwait(false);
            return new PoisonQueueProbeResult(count, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Poison queue probe failed for {QueueName} — surfacing as warning in health check",
                queueName);
            return new PoisonQueueProbeResult(null, ex.Message);
        }
    }

    private readonly record struct PoisonQueueProbeResult(long? Count, string? Error);

    private int ParsePositiveInt(string key, int fallback)
    {
        var raw = _configuration[key];
        return int.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
    }

    private long ParsePositiveLong(string key, long fallback)
    {
        var raw = _configuration[key];
        return long.TryParse(raw, out var parsed) && parsed > 0 ? parsed : fallback;
    }
}

/// <summary>
/// Result of a health check operation
/// </summary>
public class HealthCheckResult
{
    public DateTime Timestamp { get; set; }
    public string OverallStatus { get; set; } = "unknown";
    public List<HealthCheck> Checks { get; set; } = new();
}

/// <summary>
/// Individual health check result
/// </summary>
public class HealthCheck
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Status { get; set; } = "unknown";
    public string Message { get; set; } = string.Empty;
    public Dictionary<string, object>? Details { get; set; }
}
