#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Backlog era anchoring (2026-09-04, session a7140f98). The property under test: the
    /// bootstrap execution record — stdout in AgentExecutor.log, policy result in the service
    /// log — measured against the install marker anchors exactly the era it sits in; a SKIP
    /// re-run, a missing marker, an MSI bootstrap and an off-grid residual anchor nothing; the
    /// anchor never crosses an "EMS Agent Started" boundary; sibling files join by local time
    /// only while the service eras are unambiguous.
    /// </summary>
    public sealed class ImeLogEraPreScanTests
    {
        private static readonly DateTime Deployed = new DateTime(2026, 9, 4, 13, 31, 49, 100, DateTimeKind.Utc);
        private static readonly TimeSpan Pacific = TimeSpan.FromHours(-7);
        private static readonly TimeSpan Eastern = TimeSpan.FromHours(-4);
        private const string PolicyId = "93d9df23-1111-2222-3333-444455556666";

        private static string Entry(string message, DateTime utcInstant, TimeSpan writerOffset)
        {
            var local = utcInstant + writerOffset;
            return $"<![LOG[{message}]LOG]!><time=\"{local:HH:mm:ss.fffffff}\" date=\"{local:M-d-yyyy}\" " +
                   "component=\"X\" context=\"\" type=\"1\" thread=\"1\" file=\"\">";
        }

        private static string ScriptStart(DateTime utc, TimeSpan offset) =>
            Entry($@"Adding argument powershell with value C:\Program Files (x86)\Microsoft Intune Management Extension\Policies\Scripts\00000000-0000-0000-0000-000000000000_{PolicyId}.ps1", utc, offset);

        private static string BootstrapStdout(DateTime utc, TimeSpan offset, string tail = "===== Bootstrap Completed Successfully =====") =>
            Entry("write output done. output = ===== Autopilot Monitor Bootstrap Started =====\nBootstrap script version: v2.0\nAgent install mode completed successfully\n" + tail + ", error = ", utc, offset);

        private static string PolicyResult(DateTime utc, TimeSpan offset, string policyId = PolicyId) =>
            Entry($"[PowerShell] User Id = 00000000-0000-0000-0000-000000000000, Policy id = {policyId}, policy result = Success", utc, offset);

        private static string ServiceStarted(DateTime utc, TimeSpan offset) => Entry("EMS Agent Started", utc, offset);

        private sealed class Fixture : IDisposable
        {
            public TempDirectory Tmp { get; } = new TempDirectory();
            private readonly List<ImeLogEraPreScan.ScanInput> _inputs = new List<ImeLogEraPreScan.ScanInput>();

            public string Write(string fileName, params string[] entries)
            {
                var path = Path.Combine(Tmp.Path, fileName);
                File.WriteAllText(path, string.Join(Environment.NewLine, entries) + Environment.NewLine);
                _inputs.Add(new ImeLogEraPreScan.ScanInput(path, 0));
                return path;
            }

            public long OffsetOfLine(string fileName, int lineIndex)
            {
                // Entries are written line by line; the byte offset of entry N is the sum of the
                // preceding lines (CRLF-agnostic through Environment.NewLine).
                var path = Path.Combine(Tmp.Path, fileName);
                var lines = File.ReadAllLines(path);
                long offset = 0;
                for (var i = 0; i < lineIndex; i++)
                    offset += System.Text.Encoding.UTF8.GetByteCount(lines[i]) + Environment.NewLine.Length;
                return offset;
            }

            public CmTraceEraTable Build(DateTime? deployed) =>
                ImeLogEraPreScan.Build(_inputs, deployed, ImeLogTracker.MaxEntryBytes, null);

            public void Dispose() => Tmp.Dispose();
        }

        [Fact]
        public void Bootstrap_record_anchors_service_era_and_executor_block_only_within_their_boundaries()
        {
            using var f = new Fixture();
            f.Write("AgentExecutor.log",
                ScriptStart(Deployed.AddSeconds(-20), Pacific),
                BootstrapStdout(Deployed.AddMilliseconds(200), Pacific),
                Entry("Adding argument powershell with value C:\\x\\Policies\\Scripts\\u_aaaaaaaa-0000-0000-0000-000000000000.ps1", Deployed.AddMinutes(5), Pacific),
                Entry("write output done. output = other script, error = ", Deployed.AddMinutes(5).AddSeconds(2), Pacific));
            f.Write("IntuneManagementExtension.log",
                Entry("In EspPhase: DeviceSetup", Deployed.AddMinutes(-1), Pacific),                       // line 0, era 0
                PolicyResult(Deployed.AddMilliseconds(260), Pacific),                                   // line 1, anchor
                Entry("In EspPhase: DeviceSetup", Deployed.AddMinutes(10), Pacific),                      // line 2, era 0
                ServiceStarted(Deployed.AddMinutes(20), Eastern),                                       // line 3, era 1 (post-boot, new zone)
                Entry("Successfully get the token", Deployed.AddMinutes(21), Eastern));                   // line 4, era 1

            var table = f.Build(Deployed);

            Assert.Equal(2, table.AnchoredEraCount);
            Assert.False(table.TransferAmbiguous);

            // Service era 0: every offset before "EMS Agent Started" resolves to the Pacific offset.
            Assert.True(table.TryResolveByOffset("IntuneManagementExtension.log", f.OffsetOfLine("IntuneManagementExtension.log", 0), out var off0, out var kind0));
            Assert.Equal(Pacific, off0);
            Assert.Equal(ImeLogEraPreScan.AnchorKindBootstrapPolicyResult, kind0);
            Assert.True(table.TryResolveByOffset("IntuneManagementExtension.log", f.OffsetOfLine("IntuneManagementExtension.log", 2), out var off2, out _));
            Assert.Equal(Pacific, off2);

            // Era 1 has no anchor of its own — the Pacific anchor must not leak across the boundary.
            Assert.False(table.TryResolveByOffset("IntuneManagementExtension.log", f.OffsetOfLine("IntuneManagementExtension.log", 3), out _, out _));
            Assert.False(table.TryResolveByOffset("IntuneManagementExtension.log", f.OffsetOfLine("IntuneManagementExtension.log", 4), out _, out _));

            // Executor: only the bootstrap execution block is anchored, not the next script's block.
            Assert.True(table.TryResolveByOffset("AgentExecutor.log", f.OffsetOfLine("AgentExecutor.log", 0), out var offX, out var kindX));
            Assert.Equal(Pacific, offX);
            Assert.Equal(ImeLogEraPreScan.AnchorKindBootstrapStdout, kindX);
            Assert.False(table.TryResolveByOffset("AgentExecutor.log", f.OffsetOfLine("AgentExecutor.log", 5), out _, out _));

            // Transfer for in-process sibling files: a local time inside era 0's range resolves,
            // one inside era 1's range does not.
            Assert.True(table.TryResolveByLocalTime(Deployed.AddMinutes(5) + Pacific, out var offT, out var kindT));
            Assert.Equal(Pacific, offT);
            Assert.EndsWith("/service-era-transfer", kindT);
            Assert.False(table.TryResolveByLocalTime(Deployed.AddMinutes(25) + Eastern, out _, out _));
        }

        [Fact]
        public void Without_deployed_marker_nothing_is_anchored()
        {
            using var f = new Fixture();
            f.Write("AgentExecutor.log", ScriptStart(Deployed.AddSeconds(-20), Pacific), BootstrapStdout(Deployed.AddMilliseconds(200), Pacific));
            f.Write("IntuneManagementExtension.log", PolicyResult(Deployed.AddMilliseconds(260), Pacific));

            var table = f.Build(null);

            Assert.Equal(0, table.AnchoredEraCount);
        }

        [Fact]
        public void Msi_bootstrap_without_stdout_record_anchors_nothing()
        {
            // The MSI runner never passes through AgentExecutor; without the policy id the
            // service log's result lines cannot be told apart from any other script's.
            using var f = new Fixture();
            f.Write("AgentExecutor.log", Entry("Adding argument powershell with value C:\\x\\Policies\\Scripts\\u_aaaaaaaa-0000-0000-0000-000000000000.ps1", Deployed.AddMinutes(3), Pacific));
            f.Write("IntuneManagementExtension.log", PolicyResult(Deployed.AddMilliseconds(260), Pacific));

            var table = f.Build(Deployed);

            Assert.Equal(0, table.AnchoredEraCount);
        }

        [Fact]
        public void Skip_rerun_record_is_never_offered_and_first_record_off_grid_anchors_nothing()
        {
            // The install run is the FIRST bootstrap record; a SKIP re-run 15 minutes later
            // would round onto the grid with zero residual and anchor a wrong offset.
            using var f = new Fixture();
            f.Write("AgentExecutor.log",
                ScriptStart(Deployed.AddMinutes(-9), Pacific),
                BootstrapStdout(Deployed.AddMinutes(-8), Pacific),                                          // first record: residual 8 min → rejected
                ScriptStart(Deployed.AddMinutes(15).AddSeconds(-2), Pacific),
                BootstrapStdout(Deployed.AddMinutes(15), Pacific));                                         // would be a perfect (wrong) grid hit
            f.Write("IntuneManagementExtension.log",
                PolicyResult(Deployed.AddMinutes(-8), Pacific),
                PolicyResult(Deployed.AddMinutes(15), Pacific));

            var table = f.Build(Deployed);

            Assert.Equal(0, table.AnchoredEraCount);
        }

        [Fact]
        public void Service_era_continues_across_a_log_rotation()
        {
            using var f = new Fixture();
            f.Write("AgentExecutor.log", ScriptStart(Deployed.AddSeconds(-20), Pacific), BootstrapStdout(Deployed.AddMilliseconds(200), Pacific));
            f.Write("IntuneManagementExtension-20260904-133000.log",
                PolicyResult(Deployed.AddMilliseconds(260), Pacific),
                Entry("In EspPhase: DeviceSetup", Deployed.AddMinutes(2), Pacific));
            f.Write("IntuneManagementExtension.log",
                Entry("In EspPhase: DeviceSetup", Deployed.AddMinutes(12), Pacific),   // same process, rotated file
                ServiceStarted(Deployed.AddMinutes(20), Eastern));

            var table = f.Build(Deployed);

            Assert.True(table.TryResolveByOffset("IntuneManagementExtension.log", 0, out var off, out _));
            Assert.Equal(Pacific, off);
            Assert.False(table.TryResolveByOffset("IntuneManagementExtension.log", f.OffsetOfLine("IntuneManagementExtension.log", 1), out _, out _));
        }

        [Fact]
        public void Overlapping_service_era_local_ranges_disable_the_transfer()
        {
            // A westward zone change between two service starts makes era 1 start "before"
            // era 0 in local time; the sibling-file transfer must refuse rather than guess.
            using var f = new Fixture();
            f.Write("AgentExecutor.log", ScriptStart(Deployed.AddSeconds(-20), TimeSpan.FromHours(2)), BootstrapStdout(Deployed.AddMilliseconds(200), TimeSpan.FromHours(2)));
            f.Write("IntuneManagementExtension.log",
                ServiceStarted(Deployed.AddMinutes(-30), TimeSpan.FromHours(2)),
                PolicyResult(Deployed.AddMilliseconds(260), TimeSpan.FromHours(2)),
                ServiceStarted(Deployed.AddMinutes(20), TimeSpan.FromHours(-7)));

            var table = f.Build(Deployed);

            Assert.True(table.TransferAmbiguous);
            Assert.False(table.TryResolveByLocalTime(Deployed.AddMinutes(5) + TimeSpan.FromHours(2), out _, out _));
            // Offset-based lookup is unaffected.
            Assert.True(table.TryResolveByOffset("IntuneManagementExtension.log", f.OffsetOfLine("IntuneManagementExtension.log", 1), out var off, out _));
            Assert.Equal(TimeSpan.FromHours(2), off);
        }

        [Fact]
        public void Deployment_marker_parses_roundtrip_utc()
        {
            Assert.Equal(Deployed, AutopilotMonitor.Agent.V2.Core.Security.DeploymentMarker.Parse("2026-09-04T13:31:49.1000000Z"));
            Assert.Null(AutopilotMonitor.Agent.V2.Core.Security.DeploymentMarker.Parse(""));
            Assert.Null(AutopilotMonitor.Agent.V2.Core.Security.DeploymentMarker.Parse("not a date"));
        }
    }
}
