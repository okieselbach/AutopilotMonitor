using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Offboarding;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Rev-5-F3: the enumerator MUST fail-loud on storage exceptions. The existing
/// <c>GetSessionsByDateRangeAsync</c> swallows errors and returns empty — borrowing that
/// behaviour for offboarding would let a transient outage trigger a same-cycle wipe with
/// zero cascade backup. Tests pin both halves of the contract: happy-path yield-all, and
/// exception-propagates-no-silent-empty.
/// </summary>
public class OffboardingSessionEnumeratorTests
{
    private const string TenantId = "33333333-3333-3333-3333-333333333333";

    [Fact]
    public async Task EnumerateAsync_YieldsAllSessionsFromRepo()
    {
        var repo = new FakeMaintenanceRepository(
            sessionIds: new[] { "s1", "s2", "s3" });
        var sut = new OffboardingSessionEnumerator(repo);

        var collected = new List<string>();
        await foreach (var s in sut.EnumerateAsync(TenantId))
        {
            collected.Add(s);
        }

        Assert.Equal(new[] { "s1", "s2", "s3" }, collected);
    }

    [Fact]
    public async Task EnumerateAsync_PropagatesStorageException_NoSilentEmpty()
    {
        // 503 mid-enumeration is the exact scenario that would otherwise let the offboard
        // worker proceed to wipe with zero cascade backup. The enumerator MUST throw.
        var repo = new FakeMaintenanceRepository(
            sessionIds: new[] { "s1" },
            throwOnIteration: new InvalidOperationException("simulated 503"));
        var sut = new OffboardingSessionEnumerator(repo);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in sut.EnumerateAsync(TenantId))
            {
                // drain
            }
        });
    }

    [Fact]
    public async Task EnumerateAsync_RejectsNonGuidTenantId()
    {
        var sut = new OffboardingSessionEnumerator(new FakeMaintenanceRepository(Array.Empty<string>()));

        await Assert.ThrowsAsync<ArgumentException>(async () =>
        {
            await foreach (var _ in sut.EnumerateAsync("not-a-guid"))
            {
                // never reached
            }
        });
    }

    // ── Test double ─────────────────────────────────────────────────────────────

    private sealed class FakeMaintenanceRepository : IMaintenanceRepository
    {
        private readonly string[] _sessions;
        private readonly Exception? _throwOnIteration;

        public FakeMaintenanceRepository(string[] sessionIds, Exception? throwOnIteration = null)
        {
            _sessions = sessionIds;
            _throwOnIteration = throwOnIteration;
        }

        public async IAsyncEnumerable<string> EnumerateSessionsForOffboardingAsync(
            string tenantId, [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var s in _sessions)
            {
                if (_throwOnIteration != null)
                {
                    // Yield the first one, then throw — matches "mid-enumeration outage".
                    yield return s;
                    throw _throwOnIteration;
                }
                yield return s;
            }
            await Task.CompletedTask;
        }

        // ── Unused for these tests ─────────────────────────────────────────────

        public Task<bool> LogAuditEntryAsync(string tenantId, string action, string entityType, string entityId, string performedBy, Dictionary<string, string>? details = null) => throw new NotImplementedException();
        public Task<List<AuditLogEntry>> GetAuditLogsAsync(string tenantId, DateTime? dateFrom = null, DateTime? dateTo = null, AuditLogQueryFilters? filters = null) => throw new NotImplementedException();
        public Task<List<AuditLogEntry>> GetAllAuditLogsAsync(DateTime? dateFrom = null, DateTime? dateTo = null, AuditLogQueryFilters? filters = null) => throw new NotImplementedException();
        public Task<RawPage<AuditLogEntry>> GetAuditLogsPageAsync(string tenantId, DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation, bool excludeDeletions = false, AuditLogQueryFilters? filters = null) => throw new NotImplementedException();
        public Task<RawPage<AuditLogEntry>> GetAllAuditLogsPageAsync(DateTime? dateFrom, DateTime? dateTo, int pageSize, string? continuation, bool excludeDeletions = false, AuditLogQueryFilters? filters = null) => throw new NotImplementedException();
        public Task<int> DeleteAuditLogsOlderThanAsync(DateTime cutoffUtc) => throw new NotImplementedException();
        public Task<List<SessionSummary>> GetSessionsOlderThanAsync(string tenantId, DateTime cutoffDate, int maxResults = int.MaxValue, bool excludeInFlightDeletions = false) => throw new NotImplementedException();
        public Task<List<SessionSummary>> GetSessionsByDateRangeAsync(DateTime startDate, DateTime endDate, string? tenantId = null) => throw new NotImplementedException();
        public Task<List<SessionSummary>> GetUsageWindowSessionsAsync(DateTime startDate, DateTime endDate, string? tenantId = null) => throw new NotImplementedException();
        public Task<List<SessionSummary>> GetGeoWindowSessionsAsync(DateTime startDate, DateTime endDate, string? tenantId = null) => throw new NotImplementedException();
        public Task<List<SessionSummary>> GetStalledSessionsAsync(string tenantId, DateTime cutoffTime) => throw new NotImplementedException();
        public Task<List<SessionSummary>> GetLegacyTimeoutFailedSessionsAsync(string tenantId, int maxResults) => throw new NotImplementedException();
        public Task<List<SessionSummary>> GetSessionsLeanAsync(string tenantId) => throw new NotImplementedException();
        public Task<List<SessionSummary>> GetAgentSilentSessionsAsync(string tenantId, DateTime silenceCutoff, DateTime hardCutoff) => throw new NotImplementedException();
        public Task<List<SessionSummary>> GetExcessiveDataSendersAsync(string tenantId, DateTime windowCutoff, int maxSessionWindowHours) => throw new NotImplementedException();
        public Task<List<string>> GetAllTenantIdsAsync() => throw new NotImplementedException();
        public Task<int> DeleteSessionEventsAsync(string tenantId, string sessionId) => throw new NotImplementedException();
        public Task<int> DeleteSessionRuleResultsAsync(string tenantId, string sessionId) => throw new NotImplementedException();
        public Task<int> BackfillSessionIndexAsync() => throw new NotImplementedException();
        public Task<int> CleanupGhostSessionIndexEntriesAsync() => throw new NotImplementedException();
        public Task<bool> IsSessionIndexEmptyAsync() => throw new NotImplementedException();
        public Task<List<OrphanedEventSession>> GetOrphanedEventSessionsAsync(TimeSpan gracePeriod) => throw new NotImplementedException();
        public Task DeleteEventSessionIndexEntryAsync(string tenantId, string sessionId) => throw new NotImplementedException();
    }
}
