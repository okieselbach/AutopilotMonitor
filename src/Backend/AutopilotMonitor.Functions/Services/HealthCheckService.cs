using AutopilotMonitor.Functions.Services.Monitoring;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
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
    /// Performs all health checks and returns the results.
    /// <paramref name="includeEndpointUrls"/> adds the probed/backing endpoint URLs to the
    /// per-check details — infrastructure topology, so callers must only enable it for
    /// Global Admins.
    /// </summary>
    public async Task<HealthCheckResult> PerformAllChecksAsync(bool includeEndpointUrls = false)
    {
        var result = new HealthCheckResult
        {
            Timestamp = DateTime.UtcNow,
            Checks = new List<HealthCheck>()
        };

        // NOTE: CheckMcpServerAsync is deliberately NOT part of this batch. The MCP
        // Container App scales to zero on idle; a cold probe answers "warming" after a
        // short budget and the caller re-polls until the replica is up. That re-poll loop
        // must stay off the blocking batch, and "warming" must never reach this aggregate
        // — a scale-from-zero is expected behaviour, not a degraded platform. It is
        // exposed via its own endpoint (GET health/mcp) that the frontend fetches
        // independently and folds into the card grid incrementally.
        var checks = await Task.WhenAll(
            CheckStorageBackendAsync(includeEndpointUrls),
            CheckProcessingBackendAsync(includeEndpointUrls),
            CheckAgentBinariesAsync(includeEndpointUrls),
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
    private async Task<HealthCheck> CheckStorageBackendAsync(bool includeEndpointUrls)
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

        if (includeEndpointUrls)
        {
            // Same resolution order as TableStorageService: account name (Managed Identity)
            // is the production path, connection string is the local-dev fallback.
            var accountName = _configuration["AzureStorageAccountName"];
            check.Details = new Dictionary<string, object>
            {
                ["Table endpoint"] = string.IsNullOrWhiteSpace(accountName)
                    ? "connection string (local dev)"
                    : $"https://{accountName}.table.core.windows.net"
            };
        }

        return check;
    }

    /// <summary>
    /// Checks that the Azure Functions host is running
    /// </summary>
    private static Task<HealthCheck> CheckProcessingBackendAsync(bool includeEndpointUrls)
    {
        var uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

        var check = new HealthCheck
        {
            Name = "Processing Backend",
            Description = "Application host process",
            Status = "healthy",
            Message = $"Host running (uptime: {(int)uptime.TotalMinutes}m {uptime.Seconds}s)"
        };

        if (includeEndpointUrls)
        {
            // WEBSITE_HOSTNAME is stamped by App Service and reflects the host actually
            // serving this process — no hardcoded URL to go stale after a migration.
            var hostname = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
            if (!string.IsNullOrWhiteSpace(hostname))
            {
                check.Details = new Dictionary<string, object>
                {
                    ["API endpoint"] = $"https://{hostname}"
                };
            }
        }

        return Task.FromResult(check);
    }

    /// <summary>
    /// Checks that the agent binaries (ZIP) and bootstrap script (PS1) are reachable on the
    /// canonical download alias (Front Door → current blob origin) AND on the legacy blob
    /// keepalive account that already-deployed customer bootstrap scripts still use.
    /// Alias failure is unhealthy (new installs break); legacy failure is a warning
    /// (existing customers break until they migrate to the alias).
    /// </summary>
    internal async Task<HealthCheck> CheckAgentBinariesAsync(bool includeEndpointUrls)
    {
        var check = new HealthCheck
        {
            Name = "Agent Binaries",
            Description = "Agent download package availability"
        };

        var aliasZipUrl = $"{Constants.AgentDownloadBaseUrl}/{Constants.AgentZipFileName}";
        var aliasPs1Url = $"{Constants.AgentDownloadBaseUrl}/{Constants.BootstrapScriptName}";
        var legacyZipUrl = $"{Constants.AgentBlobBaseUrl}/{Constants.AgentZipFileName}";
        var legacyPs1Url = $"{Constants.AgentBlobBaseUrl}/{Constants.BootstrapScriptName}";

        if (includeEndpointUrls)
        {
            check.Details = new Dictionary<string, object>
            {
                ["Agent ZIP"] = aliasZipUrl,
                ["Bootstrap script"] = aliasPs1Url,
                ["Legacy blob (keepalive)"] = Constants.AgentBlobBaseUrl
            };
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var sw = System.Diagnostics.Stopwatch.StartNew();

            // HEAD requests to check availability without downloading
            var results = await Task.WhenAll(
                client.SendAsync(new HttpRequestMessage(HttpMethod.Head, aliasZipUrl)),
                client.SendAsync(new HttpRequestMessage(HttpMethod.Head, aliasPs1Url)),
                client.SendAsync(new HttpRequestMessage(HttpMethod.Head, legacyZipUrl)),
                client.SendAsync(new HttpRequestMessage(HttpMethod.Head, legacyPs1Url))
            );

            sw.Stop();

            var aliasZipOk = results[0].IsSuccessStatusCode;
            var aliasPs1Ok = results[1].IsSuccessStatusCode;
            var legacyZipOk = results[2].IsSuccessStatusCode;
            var legacyPs1Ok = results[3].IsSuccessStatusCode;

            var issues = new List<string>();
            if (!aliasZipOk) issues.Add($"Agent ZIP (download alias): {(int)results[0].StatusCode}");
            if (!aliasPs1Ok) issues.Add($"Bootstrap script (download alias): {(int)results[1].StatusCode}");
            if (!legacyZipOk) issues.Add($"Agent ZIP (legacy blob): {(int)results[2].StatusCode}");
            if (!legacyPs1Ok) issues.Add($"Bootstrap script (legacy blob): {(int)results[3].StatusCode}");

            if (issues.Count == 0)
            {
                check.Status = "healthy";
                // Neutral wording for everyone: the legacy keepalive account is operator detail
                // (Details, Global Admins only) and is not advertised to tenant admins.
                check.Message = $"Agent package and bootstrap script available ({sw.ElapsedMilliseconds}ms)";
            }
            else if (aliasZipOk && aliasPs1Ok)
            {
                // Alias intact, legacy keepalive degraded — existing customer bootstrap
                // scripts still download from the legacy account until they migrate.
                check.Status = "warning";
                check.Message = $"Download alias OK, but legacy keepalive degraded — {string.Join(", ", issues)}";
            }
            else
            {
                check.Status = "unhealthy";
                check.Message = $"Missing: {string.Join(", ", issues)}";
            }
        }
        catch (Exception ex)
        {
            check.Status = "unhealthy";
            check.Message = $"Agent download endpoints unreachable: {ex.Message}";
            _logger.LogError(ex, "Agent binaries health check failed");
        }

        return check;
    }

    /// <summary>
    /// Probes the MCP server's public <c>/health</c> endpoint (which sits in front of the
    /// <c>/mcp</c> access guard, so no auth is required) and surfaces reachability + the
    /// deployed MCP build version. Visible to all authenticated users, so the server URL is
    /// only included in the details when <paramref name="includeEndpointUrl"/> is set —
    /// callers must only enable it for Global Admins.
    /// <para>
    /// The Container App runs with minReplicas=0 and scales to zero on idle, and a cold start
    /// is dominated by Container Apps activation, not by us: measured at 24.5s and 31.5s to
    /// first byte, of which ~98% is KEDA scheduling plus the image mount and ~500ms is the
    /// application (see <c>internal/docs/mcp/docs-corpus.md</c>, "Cold start"). No UI wait
    /// covers that, so this probe deliberately does not try to sit it out — an earlier design
    /// waited 30s as a "wake signal" and still timed out in 50 of 83 observed calls, i.e. it
    /// bought a long wall in front of the same answer.
    /// </para>
    /// <para>
    /// The probe therefore has two jobs: answer fast and honestly within
    /// <c>McpServerHealthTimeoutSeconds</c> (default 3s — chosen to sit in the widest observed
    /// latency gap, 2347ms..3930ms, so jitter cannot flip the verdict), and leave the wake
    /// running. The inbound request has already reached the Container Apps activator by the
    /// time we give up, so aborting our side does not undo the scale-from-zero it triggered;
    /// the caller re-checks and a later probe finds the replica warm.
    /// </para>
    /// <para>
    /// Status vocabulary: <c>healthy</c> = answered 2xx; <c>warming</c> = did not answer inside
    /// the budget, a cold start is in progress and the caller should re-check (must NOT be
    /// rated into any aggregate — it is expected, not a fault); <c>warning</c> = reachable but
    /// the connection failed; <c>unhealthy</c> = answered non-2xx. Exposed via its own
    /// <c>health/mcp</c> endpoint, never the blocking all-checks batch.
    /// </para>
    /// <para>
    /// <c>McpServerHealthTimeoutSeconds</c> is set nowhere in the repo, infra or workflows, so
    /// the default below IS the live production value — changing it changes production.
    /// </para>
    /// </summary>
    internal async Task<HealthCheck> CheckMcpServerAsync(bool includeEndpointUrl = false)
    {
        var check = new HealthCheck
        {
            Name = "MCP Server",
            Description = "AI query interface availability"
        };

        var baseUrl = (_configuration["McpServerUrl"] ?? Constants.McpServerBaseUrl).TrimEnd('/');
        var healthUrl = $"{baseUrl}/health";

        if (includeEndpointUrl)
        {
            check.Details = new Dictionary<string, object> { ["Server URL"] = baseUrl };
        }
        // 3s: warm answers were measured at 4..148ms over 14 days, so this is ~20x the warm
        // maximum, while a cold start needs 20-30s and is unreachable for any UI wait.
        var timeoutSeconds = ParsePositiveInt("McpServerHealthTimeoutSeconds", 3);

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

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
            check.Message = McpReachableMessage(sw.ElapsedMilliseconds);
            if (!string.IsNullOrWhiteSpace(version))
            {
                check.Details ??= new Dictionary<string, object>();
                check.Details["Version"] = version;
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            // Our own timeout: no external cancellation token reaches this path, so a
            // cancellation here is always the budget above expiring. The scale-from-zero we
            // triggered keeps running after we give up, so this is a re-check prompt, not a
            // fault — and with minReplicas=0 it is the expected path for an idle server,
            // which is why it logs at Information and must not colour any aggregate.
            check.Status = "warming";
            // Short on purpose: the portal card renders its own one-line warming text and
            // ignores this message, so it only has to be honest for a direct API caller.
            check.Message = $"MCP server is starting (no answer within {timeoutSeconds}s; it scales to zero when idle)";
            _logger.LogInformation(ex, "MCP server health probe found a cold server (no answer within {TimeoutSeconds}s); activation continues in the background", timeoutSeconds);
        }
        catch (HttpRequestException ex)
        {
            // Reached the network but could not connect — distinct from the cold-start path
            // above and genuinely worth a warning. ex.Message can carry the host name, so it
            // stays out of the user-visible text (same rule as the non-2xx branch).
            check.Status = "warning";
            check.Message = "MCP server unreachable (connection failed)";
            _logger.LogWarning(ex, "MCP server health probe could not connect");
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
    /// An answer slower than this came from a replica that had just started, not from a warm
    /// one, so the message can say so. 750ms is ~5x the warm maximum measured over 14 days
    /// (4..148ms) and deliberately generous: those measurements come from a near-idle,
    /// single-user system, and warm latency will rise under concurrency. It stays well below
    /// the next observed cluster (2289ms), so the distinction remains sharp.
    /// </summary>
    private const long WarmHitThresholdMs = 750;

    /// <summary>
    /// Wording for a reachable MCP server. Extracted so the threshold can be pinned by a test
    /// without a real delay. Both arms keep the word "reachable".
    /// </summary>
    internal static string McpReachableMessage(long elapsedMs) =>
        elapsedMs > WarmHitThresholdMs
            ? $"MCP server reachable after a cold start ({elapsedMs}ms)"
            : $"MCP server reachable ({elapsedMs}ms)";

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
            Description = "Connection + daily message usage vs. plan limits"
        };

        var resourceId = _configuration["SignalRResourceId"];
        if (string.IsNullOrWhiteSpace(resourceId))
        {
            check.Status = "unknown";
            check.Message = "SignalR quota watcher not configured (SignalRResourceId app setting unset)";
            return check;
        }

        var connectionLimit = ParsePositiveInt(
            "SignalRConnectionLimit", MaintenanceService.DefaultSignalRConnectionLimit);
        var messageLimit = ParsePositiveLong(
            "SignalRDailyMessageLimit", MaintenanceService.DefaultSignalRDailyMessageLimit);

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
            MaintenanceService.SignalRQuotaTier.Critical => "Plan limit nearly saturated — add SignalR units before clients get 429'd",
            MaintenanceService.SignalRQuotaTier.Warning => "Approaching plan limit — consider adding SignalR units",
            _ => "Within plan limits"
        };
        check.Details = details;
        return check;
    }

    /// <summary>
    /// Enumerates every existing <c>-poison</c> queue in the storage account, polls each
    /// approximate message count in parallel and surfaces the worst tier as a single
    /// health entry. Poison queues are created lazily on first poison-move, so an absent
    /// queue has never failed — dynamic enumeration covers every queue a static
    /// watch-list would, without a list to forget updating. Individual probe failures
    /// are recorded in the details dictionary as <c>error: &lt;message&gt;</c> and force the
    /// overall status to at least <c>warning</c> so a flaky Storage call cannot silently
    /// mask a real backlog; an enumeration failure does the same. Thresholds live in
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

        IReadOnlyList<string> poisonQueues;
        try
        {
            poisonQueues = await _poisonQueueProbe
                .ListPoisonQueuesAsync(CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Poison queue enumeration failed — surfacing as warning in health check");
            check.Status = "warning";
            check.Message = "Poison queue enumeration failed — backlog state unknown";
            check.Details = new Dictionary<string, object> { ["error"] = ex.Message };
            return check;
        }

        var warningThreshold = ParsePositiveInt(
            "PoisonQueueWarningThreshold", MaintenanceService.DefaultPoisonQueueWarningThreshold);
        var criticalThreshold = ParsePositiveInt(
            "PoisonQueueCriticalThreshold", MaintenanceService.DefaultPoisonQueueCriticalThreshold);

        var probes = poisonQueues
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
/// Result of a health check operation. Not itself on the wire — HealthCheckFunction decomposes
/// it into the typed response envelopes; the per-check item (HealthCheck) is wire contract and
/// lives in AutopilotMonitor.Shared.Models (InfrastructureApiModels.cs).
/// </summary>
public class HealthCheckResult
{
    public DateTime Timestamp { get; set; }
    public string OverallStatus { get; set; } = "unknown";
    public List<HealthCheck> Checks { get; set; } = new();
}
