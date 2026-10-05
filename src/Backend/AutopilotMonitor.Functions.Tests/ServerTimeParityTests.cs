using System.Globalization;
using AutopilotMonitor.Functions.Helpers;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// <c>tests/fixtures/server-time/cases.json</c> is the one definition of the server-time rule; the web
/// runs the same file against <c>lib/serverTime.ts</c>. A change to the rule goes into that file first;
/// this test and the web's then say which side is behind.
/// </summary>
public class ServerTimeParityTests
{
    private static readonly Lazy<JObject> Fixture = new(() =>
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "fixtures", "server-time", "cases.json");
        // No date parsing: every timestamp stays the string both sides parse themselves.
        return JsonConvert.DeserializeObject<JObject>(
            File.ReadAllText(path), new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!;
    });

    private static IEnumerable<JToken> Cases() => Fixture.Value["cases"]!;

    public static IEnumerable<object[]> CaseNames() => Cases().Select(c => new object[] { c.Value<string>("name")! });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Rule_matches_the_shared_case(string name)
    {
        var c = Cases().Single(x => x.Value<string>("name") == name);
        var events = Events(c);
        var expected = c["expect"]!;

        var actual = ServerTime.Compute(events, OptionalUtc(c["completedAt"]), c.Value<bool>("isPreProvisioned"));

        AssertTime(expected["start"], actual.Start, "start");
        AssertTime(expected["end"], actual.End, "end");
        AssertSeconds(expected["startOffset"], actual.StartOffsetSeconds, "startOffset");
        AssertSeconds(expected["duration"], actual.DurationSeconds, "duration");
        AssertSeconds(expected["part1"], actual.Part1Seconds, "part1");
        AssertSeconds(expected["part2"], actual.Part2Seconds, "part2");
        AssertTime(expected["part2Start"], actual.Part2Start, "part2Start");

        foreach (var probe in expected["eventServerTimes"]!)
        {
            var index = events.ToList().FindIndex(e => e.Sequence == probe.Value<long>("seq"));
            AssertTime(probe["server"], ServerTime.EventServerTime(events, index), $"event {probe.Value<long>("seq")}");
        }
    }

    [Fact]
    public void Shared_cases_are_present_and_well_formed()
    {
        var cases = Cases().ToList();
        Assert.True(cases.Count >= 20, "cases.json lost cases — the parity guard is only as wide as the file.");
        foreach (var c in cases)
        {
            var events = Events(c);
            // Listed in sequence order, sequence unique.
            Assert.True(events.Select(e => e.Sequence).SequenceEqual(events.Select(e => e.Sequence).OrderBy(s => s)), $"{c["name"]}: events not in sequence order");
            Assert.Equal(events.Count, events.Select(e => e.Sequence).Distinct().Count());
            // An upload is received after it was sent in true time; per event the pair is complete or absent.
            Assert.All(events, e => Assert.Equal(e.SentAt == null, e.ReceivedAt == null));
        }

        // Every rule element is pinned by a named case.
        var names = cases.Select(c => c.Value<string>("name")).ToHashSet();
        foreach (var required in new[]
                 {
                     "baseline", "constant-ahead-9h", "run2-clock-2h-back", "live-step-back-4m10s", "wg-part1-step-27m",
                     "end-upload-late-62s", "start-upload-late-70s", "no-sent-at", "frame1-closes-on-step",
                     "running-unconfirmed-upload", "running-confirmed-upload", "first-upload-without-agent-start",
                     "wg-part2-clock-back-swept",
                 })
        {
            Assert.Contains(required, names);
        }
    }

    [Theory]
    [InlineData(new[] { 5.0 }, 5.0)]
    [InlineData(new[] { 5.0, 1.0 }, 3.0)]
    [InlineData(new[] { 9.0, -70_000.0, 3.0 }, 3.0)]
    public void Median_of_up_to_three(double[] values, double expected) => Assert.Equal(expected, ServerTime.Median(values));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Tracker_gives_the_rule_start_after_every_upload(string name)
    {
        var c = Cases().Single(x => x.Value<string>("name") == name);
        var events = Events(c);
        var isPreProvisioned = c.Value<bool>("isPreProvisioned");

        var uploads = Uploads(events).ToList();
        if (uploads.Count == 0 || !uploads[0].Any(e => e.EventType == "agent_started"))
        {
            // Not followed live: ingest begins a tracker only with a first upload that carries agent_started,
            // and the tracker itself ignores uploads until one does.
            if (uploads.Count > 0)
            {
                var notBegun = new ServerTime.Tracker();
                notBegun.Absorb(uploads[0]);
                Assert.Equal(0, notBegun.Runs);
                Assert.Null(notBegun.StartedAtServer);
            }
            return;
        }

        var tracker = new ServerTime.Tracker();
        var seen = new List<ServerTime.Event>();
        foreach (var upload in uploads)
        {
            // Stored on the session row between uploads, as ingest does: the stored form must lose nothing.
            tracker = ServerTime.Tracker.FromJson(tracker.ToJson())!;
            tracker.Absorb(upload);
            seen.AddRange(upload);
            var rule = ServerTime.Compute(seen.OrderBy(e => e.Sequence).ToList(), null, isPreProvisioned);
            AssertSame(rule.Start, tracker.StartedAtServer, $"start after {seen.Count} events");
        }

        var final = ServerTime.Compute(events, OptionalUtc(c["completedAt"]), isPreProvisioned);
        AssertSame(final.Part2Start, tracker.ResumedAtServer, "WhiteGlove Part 2 start");
    }

    [Fact]
    public void Shared_cases_cover_a_white_glove_part_2_and_an_unfollowed_session()
    {
        // The tracker comparisons above are only as wide as the cases: at least one Part 2 start, one
        // Part 2 that does not fit the duration, and one session whose first upload has no agent_started.
        Assert.Contains(Cases(), c => c["expect"]!["part2Start"]!.Type != JTokenType.Null && c["expect"]!["part2"]!.Type != JTokenType.Null);
        Assert.Contains(Cases(), c => c["expect"]!["part2Start"]!.Type != JTokenType.Null && c["expect"]!["part2"]!.Type == JTokenType.Null);
        Assert.Contains(Cases(), c => c.Value<string>("name") == "first-upload-without-agent-start");
    }

    [Fact]
    public void Tracker_does_not_count_a_retried_agent_start_as_a_restart()
    {
        // The agent re-sends an upload whose response it never saw; the server processes it twice.
        var c = Cases().Single(x => x.Value<string>("name") == "baseline");
        var uploads = Uploads(Events(c)).ToList();

        var once = new ServerTime.Tracker();
        var withRetry = new ServerTime.Tracker();
        for (var i = 0; i < uploads.Count; i++)
        {
            once.Absorb(uploads[i]);
            withRetry.Absorb(uploads[i]);
            if (i == 0)
                withRetry.Absorb(Resent(uploads[i], latency: TimeSpan.FromSeconds(2)));
        }

        Assert.Contains(uploads[0], u => u.EventType == "agent_started");
        Assert.Equal(once.Runs, withRetry.Runs);
        Assert.Equal(once.FrameClosed, withRetry.FrameClosed);
        // The retry is one more offset sample of the same clock: the start moves by its latency at most.
        var diff = Math.Abs((withRetry.StartedAtServer!.Value - once.StartedAtServer!.Value).TotalSeconds);
        Assert.True(diff <= 2, $"a retried upload moved the start by {diff} s");
    }

    [Fact]
    public void Tracker_ignores_an_upload_without_send_time()
    {
        var tracker = new ServerTime.Tracker();
        tracker.Absorb(new[] { new ServerTime.Event(1, "agent_started", "Agent", new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), null, null) });

        Assert.Equal(0, tracker.Runs);
        Assert.Null(tracker.StartedAtServer);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{\"version\":2,\"runs\":1}")]
    public void Tracker_state_that_is_absent_or_of_another_format_is_not_used(string? json)
        => Assert.Null(ServerTime.Tracker.FromJson(json));

    [Fact]
    public void Tracker_state_that_is_unreadable_throws()
        => Assert.ThrowsAny<System.Text.Json.JsonException>(() => ServerTime.Tracker.FromJson("{"));

    /// <summary>Uploads as the backend receives them: one per (send, receive) pair, in receipt order.</summary>
    private static IEnumerable<List<ServerTime.Event>> Uploads(IReadOnlyList<ServerTime.Event> events) =>
        events.Where(e => e.SentAt != null && e.ReceivedAt != null)
            .GroupBy(e => (e.SentAt, e.ReceivedAt))
            .OrderBy(g => g.Key.ReceivedAt)
            .ThenBy(g => g.Min(e => e.Sequence))
            .Select(g => g.ToList());

    private static List<ServerTime.Event> Resent(List<ServerTime.Event> upload, TimeSpan latency) =>
        upload.Select(e => e with { SentAt = e.SentAt + TimeSpan.FromMinutes(1), ReceivedAt = e.ReceivedAt + TimeSpan.FromMinutes(1) + latency })
            .ToList();

    private static void AssertSame(DateTime? expected, DateTime? actual, string what)
    {
        Assert.True(expected.HasValue == actual.HasValue, $"{what}: expected {expected:o}, got {actual:o}");
        if (expected.HasValue)
        {
            var diff = Math.Abs((actual!.Value - expected.Value).TotalMilliseconds);
            Assert.True(diff <= 1, $"{what}: expected {expected:o}, got {actual:o} (off by {diff} ms)");
        }
    }

    private static IReadOnlyList<ServerTime.Event> Events(JToken c) =>
        c["events"]!.Select(e => new ServerTime.Event(
            e.Value<long>("seq"),
            e.Value<string>("type"),
            e.Value<string>("source"),
            Utc(e.Value<string>("t")!),
            OptionalUtc(e["sent"]),
            OptionalUtc(e["recv"]))).ToList();

    private static void AssertTime(JToken? expected, DateTime? actual, string what)
    {
        if (expected == null || expected.Type == JTokenType.Null)
        {
            Assert.True(actual == null, $"{what}: expected null, got {actual:o}");
            return;
        }
        Assert.True(actual != null, $"{what}: expected {expected}, got null");
        var diff = Math.Abs((actual!.Value - Utc(expected.Value<string>()!)).TotalMilliseconds);
        Assert.True(diff <= 1, $"{what}: expected {expected}, got {actual:o} (off by {diff} ms)");
    }

    private static void AssertSeconds(JToken? expected, double? actual, string what)
    {
        if (expected == null || expected.Type == JTokenType.Null)
        {
            Assert.True(actual == null, $"{what}: expected null, got {actual}");
            return;
        }
        Assert.True(actual != null, $"{what}: expected {expected}, got null");
        Assert.True(Math.Abs(actual!.Value - expected.Value<double>()) <= 0.001, $"{what}: expected {expected}, got {actual}");
    }

    private static DateTime? OptionalUtc(JToken? raw) =>
        raw == null || raw.Type == JTokenType.Null ? null : Utc(raw.Value<string>()!);

    private static DateTime Utc(string raw) =>
        DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AutopilotMonitor.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
