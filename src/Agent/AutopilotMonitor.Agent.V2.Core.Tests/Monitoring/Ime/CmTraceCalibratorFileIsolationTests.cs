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
    /// Pins the per-FILE isolation of the offset measurement across a full read cycle.
    ///
    /// <para>
    /// Background (2026-08-20, session e9753578): the field showed "+02:00 measured for
    /// IntuneManagementExtension.log" although that file held no +2-era line — the anchor had
    /// crossed file boundaries. The committed code was proven innocent (this very scenario is
    /// green here); the leak lived in a binary built from an uncommitted tree. This test keeps
    /// the property pinned so a real regression of the file scoping can never ship silently
    /// again — it replays the exact field pass structure: a NEW file appearing mid-run with a
    /// different writer era than the file already being tailed.
    /// </para>
    /// </summary>
    public sealed class CmTraceCalibratorFileIsolationTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 8, 20, 12, 13, 0, DateTimeKind.Utc);

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

        private static string Line(string message, DateTime utcInstant, TimeSpan writerOffset)
        {
            var local = utcInstant + writerOffset;
            return $"<![LOG[{message}]LOG]!><time=\"{local:HH:mm:ss.fffffff}\" date=\"{local:M-d-yyyy}\" " +
                   "component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";
        }

        [Fact]
        public async Task NewFileWithOtherEra_MustNotRecalibrateTheOldFile()
        {
            var pdt = TimeSpan.FromHours(-7);   // IME service era
            var cest = TimeSpan.FromHours(2);   // fresh child-process era

            using var tmp = new TempDirectory();
            var now = T0;
            var tracker = new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: Patterns(),
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info));
            try
            {
                tracker.UtcNowProvider = () => now;
                var imeLog = Path.Combine(tmp.Path, "IntuneManagementExtension.log");
                var execLog = Path.Combine(tmp.Path, "AgentExecutor.log");

                // Pass 1: IME.log first sight (no calibration).
                File.AppendAllText(imeLog, Line("marker 1", now, pdt) + Environment.NewLine);
                await tracker.CheckLogFilesAsync(CancellationToken.None);

                // Pass 2: IME.log grew -> measures -7.
                now = T0.AddSeconds(10);
                File.AppendAllText(imeLog, Line("marker 2", now, pdt) + Environment.NewLine);
                await tracker.CheckLogFilesAsync(CancellationToken.None);

                TimeSpan imeMeasured;
                Assert.True(tracker.OffsetCalibrator.TryGetOffset("IntuneManagementExtension.log", out imeMeasured));
                Assert.Equal(pdt, imeMeasured);

                // Pass 3: AgentExecutor.log appears for the FIRST time with fresh +2-era lines
                // while IME.log grows too — the exact field pass structure. The +2 anchor must
                // never land under IME.log's key.
                now = T0.AddSeconds(20);
                File.AppendAllText(execLog, Line("marker 3", now, cest) + Environment.NewLine);
                File.AppendAllText(imeLog, Line("marker 4", now, pdt) + Environment.NewLine);
                await tracker.CheckLogFilesAsync(CancellationToken.None);

                Assert.True(tracker.OffsetCalibrator.TryGetOffset("IntuneManagementExtension.log", out imeMeasured));
                Assert.Equal(pdt, imeMeasured);

                // Pass 4: both grow — each file measures its own writer's era.
                now = T0.AddSeconds(30);
                File.AppendAllText(execLog, Line("marker 5", now, cest) + Environment.NewLine);
                File.AppendAllText(imeLog, Line("marker 6", now, pdt) + Environment.NewLine);
                await tracker.CheckLogFilesAsync(CancellationToken.None);

                TimeSpan execMeasured;
                Assert.True(tracker.OffsetCalibrator.TryGetOffset("AgentExecutor.log", out execMeasured));
                Assert.Equal(cest, execMeasured);
                Assert.True(tracker.OffsetCalibrator.TryGetOffset("IntuneManagementExtension.log", out imeMeasured));
                Assert.Equal(pdt, imeMeasured);
            }
            finally
            {
                tracker.Dispose();
            }
        }
    }
}
