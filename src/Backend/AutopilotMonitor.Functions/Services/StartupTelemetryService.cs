using System.Diagnostics;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Services;

/// <summary>
/// Emits one App Insights metric per process start so startup duration is a trend, not an
/// anecdote: <c>BackendStartupMs</c> = process start → host ApplicationStarted (includes
/// runtime + DI + every hosted service), plus <c>BackendTableInitMs</c> for the table
/// initialization slice (see TableStorageService.InitializeTablesAsync / schema sentinel).
/// Metrics are not subject to adaptive sampling and reach App Insights regardless of the
/// worker log level, unlike Information traces.
/// Query: <c>customMetrics | where name == "BackendStartupMs" | project timestamp, value, customDimensions</c>.
/// </summary>
public sealed class StartupTelemetryService : IHostedService
{
    public const string StartupMetricName = "BackendStartupMs";
    public const string TableInitMetricName = "BackendTableInitMs";

    private readonly IHostApplicationLifetime _lifetime;
    private readonly TelemetryClient _telemetry;
    private readonly TableStorageService _tableStorage;
    private readonly BackendBuildInfo _buildInfo;
    private readonly ILogger<StartupTelemetryService> _logger;

    public StartupTelemetryService(
        IHostApplicationLifetime lifetime,
        TelemetryClient telemetry,
        TableStorageService tableStorage,
        BackendBuildInfo buildInfo,
        ILogger<StartupTelemetryService> logger)
    {
        _lifetime = lifetime;
        _telemetry = telemetry;
        _tableStorage = tableStorage;
        _buildInfo = buildInfo;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // ApplicationStarted fires after ALL hosted services completed StartAsync — that is
        // the moment the host is ready to serve, independent of registration order.
        _lifetime.ApplicationStarted.Register(Emit);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void Emit()
    {
        try
        {
            var startupMs = (DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalMilliseconds;
            var init = _tableStorage.LastInitialization;

            var props = new Dictionary<string, string>
            {
                ["version"] = _buildInfo.Version,
                ["commit"] = _buildInfo.CommitHash,
                ["tableInitFullPass"] = init.FullPassRan.ToString(),
                ["instance"] = Environment.MachineName
            };

            _telemetry.TrackMetric(new MetricTelemetry(StartupMetricName, Math.Round(startupMs)).WithProperties(props));
            _telemetry.TrackMetric(new MetricTelemetry(TableInitMetricName, Math.Round(init.DurationMs)).WithProperties(props));

            _logger.LogInformation("Backend startup took {StartupMs}ms (table init {TableInitMs}ms, fullPass={FullPass})",
                Math.Round(startupMs), Math.Round(init.DurationMs), init.FullPassRan);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit startup telemetry");
        }
    }
}

internal static class MetricTelemetryExtensions
{
    public static MetricTelemetry WithProperties(this MetricTelemetry metric, IReadOnlyDictionary<string, string> props)
    {
        foreach (var (key, value) in props)
            metric.Properties[key] = value;
        return metric;
    }
}
