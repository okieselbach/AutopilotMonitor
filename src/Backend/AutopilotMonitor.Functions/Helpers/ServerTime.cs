using System.Text.Json;
using System.Text.Json.Serialization;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Durations in server time. Device clocks are wrong in a measurable share of sessions — off by
/// whole hours for the entire session (time zone in the hardware clock), stepped mid-session by a
/// program or a sync, or on a different era after a restart — while the order of events stays the
/// sequence. Every upload carries the device send time (<c>X-Send-Time-Utc</c>) next to the server
/// receive time, so its offset (<c>sent − recv</c>) is the device clock error minus the upload's
/// latency, and an event's server time is its device time minus that offset.
/// <para>
/// The one definition is <c>tests/fixtures/server-time/cases.json</c> (with its README); the web
/// port is <c>lib/serverTime.ts</c>. A change goes into the case file first.
/// </para>
/// </summary>
public static class ServerTime
{
    /// <summary>An offset change of at least this much is a clock change, below it upload noise.</summary>
    public const double FrameStepMs = 60_000;

    /// <summary>The start never moves more than this before the first agent start (today's StartedAt guard).</summary>
    public const double StartGuardMs = 2 * 3_600_000;

    /// <summary>An end anchor must have been uploaded live: buffer wait within [0, this].</summary>
    public const double LiveWaitMs = 60_000;

    /// <summary>One event as the rule needs it: device time, the upload's device send time and server receive time.</summary>
    public sealed record Event(long Sequence, string? EventType, string? Source, DateTime Time, DateTime? SentAt, DateTime? ReceivedAt)
    {
        /// <summary>The rule's view of an ingested event: its device time is the original one when the backend clamped it.</summary>
        public static Event From(EnrollmentEvent e) => new(
            e.Sequence,
            e.EventType,
            e.Source,
            e.TimestampClamped && e.OriginalTimestamp.HasValue ? e.OriginalTimestamp.Value : e.Timestamp,
            e.SentAt,
            e.ReceivedAt);
    }

    /// <summary>
    /// Start and end in server time and the duration they give. Durations and the start offset are
    /// seconds; null where the input does not allow a value (no send times, still running).
    /// <see cref="Part2Start"/> is the WhiteGlove Part 2 start whenever the events show one, also
    /// while running and when Part 2 does not fit the duration (it starts after the end).
    /// </summary>
    public sealed record Result(
        DateTime? Start,
        double? StartOffsetSeconds,
        DateTime? End,
        double? DurationSeconds,
        double? Part1Seconds,
        double? Part2Seconds,
        DateTime? Part2Start)
    {
        public static readonly Result None = new(null, null, null, null, null, null, null);
    }

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed class Upload
    {
        public double Sent;
        public double Recv;
        public double Offset => Sent - Recv;
        public long MinSequence;
        public int Run;
        public double Smooth;
        public double RunHead;
        public readonly List<int> Events = new();
    }

    /// <summary>
    /// Session start, end and duration in server time (see the README of the case file).
    /// <paramref name="completedAt"/> is the stored device-clock completion, null while running.
    /// </summary>
    public static Result Compute(IReadOnlyList<Event> events, DateTime? completedAt, bool isPreProvisioned)
    {
        var (uploads, byRun, uploadOf) = BuildUploads(events);
        if (uploads.Count == 0)
            return Result.None;

        var run0 = byRun[0];
        var startOffset = run0[0].RunHead;
        var frame1 = new List<Upload>();
        foreach (var u in run0)
        {
            if (Math.Abs(u.Smooth - startOffset) >= FrameStepMs)
                break;
            frame1.Add(u);
        }

        // The smoothed frame test lags one upload behind a step, and that first post-step upload
        // carries device times of the new era: an upload whose own offset is off the start offset is
        // trusted only when the next upload is still inside frame 1 (a delayed upload, not a step).
        var candidates = new List<int>();
        for (var j = 0; j < frame1.Count; j++)
        {
            var trusted = Math.Abs(frame1[j].Offset - startOffset) < FrameStepMs || j + 1 < frame1.Count;
            if (!trusted)
                continue;
            candidates.AddRange(frame1[j].Events.Where(i => IsEligible(events[i])));
        }

        var agentStarts = Enumerable.Range(0, events.Count)
            .Where(i => events[i].EventType == Constants.EventTypes.AgentStarted && events[i].SentAt != null)
            .OrderBy(i => events[i].Sequence)
            .ToList();
        if (candidates.Count == 0)
            return Result.None;
        var reference = agentStarts.Count > 0 ? Ms(events[agentStarts[0]].Time) : candidates.Min(i => Ms(events[i].Time));
        var inGuard = candidates.Select(i => Ms(events[i].Time)).Where(t => t >= reference - StartGuardMs).ToList();
        if (agentStarts.Count > 0)
            inGuard.Add(reference);
        var start = inGuard.Min() - startOffset;

        // WhiteGlove Part 2 start: the first agent_started after the first whiteglove_complete, minus
        // the start offset of its run.
        var whiteGlove = Enumerable.Range(0, events.Count)
            .Where(i => events[i].EventType == Constants.EventTypes.WhiteGloveComplete && events[i].SentAt != null)
            .OrderBy(i => events[i].Sequence)
            .Select(i => (int?)i)
            .FirstOrDefault();
        double? part2Start = null;
        if (whiteGlove is int w)
        {
            var uw = uploadOf[w]!;
            var next = agentStarts.FirstOrDefault(i =>
                uploadOf[i]!.Recv > uw.Recv || (uploadOf[i]!.Recv == uw.Recv && events[i].Sequence > events[w].Sequence), -1);
            if (next >= 0)
                part2Start = Ms(events[next].Time) - uploadOf[next]!.RunHead;
        }
        var startOnly = new Result(FromMs(start), startOffset / 1000, null, null, null, null, ToTime(part2Start));

        if (completedAt == null)
            return startOnly;

        var live = Enumerable.Range(0, events.Count)
            .Where(i => events[i].SentAt != null && events[i].ReceivedAt != null && IsEligible(events[i]))
            .Where(i =>
            {
                var w = Ms(events[i].SentAt!.Value) - Ms(events[i].Time);
                return w >= 0 && w <= LiveWaitMs;
            })
            .ToList();
        var done = Ms(completedAt.Value);
        int endIndex;
        var match = live.Where(i => Math.Abs(Ms(events[i].Time) - done) < 1000)
            .OrderBy(i => IsTerminal(events[i]) ? 0 : 1)
            .ThenBy(i => events[i].Sequence)
            .ToList();
        if (match.Count > 0)
        {
            endIndex = match[0];
        }
        else
        {
            var before = live.Where(i => Ms(events[i].Time) <= done).ToList();
            if (before.Count == 0)
                return startOnly;
            endIndex = before.OrderByDescending(i => Ms(events[i].Time)).ThenByDescending(i => events[i].Sequence).First();
        }

        var end = Ms(events[endIndex].Time) - uploadOf[endIndex]!.Smooth;
        var duration = end - start;
        double? part1 = null, part2 = null;
        if (isPreProvisioned && whiteGlove is int wg && part2Start is double p2 && p2 <= end)
        {
            var part1End = Ms(events[wg].Time) - uploadOf[wg]!.Smooth;
            part1 = (part1End - start) / 1000;
            part2 = (end - p2) / 1000;
            duration = part1End - start + (end - p2);
        }

        return startOnly with { End = FromMs(end), DurationSeconds = duration / 1000, Part1Seconds = part1, Part2Seconds = part2 };
    }

    /// <summary>Server time of one event: its device time minus the offset in use for its upload; null without send times.</summary>
    public static DateTime? EventServerTime(IReadOnlyList<Event> events, int index)
    {
        var (_, _, uploadOf) = BuildUploads(events);
        var u = uploadOf[index];
        return u == null ? null : FromMs(Ms(events[index].Time) - u.Smooth);
    }

    /// <summary>
    /// The rule fed one upload at a time in receipt order: the state ingest keeps on the session row
    /// (<c>ServerTimeState</c>), so the live start and the WhiteGlove Part 2 start need no event
    /// history. It begins with the session's first upload, which carries agent_started (uploads
    /// before one are ignored); from there, after every upload it gives the start and the Part 2
    /// start <see cref="Compute"/> gives for the same events (pinned on every upload prefix of the
    /// shared cases by <c>ServerTimeParityTests</c>). An upload the agent retried counts as one more
    /// offset sample of the same clock: seconds, never a restart. Times and offsets are epoch
    /// milliseconds.
    /// </summary>
    public sealed class Tracker
    {
        /// <summary>Format of the persisted state; a row in another format is left alone.</summary>
        public const int CurrentVersion = 1;

        private const int MaxRememberedAgentStarts = 16;

        private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public int Version { get; set; } = CurrentVersion;

        /// <summary>agent_started uploads absorbed: the first opens run 1, every further one the next run; 0 = not begun.</summary>
        public int Runs { get; set; }

        /// <summary>Offsets of the first three uploads of run 1; their median is the start offset.</summary>
        public List<double> Head { get; set; } = new();

        /// <summary>Offsets of the last three uploads of run 1; their median is the smoothed offset.</summary>
        public List<double> Recent { get; set; } = new();

        /// <summary>Frame 1 has ended: the smoothed offset moved 60 s or more off the start offset, or a second run began.</summary>
        public bool FrameClosed { get; set; }

        /// <summary>Device time of the first agent_started: the reference of the 2-h guard and itself a start candidate.</summary>
        public double? Reference { get; set; }

        /// <summary>Earliest confirmed start candidate (device time).</summary>
        public double? StartDevice { get; set; }

        /// <summary>
        /// Earliest candidate of the last upload whose own offset is 60 s or more off the start offset:
        /// a delayed upload when the next upload is still in frame 1 (then it counts), a clock step otherwise.
        /// </summary>
        public double? PendingDevice { get; set; }

        /// <summary>agent_started events already counted, as (sequence, device time): an agent retry sends them again.</summary>
        public List<double[]> AgentStarts { get; set; } = new();

        /// <summary>A whiteglove_complete was absorbed.</summary>
        public bool WhiteGloveComplete { get; set; }

        /// <summary>Device time of the first agent_started after whiteglove_complete (WhiteGlove Part 2 start).</summary>
        public double? Part2Device { get; set; }

        /// <summary>The run <see cref="Part2Device"/> opened.</summary>
        public int? Part2Run { get; set; }

        /// <summary>Offsets of the first three uploads of the Part 2 run.</summary>
        public List<double> Part2Head { get; set; } = new();

        /// <summary>The session start on the server clock; null until an upload with send time carried a candidate.</summary>
        [JsonIgnore]
        public DateTime? StartedAtServer =>
            StartDevice == null || Head.Count == 0 ? null : FromMs(StartDevice.Value - Median(Head));

        /// <summary>The WhiteGlove Part 2 start on the server clock; null until Part 2 began.</summary>
        [JsonIgnore]
        public DateTime? ResumedAtServer =>
            Part2Device == null || Part2Head.Count == 0 ? null : FromMs(Part2Device.Value - Median(Part2Head));

        /// <summary>Absorbs one upload (all its events share one send and one receive time); a no-op without send time.</summary>
        public void Absorb(IReadOnlyList<Event> upload)
        {
            if (upload.Count == 0 || upload[0].SentAt == null || upload[0].ReceivedAt == null)
                return;
            var offset = Ms(upload[0].SentAt!.Value) - Ms(upload[0].ReceivedAt!.Value);
            var events = upload.OrderBy(e => e.Sequence).ToList();

            // A run opens on an agent_started not counted before; a retried upload carries the same one.
            var starts = events
                .Where(e => e.EventType == Constants.EventTypes.AgentStarted
                            && !AgentStarts.Any(a => a[0] == e.Sequence && a[1] == Ms(e.Time)))
                .ToList();
            foreach (var e in starts)
                AgentStarts.Add(new[] { e.Sequence, Ms(e.Time) });
            if (AgentStarts.Count > MaxRememberedAgentStarts)
                AgentStarts.RemoveRange(0, AgentStarts.Count - MaxRememberedAgentStarts);
            if (Runs == 0 && starts.Count == 0)
                return; // not begun: the tracker starts with an agent_started
            var whiteGloveSequence = events.FirstOrDefault(e => e.EventType == Constants.EventTypes.WhiteGloveComplete)?.Sequence;

            if (starts.Count > 0)
            {
                Runs++;
                if (Runs > 1)
                {
                    FrameClosed = true;
                    PendingDevice = null;
                    Recent.Clear();
                }
                else
                {
                    // The first agent_started: the reference of the 2-h guard and itself a start candidate.
                    Reference = Ms(starts[0].Time);
                    StartDevice = Reference;
                }
                if (Part2Device == null)
                {
                    var part2 = starts.FirstOrDefault(e =>
                        WhiteGloveComplete || (whiteGloveSequence != null && e.Sequence > whiteGloveSequence));
                    if (part2 != null)
                    {
                        Part2Device = Ms(part2.Time);
                        Part2Run = Runs;
                    }
                }
            }
            if (Part2Device != null && Runs == Part2Run && Part2Head.Count < 3)
                Part2Head.Add(offset);
            if (whiteGloveSequence != null)
                WhiteGloveComplete = true;

            if (FrameClosed)
                return;
            var headWasFull = Head.Count == 3;
            if (!headWasFull)
                Head.Add(offset);
            Recent.Add(offset);
            if (Recent.Count > 3)
                Recent.RemoveAt(0);
            var startOffset = Median(Head);
            // The first three uploads always belong to frame 1: their smoothed offset is the start offset.
            if (headWasFull && Math.Abs(Median(Recent) - startOffset) >= FrameStepMs)
            {
                FrameClosed = true;
                PendingDevice = null;
                Recent.Clear();
                return;
            }
            if (PendingDevice != null)
            {
                StartDevice = Math.Min(StartDevice ?? PendingDevice.Value, PendingDevice.Value);
                PendingDevice = null;
            }
            var guardFrom = Reference!.Value - StartGuardMs;
            var candidates = events
                .Where(IsEligible)
                .Select(e => Ms(e.Time))
                .Where(t => t >= guardFrom)
                .ToList();
            if (candidates.Count == 0)
                return;
            var earliest = candidates.Min();
            if (Math.Abs(offset - startOffset) < FrameStepMs)
                StartDevice = Math.Min(StartDevice ?? earliest, earliest);
            else
                PendingDevice = earliest;
        }

        public string ToJson() => JsonSerializer.Serialize(this, Json);

        /// <summary>
        /// The persisted state, or null when the column is empty or of another format (another build
        /// owns it). Unreadable JSON throws <see cref="JsonException"/>.
        /// </summary>
        public static Tracker? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;
            var tracker = JsonSerializer.Deserialize<Tracker>(json, Json);
            return tracker?.Version == CurrentVersion ? tracker : null;
        }
    }

    private static (List<Upload> Uploads, Dictionary<int, List<Upload>> ByRun, Upload?[] UploadOf) BuildUploads(IReadOnlyList<Event> events)
    {
        var byKey = new Dictionary<(double, double), Upload>();
        for (var i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if (e.SentAt == null || e.ReceivedAt == null)
                continue;
            var key = (Ms(e.SentAt.Value), Ms(e.ReceivedAt.Value));
            if (!byKey.TryGetValue(key, out var u))
            {
                u = new Upload { Sent = key.Item1, Recv = key.Item2, MinSequence = e.Sequence };
                byKey[key] = u;
            }
            u.Events.Add(i);
            u.MinSequence = Math.Min(u.MinSequence, e.Sequence);
        }

        var uploads = byKey.Values.OrderBy(u => u.Recv).ThenBy(u => u.MinSequence).ToList();
        var run = -1;
        foreach (var u in uploads)
        {
            if (u.Events.Any(i => events[i].EventType == Constants.EventTypes.AgentStarted))
                run++;
            u.Run = Math.Max(run, 0);
        }

        var byRun = uploads.GroupBy(u => u.Run).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var list in byRun.Values)
        {
            var head = Median(list.Take(3).Select(u => u.Offset));
            for (var j = 0; j < list.Count; j++)
            {
                list[j].RunHead = head;
                list[j].Smooth = j < 2 ? head : Median(list.Skip(j - 2).Take(3).Select(u => u.Offset));
            }
        }

        var uploadOf = new Upload?[events.Count];
        foreach (var u in uploads)
            foreach (var i in u.Events)
                uploadOf[i] = u;
        return (uploads, byRun, uploadOf);
    }

    /// <summary>Median of one to three values (mean of the middle two for an even count).</summary>
    internal static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        if (v.Length == 0)
            throw new ArgumentException("Median of an empty set", nameof(values));
        var mid = v.Length / 2;
        return v.Length % 2 == 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2;
    }

    private static bool IsEligible(Event e) => Constants.EventSources.IsSessionAnchorEligible(e.Source);

    private static bool IsTerminal(Event e) =>
        e.EventType == Constants.EventTypes.EnrollmentComplete || e.EventType == Constants.EventTypes.EnrollmentFailed;

    private static double Ms(DateTime t) => (DateTime.SpecifyKind(t, DateTimeKind.Utc) - Epoch).TotalMilliseconds;

    private static DateTime? ToTime(double? ms) => ms == null ? null : FromMs(ms.Value);

    private static DateTime FromMs(double ms) => Epoch.AddTicks((long)Math.Round(ms * TimeSpan.TicksPerMillisecond));
}
