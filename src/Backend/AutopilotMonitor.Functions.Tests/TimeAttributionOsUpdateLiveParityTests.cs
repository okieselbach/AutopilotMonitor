using System.Globalization;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The web shows the OOBE quality update while the session still runs (<c>lib/osUpdateLive.ts</c>);
/// its numbers are a port of the calculator's update interval for one window.
/// <c>tests/fixtures/os-update-live/cases.json</c> holds the cases both sides run: each cut is the
/// session as the backend had it at that moment, and its interval is what the time attribution
/// reports if the session ended then. A change to the calculator's update logic goes into that
/// file first; this test and the web's then say which side is behind.
/// </summary>
public class TimeAttributionOsUpdateLiveParityTests
{
    private static readonly Lazy<JObject> Fixture = new(() =>
    {
        var path = Path.Combine(FindRepoRoot(), "tests", "fixtures", "os-update-live", "cases.json");
        // No date parsing: every timestamp stays the string both sides parse themselves.
        return JsonConvert.DeserializeObject<JObject>(
            File.ReadAllText(path), new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!;
    });

    private static IEnumerable<JToken> Scenarios() => Fixture.Value["scenarios"]!;

    public static IEnumerable<object[]> Cuts()
        => Scenarios().SelectMany(s => s["cuts"]!.Select(c => new object[] { s.Value<string>("name")!, c.Value<string>("now")! }));

    [Theory]
    [MemberData(nameof(Cuts))]
    public void Interval_matches_the_shared_case(string scenario, string now)
    {
        var s = Scenarios().Single(x => x.Value<string>("name") == scenario);
        var expected = s["cuts"]!.Single(c => c.Value<string>("now") == now)["interval"]!;

        var actual = ComputeAt(s, Utc(now));

        if (expected.Type == JTokenType.Null)
        {
            Assert.Null(actual);
            return;
        }
        Assert.NotNull(actual);
        Assert.Equal(Utc(expected.Value<string>("start")!), actual!.StartUtc);
        Assert.Equal(Utc(expected.Value<string>("end")!), actual.EndUtc);
        Assert.Equal(expected.Value<int>("rebootCount"), actual.RebootCount);
        Assert.Equal(expected.Value<string>("outcome"), actual.Outcome);
        Assert.Equal(expected["kbs"]!.ToObject<string[]>()!, actual.Kbs);
        Assert.Equal(expected["notInstalledKbs"]!.ToObject<string[]>()!, actual.NotInstalledKbs);
    }

    [Fact]
    public void Shared_cases_are_present_and_well_formed()
    {
        var scenarios = Scenarios().ToList();
        Assert.True(scenarios.Count >= 15, "cases.json lost scenarios — the parity guard is only as wide as the file.");

        foreach (var s in scenarios)
        {
            // Listed in the order the agent sent them (= sequence): by arrival.
            var arrivals = s["events"]!.Select(e => Utc(e.Value<string>("arrives") ?? e.Value<string>("t")!)).ToList();
            Assert.True(arrivals.SequenceEqual(arrivals.OrderBy(a => a)), $"{s["name"]}: events are not in arrival order");
            Assert.All(s["events"]!, e => Assert.True(
                e["arrives"] == null || Utc(e.Value<string>("arrives")!) >= Utc(e.Value<string>("t")!),
                $"{s["name"]}: an event arrives before it happened"));
            Assert.All(s["cuts"]!, c =>
            {
                Assert.NotNull(c["interval"]);
                Assert.NotNull(c["live"]);
            });
        }

        // Every outcome is pinned somewhere.
        var outcomes = scenarios
            .SelectMany(s => s["cuts"]!)
            .Select(c => c["interval"]!)
            .Where(i => i.Type != JTokenType.Null)
            .Select(i => i.Value<string>("outcome"))
            .ToHashSet();
        Assert.All(OsUpdateOutcomes.All, o => Assert.Contains(o, outcomes));
    }

    /// <summary>
    /// The update in the current part (from ResumedAt, else StartedAt) if the session had ended at
    /// <paramref name="now"/> with the events the backend had by then — windows as the terminal
    /// writer derives them: [StartedAt, now], or for WhiteGlove part 1 up to its completion plus
    /// [ResumedAt, now].
    /// </summary>
    private static OsUpdateSpan? ComputeAt(JToken scenario, DateTime now)
    {
        var session = scenario["session"]!;
        var startedAt = Utc(session.Value<string>("startedAt")!);
        var resumedAt = session.Value<string?>("resumedAt") is { } resumed ? Utc(resumed) : (DateTime?)null;

        var events = new List<EnrollmentEvent>();
        long sequence = 0;
        foreach (var e in scenario["events"]!)
        {
            sequence++;
            if (Utc(e.Value<string>("arrives") ?? e.Value<string>("t")!) >= now) continue;
            events.Add(new EnrollmentEvent
            {
                EventType = e.Value<string>("type")!,
                Timestamp = Utc(e.Value<string>("t")!),
                Sequence = sequence,
                Phase = e.Value<string?>("phase") is { } phase ? Enum.Parse<EnrollmentPhase>(phase) : EnrollmentPhase.Unknown,
                Data = e["data"] is JObject data
                    ? data.Properties().ToDictionary(p => p.Name, p => ((JValue)p.Value).Value!)
                    : new Dictionary<string, object>(),
            });
        }

        var windowStart = resumedAt ?? startedAt;
        var duration = (int)(now - windowStart).TotalSeconds;
        if (resumedAt.HasValue)
            duration += (int)(events.Last(e => e.EventType == "whiteglove_part1_complete").Timestamp - startedAt).TotalSeconds;

        var breakdown = TimeAttributionCalculator.Compute(new TimeAttributionInput
        {
            TenantId = "00000000-0000-0000-0000-0000000000t1",
            SessionId = "00000000-0000-0000-0000-0000000000s1",
            Status = "Succeeded",
            StartedAt = startedAt,
            CompletedAt = now,
            DurationSeconds = duration,
            IsPreProvisioned = session.Value<bool?>("isPreProvisioned") ?? false,
            ResumedAt = resumedAt,
            Events = events,
        });
        Assert.NotNull(breakdown);
        return breakdown!.OsUpdates.SingleOrDefault(u => u.StartUtc >= windowStart);
    }

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
