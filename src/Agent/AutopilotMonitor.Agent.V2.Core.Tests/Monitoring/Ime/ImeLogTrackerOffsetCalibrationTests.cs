using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// End-to-end offset calibration through a real read cycle.
    ///
    /// <para>
    /// A CMTrace line states local time and nothing else, so its UTC value depends on which zone
    /// the WRITING process believed it was in. The tracker used to substitute its own belief,
    /// which is right only when the two happen to agree.
    /// </para>
    ///
    /// <para>
    /// These tests are deliberately independent of the machine they run on: the assertion is
    /// always "a line written at instant T resolves to T", for every writer belief. That single
    /// property covers both field cases at once — the writer disagreeing with us (the bug) and
    /// the writer agreeing with us (the majority, which a naive fix would have broken).
    /// </para>
    /// </summary>
    public sealed class ImeLogTrackerOffsetCalibrationTests
    {
        private const string LogFileName = "IntuneManagementExtension.log";
        private static readonly DateTime T0 = new DateTime(2026, 8, 20, 7, 30, 0, DateTimeKind.Utc);

        private static List<ImeLogPattern> Patterns() => new List<ImeLogPattern>
        {
            new ImeLogPattern
            {
                PatternId = "T-MARK", Category = "always", Enabled = true,
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
                   "component=\"IntuneManagementExtension\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";
        }

        private sealed class Harness : IDisposable
        {
            private readonly TempDirectory _tmp = new TempDirectory();
            public ImeLogTracker Tracker { get; }
            public DateTime Now { get; set; } = T0;

            public Harness()
            {
                Tracker = new ImeLogTracker(
                    logFolder: _tmp.Path,
                    patterns: Patterns(),
                    logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info));
                Tracker.UtcNowProvider = () => Now;
            }

            public string LogPath => Path.Combine(_tmp.Path, LogFileName);

            public void Append(string line) => File.AppendAllText(LogPath, line + Environment.NewLine);

            public Task Pass() => Tracker.CheckLogFilesAsync(CancellationToken.None);

            public void Dispose()
            {
                Tracker.Dispose();
                _tmp.Dispose();
            }
        }

        [Theory]
        // The writer's belief. Whatever it is, a line written at T must come back as T.
        [InlineData(2)]     // CEST — the bfff9b1c writer, while the agent still believed BST
        [InlineData(1)]     // BST — writer and a UK-defaulted agent agreeing
        [InlineData(-7)]    // PDT — the OOBE default behind the -17 h sessions
        [InlineData(10)]    // E. Australia
        [InlineData(0)]     // UTC
        public async Task ResolvesLineToTheInstantItWasWritten_ForAnyWriterBelief(int writerOffsetHours)
        {
            var writerOffset = TimeSpan.FromHours(writerOffsetHours);
            using var h = new Harness();

            // Pass 1 — first sight of the file. Its content may be arbitrarily old, so nothing is
            // calibrated from it.
            h.Append(Line("marker 1", h.Now, writerOffset));
            await h.Pass();

            // Pass 2 — the file has grown against a size we already observed, so this line was
            // written within a poll interval and is a valid anchor.
            h.Now = T0.AddSeconds(10);
            h.Append(Line("marker 2", h.Now, writerOffset));
            await h.Pass();

            // Pass 3 — resolved with the measured offset.
            h.Now = T0.AddSeconds(20);
            var writtenAt = h.Now;
            h.Append(Line("marker 3", writtenAt, writerOffset));
            await h.Pass();

            Assert.True(h.Tracker.LastMatchedLogTimestamp.HasValue);
            Assert.Equal(writtenAt, h.Tracker.LastMatchedLogTimestamp.Value);
        }

        [Fact]
        public async Task MeasuresTheWritersOffset_NotItsOwn()
        {
            using var h = new Harness();
            var writerOffset = TimeSpan.FromHours(2);

            h.Append(Line("marker 1", h.Now, writerOffset));
            await h.Pass();

            h.Now = T0.AddSeconds(10);
            h.Append(Line("marker 2", h.Now, writerOffset));
            await h.Pass();

            TimeSpan measured;
            Assert.True(h.Tracker.OffsetCalibrator.TryGetOffset(LogFileName, out measured));
            Assert.Equal(writerOffset, measured);
        }

        [Fact]
        public async Task DoesNotCalibrateOnFirstSightOfAFile()
        {
            // The first pass reads from position 0, so the newest line in the file may be hours
            // old. Anchoring on it would measure the line's age instead of the writer's offset.
            using var h = new Harness();

            h.Append(Line("marker 1", T0.AddHours(-3), TimeSpan.FromHours(2)));
            await h.Pass();

            TimeSpan ignored;
            Assert.False(h.Tracker.OffsetCalibrator.TryGetOffset(LogFileName, out ignored));
        }

        [Fact]
        public async Task FollowsTheWriterAcrossARestartIntoANewZone()
        {
            // IME restarts mid-enrollment and picks up the timezone the OS moved to. The
            // measurement has to follow, otherwise every later line is off by the difference.
            using var h = new Harness();

            h.Append(Line("marker 1", h.Now, TimeSpan.FromHours(1)));
            await h.Pass();

            h.Now = T0.AddSeconds(10);
            h.Append(Line("marker 2", h.Now, TimeSpan.FromHours(1)));
            await h.Pass();

            TimeSpan before;
            Assert.True(h.Tracker.OffsetCalibrator.TryGetOffset(LogFileName, out before));
            Assert.Equal(TimeSpan.FromHours(1), before);

            // Writer restarts, now on +2.
            h.Now = T0.AddSeconds(20);
            h.Append(Line("marker 3", h.Now, TimeSpan.FromHours(2)));
            await h.Pass();

            h.Now = T0.AddSeconds(30);
            var writtenAt = h.Now;
            h.Append(Line("marker 4", writtenAt, TimeSpan.FromHours(2)));
            await h.Pass();

            TimeSpan after;
            Assert.True(h.Tracker.OffsetCalibrator.TryGetOffset(LogFileName, out after));
            Assert.Equal(TimeSpan.FromHours(2), after);
            Assert.True(h.Tracker.LastMatchedLogTimestamp.HasValue);
            Assert.Equal(writtenAt, h.Tracker.LastMatchedLogTimestamp.Value);
        }

        [Fact]
        public async Task HonorsAWriterDeclaredBiasWithoutMeasuring()
        {
            // A line carrying its own bias states the offset outright; it must be used as-is and
            // must never become a calibration anchor.
            using var h = new Harness();

            h.Append(Line("marker 1", h.Now, TimeSpan.FromHours(2)));
            await h.Pass();

            h.Now = T0.AddSeconds(10);
            var writtenAt = h.Now;
            var local = writtenAt + TimeSpan.FromHours(-8);        // a PST writer
            h.Append($"<![LOG[marker 2]LOG]!><time=\"{local:HH:mm:ss.fffffff}+480\" date=\"{local:M-d-yyyy}\" " +
                     "component=\"IntuneManagementExtension\" context=\"\" type=\"1\" thread=\"1\" file=\"\">");
            await h.Pass();

            Assert.True(h.Tracker.LastMatchedLogTimestamp.HasValue);
            Assert.Equal(writtenAt, h.Tracker.LastMatchedLogTimestamp.Value);

            TimeSpan ignored;
            Assert.False(h.Tracker.OffsetCalibrator.TryGetOffset(LogFileName, out ignored));
        }
    }
}
