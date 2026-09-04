#nullable enable
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
    /// End-to-end through the tracker: a restart/first-sight backlog whose pre-reboot era was
    /// written under the OOBE default zone (a7140f98: Pacific) while the agent believes the
    /// zone Windows set later (Eastern). With the bootstrap record anchoring that era, every
    /// pre-reboot line resolves to its true instant with origin <c>era-anchored</c>; the
    /// post-reboot era without an anchor keeps the marked fallback; in-process sibling files
    /// join by local time; a fresh pass still self-anchors per line.
    /// </summary>
    public sealed class ImeLogTrackerEraAnchoringTests
    {
        private static readonly DateTime Deployed = new DateTime(2026, 9, 4, 13, 31, 49, 100, DateTimeKind.Utc);
        private static readonly TimeSpan Pacific = TimeSpan.FromHours(-7);
        private static readonly TimeSpan Eastern = TimeSpan.FromHours(-4);
        private const string PolicyId = "93d9df23-1111-2222-3333-444455556666";

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

        private static string Entry(string message, DateTime utcInstant, TimeSpan writerOffset)
        {
            var local = utcInstant + writerOffset;
            return $"<![LOG[{message}]LOG]!><time=\"{local:HH:mm:ss.fffffff}\" date=\"{local:M-d-yyyy}\" " +
                   "component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";
        }

        private sealed class Harness : IDisposable
        {
            private readonly TempDirectory _tmp = new TempDirectory();
            public ImeLogTracker Tracker { get; }
            public DateTime Now { get; set; } = Deployed.AddMinutes(30);
            public List<(string Message, DateTime? Ts, CmTraceOffsetOrigin Origin, int? Offset, string Anchor)> Resolved { get; } =
                new List<(string, DateTime?, CmTraceOffsetOrigin, int?, string)>();

            public Harness()
            {
                Tracker = new ImeLogTracker(
                    logFolder: _tmp.Path,
                    patterns: Patterns(),
                    logger: new AgentLogger(_tmp.Path, AgentLogLevel.Info));
                Tracker.UtcNowProvider = () => Now;
                Tracker.DeployedUtcProvider = () => Deployed;
                Tracker.OnPatternMatched += _ => Resolved.Add((
                    Tracker.LastMatchedPatternId ?? string.Empty,
                    Tracker.LastMatchedLogTimestamp,
                    Tracker.LastMatchedSourceOffsetOrigin,
                    Tracker.LastMatchedSourceOffsetMinutes,
                    Tracker.LastMatchedEraAnchorKind));
            }

            public void Append(string fileName, params string[] lines)
                => File.AppendAllText(Path.Combine(_tmp.Path, fileName), string.Join(Environment.NewLine, lines) + Environment.NewLine);

            public Task Pass() => Tracker.CheckLogFilesAsync(CancellationToken.None);

            public void Dispose()
            {
                Tracker.Dispose();
                _tmp.Dispose();
            }
        }

        [Fact]
        public async Task Backlog_lines_of_the_bootstrap_era_resolve_through_the_anchor_and_the_next_era_stays_on_fallback()
        {
            using var h = new Harness();
            h.Append("AgentExecutor.log",
                Entry($@"Adding argument powershell with value C:\x\Policies\Scripts\u_{PolicyId}.ps1", Deployed.AddSeconds(-20), Pacific),
                Entry("write output done. output = ===== Autopilot Monitor Bootstrap Started =====\n===== Bootstrap Completed Successfully =====, error = ", Deployed.AddMilliseconds(200), Pacific),
                Entry("marker 10", Deployed.AddSeconds(1), Pacific));
            h.Append("IntuneManagementExtension.log",
                Entry("marker 1", Deployed.AddMinutes(-1), Pacific),
                Entry($"[PowerShell] User Id = 00000000-0000-0000-0000-000000000000, Policy id = {PolicyId}, policy result = Success", Deployed.AddMilliseconds(260), Pacific),
                Entry("marker 2", Deployed.AddMinutes(10), Pacific),
                Entry("EMS Agent Started", Deployed.AddMinutes(20), Eastern),
                Entry("marker 3", Deployed.AddMinutes(21), Eastern));
            h.Append("AppWorkload.log",
                Entry("marker 4", Deployed.AddMinutes(5), Pacific),
                Entry("marker 5", Deployed.AddMinutes(25), Eastern));

            h.Tracker.PreScanEras();
            await h.Pass();   // first sight: backlog, never fresh

            var byMarker = new Dictionary<string, (string Message, DateTime? Ts, CmTraceOffsetOrigin Origin, int? Offset, string Anchor)>();
            foreach (var r in h.Resolved) byMarker[r.Message] = r;
            Assert.Equal(6, h.Resolved.Count);

            // Marker text is the pattern id (T-ALL) — index by resolution order instead.
            var r1 = h.Resolved[0];  // AgentExecutor marker 10 (files sort: AgentExecutor < AppWorkload < IntuneManagementExtension)
            Assert.Equal(CmTraceOffsetOrigin.EraAnchored, r1.Origin);
            Assert.Equal(Deployed.AddSeconds(1), r1.Ts);
            Assert.Equal(-420, r1.Offset);
            Assert.Equal(ImeLogEraPreScan.AnchorKindBootstrapStdout, r1.Anchor);

            var r4 = h.Resolved[1];  // AppWorkload marker 4 — transfer by local time into the service era
            Assert.Equal(CmTraceOffsetOrigin.EraAnchored, r4.Origin);
            Assert.Equal(Deployed.AddMinutes(5), r4.Ts);
            Assert.EndsWith("/service-era-transfer", r4.Anchor);

            var r5 = h.Resolved[2];  // AppWorkload marker 5 — post-boot era, unanchored
            Assert.Equal(CmTraceOffsetOrigin.None, r5.Origin);
            Assert.Equal(string.Empty, r5.Anchor);

            var rm1 = h.Resolved[3]; // IME marker 1
            Assert.Equal(CmTraceOffsetOrigin.EraAnchored, rm1.Origin);
            Assert.Equal(Deployed.AddMinutes(-1), rm1.Ts);
            Assert.Equal(ImeLogEraPreScan.AnchorKindBootstrapPolicyResult, rm1.Anchor);

            var rm2 = h.Resolved[4]; // IME marker 2
            Assert.Equal(CmTraceOffsetOrigin.EraAnchored, rm2.Origin);
            Assert.Equal(Deployed.AddMinutes(10), rm2.Ts);

            var rm3 = h.Resolved[5]; // IME marker 3 — after "EMS Agent Started", no anchor
            Assert.Equal(CmTraceOffsetOrigin.None, rm3.Origin);
        }

        [Fact]
        public async Task Fresh_lines_still_self_anchor_after_the_pre_scan()
        {
            using var h = new Harness();
            h.Append("IntuneManagementExtension.log", Entry("marker 1", h.Now, Eastern));
            h.Tracker.PreScanEras();
            await h.Pass();

            h.Now = h.Now.AddSeconds(10);
            var writtenAt = h.Now.AddMilliseconds(-80);
            h.Append("IntuneManagementExtension.log", Entry("marker 2", writtenAt, Eastern));
            await h.Pass();

            var last = h.Resolved[h.Resolved.Count - 1];
            Assert.Equal(CmTraceOffsetOrigin.LineAnchored, last.Origin);
            Assert.Equal(writtenAt, last.Ts);
            Assert.Equal(string.Empty, last.Anchor);
        }

        [Fact]
        public async Task Without_marker_the_backlog_keeps_the_reader_zone_fallback()
        {
            using var h = new Harness();
            h.Tracker.DeployedUtcProvider = () => null;
            h.Append("IntuneManagementExtension.log", Entry("marker 1", Deployed.AddMinutes(-1), Pacific));
            h.Tracker.PreScanEras();
            await h.Pass();

            Assert.Single(h.Resolved);
            Assert.Equal(CmTraceOffsetOrigin.None, h.Resolved[0].Origin);
            Assert.Equal(0, h.Tracker.EraTable.AnchoredEraCount);
        }
    }
}
