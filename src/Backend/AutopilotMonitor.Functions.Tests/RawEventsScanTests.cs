using AutopilotMonitor.Functions.Pagination;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Pagination;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Budget/cursor semantics of the cross-session raw-events walk. The invariants under test:
/// every call processes at least one chunk (forward progress), the cursor only ever points
/// after a fully processed chunk (a budget stop loses nothing), <c>Partial</c> is set exactly
/// when the budget ended a page that still had index rows, the per-session fan-out is bounded,
/// and chunk order survives the parallel fan-out so a re-run is deterministic.
/// </summary>
public class RawEventsScanTests
{
    private const string Tenant = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";

    private static EventTypeIndexEntry Entry(string sessionId) => new(Tenant, sessionId);

    private static IReadOnlyDictionary<string, object?> Row(string sessionId, int seq) =>
        new Dictionary<string, object?> { ["SessionId"] = sessionId, ["Sequence"] = (long)seq };

    /// <summary>Scripted index: each call returns the next scripted (items, token) pair and records what it was asked for.</summary>
    private sealed class ScriptedIndex
    {
        private readonly Queue<(IReadOnlyList<EventTypeIndexEntry> Items, string? Token)> _script = new();
        public List<(int ChunkSize, string? Continuation)> Calls { get; } = new();

        public ScriptedIndex Then(string? token, params string[] sessionIds)
        {
            _script.Enqueue((sessionIds.Select(Entry).ToList(), token));
            return this;
        }

        public Task<RawPage<EventTypeIndexEntry>> Fetch(int chunkSize, string? continuation)
        {
            Calls.Add((chunkSize, continuation));
            if (_script.Count == 0) throw new InvalidOperationException("index fetched past the end of the script");
            var (items, token) = _script.Dequeue();
            return Task.FromResult(new RawPage<EventTypeIndexEntry>(items, token));
        }
    }

    private static SessionEventsFetcher OneRowPerSession() =>
        entry => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(new[] { Row(entry.SessionId, 1) });

    /// <summary>Clock that returns the scripted instants in order and holds the last one afterwards.</summary>
    private static Func<DateTime> Clock(params int[] secondsSinceEpoch)
    {
        var q = new Queue<int>(secondsSinceEpoch);
        var last = secondsSinceEpoch[^1];
        var epoch = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        return () => epoch.AddSeconds(q.Count > 0 ? q.Dequeue() : last);
    }

    [Fact]
    public async Task Fills_the_page_across_chunks_and_returns_the_token_after_the_last_one()
    {
        var index = new ScriptedIndex()
            .Then("tok1", "s1", "s2")
            .Then("tok2", "s3", "s4")
            .Then("tok3", "s5");

        var result = await RawEventsScan.RunAsync(index.Fetch, OneRowPerSession(), startToken: null,
            new RawEventsScanOptions { PageSize = 5, ChunkSize = 2, Budget = TimeSpan.FromMinutes(1) }, Clock(0));

        Assert.Equal(new[] { (2, (string?)null), (2, "tok1"), (1, "tok2") }, index.Calls);
        Assert.Equal(5, result.ScannedIndexRows);
        Assert.Equal(5, result.Events.Count);
        Assert.Equal("tok3", result.NextRawToken);
        Assert.False(result.Partial);
    }

    [Fact]
    public async Task Budget_stop_keeps_the_cursor_after_the_last_completed_chunk_and_marks_partial()
    {
        var index = new ScriptedIndex()
            .Then("tok1", "s1", "s2")
            .Then("tok2", "s3", "s4"); // never reached

        // deadline = 0 + 10 s; the check after chunk 1 sees t = 11 s.
        var result = await RawEventsScan.RunAsync(index.Fetch, OneRowPerSession(), startToken: "start",
            new RawEventsScanOptions { PageSize = 100, ChunkSize = 2, Budget = TimeSpan.FromSeconds(10) }, Clock(0, 11));

        Assert.Single(index.Calls);
        Assert.Equal("start", index.Calls[0].Continuation);
        Assert.Equal(new[] { "s1", "s2" }, result.Events.Select(e => e["SessionId"]));
        Assert.Equal("tok1", result.NextRawToken);
        Assert.True(result.Partial);
    }

    [Fact]
    public async Task Drained_index_is_never_partial_even_when_the_budget_is_spent()
    {
        var index = new ScriptedIndex().Then(null, "s1");

        var result = await RawEventsScan.RunAsync(index.Fetch, OneRowPerSession(), startToken: null,
            new RawEventsScanOptions { PageSize = 100, ChunkSize = 50, Budget = TimeSpan.FromSeconds(1) }, Clock(0, 999));

        Assert.Null(result.NextRawToken);
        Assert.False(result.Partial);
        Assert.Single(result.Events);
    }

    [Fact]
    public async Task A_full_page_is_not_partial_even_when_the_budget_is_spent_on_the_last_chunk()
    {
        var index = new ScriptedIndex().Then("tok1", "s1", "s2");

        var result = await RawEventsScan.RunAsync(index.Fetch, OneRowPerSession(), startToken: null,
            new RawEventsScanOptions { PageSize = 2, ChunkSize = 2, Budget = TimeSpan.FromSeconds(1) }, Clock(0, 999));

        Assert.Equal("tok1", result.NextRawToken);
        Assert.False(result.Partial); // page filled → ordinary nextLink
    }

    [Fact]
    public async Task Always_processes_one_chunk_even_if_the_deadline_has_already_passed()
    {
        var index = new ScriptedIndex().Then("tok1", "s1");

        // Clock jumps past the deadline right after it was computed.
        var result = await RawEventsScan.RunAsync(index.Fetch, OneRowPerSession(), startToken: null,
            new RawEventsScanOptions { PageSize = 100, ChunkSize = 10, Budget = TimeSpan.Zero }, Clock(0, 100));

        Assert.Single(index.Calls);
        Assert.Single(result.Events);
        Assert.Equal("tok1", result.NextRawToken);
        Assert.True(result.Partial);
    }

    [Fact]
    public async Task Empty_chunk_with_a_token_keeps_walking()
    {
        // A filtered cross-tenant scan legitimately yields zero matches per server round-trip.
        var index = new ScriptedIndex()
            .Then("tok1")
            .Then(null, "s1", "s2");

        var result = await RawEventsScan.RunAsync(index.Fetch, OneRowPerSession(), startToken: null,
            new RawEventsScanOptions { PageSize = 10, ChunkSize = 5, Budget = TimeSpan.FromMinutes(1) }, Clock(0));

        Assert.Equal(2, index.Calls.Count);
        Assert.Equal(2, result.ScannedIndexRows);
        Assert.Equal(2, result.Events.Count);
        Assert.Null(result.NextRawToken);
        Assert.False(result.Partial);
    }

    [Fact]
    public async Task Fan_out_is_bounded_by_MaxParallel_and_reaches_every_session()
    {
        var ids = Enumerable.Range(1, 12).Select(i => $"s{i}").ToArray();
        var index = new ScriptedIndex().Then(null, ids);

        int inFlight = 0, peak = 0;
        var fetched = new System.Collections.Concurrent.ConcurrentBag<string>();
        SessionEventsFetcher fetch = async entry =>
        {
            var now = Interlocked.Increment(ref inFlight);
            int seen;
            do { seen = peak; } while (now > seen && Interlocked.CompareExchange(ref peak, now, seen) != seen);
            await Task.Delay(5);
            Interlocked.Decrement(ref inFlight);
            fetched.Add(entry.SessionId);
            return new[] { Row(entry.SessionId, 1) };
        };

        var result = await RawEventsScan.RunAsync(index.Fetch, fetch, startToken: null,
            new RawEventsScanOptions { PageSize = 100, ChunkSize = 100, MaxParallel = 3 }, Clock(0));

        Assert.Equal(12, result.Events.Count);
        Assert.Equal(ids.OrderBy(x => x), fetched.OrderBy(x => x));
        Assert.InRange(peak, 1, 3);
    }

    [Fact]
    public async Task Events_keep_chunk_order_regardless_of_fetch_completion_order()
    {
        var index = new ScriptedIndex().Then(null, "a", "b", "c");

        SessionEventsFetcher fetch = async entry =>
        {
            // Later sessions finish first.
            await Task.Delay(entry.SessionId == "a" ? 30 : entry.SessionId == "b" ? 15 : 1);
            return new[] { Row(entry.SessionId, 1) };
        };

        var result = await RawEventsScan.RunAsync(index.Fetch, fetch, startToken: null,
            new RawEventsScanOptions { PageSize = 10, ChunkSize = 10 }, Clock(0));

        Assert.Equal(new[] { "a", "b", "c" }, result.Events.Select(e => e["SessionId"]));
    }

    [Fact]
    public async Task Rejects_invalid_options()
    {
        var index = new ScriptedIndex().Then(null, "s1");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            RawEventsScan.RunAsync(index.Fetch, OneRowPerSession(), null, new RawEventsScanOptions { PageSize = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            RawEventsScan.RunAsync(index.Fetch, OneRowPerSession(), null, new RawEventsScanOptions { ChunkSize = 0 }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            RawEventsScan.RunAsync(index.Fetch, OneRowPerSession(), null, new RawEventsScanOptions { MaxParallel = 0 }));
    }
}
