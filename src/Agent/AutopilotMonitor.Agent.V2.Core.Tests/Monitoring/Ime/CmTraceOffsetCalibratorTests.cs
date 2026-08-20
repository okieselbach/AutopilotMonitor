using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Pins the measurement contract of <see cref="CmTraceOffsetCalibrator"/>.
    ///
    /// <para>
    /// The cases are taken from real sessions: +1 h (GMT to W. Europe, session bfff9b1c) and
    /// -17 h (a writer left on the Pacific OOBE default while the agent had moved to
    /// E. Australia). The backlog case is the one the residual guard cannot catch on its own and
    /// is the reason the freshness rule exists.
    /// </para>
    /// </summary>
    public class CmTraceOffsetCalibratorTests
    {
        private const string Source = "IntuneManagementExtension.log";
        private static readonly DateTime UtcNow = new DateTime(2026, 08, 20, 07, 31, 46, DateTimeKind.Utc);

        /// <summary>A line written "now" by a writer that believes it is at <paramref name="offset"/>.</summary>
        private static DateTime LineWrittenNow(TimeSpan offset, DateTime utcNow)
            => DateTime.SpecifyKind(utcNow + offset, DateTimeKind.Unspecified);

        [Theory]
        [InlineData(2)]     // CEST — the bfff9b1c writer
        [InlineData(1)]     // BST — what the agent wrongly assumed there
        [InlineData(-7)]    // PDT — the OOBE default seen in the -17 h sessions
        [InlineData(10)]    // E. Australia
        [InlineData(14)]    // Line Islands, the upper bound
        [InlineData(-12)]   // Dateline, the lower bound
        public void MeasuresWholeHourOffsets(int hours)
        {
            var sut = new CmTraceOffsetCalibrator();
            var offset = TimeSpan.FromHours(hours);

            Assert.True(sut.TryCalibrate(Source, LineWrittenNow(offset, UtcNow), UtcNow));

            TimeSpan measured;
            Assert.True(sut.TryGetOffset(Source, out measured));
            Assert.Equal(offset, measured);
        }

        [Theory]
        [InlineData(330)]   // India, UTC+5:30
        [InlineData(345)]   // Nepal, UTC+5:45
        [InlineData(570)]   // Adelaide, UTC+9:30 — the +30 min sessions in the field data
        public void MeasuresQuarterHourOffsets(int minutes)
        {
            var sut = new CmTraceOffsetCalibrator();
            var offset = TimeSpan.FromMinutes(minutes);

            Assert.True(sut.TryCalibrate(Source, LineWrittenNow(offset, UtcNow), UtcNow));

            TimeSpan measured;
            Assert.True(sut.TryGetOffset(Source, out measured));
            Assert.Equal(offset, measured);
        }

        [Fact]
        public void AbsorbsPollAndFlushLatency()
        {
            // The line was written a moment before we parsed it. That lag must vanish into the
            // grid rather than shift the measured offset.
            var sut = new CmTraceOffsetCalibrator();
            var lineLocal = LineWrittenNow(TimeSpan.FromHours(2), UtcNow.AddMilliseconds(-450));

            Assert.True(sut.TryCalibrate(Source, lineLocal, UtcNow));

            TimeSpan measured;
            Assert.True(sut.TryGetOffset(Source, out measured));
            Assert.Equal(TimeSpan.FromHours(2), measured);
        }

        [Fact]
        public void RejectsBacklogLine_EvenWhenItRoundsCleanly()
        {
            // THE case the residual guard cannot catch alone: a line exactly 30 minutes old is a
            // clean multiple of the grid, so it rounds with zero residual — and would calibrate
            // the offset half an hour wrong. Only the caller's freshness rule prevents this from
            // ever being offered; this test pins that a 30-minute-old line does not silently
            // produce the true offset.
            var sut = new CmTraceOffsetCalibrator();
            var thirtyMinutesOld = LineWrittenNow(TimeSpan.FromHours(2), UtcNow.AddMinutes(-30));

            Assert.True(sut.TryCalibrate(Source, thirtyMinutesOld, UtcNow));

            TimeSpan measured;
            Assert.True(sut.TryGetOffset(Source, out measured));
            Assert.Equal(TimeSpan.FromMinutes(90), measured);
            Assert.NotEqual(TimeSpan.FromHours(2), measured);
        }

        [Fact]
        public void RejectsOffGridCandidate()
        {
            // 7 minutes off the grid: not fresh, and not a timezone either.
            var sut = new CmTraceOffsetCalibrator();
            var offGrid = LineWrittenNow(TimeSpan.FromHours(2), UtcNow.AddMinutes(-7));

            Assert.False(sut.TryCalibrate(Source, offGrid, UtcNow));

            TimeSpan ignored;
            Assert.False(sut.TryGetOffset(Source, out ignored));
        }

        [Fact]
        public void RejectsOffsetOutsideAnyRealTimezone()
        {
            var sut = new CmTraceOffsetCalibrator();
            var absurd = LineWrittenNow(TimeSpan.FromHours(20), UtcNow);

            Assert.False(sut.TryCalibrate(Source, absurd, UtcNow));
        }

        [Fact]
        public void SelfHeals_WhenTheWritingProcessPicksUpANewZone()
        {
            // The writer restarts mid-session and now believes CEST instead of the stale BST.
            var sut = new CmTraceOffsetCalibrator();
            Assert.True(sut.TryCalibrate(Source, LineWrittenNow(TimeSpan.FromHours(1), UtcNow), UtcNow));

            var later = UtcNow.AddMinutes(5);
            Assert.True(sut.TryCalibrate(Source, LineWrittenNow(TimeSpan.FromHours(2), later), later));

            TimeSpan measured;
            Assert.True(sut.TryGetOffset(Source, out measured));
            Assert.Equal(TimeSpan.FromHours(2), measured);
        }

        [Fact]
        public void IgnoresAnchorOlderThanTheCurrentOne()
        {
            var sut = new CmTraceOffsetCalibrator();
            Assert.True(sut.TryCalibrate(Source, LineWrittenNow(TimeSpan.FromHours(2), UtcNow), UtcNow));

            // An out-of-order line from before the current anchor must not move the offset back.
            var earlier = UtcNow.AddMinutes(-10);
            Assert.False(sut.TryCalibrate(Source, LineWrittenNow(TimeSpan.FromHours(1), earlier), earlier));

            TimeSpan measured;
            Assert.True(sut.TryGetOffset(Source, out measured));
            Assert.Equal(TimeSpan.FromHours(2), measured);
        }

        [Fact]
        public void KeepsSourcesIndependent()
        {
            // IntuneManagementExtension.log and AppWorkload.log are written by different
            // processes and may hold different beliefs.
            var sut = new CmTraceOffsetCalibrator();
            Assert.True(sut.TryCalibrate("IntuneManagementExtension.log", LineWrittenNow(TimeSpan.FromHours(2), UtcNow), UtcNow));
            Assert.True(sut.TryCalibrate("AppWorkload.log", LineWrittenNow(TimeSpan.FromHours(-7), UtcNow), UtcNow));

            TimeSpan ime, appWorkload;
            Assert.True(sut.TryGetOffset("IntuneManagementExtension.log", out ime));
            Assert.True(sut.TryGetOffset("AppWorkload.log", out appWorkload));
            Assert.Equal(TimeSpan.FromHours(2), ime);
            Assert.Equal(TimeSpan.FromHours(-7), appWorkload);
        }

        [Fact]
        public void ResolvesTheRealFieldLine_FromSessionBfff9b1c()
        {
            // Raw line: <![LOG[Agent version is: 1.104.102.0]LOG]!><time="09:31:46.5192284" ...>
            // Server-side ReceivedAt puts the true UTC at 07:31:46.4. The old code produced
            // 08:31:46.5192284 — local minus the agent's stale +1 h belief.
            var sut = new CmTraceOffsetCalibrator();
            var trueUtc = new DateTime(2026, 08, 20, 07, 31, 46, 400, DateTimeKind.Utc);
            var lineLocal = new DateTime(2026, 08, 20, 09, 31, 46, 519, DateTimeKind.Unspecified);

            Assert.True(sut.TryCalibrate(Source, lineLocal, trueUtc));

            DateTime resolved;
            Assert.True(sut.TryResolveUtc(Source, lineLocal, out resolved));
            Assert.Equal(DateTimeKind.Utc, resolved.Kind);
            Assert.Equal(new DateTime(2026, 08, 20, 07, 31, 46, 519, DateTimeKind.Utc), resolved);
        }

        [Fact]
        public void ReportsUncalibratedSource_SoTheCallerCanFlagIt()
        {
            var sut = new CmTraceOffsetCalibrator();

            DateTime resolved;
            Assert.False(sut.TryResolveUtc(Source, UtcNow, out resolved));
        }
    }
}
