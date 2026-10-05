using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Sessions d29e27e3 and 4377911b: a concurrent IME writer overwrote the result line of a platform script, and the
    /// overwrite check recovered it one pass later. Recovered lines were never fresh, so they resolved in the agent's own
    /// cached zone (+60) instead of anchoring themselves (+120): the end stamp landed an hour ahead of the clock and the
    /// run duration was omitted. Bytes that read differently at a position than at the last look were written after that
    /// look, so when the replaced bytes were confirmed seconds ago the new line's age is bounded like an appended line's —
    /// these tests pin that it anchors itself then, and only then.
    /// </summary>
    public sealed partial class ImeLogTrackerOverwriteRewindTests
    {
        private static void AppendExecutorLog(Harness h, string text)
            => File.AppendAllText(Path.Combine(Path.GetDirectoryName(h.LogPath)!, "AgentExecutor.log"), text, new UTF8Encoding(false));

        /// <summary>An IME log entry with the byte length of <paramref name="sameLengthAs"/>: what a stale writer's line replaces exactly.</summary>
        private static string FillerLike(string time, string sameLengthAs)
        {
            var bare = ImeEntryAt(time, "[Win32App] checking ");
            Assert.True(sameLengthAs.Length > bare.Length);
            return ImeEntryAt(time, "[Win32App] checking " + new string('x', sameLengthAs.Length - bare.Length));
        }

        /// <summary>
        /// A platform run whose IME writer lives in +120 (local = UTC + 2 h; the harness clock is 19:30Z): both logs are
        /// observed empty first, so what follows is read fresh. Returns the IME log position where the filler line sits.
        /// </summary>
        private static async Task<long> StartRunAndReadFiller(Harness h, string fillerTime, string resultEntry)
        {
            h.Append(string.Empty);
            AppendExecutorLog(h, string.Empty);
            await h.Tracker.RunPollPassAsync(default);

            AppendExecutorLog(h, RunStart("21:29:50.0000000", "1001"));
            h.Append(ImeRunStart("21:29:48.0000000"));
            await h.Tracker.RunPollPassAsync(default);

            // Another IME writer appends a line; the tracker reads it.
            var at = h.Length;
            h.Append(FillerLike(fillerTime, resultEntry));
            h.Now = h.Now.AddSeconds(1);
            await h.Tracker.RunPollPassAsync(default);
            return at;
        }

        [Fact]
        public async Task Result_line_recovered_seconds_after_its_bytes_were_read_anchors_itself_like_a_fresh_line()
        {
            using var h = new Harness("IntuneManagementExtension.log", ScriptPatterns());
            var result = ImeRunResult("21:30:01.1000000", "Success");
            var at = await StartRunAndReadFiller(h, "21:30:00.5000000", result);

            // The script ends: its end block, then the stale IME service writes the result over the filler.
            AppendExecutorLog(h, RunEnd("21:30:01.0000000", 0, "done", ""));
            h.Overwrite(at, result);
            h.Now = h.Now.AddSeconds(1);
            await PassAndSettle(h);

            Assert.Equal(1, h.Health.OverwriteRewinds);
            var script = Assert.Single(h.Completed);
            Assert.Equal(CmTraceOffsetOrigin.LineAnchored, script.ResultProvenance!.Origin);
            Assert.Equal(120, script.ResultProvenance.OffsetMinutes);
            Assert.Equal(ImeLogTrackerAdapter.DurationPairVerdict.Verified, ImeLogTrackerAdapter.JudgeDurationPair(
                script.StartedAtProvenance, script.ResultProvenance, false, script.ResultObservedAtUtc!.Value - script.StartedAtUtc!.Value, out _));
        }

        [Fact]
        public async Task Verification_passes_keep_the_replaced_bytes_current_for_a_script_that_runs_longer()
        {
            using var h = new Harness("IntuneManagementExtension.log", ScriptPatterns());
            var result = ImeRunResult("21:30:41.1000000", "Success");
            var at = await StartRunAndReadFiller(h, "21:30:00.5000000", result);

            // The script runs 40 s; the cadence check verifies the ledger every 10 s while the run is in flight.
            for (var i = 0; i < 4; i++)
            {
                h.Now = h.Now.AddSeconds(10);
                await h.Tracker.RunPollPassAsync(default);
            }

            AppendExecutorLog(h, RunEnd("21:30:41.0000000", 0, "done", ""));
            h.Overwrite(at, result);
            h.Now = h.Now.AddSeconds(1);
            await PassAndSettle(h);

            var script = Assert.Single(h.Completed);
            Assert.Equal(CmTraceOffsetOrigin.LineAnchored, script.ResultProvenance!.Origin);
            Assert.Equal(120, script.ResultProvenance.OffsetMinutes);
        }

        [Fact]
        public async Task Overwrite_does_not_move_the_files_measured_writer_offset()
        {
            using var h = new Harness("IntuneManagementExtension.log", ScriptPatterns());
            var result = ImeRunResult("21:30:01.1000000", "Success");
            var at = await StartRunAndReadFiller(h, "21:30:00.5000000", result);
            Assert.True(h.Tracker.OffsetCalibrator.TryGetOffset("IntuneManagementExtension.log", out var before));

            AppendExecutorLog(h, RunEnd("21:30:01.0000000", 0, "done", ""));
            h.Overwrite(at, result);
            h.Now = h.Now.AddSeconds(1);
            await PassAndSettle(h);

            Assert.True(h.Tracker.OffsetCalibrator.TryGetOffset("IntuneManagementExtension.log", out var after));
            Assert.Equal(before, after);
        }

        [Theory]
        // Replaced bytes confirmed 1 s / 30 s ago: written since, age bounded like an appended line's.
        [InlineData(true, true, 1, true)]
        [InlineData(true, true, 30, true)]
        // Confirmed longer ago: the new bytes could be minutes old — their age may round onto the 15-minute grid.
        [InlineData(true, true, 31, false)]
        // The clock stepped back since the confirmation: the age is unknown.
        [InlineData(true, true, -1, false)]
        // The divergence lies in bytes the ledger never held (dropped by a cap): nothing dates them.
        [InlineData(true, false, 1, false)]
        // The pass itself is not fresh — the first pass after a restart never anchors.
        [InlineData(false, true, 1, false)]
        // Nothing was replaced.
        [InlineData(true, true, null, false)]
        public void Rewound_entry_is_fresh_only_when_the_bytes_it_replaced_were_confirmed_within_the_fresh_window(
            bool passLinesAreFresh, bool entryAtRewindPointReplaced, int? secondsSinceConfirmed, bool expected)
        {
            var now = new DateTime(2026, 10, 4, 19, 2, 0, DateTimeKind.Utc);
            DateTime? confirmed = secondsSinceConfirmed.HasValue ? now.AddSeconds(-secondsSinceConfirmed.Value) : (DateTime?)null;
            Assert.Equal(expected, ImeLogTracker.RewoundEntryIsFresh(passLinesAreFresh, entryAtRewindPointReplaced, confirmed, now));
        }
    }
}
