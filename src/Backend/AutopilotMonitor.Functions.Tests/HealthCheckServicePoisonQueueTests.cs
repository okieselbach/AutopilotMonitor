using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Monitoring;
using AutopilotMonitor.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Service-level tests for the poison-queue HealthCheck card. Drives
/// <see cref="HealthCheckService.CheckPoisonQueuesAsync"/> with an in-memory probe
/// and asserts the health card's status/message/details shape. The pure
/// boundary classifier and seen-index extractor live in
/// <see cref="MaintenanceServicePoisonQueueTests"/> next to the canonical definitions.
/// </summary>
public class HealthCheckServicePoisonQueueTests
{
    [Fact]
    public async Task Check_AllPoisonQueuesEmpty_ReportsHealthy()
    {
        // Existing-but-drained poison queues: enumerated, probed, all zero.
        var probe = new FakePoisonQueueProbe();
        probe.Counts[Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.VulnerabilityCorrelate + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.TenantAutoApprove + "-poison"] = 0;

        var svc = BuildService(probe);
        var check = await svc.CheckPoisonQueuesAsync();

        Assert.Equal("Poison Queues", check.Name);
        Assert.Equal("healthy", check.Status);
        Assert.Equal("All poison queues empty", check.Message);
        Assert.Equal(3, check.Details!.Count);
        Assert.All(check.Details.Values, v => Assert.Equal("0 messages", v));
    }

    [Fact]
    public async Task Check_NoPoisonQueuesExist_ReportsHealthy()
    {
        // Steady state on a healthy system: poison queues are created lazily on
        // first poison-move, so an empty enumeration means nothing ever failed.
        var svc = BuildService(new FakePoisonQueueProbe());
        var check = await svc.CheckPoisonQueuesAsync();

        Assert.Equal("healthy", check.Status);
        Assert.Equal("All poison queues empty", check.Message);
        Assert.Empty(check.Details!);
    }

    [Fact]
    public async Task Check_EnumerationFails_ReportsWarningWithError()
    {
        // A failed listing must never read as an all-clear.
        var probe = new FakePoisonQueueProbe
        {
            ListException = new InvalidOperationException("auth denied")
        };

        var svc = BuildService(probe);
        var check = await svc.CheckPoisonQueuesAsync();

        Assert.Equal("warning", check.Status);
        Assert.Contains("enumeration failed", check.Message);
        Assert.Contains("auth denied", (string)check.Details!["error"]);
    }

    [Fact]
    public async Task Check_SingleQueueWithBacklog_ReportsWarning()
    {
        var probe = new FakePoisonQueueProbe();
        probe.Counts[Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"] = 3;
        probe.Counts[Constants.QueueNames.VulnerabilityCorrelate + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.TelemetryIndexReconcile + "-poison"] = 0;

        var svc = BuildService(probe);
        var check = await svc.CheckPoisonQueuesAsync();

        Assert.Equal("warning", check.Status);
        Assert.Equal("Operator review required — failed messages parked after 5 retries", check.Message);
        Assert.Equal("3 messages",
            check.Details![Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"]);
        Assert.Equal("0 messages",
            check.Details[Constants.QueueNames.VulnerabilityCorrelate + "-poison"]);
    }

    [Fact]
    public async Task Check_QueueAboveCritical_ReportsUnhealthy()
    {
        var probe = new FakePoisonQueueProbe();
        probe.Counts[Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"] = 15;
        probe.Counts[Constants.QueueNames.VulnerabilityCorrelate + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.TelemetryIndexReconcile + "-poison"] = 2;

        var svc = BuildService(probe);
        var check = await svc.CheckPoisonQueuesAsync();

        Assert.Equal("unhealthy", check.Status);
        Assert.Contains("Poison backlog accumulating", check.Message);
        Assert.Equal("15 messages",
            check.Details![Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"]);
    }

    [Fact]
    public async Task Check_ProbeThrows_DetailsRecordErrorAndForceWarning()
    {
        var probe = new FakePoisonQueueProbe
        {
            ExceptionFor =
            {
                [Constants.QueueNames.VulnerabilityCorrelate + "-poison"] =
                    new InvalidOperationException("storage 500")
            }
        };
        probe.Counts[Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.TelemetryIndexReconcile + "-poison"] = 0;

        var svc = BuildService(probe);
        var check = await svc.CheckPoisonQueuesAsync();

        Assert.Equal("warning", check.Status);
        var errorEntry = (string)check.Details![Constants.QueueNames.VulnerabilityCorrelate + "-poison"];
        Assert.StartsWith("error:", errorEntry);
        Assert.Contains("storage 500", errorEntry);
    }

    [Fact]
    public async Task Check_CustomThresholdsFromConfig_AreRespected()
    {
        var probe = new FakePoisonQueueProbe();
        probe.Counts[Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"] = 4;
        probe.Counts[Constants.QueueNames.VulnerabilityCorrelate + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.TelemetryIndexReconcile + "-poison"] = 0;

        // warn=5 → 4 messages now classifies as healthy (would be warning under defaults)
        var svc = BuildService(probe, new Dictionary<string, string?>
        {
            ["PoisonQueueWarningThreshold"] = "5",
            ["PoisonQueueCriticalThreshold"] = "50",
        });

        var check = await svc.CheckPoisonQueuesAsync();
        Assert.Equal("healthy", check.Status);
    }

    [Fact]
    public async Task Check_OneMessage_FormatsAsSingularInDetails()
    {
        var probe = new FakePoisonQueueProbe();
        probe.Counts[Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"] = 1;
        probe.Counts[Constants.QueueNames.VulnerabilityCorrelate + "-poison"] = 0;
        probe.Counts[Constants.QueueNames.TelemetryIndexReconcile + "-poison"] = 0;

        var svc = BuildService(probe);
        var check = await svc.CheckPoisonQueuesAsync();
        Assert.Equal("1 message",
            check.Details![Constants.QueueNames.AnalyzeOnEnrollmentEnd + "-poison"]);
    }

    /// <summary>
    /// Builds a HealthCheckService wired only with the dependencies that
    /// <see cref="HealthCheckService.CheckPoisonQueuesAsync"/> touches.
    /// The other constructor parameters are passed as <c>null!</c> — they
    /// belong to sibling checks that this test class never invokes.
    /// </summary>
    private static HealthCheckService BuildService(
        FakePoisonQueueProbe probe,
        IDictionary<string, string?>? extraConfig = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(extraConfig ?? new Dictionary<string, string?>())
            .Build();

        return new HealthCheckService(
            NullLogger<HealthCheckService>.Instance,
            adminConfigService: null!,
            httpClientFactory: null!,
            metricsReader: null!,
            poisonQueueProbe: probe,
            configuration: config);
    }

    private sealed class FakePoisonQueueProbe : IPoisonQueueProbe
    {
        public Dictionary<string, long> Counts { get; } = new();
        public Dictionary<string, Exception> ExceptionFor { get; } = new();
        public Exception? ListException { get; set; }

        public Task<IReadOnlyList<string>> ListPoisonQueuesAsync(CancellationToken ct)
        {
            if (ListException is not null)
                throw ListException;
            IReadOnlyList<string> names = Counts.Keys
                .Concat(ExceptionFor.Keys)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(names);
        }

        public Task<long> GetApproximateMessageCountAsync(string queueName, CancellationToken ct)
        {
            if (ExceptionFor.TryGetValue(queueName, out var ex))
                throw ex;
            if (Counts.TryGetValue(queueName, out var count))
                return Task.FromResult(count);
            return Task.FromResult(0L);
        }
    }
}
