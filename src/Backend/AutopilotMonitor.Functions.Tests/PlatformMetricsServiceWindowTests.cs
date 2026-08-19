using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Verifies PlatformMetricsService passes the requested window + session limit
/// through to GetAllSessionsPageAsync and surfaces both on the response, that
/// SessionsScanned reports the honest scan count (not the has-snapshots subset),
/// and that the split fetch (full events for the 20 latency-sample sessions,
/// filtered three-type fetch for the rest) preserves the aggregation semantics.
/// </summary>
public class PlatformMetricsServiceWindowTests
{
    private static RawPage<SessionSummary> EmptyPage() =>
        new RawPage<SessionSummary>(new List<SessionSummary>(), null);

    private static (PlatformMetricsService Service, Mock<ISessionRepository> Repo) CreateService()
    {
        var sessionRepo = new Mock<ISessionRepository>();
        sessionRepo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(EmptyPage());
        sessionRepo
            .Setup(r => r.GetSessionEventsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EnrollmentEvent>());
        sessionRepo
            .Setup(r => r.GetSessionEventsByTypesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EnrollmentEvent>());

        var service = new PlatformMetricsService(sessionRepo.Object, NullLogger<PlatformMetricsService>.Instance);
        return (service, sessionRepo);
    }

    private static SessionSummary MakeSession(int i) => new SessionSummary
    {
        SessionId = $"00000000-0000-0000-0000-{i:D12}",
        TenantId = "11111111-1111-1111-1111-111111111111",
        AgentVersion = "2.0.1400",
        StartedAt = DateTime.UtcNow.AddHours(-i),
        Status = SessionStatus.Succeeded
    };

    private static EnrollmentEvent MakeEvent(string eventType, Dictionary<string, object>? data = null, long sequence = 1) => new EnrollmentEvent
    {
        EventType = eventType,
        Data = data ?? new Dictionary<string, object>(),
        Sequence = sequence,
        Timestamp = DateTime.UtcNow
    };

    [Theory]
    [InlineData(11)]
    [InlineData(13)]
    [InlineData(17)]
    [InlineData(64)]
    public async Task ComputePlatformMetrics_passes_days_through_to_session_repo(int days)
    {
        var (service, repo) = CreateService();

        int? capturedDays = null;
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .Callback<string?, int?, int, string?, IReadOnlyCollection<string>?, IEnumerable<string>?>((_, d, _, _, _, _) => capturedDays = d)
            .ReturnsAsync(EmptyPage());

        var result = await service.ComputePlatformMetricsAsync(days, limit: 50);

        Assert.Equal(days, capturedDays);
        Assert.Equal(days, result.WindowDays);
    }

    [Theory]
    // Use days=200 to avoid colliding with the static cache populated by
    // the per-(days,limit) cache test below. Each row picks a unique limit
    // so cache hits across rows never short-circuit the storage callback.
    [InlineData(200, 21)]
    [InlineData(200, 101)]
    [InlineData(200, 501)]
    [InlineData(200, 1001)]
    [InlineData(200, 2000)]
    public async Task ComputePlatformMetrics_passes_limit_through_to_session_repo_pageSize(int days, int limit)
    {
        var (service, repo) = CreateService();

        int? capturedPageSize = null;
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .Callback<string?, int?, int, string?, IReadOnlyCollection<string>?, IEnumerable<string>?>((_, _, ps, _, _, _) => capturedPageSize = ps)
            .ReturnsAsync(EmptyPage());

        var result = await service.ComputePlatformMetricsAsync(days: days, limit: limit);

        Assert.Equal(limit, capturedPageSize);
        Assert.Equal(limit, result.SessionLimit);
    }

    [Fact]
    public async Task ComputePlatformMetrics_clamps_zero_to_one()
    {
        var (service, repo) = CreateService();

        int? capturedDays = null;
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .Callback<string?, int?, int, string?, IReadOnlyCollection<string>?, IEnumerable<string>?>((_, d, _, _, _, _) => capturedDays = d)
            .ReturnsAsync(EmptyPage());

        var result = await service.ComputePlatformMetricsAsync(0);

        Assert.Equal(1, capturedDays);
        Assert.Equal(1, result.WindowDays);
    }

    [Fact]
    public async Task ComputePlatformMetrics_clamps_excessive_days_to_365()
    {
        var (service, _) = CreateService();
        var result = await service.ComputePlatformMetricsAsync(99999);
        Assert.Equal(365, result.WindowDays);
    }

    [Fact]
    public async Task ComputePlatformMetrics_clamps_excessive_limit_to_2000()
    {
        var (service, _) = CreateService();
        var result = await service.ComputePlatformMetricsAsync(days: 30, limit: 99999);
        Assert.Equal(2000, result.SessionLimit);
    }

    [Fact]
    public async Task ComputePlatformMetrics_caches_per_days_limit_pair()
    {
        // Different (days, limit) combinations must hit storage independently;
        // a shared cache slot would yield the wrong sample size.
        var (service, repo) = CreateService();
        int callCount = 0;
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .Callback(() => callCount++)
            .ReturnsAsync(EmptyPage());

        await service.ComputePlatformMetricsAsync(days: 30, limit: 20);
        await service.ComputePlatformMetricsAsync(days: 30, limit: 20); // cache hit
        await service.ComputePlatformMetricsAsync(days: 30, limit: 100); // miss — different limit
        await service.ComputePlatformMetricsAsync(days: 60, limit: 20); // miss — different days

        Assert.Equal(3, callCount);
    }

    [Fact]
    public async Task ComputePlatformMetrics_reports_sessionsScanned_even_when_no_session_has_snapshots()
    {
        // The truncated check must be based on scanned sessions, not the has-snapshots
        // subset — a window full of snapshot-less sessions previously read as "not truncated".
        var (service, repo) = CreateService();
        var sessions = Enumerable.Range(1, 7).Select(MakeSession).ToList();
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new RawPage<SessionSummary>(sessions, null));

        var result = await service.ComputePlatformMetricsAsync(days: 77, limit: 50);

        Assert.Equal(7, result.SessionsScanned);
        Assert.Empty(result.Sessions);
    }

    [Fact]
    public async Task ComputePlatformMetrics_uses_full_fetch_for_latency_sample_and_filtered_fetch_for_rest()
    {
        // First 20 sessions form the delivery-latency sample (deltas over ALL events);
        // every further session only needs the three metrics event types.
        var (service, repo) = CreateService();
        var sessions = Enumerable.Range(1, 25).Select(MakeSession).ToList();
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new RawPage<SessionSummary>(sessions, null));

        var fullFetchSessions = new List<string>();
        var filteredFetchSessions = new List<string>();
        IReadOnlyCollection<string>? capturedEventTypes = null;
        repo
            .Setup(r => r.GetSessionEventsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>()))
            .Callback<string, string, int>((_, sid, _) => { lock (fullFetchSessions) fullFetchSessions.Add(sid); })
            .ReturnsAsync(new List<EnrollmentEvent>());
        repo
            .Setup(r => r.GetSessionEventsByTypesAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<int>()))
            .Callback<string, string, IReadOnlyCollection<string>, IEnumerable<string>?, int>((_, sid, types, _, _) =>
            {
                lock (filteredFetchSessions) { filteredFetchSessions.Add(sid); capturedEventTypes = types; }
            })
            .ReturnsAsync(new List<EnrollmentEvent>());

        await service.ComputePlatformMetricsAsync(days: 78, limit: 50);

        Assert.Equal(20, fullFetchSessions.Count);
        Assert.Equal(5, filteredFetchSessions.Count);
        Assert.Equal(sessions.Take(20).Select(s => s.SessionId).ToHashSet(), fullFetchSessions.ToHashSet());
        Assert.NotNull(capturedEventTypes);
        Assert.Contains("agent_metrics_snapshot", capturedEventTypes!);
        Assert.Contains("agent_started", capturedEventTypes!);
        // Without spool_pressure_detected in the filter the SpoolPressureDetected flag
        // silently dies on the filtered path.
        Assert.Contains("spool_pressure_detected", capturedEventTypes!);
    }

    [Fact]
    public async Task ComputePlatformMetrics_spool_pressure_and_crash_survive_the_filtered_path()
    {
        // A session outside the latency sample gets its events via the filtered fetch;
        // the spool-pressure flag and crash tally must still work on that stream.
        var (service, repo) = CreateService();
        var sessions = Enumerable.Range(1, 21).Select(MakeSession).ToList();
        repo
            .Setup(r => r.GetAllSessionsPageAsync(It.IsAny<string?>(), It.IsAny<int?>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<IEnumerable<string>?>()))
            .ReturnsAsync(new RawPage<SessionSummary>(sessions, null));

        var session21 = sessions[20].SessionId;
        repo
            .Setup(r => r.GetSessionEventsByTypesAsync(It.IsAny<string>(), session21, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<IEnumerable<string>?>(), It.IsAny<int>()))
            .ReturnsAsync(new List<EnrollmentEvent>
            {
                MakeEvent("agent_started", new Dictionary<string, object> { ["previousExitType"] = "exception_crash", ["previousCrashException"] = "System.NullReferenceException" }, sequence: 1),
                MakeEvent("agent_metrics_snapshot", new Dictionary<string, object>
                {
                    ["agent_cpu_percent"] = 3.5,
                    ["agent_working_set_mb"] = 90.0,
                    ["spool_pending_item_count"] = 12.0
                }, sequence: 2),
                MakeEvent("spool_pressure_detected", sequence: 3)
            });

        var result = await service.ComputePlatformMetricsAsync(days: 79, limit: 50);

        var metric = Assert.Single(result.Sessions);
        Assert.Equal(session21, metric.SessionId);
        Assert.True(metric.SpoolPressureDetected);
        Assert.Equal(1, result.CrashRate!.ExceptionCrashes);
        Assert.Equal("System.NullReferenceException", Assert.Single(result.CrashRate.TopExceptions).ExceptionType);
        Assert.Equal(21, result.SessionsScanned);
    }
}
