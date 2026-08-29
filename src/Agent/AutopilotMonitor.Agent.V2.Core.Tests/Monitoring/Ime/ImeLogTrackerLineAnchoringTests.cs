using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Logging;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Per-line self-anchoring — the era-aware successor to the reverted per-file offset
    /// application (04b1a7c6).
    ///
    /// <para>
    /// The property under test: a line read in a provably FRESH pass resolves to the instant it
    /// was written, whatever timezone its writer believed — and neighbouring lines from writers
    /// with DIFFERENT beliefs each resolve to their own era, because no cross-line state is
    /// involved. That interleaving (fixture: AgentExecutor.log with PDT and CEST children in one
    /// file, flipping both ways) is exactly what broke every cross-line anchor design.
    /// </para>
    ///
    /// <para>
    /// The freshness boundary is equally load-bearing: backlog (first sight, restart catch-up,
    /// stalled polls) must NEVER anchor, because an old line's age can round onto the 15-minute
    /// offset grid. Those paths keep the uniform reader-zone fallback.
    /// </para>
    /// </summary>
    public sealed class ImeLogTrackerLineAnchoringTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 20, 12, 13, 0, DateTimeKind.Utc);

        private static List<ImeLogPattern> Patterns() => new List<ImeLogPattern>
        {
            new ImeLogPattern
            {
                PatternId = "T-ALL", Category = "always", Enabled = true,
                Pattern = @"marker (?<n>\d+)",
                Action = "noop",
                Parameters = new Dictionary<string, string>(),
            },
        };

        /// <summary>A CMTrace line as a writer holding <paramref name="writerOffset"/> renders instant <paramref name="utcInstant"/>.</summary>
        private static string Line(string message, DateTime utcInstant, TimeSpan writerOffset)
        {
            var local = utcInstant + writerOffset;
            return $"<![LOG[{message}]LOG]!><time=\"{local:HH:mm:ss.fffffff}\" date=\"{local:M-d-yyyy}\" " +
                   "component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";
        }

        private sealed class Harness : IDisposable
        {
            private readonly TempDirectory _tmp = new TempDirectory();
            public ImeLogTracker Tracker { get; }
            public DateTime Now { get; set; } = T0;

            public Harness(string? stateDirectory = null)
            {
                Tracker = new ImeLogTracker(
                    logFolder: _tmp.Path,
                    patterns: Patterns(),
                    logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                    stateDirectory: stateDirectory);
                Tracker.UtcNowProvider = () => Now;
            }

            public string PathOf(string fileName) => Path.Combine(_tmp.Path, fileName);

            public void Append(string fileName, string line)
                => File.AppendAllText(PathOf(fileName), line + Environment.NewLine);

            public Task Pass() => Tracker.CheckLogFilesAsync(CancellationToken.None);

            public void Dispose()
            {
                Tracker.Dispose();
                _tmp.Dispose();
            }
        }

        [Theory]
        // The writer's belief. A fresh line must resolve to the instant it was written,
        // WHATEVER that belief is — including agreement with this process (the majority case,
        // which a fix to the agent's own zone cache would have broken).
        [InlineData(2)]     // CEST
        [InlineData(1)]     // BST
        [InlineData(0)]     // UTC
        [InlineData(-7)]    // PDT — the OOBE default behind the -9 h and -17 h field cases
        [InlineData(10)]    // E. Australia
        public async Task FreshLine_ResolvesToItsTrueInstant_ForAnyWriterBelief(int writerOffsetHours)
        {
            var writerOffset = TimeSpan.FromHours(writerOffsetHours);
            using var h = new Harness();

            // First sight: backlog, must not anchor.
            h.Append("IntuneManagementExtension.log", Line("marker 1", h.Now, writerOffset));
            await h.Pass();

            h.Now = T0.AddSeconds(10);
            var writtenAt = h.Now.AddMilliseconds(-80);   // written just before the poll sees it
            h.Append("IntuneManagementExtension.log", Line("marker 2", writtenAt, writerOffset));
            await h.Pass();

            Assert.Equal(CmTraceOffsetOrigin.LineAnchored, h.Tracker.LastMatchedSourceOffsetOrigin);
            Assert.Equal(writtenAt, h.Tracker.LastMatchedLogTimestamp);
            Assert.Equal(writerOffsetHours * 60, h.Tracker.LastMatchedSourceOffsetMinutes);
        }

        [Fact]
        public async Task InterleavedEras_InOneGrowingPass_EachLineResolvesToItsOwnEra()
        {
            // The e9753578 file shape: two writer processes with different zone beliefs
            // interleaved in ONE file. Any per-file offset gets one of them wrong by 9 h;
            // per-line anchoring must get both right within the same chunk.
            var pdt = TimeSpan.FromHours(-7);
            var cest = TimeSpan.FromHours(2);
            using var h = new Harness();

            h.Append("AgentExecutor.log", Line("marker 1", h.Now, pdt));
            await h.Pass();

            h.Now = T0.AddSeconds(10);
            var wrotePdt = h.Now.AddMilliseconds(-90);
            var wroteCest = h.Now.AddMilliseconds(-50);
            h.Append("AgentExecutor.log", Line("marker 2", wrotePdt, pdt));
            h.Append("AgentExecutor.log", Line("marker 3", wroteCest, cest));

            var resolved = new List<(DateTime? Ts, CmTraceOffsetOrigin Origin, int? Offset)>();
            Action<string> capture = _ => resolved.Add((
                h.Tracker.LastMatchedLogTimestamp,
                h.Tracker.LastMatchedSourceOffsetOrigin,
                h.Tracker.LastMatchedSourceOffsetMinutes));
            h.Tracker.OnPatternMatched += capture;
            try
            {
                await h.Pass();
            }
            finally
            {
                h.Tracker.OnPatternMatched -= capture;
            }

            Assert.Equal(2, resolved.Count);
            Assert.Equal(wrotePdt, resolved[0].Ts);
            Assert.Equal(-420, resolved[0].Offset);
            Assert.Equal(wroteCest, resolved[1].Ts);
            Assert.Equal(120, resolved[1].Offset);
            Assert.All(resolved, r => Assert.Equal(CmTraceOffsetOrigin.LineAnchored, r.Origin));
        }

        [Fact]
        public async Task FirstSightBacklog_FallsBackToReaderZone()
        {
            using var h = new Harness();

            h.Append("IntuneManagementExtension.log", Line("marker 1", h.Now.AddHours(-3), TimeSpan.FromHours(2)));
            await h.Pass();

            Assert.Equal(CmTraceOffsetOrigin.None, h.Tracker.LastMatchedSourceOffsetOrigin);
        }

        [Fact]
        public async Task FileFirstSeenEmpty_ItsFirstContentIsFresh()
        {
            // AgentExecutor.log in the field: the file APPEARS mid-enrollment. Seeing it empty
            // (or not existing content-wise) counts as an observation, so its very first lines
            // are provably written between passes and may anchor. This closes the warm-up gap
            // for exactly the file whose eras flip the most.
            using var h = new Harness();

            File.WriteAllText(h.PathOf("AgentExecutor.log"), string.Empty);
            await h.Pass();

            h.Now = T0.AddSeconds(5);
            var writtenAt = h.Now.AddMilliseconds(-60);
            h.Append("AgentExecutor.log", Line("marker 1", writtenAt, TimeSpan.FromHours(2)));
            await h.Pass();

            Assert.Equal(CmTraceOffsetOrigin.LineAnchored, h.Tracker.LastMatchedSourceOffsetOrigin);
            Assert.Equal(writtenAt, h.Tracker.LastMatchedLogTimestamp);
        }

        [Fact]
        public async Task PollGapBeyondFreshWindow_FallsBack()
        {
            // A stalled agent (GC, CPU starvation) re-polls after a long gap: the new bytes span
            // the whole gap and their age is unbounded, so the pass must not anchor — even
            // though most of its lines may in fact be recent. Conservative by design.
            using var h = new Harness();

            h.Append("IntuneManagementExtension.log", Line("marker 1", h.Now, TimeSpan.FromHours(2)));
            await h.Pass();
            h.Now = T0.AddSeconds(10);
            h.Append("IntuneManagementExtension.log", Line("marker 2", h.Now, TimeSpan.FromHours(2)));
            await h.Pass();
            Assert.Equal(CmTraceOffsetOrigin.LineAnchored, h.Tracker.LastMatchedSourceOffsetOrigin);

            // Gap far beyond FreshLineMaxAge.
            h.Now = h.Now + ImeLogTracker.FreshLineMaxAge + TimeSpan.FromSeconds(10);
            h.Append("IntuneManagementExtension.log", Line("marker 3", h.Now, TimeSpan.FromHours(2)));
            await h.Pass();

            Assert.Equal(CmTraceOffsetOrigin.None, h.Tracker.LastMatchedSourceOffsetOrigin);

            // And the very next tight poll is fresh again.
            h.Now = h.Now.AddSeconds(1);
            h.Append("IntuneManagementExtension.log", Line("marker 4", h.Now, TimeSpan.FromHours(2)));
            await h.Pass();
            Assert.Equal(CmTraceOffsetOrigin.LineAnchored, h.Tracker.LastMatchedSourceOffsetOrigin);
        }

        [Fact]
        public async Task RestartWithRestoredBookmark_FirstPassIsBacklog_SecondPassAnchors()
        {
            // The persisted position bookmark survives a restart; the freshness guarantee must
            // not — the first pass after a restart reads the downtime backlog, whose lines can
            // be arbitrarily old.
            using var stateDir = new TempDirectory();
            using var h = new Harness(stateDirectory: stateDir.Path);

            h.Append("IntuneManagementExtension.log", Line("marker 1", h.Now, TimeSpan.FromHours(2)));
            await h.Pass();
            h.Tracker.SaveStateForTest();

            // "Restart": a second tracker over the same folder restores the bookmark.
            using var h2 = new HarnessOver(h, stateDir.Path);
            h2.Tracker.LoadStateForTest();

            // Backlog written during the downtime — 30 min old, exactly on the offset grid,
            // the nastiest possible backlog shape. Must NOT anchor.
            h2.Now = T0.AddMinutes(30);
            h2.Append(Line("marker 2", T0.AddSeconds(20), TimeSpan.FromHours(2)));
            await h2.Pass();
            Assert.Equal(CmTraceOffsetOrigin.None, h2.Tracker.LastMatchedSourceOffsetOrigin);

            // The next tight poll is fresh again.
            h2.Now = h2.Now.AddSeconds(1);
            var writtenAt = h2.Now.AddMilliseconds(-70);
            h2.Append(Line("marker 3", writtenAt, TimeSpan.FromHours(2)));
            await h2.Pass();
            Assert.Equal(CmTraceOffsetOrigin.LineAnchored, h2.Tracker.LastMatchedSourceOffsetOrigin);
            Assert.Equal(writtenAt, h2.Tracker.LastMatchedLogTimestamp);
        }

        /// <summary>Second tracker over the FIRST harness's log folder — the "restarted agent".</summary>
        private sealed class HarnessOver : IDisposable
        {
            private readonly string _logFolder;
            public ImeLogTracker Tracker { get; }
            public DateTime Now { get; set; }

            public HarnessOver(Harness original, string stateDirectory)
            {
                _logFolder = Path.GetDirectoryName(original.PathOf("x"));
                Tracker = new ImeLogTracker(
                    logFolder: _logFolder,
                    patterns: Patterns(),
                    logger: new AgentLogger(_logFolder, AgentLogLevel.Info),
                    stateDirectory: stateDirectory);
                Tracker.UtcNowProvider = () => Now;
            }

            public void Append(string line)
                => File.AppendAllText(Path.Combine(_logFolder, "IntuneManagementExtension.log"), line + Environment.NewLine);

            public Task Pass() => Tracker.CheckLogFilesAsync(CancellationToken.None);

            public void Dispose() => Tracker.Dispose();
        }

        [Fact]
        public async Task FreshlyWrittenReplayLine_OnExactGridAge_ResolvesToRoughlyNow()
        {
            // Characterization of the one accepted edge: a line freshly WRITTEN but carrying a
            // REPLAYED timestamp whose age is exactly a grid multiple (here 30 min) measures
            // that age as an "offset" and resolves to ~now instead of its replayed past. The
            // error is bounded by construction (an anchored line always lands at now ± 2 min),
            // and sub-24h replays are treated as current by the historic-replay guard anyway.
            using var h = new Harness();

            h.Append("IntuneManagementExtension.log", Line("marker 1", h.Now, TimeSpan.Zero));
            await h.Pass();

            h.Now = T0.AddSeconds(10);
            var replayedInstant = h.Now.AddMinutes(-30);
            h.Append("IntuneManagementExtension.log", Line("marker 2", replayedInstant, TimeSpan.Zero));
            await h.Pass();

            Assert.Equal(CmTraceOffsetOrigin.LineAnchored, h.Tracker.LastMatchedSourceOffsetOrigin);
            Assert.NotNull(h.Tracker.LastMatchedLogTimestamp);
            var error = (h.Tracker.LastMatchedLogTimestamp!.Value - h.Now).Duration();
            Assert.True(error <= CmTraceOffsetCalibrator.MaxGridResidual,
                $"anchored line must resolve to now ± residual, was off by {error}");
        }

        // =====================================================================
        // The committed field fixture — session e9753578, ImeLogs/AgentExecutor.log.
        // One file, two writer eras (PDT hour-05 / CEST hour-14), interleaved, flipping
        // BOTH ways. Replayed here as the live tail the field agent saw: entry by entry,
        // with the clock following the true write time.
        // =====================================================================

        private static string FixturePath()
        {
            var dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12; i++)
            {
                if (File.Exists(Path.Combine(dir, "AutopilotMonitor.sln")))
                    return Path.Combine(dir, "tests", "fixtures", "cmtrace-logs",
                        "agentexecutor-two-writer-eras-v1.cmtrace");
                var parent = Directory.GetParent(dir)?.FullName;
                if (parent == null || parent == dir) break;
                dir = parent;
            }
            throw new DirectoryNotFoundException(
                "Could not locate repo root (AutopilotMonitor.sln) walking up from " + AppContext.BaseDirectory);
        }

        /// <summary>Group the fixture's physical lines into complete CMTrace entries (multiline-safe).</summary>
        private static List<string> FixtureEntries()
        {
            var entries = new List<string>();
            string? pending = null;
            foreach (var rawWithBom in File.ReadAllLines(FixturePath()))
            {
                // The fixture keeps the original file's BOM verbatim — it sat at the START of
                // the real AgentExecutor.log, which is mid-file in this excerpt.
                var raw = rawWithBom.TrimStart('﻿');
                if (pending == null)
                {
                    if (raw.Length == 0 || raw[0] == '#') continue;
                    if (!raw.StartsWith("<![LOG[", StringComparison.Ordinal)) continue;
                    if (raw.Contains("]LOG]!>")) { entries.Add(raw); continue; }
                    pending = raw;
                }
                else
                {
                    pending += "\n" + raw;
                    if (raw.Contains("]LOG]!>")) { entries.Add(pending!); pending = null; }
                }
            }
            return entries;
        }

        [Fact]
        public async Task TwoWriterEraFixture_EveryLineOfBothErasResolvesToItsTrueInstant()
        {
            // The committed fixture is a curated, verbatim excerpt of the transition window
            // (commit policy: anonymized, versioned) — 20 entries covering both eras and the flip.
            var entries = FixtureEntries();
            Assert.True(entries.Count >= 15, $"fixture unexpectedly small: {entries.Count} entries");

            using var h = new Harness();

            // Match every fixture entry.
            h.Tracker.CompilePatterns(new List<ImeLogPattern>
            {
                new ImeLogPattern
                {
                    PatternId = "T-ANY", Category = "always", Enabled = true,
                    Pattern = ".+", Action = "noop",
                    Parameters = new Dictionary<string, string>(),
                },
            });

            // The file exists (empty) before its first content — first sight is the observation
            // baseline, everything after is a fresh tail, exactly like the field.
            File.WriteAllText(h.PathOf("AgentExecutor.log"), string.Empty);
            h.Now = new DateTime(2026, 8, 20, 12, 13, 30, DateTimeKind.Utc);
            await h.Pass();

            var anchored = 0;
            var resolvedList = new List<(DateTime Resolved, DateTime Expected)>();
            Action<string> capture = _ =>
            {
                if (h.Tracker.LastMatchedSourceOffsetOrigin == CmTraceOffsetOrigin.LineAnchored)
                    anchored++;
                if (h.Tracker.LastMatchedLogTimestamp.HasValue && _expectedForCurrentEntry.HasValue)
                    resolvedList.Add((h.Tracker.LastMatchedLogTimestamp.Value, _expectedForCurrentEntry.Value));
            };
            h.Tracker.OnPatternMatched += capture;
            try
            {
                foreach (var entry in entries)
                {
                    CmTraceLogEntry? parsed;
                    Assert.True(CmTraceLogParser.TryParseLine(entry, out parsed), "fixture entry did not parse");

                    // Era by local hour: the fixture holds exactly two eras — PDT (UTC-7) lines
                    // render at local hour 05, CEST (UTC+2) lines at hour 14 (fixture header).
                    var eraOffset = parsed.LocalTimestamp.Hour == 14
                        ? TimeSpan.FromHours(2)
                        : TimeSpan.FromHours(-7);
                    var trueUtc = DateTime.SpecifyKind(parsed.LocalTimestamp - eraOffset, DateTimeKind.Utc);

                    // The clock follows the write; never move backwards (interleaved eras are
                    // written in true-UTC order, but guard against sub-ms jitter).
                    var readAt = trueUtc.AddMilliseconds(120);
                    if (readAt > h.Now) h.Now = readAt;

                    // In production the 100 ms poll keeps running through write silences — every
                    // empty look refreshes the freshness window. The fixture's transition window
                    // spans multi-minute write gaps, so replay that cadence with one empty poll
                    // just before each append; without it the NEXT pass would (correctly!)
                    // classify the gap as a stall and refuse to anchor.
                    h.Now = h.Now.AddMilliseconds(-100);
                    await h.Pass();
                    h.Now = readAt;

                    _expectedForCurrentEntry = trueUtc;
                    File.AppendAllText(h.PathOf("AgentExecutor.log"), entry + Environment.NewLine);
                    await h.Pass();
                    _expectedForCurrentEntry = null;
                }
            }
            finally
            {
                h.Tracker.OnPatternMatched -= capture;
            }

            // Every matched entry resolved to the instant its writer rendered — across BOTH
            // eras, including the flips. This is the exact property whose absence produced the
            // -9 h script durations.
            Assert.Equal(entries.Count, resolvedList.Count);
            Assert.All(resolvedList, r => Assert.Equal(r.Expected, r.Resolved));
            Assert.True(anchored == resolvedList.Count,
                $"expected every entry line-anchored, got {anchored}/{resolvedList.Count}");

            // Both eras genuinely exercised: PDT entries render at local hour 05, CEST at 14.
            Assert.Contains(resolvedList, r => (r.Expected + TimeSpan.FromHours(-7)).Hour == 5);
            Assert.Contains(resolvedList, r => (r.Expected + TimeSpan.FromHours(2)).Hour == 14);
        }

        private DateTime? _expectedForCurrentEntry;

        // ── Multiline buffer caps ─────────────────────────────────────────────

        /// <summary>
        /// 100 raw lines of unbounded length could still assemble a multi-megabyte entry. The
        /// char cap drops such an entry (its marker never fires) and says so at Warning, which
        /// is visible at the default Info level — a capped real entry would be news worth
        /// reacting to. A same-shaped entry under the cap still assembles and matches.
        /// </summary>
        [Theory]
        [InlineData(2 * 1024 * 1024, false)]   // over cap → dropped, warned
        [InlineData(64 * 1024, true)]          // under cap → assembled, matched
        public async Task MultilineEntry_IsDroppedAndWarned_WhenItExceedsTheCharCap(int fillerChars, bool expectMatch)
        {
            using var h = new Harness();
            h.Append("AgentExecutor.log", Line("marker 0", h.Now, TimeSpan.Zero));
            await h.Pass();

            var matched = new List<string>();
            h.Tracker.OnPatternMatched += id => matched.Add(id);

            // One entry spanning 3 physical lines; the closing trailer sits on the last line.
            var filler = new string('x', fillerChars / 2);
            h.Append("AgentExecutor.log", "<![LOG[write output done. output = " + filler);
            h.Append("AgentExecutor.log", filler);
            h.Append("AgentExecutor.log", Line("marker 7", h.Now, TimeSpan.Zero).Substring("<![LOG[".Length));
            h.Now = T0.AddSeconds(10);
            await h.Pass();

            Assert.Equal(expectMatch ? 1 : 0, matched.Count);
            var agentLog = string.Concat(Directory.GetFiles(h.PathOf(""), "*.log")
                .Where(f => !Path.GetFileName(f).Equals("AgentExecutor.log", StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
            if (expectMatch)
                Assert.DoesNotContain("discarding multiline CMTrace buffer", agentLog);
            else
                Assert.Contains("discarding multiline CMTrace buffer in AgentExecutor.log", agentLog);
        }
    }
}
