#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.DecisionCore.Engine;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    internal enum DownloadProgressSource
    {
        DeliveryOptimization,
        Ime,
    }

    internal enum DownloadProgressVerdict
    {
        /// <summary>Send the sample now.</summary>
        Emit,
        /// <summary>Too soon for this app's interval; keep it as the app's pending sample.</summary>
        Hold,
        /// <summary>Never send: nothing new, or the other source already reports this download.</summary>
        Drop,
    }

    /// <summary>
    /// Decides which download progress samples become <c>download_progress</c> events.
    /// <list type="bullet">
    ///   <item>One stream per download: while the Delivery Optimization collector reports an app
    ///     (a sample within <see cref="DoPreferenceWindow"/>, agent clock), the IME log's progress
    ///     lines for it are dropped. DO has the finer byte count, the peer values and a fixed poll;
    ///     mixing both made the live speed in the portal jump.</item>
    ///   <item>Always sent: the first sample of a download and its completion (bytes reach the total).
    ///     A repeat of the last sent byte count is never sent.</item>
    ///   <item>Otherwise at most one event per app per interval. The interval follows the session's
    ///     emitted total (<see cref="Stages"/>): 3 s as before for almost every session, then slower,
    ///     so long downloads cannot push the session over the event limit. Each download still
    ///     reports its first <see cref="LiveStart"/> from its first byte every 3 s.</item>
    /// </list>
    /// Intervals compare event times (the IME line's time for IME samples), so replayed lines get the
    /// same density as live ones. Not thread-safe: <see cref="DownloadProgressPublisher"/> serializes
    /// every call.
    /// </summary>
    internal sealed class DownloadProgressGate
    {
        /// <summary>
        /// From this many emitted <c>download_progress</c> events in the session on, the interval per
        /// app applies (D-444). Stage 0 must cover practically every session, so the live tracker stays
        /// at 3 s; no interval may reach the agent's first 2-minute stall probe, because progress
        /// counts as activity.
        /// </summary>
        internal static readonly IReadOnlyList<(int FromEventsSent, int IntervalSeconds)> Stages = new[]
        {
            (0, 3),
            (400, 10),
            (640, 30),
            (760, 60),
        };

        /// <summary>How long each download reports every 3 s, counted from its first byte.</summary>
        internal static readonly TimeSpan LiveStart = TimeSpan.FromSeconds(60);

        /// <summary>IME progress lines are dropped while a DO sample for the app is younger than this.</summary>
        internal static readonly TimeSpan DoPreferenceWindow = TimeSpan.FromSeconds(10);

        // Samples arrive on a fixed poll (DO 3 s, IME ~5 s): a sample a few ms early still counts.
        private static readonly TimeSpan IntervalTolerance = TimeSpan.FromMilliseconds(500);

        private readonly DownloadProgressBudget _budget;
        private readonly IClock _clock;
        private readonly Dictionary<string, AppProgress> _apps =
            new Dictionary<string, AppProgress>(StringComparer.OrdinalIgnoreCase);

        public DownloadProgressGate(DownloadProgressBudget budget, IClock clock)
        {
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public static int StageFor(int eventsSent)
        {
            var stage = 0;
            for (var i = 0; i < Stages.Count; i++)
            {
                if (eventsSent >= Stages[i].FromEventsSent) stage = i;
            }
            return stage;
        }

        public static int IntervalSecondsFor(int stage) =>
            Stages[Math.Max(0, Math.Min(stage, Stages.Count - 1))].IntervalSeconds;

        public DownloadProgressVerdict Evaluate(
            string appId, DownloadProgressSource source, long bytes, long total, DateTime eventTimeUtc)
        {
            var app = GetOrAdd(appId);

            var now = _clock.UtcNow;
            if (source == DownloadProgressSource.DeliveryOptimization)
            {
                app.LastDoSampleAtUtc = now;
            }
            else if (app.LastDoSampleAtUtc.HasValue)
            {
                var sinceDo = now - app.LastDoSampleAtUtc.Value;
                if (sinceDo >= TimeSpan.Zero && sinceDo < DoPreferenceWindow)
                    return DownloadProgressVerdict.Drop;
            }

            // The live start counts from the first byte, not the first sample: IME logs a 0 % line
            // when the download is queued, up to a minute before any byte moves.
            if (bytes > 0 && !app.FirstByteAtUtc.HasValue) app.FirstByteAtUtc = eventTimeUtc;

            if (!app.LastEmittedAtUtc.HasValue) return DownloadProgressVerdict.Emit;
            if (bytes == app.LastEmittedBytes && total == app.LastEmittedTotal) return DownloadProgressVerdict.Drop;
            if (total > 0 && bytes >= total) return DownloadProgressVerdict.Emit;

            var elapsed = eventTimeUtc - app.LastEmittedAtUtc.Value;
            // A clock stepped backwards must not mute the app until the clock catches up again.
            if (elapsed < TimeSpan.Zero) return DownloadProgressVerdict.Emit;

            return elapsed >= CurrentInterval(app, eventTimeUtc) - IntervalTolerance
                ? DownloadProgressVerdict.Emit
                : DownloadProgressVerdict.Hold;
        }

        /// <summary>Records a sent progress event; returns the stage it moved the session into, or null.</summary>
        public int? RecordEmitted(string appId, long bytes, long total, DateTime eventTimeUtc)
        {
            var app = GetOrAdd(appId);
            app.LastEmittedAtUtc = eventTimeUtc;
            app.LastEmittedBytes = bytes;
            app.LastEmittedTotal = total;
            return RecordSessionEvent();
        }

        /// <summary>
        /// Counts a <c>download_progress</c> event sent without a decision (the terminal tick) toward
        /// the session total; returns the stage it moved the session into, or null.
        /// </summary>
        public int? RecordSessionEvent()
        {
            var sent = _budget.RecordEvent();
            var stage = StageFor(sent);
            return _budget.TryAdvanceStage(stage) ? stage : (int?)null;
        }

        /// <summary>The app left Downloading: its next download starts fresh (first tick, live start).</summary>
        public void EndDownload(string appId)
        {
            // The DO timestamp stays: a late IME line for the finished download is still the
            // duplicate of what DO already reported.
            if (_apps.TryGetValue(appId, out var app)) app.ResetDownload();
        }

        private TimeSpan CurrentInterval(AppProgress app, DateTime eventTimeUtc)
        {
            if (app.FirstByteAtUtc.HasValue)
            {
                var sinceFirstByte = eventTimeUtc - app.FirstByteAtUtc.Value;
                if (sinceFirstByte >= TimeSpan.Zero && sinceFirstByte < LiveStart)
                    return TimeSpan.FromSeconds(Stages[0].IntervalSeconds);
            }
            return TimeSpan.FromSeconds(IntervalSecondsFor(StageFor(_budget.EventsSent)));
        }

        private AppProgress GetOrAdd(string appId)
        {
            if (!_apps.TryGetValue(appId, out var app))
            {
                app = new AppProgress();
                _apps[appId] = app;
            }
            return app;
        }

        private sealed class AppProgress
        {
            public DateTime? LastEmittedAtUtc;
            public long LastEmittedBytes = -1;
            public long LastEmittedTotal = -1;
            public DateTime? FirstByteAtUtc;
            // Agent clock of the last DO sample (any verdict), not an event time.
            public DateTime? LastDoSampleAtUtc;

            public void ResetDownload()
            {
                LastEmittedAtUtc = null;
                LastEmittedBytes = -1;
                LastEmittedTotal = -1;
                FirstByteAtUtc = null;
            }
        }
    }
}
