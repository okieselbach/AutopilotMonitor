using AutopilotMonitor.Functions.DataAccess.TableStorage;
using static AutopilotMonitor.Functions.DataAccess.TableStorage.PerPartitionFanOutMerge;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Cross-partition paging behind the global all-category ops-events page and the global
/// cross-tenant audit-log page. Every page fans out one read per partition and merges by
/// (RowKey, Partition); the continuation is the watermark of the last returned row. The tests
/// drive the helper against an in-memory partition store that honours the RowKey bound exactly
/// like the OData clause does.
/// </summary>
public class PerPartitionFanOutMergeTests
{
    // Reverse-tick RowKey like the storage layer writes it: a smaller string is a newer row.
    private static string Rk(int minutesAgo, string suffix = "")
    {
        var t0 = new DateTime(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);
        return $"{DateTime.MaxValue.Ticks - t0.AddMinutes(-minutesAgo).Ticks:D19}{suffix}";
    }

    private sealed class Store
    {
        private readonly Dictionary<string, List<string>> _rows;
        private readonly Func<string, bool> _matches;
        public List<(string Partition, RowKeyBound? Bound, int Limit)> Calls { get; } = new();

        public Store(Dictionary<string, List<string>> rows, Func<string, bool>? matches = null)
        {
            _rows = rows;
            _matches = matches ?? (_ => true);
        }

        public IEnumerable<string> Partitions => _rows.Keys;

        public PartitionReader<string> Reader => (partition, bound, limit, _) =>
        {
            lock (Calls) Calls.Add((partition, bound, limit));
            var read = _rows[partition]
                .Where(_matches)
                .Where(rk => bound == null
                    || (bound.Inclusive
                        ? string.CompareOrdinal(rk, bound.RowKey) >= 0
                        : string.CompareOrdinal(rk, bound.RowKey) > 0))
                .OrderBy(rk => rk, StringComparer.Ordinal)
                .Take(limit)
                .Select(rk => (rk, $"{partition}/{rk}"))
                .ToList();
            return Task.FromResult(read);
        };

        /// <summary>The full newest-first order the pages must reproduce.</summary>
        public List<string> Expected() => _rows
            .SelectMany(kv => kv.Value.Where(_matches).Select(rk => (Partition: kv.Key, RowKey: rk)))
            .OrderBy(r => r.RowKey, StringComparer.Ordinal)
            .ThenBy(r => r.Partition, StringComparer.Ordinal)
            .Select(r => $"{r.Partition}/{r.RowKey}")
            .ToList();
    }

    private static async Task<List<List<string>>> WalkAsync(Store store, int pageSize)
    {
        var pages = new List<List<string>>();
        string? continuation = null;
        do
        {
            var page = await FetchPageAsync(store.Partitions, pageSize, continuation, store.Reader);
            pages.Add(page.Items.ToList());
            continuation = page.NextRawToken;
            if (pages.Count > 1000) Assert.Fail("Walk did not terminate");
        }
        while (continuation != null);
        return pages;
    }

    // ────────── Order ──────────────────────────────────────────────────────

    [Fact]
    public async Task First_page_is_newest_across_partitions_not_alphabetically_first()
    {
        var store = new Store(new()
        {
            ["Agent"] = new() { Rk(5) },
            ["Security"] = new() { Rk(0) },
            ["Tenant"] = new() { Rk(10) },
        });

        var page = await FetchPageAsync(store.Partitions, 3, null, store.Reader);

        Assert.Equal(new[] { $"Security/{Rk(0)}", $"Agent/{Rk(5)}", $"Tenant/{Rk(10)}" }, page.Items);
        Assert.Null(page.NextRawToken);
    }

    [Fact]
    public async Task First_page_reads_every_partition_unbounded_with_one_row_of_lookahead()
    {
        var store = new Store(new() { ["A"] = new() { Rk(1) }, ["B"] = new(), ["C"] = new() { Rk(2) } });

        await FetchPageAsync(store.Partitions, 15, null, store.Reader);

        Assert.Equal(new[] { "A", "B", "C" }, store.Calls.Select(c => c.Partition).OrderBy(p => p));
        Assert.All(store.Calls, c => Assert.Null(c.Bound));
        Assert.All(store.Calls, c => Assert.Equal(16, c.Limit));
    }

    // ────────── Watermark and bounds ───────────────────────────────────────

    [Fact]
    public void Bound_is_exclusive_up_to_the_watermark_partition_and_inclusive_after_it()
    {
        var w = new Watermark("0000000000000000042", "Maintenance");

        Assert.Null(BoundAfter(null, "Agent"));
        Assert.Equal(new RowKeyBound(w.RowKey, Inclusive: false), BoundAfter(w, "Agent"));
        Assert.Equal(new RowKeyBound(w.RowKey, Inclusive: false), BoundAfter(w, "Maintenance"));
        Assert.Equal(new RowKeyBound(w.RowKey, Inclusive: true), BoundAfter(w, "Security"));
    }

    [Fact]
    public void Bound_renders_as_an_escaped_odata_rowkey_clause()
    {
        Assert.Equal("RowKey gt '!0042_a'", new RowKeyBound("!0042_a", false).ToODataClause());
        Assert.Equal("RowKey ge 'it''s'", new RowKeyBound("it's", true).ToODataClause());
    }

    [Fact]
    public async Task Equal_rowkeys_in_different_partitions_all_survive_a_page_boundary()
    {
        // Ops-event RowKeys are bare reverse ticks, so two categories can share one. With a
        // RowKey-only watermark the second row would be skipped after the cut.
        var shared = Rk(3);
        var store = new Store(new()
        {
            ["Agent"] = new() { shared },
            ["Security"] = new() { shared },
            ["Tenant"] = new() { shared, Rk(4) },
        });

        var pages = await WalkAsync(store, pageSize: 1);

        Assert.Equal(store.Expected(), pages.SelectMany(p => p));
        Assert.Equal(4, pages.Count);
    }

    // ────────── Exact end of list ──────────────────────────────────────────

    [Fact]
    public async Task No_continuation_when_the_rows_exactly_fill_the_page()
    {
        var store = new Store(new() { ["A"] = new() { Rk(1), Rk(2), Rk(3) }, ["B"] = new() });

        var page = await FetchPageAsync(store.Partitions, 3, null, store.Reader);

        Assert.Equal(3, page.Items.Count);
        Assert.Null(page.NextRawToken);
    }

    [Fact]
    public async Task Continuation_when_one_row_is_left_over()
    {
        var store = new Store(new() { ["A"] = new() { Rk(1), Rk(2), Rk(3), Rk(4) } });

        var page = await FetchPageAsync(store.Partitions, 3, null, store.Reader);

        Assert.Equal(3, page.Items.Count);
        Assert.NotNull(page.NextRawToken);
    }

    // ────────── Full walks ─────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(100)]
    public async Task Walk_returns_every_row_once_in_global_order_and_never_an_empty_page(int pageSize)
    {
        // Uneven partitions: one empty, one dominant, interleaved timestamps and a tie.
        var store = new Store(new()
        {
            ["00000000-0000-0000-0000-000000000000"] = new() { Rk(0, "_g0"), Rk(40, "_g1") },
            ["1a"] = new() { Rk(1, "_a"), Rk(2, "_a"), Rk(3, "_a"), Rk(4, "_a"), Rk(5, "_a"), Rk(6, "_a"), Rk(30, "_a") },
            ["2b"] = new(),
            ["3c"] = new() { Rk(2, "_c"), Rk(7, "_c"), Rk(8, "_c") },
            ["4d"] = new() { Rk(9, "_a") },
            ["5e"] = new() { Rk(9, "_a"), Rk(50, "_e") },
        });

        var pages = await WalkAsync(store, pageSize);

        var expected = store.Expected();
        Assert.Equal(expected, pages.SelectMany(p => p));
        Assert.All(pages, p => Assert.NotEmpty(p));
        Assert.All(pages.Take(pages.Count - 1), p => Assert.Equal(pageSize, p.Count));
    }

    [Fact]
    public async Task Walk_with_a_server_side_filter_returns_exactly_the_matching_rows()
    {
        // The reader applies the filter (like `Action ne 'deletion_started'`), so excluded rows
        // never take a page slot and never become a watermark.
        var store = new Store(
            new()
            {
                ["A"] = new() { Rk(1, "_keep"), Rk(2, "_drop"), Rk(3, "_drop"), Rk(4, "_keep") },
                ["B"] = new() { Rk(2, "_drop"), Rk(5, "_keep"), Rk(6, "_drop") },
            },
            matches: rk => rk.EndsWith("_keep", StringComparison.Ordinal));

        var pages = await WalkAsync(store, pageSize: 2);

        Assert.Equal(store.Expected(), pages.SelectMany(p => p));
        Assert.Equal(3, store.Expected().Count);
    }

    [Fact]
    public async Task Empty_store_yields_one_empty_page_without_continuation()
    {
        var store = new Store(new() { ["A"] = new(), ["B"] = new() });

        var page = await FetchPageAsync(store.Partitions, 15, null, store.Reader);

        Assert.Empty(page.Items);
        Assert.Null(page.NextRawToken);
    }

    // ────────── Token ──────────────────────────────────────────────────────

    [Fact]
    public async Task Continuation_size_does_not_grow_with_the_number_of_partitions()
    {
        // The per-partition map this replaced grew with every partition holding rows until the
        // nextLink failed with HTTP 414. Every partition here has rows that lose the merge cut.
        static Store Tenants(int count) => new(Enumerable.Range(0, count).ToDictionary(
            i => Guid.Parse($"00000000-0000-0000-0000-{i:D12}").ToString(),
            i => new List<string> { Rk(i, "_" + Guid.Empty.ToString("N")), Rk(i + 1000, "_" + Guid.Empty.ToString("N")) }));

        var few = await FetchPageAsync(Tenants(3).Partitions, 1, null, Tenants(3).Reader);
        var many = await FetchPageAsync(Tenants(500).Partitions, 1, null, Tenants(500).Reader);

        Assert.NotNull(many.NextRawToken);
        Assert.Equal(few.NextRawToken!.Length, many.NextRawToken!.Length);
        Assert.True(many.NextRawToken.Length < 200, $"raw continuation is {many.NextRawToken.Length} characters");
    }

    [Fact]
    public void Watermark_round_trips()
    {
        var w = new Watermark("!2516116980007133310_a4bb51498cad468f89431b15f1e701f7", "tenant-a");

        Assert.Equal(w, DecodeWatermark(EncodeWatermark(w)));
    }

    [Fact]
    public void Decode_rejects_anything_that_is_not_a_v3_watermark()
    {
        static string B64(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json));

        Assert.Null(DecodeWatermark(null));
        Assert.Null(DecodeWatermark(""));
        Assert.Null(DecodeWatermark("not-base64-!@#"));
        Assert.Null(DecodeWatermark(B64("this is not json")));
        Assert.Null(DecodeWatermark(B64("""{"v":2,"c":{"Agent":{"rk":"0123"}}}""")));
        Assert.Null(DecodeWatermark(B64("""{"v":3,"rk":"","p":"Agent"}""")));
        Assert.Null(DecodeWatermark(B64("""{"v":3,"rk":"0123"}""")));
        Assert.Null(DecodeWatermark(B64("""{"v":"3","rk":"0123","p":"Agent"}""")));
    }

    [Fact]
    public async Task A_token_from_an_older_deploy_ends_the_list_without_reading_storage()
    {
        var store = new Store(new() { ["Agent"] = new() { Rk(1) } });
        var v2 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("""{"v":2,"c":{"Agent":{"rk":null}}}"""));

        var page = await FetchPageAsync(store.Partitions, 15, v2, store.Reader);

        Assert.Empty(page.Items);
        Assert.Null(page.NextRawToken);
        Assert.Empty(store.Calls);
    }
}
