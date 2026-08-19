using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// AgentEfficiencyMetricsService: per-agent-version bucketing, nearest-rank percentiles,
/// crash tally per bucket, top-offender selection and the scanned/with-snapshots split.
/// Distinct `days` values per test keep the static per-(days,limit,tenant) cache from
/// cross-talking between tests.
/// </summary>
public class AgentEfficiencyMetricsServiceTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";

    private static (AgentEfficiencyMetricsService Service, Mock<ISessionRepository> Repo) CreateService()
    {
        var repo = new Mock<ISessionRepository>();
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new RawPage<SessionSummary>(new List<SessionSummary>(), null));
        repo
            .Setup(r => r.GetSessionEventsByTypesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EnrollmentEvent>());

        var service = new AgentEfficiencyMetricsService(repo.Object, NullLogger<AgentEfficiencyMetricsService>.Instance);
        return (service, repo);
    }

    private static SessionSummary MakeSession(int i, string agentVersion, double? avgLatency = null, int? requestCount = null) => new SessionSummary
    {
        SessionId = $"00000000-0000-0000-0000-{i:D12}",
        TenantId = Tenant,
        AgentVersion = agentVersion,
        DeviceName = $"PC-{i}",
        StartedAt = DateTime.UtcNow.AddHours(-i),
        Status = SessionStatus.Succeeded,
        AvgApiLatencyMs = avgLatency,
        ApiRequestCount = requestCount
    };

    private static EnrollmentEvent Snapshot(double cpu, double ws, double handles = 0, double threads = 0, double spool = 0, long sequence = 1) => new EnrollmentEvent
    {
        EventType = "agent_metrics_snapshot",
        Sequence = sequence,
        Data = new Dictionary<string, object>
        {
            ["agent_cpu_percent"] = cpu,
            ["agent_working_set_mb"] = ws,
            ["agent_handle_count"] = handles,
            ["agent_thread_count"] = threads,
            ["spool_pending_item_count"] = spool
        }
    };

    private static EnrollmentEvent AgentStarted(string exitType, string? exception = null)
    {
        var data = new Dictionary<string, object> { ["previousExitType"] = exitType };
        if (exception != null) data["previousCrashException"] = exception;
        return new EnrollmentEvent { EventType = "agent_started", Data = data };
    }

    private static void SetupSessions(Mock<ISessionRepository> repo, List<SessionSummary> sessions)
    {
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new RawPage<SessionSummary>(sessions, null));
    }

    private static void SetupEvents(Mock<ISessionRepository> repo, string sessionId, params EnrollmentEvent[] events)
    {
        repo
            .Setup(r => r.GetSessionEventsByTypesAsync(It.IsAny<string>(), sessionId, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<int>()))
            .ReturnsAsync(events.ToList());
    }

    [Fact]
    public async Task Compute_buckets_by_agent_version_with_nearest_rank_percentiles()
    {
        var (service, repo) = CreateService();
        var sessions = new List<SessionSummary>
        {
            MakeSession(1, "2.0.1400"), MakeSession(2, "2.0.1400"),
            MakeSession(3, "2.0.1400"), MakeSession(4, "2.0.1400"),
            MakeSession(5, "2.0.1402")
        };
        SetupSessions(repo, sessions);
        // Per-session max working set for the 1400 bucket: 100, 200, 300, 400.
        SetupEvents(repo, sessions[0].SessionId, Snapshot(cpu: 1, ws: 50), Snapshot(cpu: 3, ws: 100, sequence: 2));
        SetupEvents(repo, sessions[1].SessionId, Snapshot(cpu: 2, ws: 200));
        SetupEvents(repo, sessions[2].SessionId, Snapshot(cpu: 4, ws: 300));
        SetupEvents(repo, sessions[3].SessionId, Snapshot(cpu: 8, ws: 400));
        SetupEvents(repo, sessions[4].SessionId, Snapshot(cpu: 5, ws: 500));

        var result = await service.ComputeAsync(days: 41, limit: 100);

        Assert.Equal(5, result.SessionsScanned);
        Assert.Equal(5, result.SessionsWithSnapshots);
        Assert.Equal(2, result.ByVersion.Count);

        var v1400 = result.ByVersion.Single(b => b.AgentVersion == "2.0.1400");
        Assert.Equal(4, v1400.SessionsScanned);
        Assert.Equal(4, v1400.SessionsWithSnapshots);
        // Nearest-rank over sorted [100, 200, 300, 400]: p50 → 2nd (200), p95 → 4th (400).
        Assert.Equal(200, v1400.MaxWorkingSetMb!.P50);
        Assert.Equal(400, v1400.MaxWorkingSetMb.P95);
        Assert.Equal(400, v1400.MaxWorkingSetMb.Max);
        Assert.Equal(4, v1400.MaxWorkingSetMb.SampleCount);
        // Session 1's max CPU is 3 (max over its two snapshots), not the last value.
        Assert.Equal(8, v1400.MaxCpuPercent!.Max);

        var v1402 = result.ByVersion.Single(b => b.AgentVersion == "2.0.1402");
        Assert.Equal(1, v1402.SessionsScanned);
        Assert.Equal(500, v1402.MaxWorkingSetMb!.Max);

        Assert.NotNull(result.Overall);
        Assert.Null(result.Overall!.AgentVersion);
        Assert.Equal(5, result.Overall.SessionsScanned);
        Assert.Equal(500, result.Overall.MaxWorkingSetMb!.Max);
    }

    [Fact]
    public async Task Compute_tallies_crash_rate_per_version_bucket()
    {
        var (service, repo) = CreateService();
        var sessions = new List<SessionSummary> { MakeSession(1, "2.0.1400"), MakeSession(2, "2.0.1402") };
        SetupSessions(repo, sessions);
        SetupEvents(repo, sessions[0].SessionId,
            AgentStarted("first_run"),
            AgentStarted("exception_crash", "System.IO.IOException"),
            AgentStarted("clean"));
        SetupEvents(repo, sessions[1].SessionId, AgentStarted("first_run"), AgentStarted("clean"));

        var result = await service.ComputeAsync(days: 42, limit: 100);

        var v1400 = result.ByVersion.Single(b => b.AgentVersion == "2.0.1400");
        Assert.Equal(3, v1400.CrashRate!.TotalStarts);
        Assert.Equal(1, v1400.CrashRate.ExceptionCrashes);
        Assert.Equal("System.IO.IOException", Assert.Single(v1400.CrashRate.TopExceptions).ExceptionType);
        // 2 non-first-run starts, 1 crash → 50%.
        Assert.Equal(50, v1400.CrashRate.CrashRatePercent);

        var v1402 = result.ByVersion.Single(b => b.AgentVersion == "2.0.1402");
        Assert.Equal(0, v1402.CrashRate!.ExceptionCrashes);
        Assert.Equal(0, v1402.CrashRate.CrashRatePercent);
    }

    [Fact]
    public async Task Compute_selects_top_offenders_per_dimension_descending()
    {
        var (service, repo) = CreateService();
        var sessions = Enumerable.Range(1, 5).Select(i => MakeSession(i, "2.0.1400")).ToList();
        SetupSessions(repo, sessions);
        for (var i = 0; i < 5; i++)
        {
            // Working sets 100..500 — top 3 must be sessions 5, 4, 3 in that order.
            SetupEvents(repo, sessions[i].SessionId, Snapshot(cpu: 1, ws: (i + 1) * 100));
        }

        var result = await service.ComputeAsync(days: 43, limit: 100);

        var bucket = result.ByVersion.Single();
        var wsOffenders = bucket.TopOffenders!.Where(o => o.Dimension == "maxWorkingSetMb").ToList();
        Assert.Equal(3, wsOffenders.Count);
        Assert.Equal(new[] { 500d, 400d, 300d }, wsOffenders.Select(o => o.Value).ToArray());
        Assert.Equal(sessions[4].SessionId, wsOffenders[0].SessionId);
        Assert.Equal("PC-5", wsOffenders[0].DeviceName);
    }

    [Fact]
    public async Task Compute_snapshotless_sessions_still_contribute_index_mirrored_latency()
    {
        var (service, repo) = CreateService();
        var sessions = new List<SessionSummary>
        {
            MakeSession(1, "2.0.1400", avgLatency: 80, requestCount: 200),
            MakeSession(2, "2.0.1400", avgLatency: 120, requestCount: 300) // no snapshots
        };
        SetupSessions(repo, sessions);
        SetupEvents(repo, sessions[0].SessionId, Snapshot(cpu: 1, ws: 100));

        var result = await service.ComputeAsync(days: 44, limit: 100);

        Assert.Equal(2, result.SessionsScanned);
        Assert.Equal(1, result.SessionsWithSnapshots);
        var bucket = result.ByVersion.Single();
        // Latency stats cover BOTH sessions (mirror-sourced), resource stats only the one with snapshots.
        Assert.Equal(2, bucket.ApiLatencyMs!.SampleCount);
        Assert.Equal(120, bucket.ApiLatencyMs.Max);
        Assert.Equal(1, bucket.MaxWorkingSetMb!.SampleCount);
    }

    [Fact]
    public async Task Compute_passes_projection_and_tenant_filter_to_repo_and_echoes_tenant()
    {
        var (service, repo) = CreateService();
        string? capturedTenant = null;
        IEnumerable<string>? capturedSelect = null;
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .Callback<string?, int?, int, string?, IReadOnlyCollection<string>?, IEnumerable<string>?>((t, _, _, _, _, sel) => { capturedTenant = t; capturedSelect = sel; })
            .ReturnsAsync(new RawPage<SessionSummary>(new List<SessionSummary>(), null));

        var result = await service.ComputeAsync(days: 45, limit: 100, tenantId: Tenant);

        Assert.Equal(Tenant, capturedTenant);
        Assert.Same(AgentEfficiencyMetricsService.SessionIndexProjection, capturedSelect);
        Assert.Equal(Tenant, result.TenantId);
        Assert.Equal(45, result.WindowDays);
        Assert.Equal(100, result.SessionLimit);
    }

    [Fact]
    public async Task Compute_caches_per_days_limit_tenant_key()
    {
        var (service, repo) = CreateService();
        int callCount = 0;
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .Callback(() => callCount++)
            .ReturnsAsync(new RawPage<SessionSummary>(new List<SessionSummary>(), null));

        var first = await service.ComputeAsync(days: 46, limit: 100);
        var second = await service.ComputeAsync(days: 46, limit: 100);   // cache hit
        await service.ComputeAsync(days: 46, limit: 100, tenantId: Tenant); // miss — tenant-scoped

        Assert.Equal(2, callCount);
        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
    }
}
