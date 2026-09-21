#nullable enable
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Transport;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    /// <summary>
    /// The hard-blocked event-log channels (Security, PowerShell, Sysmon) are unreadable through
    /// the diagnostics package as well — by path, by wildcard, by resolved channel, and in
    /// unrestricted mode. Before this the block held only in the event-log collector.
    /// </summary>
    public sealed class DiagnosticsEventLogBlockTests
    {
        private const string WinevtLogs = @"C:\Windows\System32\winevt\Logs";

        // A channel no machine has: the export fails and the packager falls back to the raw copy.
        private const string HarmlessChannel = "AutopilotMonitor-Tests-NoSuchChannel/Operational";

        [Theory]
        [InlineData("Security.evtx")]
        [InlineData("security.EVTX")]
        [InlineData("Microsoft-Windows-PowerShell%4Operational.evtx")]
        [InlineData("Microsoft-Windows-PowerShell%4Admin.evtx")]
        [InlineData("Windows PowerShell.evtx")]
        [InlineData("Microsoft-Windows-Sysmon%4Operational.evtx")]
        [InlineData("Archive-Security-2026-09-21-10-15-30-123.evtx")]
        [InlineData("Archive-Microsoft-Windows-PowerShell%4Operational-2026-09-21-10-15-30-123.evtx")]
        public void Hard_blocked_channel_files_are_recognised_by_name(string fileName)
        {
            Assert.True(DiagnosticsPathGuards.IsHardBlockedEventLogFile(Path.Combine(WinevtLogs, fileName)));
        }

        [Theory]
        [InlineData("System.evtx")]
        [InlineData("Application.evtx")]
        [InlineData("SecurityAudit.evtx")]
        [InlineData("Security.log")]
        [InlineData("Microsoft-Windows-Security-Mitigations%4KernelMode.evtx")]
        // Same boundary as the collector: only '/' continues a blocked channel name.
        [InlineData("Microsoft-Windows-PowerShell-DesiredStateConfiguration-FileDownloadManager%4Operational.evtx")]
        [InlineData("Archive-System-2026-09-21-10-15-30-123.evtx")]
        public void Other_files_are_not_mistaken_for_a_blocked_channel(string fileName)
        {
            Assert.False(DiagnosticsPathGuards.IsHardBlockedEventLogFile(Path.Combine(WinevtLogs, fileName)));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Path_guard_refuses_a_blocked_channel_file_in_every_mode(bool unrestrictedMode)
        {
            Assert.False(DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                WinevtLogs + @"\Security.evtx", unrestrictedMode));
            Assert.False(DiagnosticsPathGuards.IsDiagnosticsPathAllowed(
                WinevtLogs + @"\Microsoft-Windows-PowerShell%4Operational.evtx", unrestrictedMode));
        }

        [Fact]
        public void Path_guard_still_admits_other_channels_and_wildcards()
        {
            Assert.True(DiagnosticsPathGuards.IsDiagnosticsPathAllowed(WinevtLogs + @"\System.evtx"));
            // Undecidable on the string — the packager filters per file (tests below).
            Assert.True(DiagnosticsPathGuards.IsDiagnosticsPathAllowed(WinevtLogs + @"\*.evtx"));
        }

        [Theory]
        [InlineData("Security", true)]
        [InlineData("Microsoft-Windows-PowerShell/Operational", true)]
        [InlineData(" Windows PowerShell ", true)]
        [InlineData("Microsoft-Windows-Sysmon/Operational", true)]
        [InlineData("System", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Channel_block_is_one_matcher_for_collector_and_package(string? channel, bool blocked)
        {
            Assert.Equal(blocked, GatherRuleGuards.IsHardBlockedEventLogChannel(channel!));
        }

        // ------------------------------------------------------------ packager ----

        // %TEMP% lives under C:\Users, which the path guard blocks in every mode, so the
        // configured folder sits beside the test assembly and the run is unrestricted — which
        // is also the strongest form of the claim: not even unrestricted mode collects these.
        private sealed class EvtxFolder : System.IDisposable
        {
            public string Path { get; } = System.IO.Path.Combine(
                System.AppContext.BaseDirectory, "diag-evtx-" + System.Guid.NewGuid().ToString("N"));

            public EvtxFolder(params string[] fileNames)
            {
                Directory.CreateDirectory(Path);
                foreach (var name in fileNames)
                    File.WriteAllBytes(System.IO.Path.Combine(Path, name), new byte[] { 1, 2, 3 });
            }

            public void Dispose()
            {
                try { Directory.Delete(Path, recursive: true); } catch { }
            }
        }

        private static DiagnosticsPackageService BuildService(
            TempDirectory scratch, string configuredPath, System.Func<string, string> channelResolver)
        {
            var logger = new AgentLogger(Directory.CreateDirectory(Path.Combine(scratch.Path, "logs")).FullName);
            var apiClient = new BackendApiClient(
                httpClient: new System.Net.Http.HttpClient(),
                baseUrl: "http://localhost",
                manufacturer: string.Empty,
                model: string.Empty,
                serialNumber: string.Empty,
                useBootstrapTokenAuth: false,
                bootstrapToken: null,
                agentVersion: "0.0.0",
                logger: logger);

            var cfg = new AgentConfiguration
            {
                SessionId = "S1",
                TenantId = "T1",
                ApiBaseUrl = "http://localhost",
                UnrestrictedMode = true,
            };
            cfg.DiagnosticsLogPaths.Add(new DiagnosticsLogPath { Path = configuredPath });

            string Sub(string name) => Directory.CreateDirectory(Path.Combine(scratch.Path, name)).FullName;
            return new DiagnosticsPackageService(
                cfg,
                logger,
                apiClient,
                agentLogFolderOverride: Sub("agentlogs"),
                imeLogFolderOverride: Sub("ime"),
                agentStateFolderOverride: Sub("state"),
                agentSpoolFolderOverride: Sub("spool"),
                agentDataFolderOverride: Sub("data"),
                sectionFolderOverrides: null,
                devicePreparationProbe: () => false)
            {
                EventLogChannelResolver = channelResolver,
            };
        }

        private static List<string> ZipEntryNames(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            return archive.Entries.Select(e => e.FullName).ToList();
        }

        private static string ReadManifest(byte[] bytes)
        {
            using var ms = new MemoryStream(bytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
            using var reader = new StreamReader(archive.GetEntry("package-manifest.txt")!.Open());
            return reader.ReadToEnd();
        }

        [Fact]
        public void Wildcard_entry_skips_blocked_channel_files_and_keeps_the_rest()
        {
            using var scratch = new TempDirectory();
            using var folder = new EvtxFolder(
                "Security.evtx", "Microsoft-Windows-PowerShell%4Operational.evtx", "Setup.evtx");

            var service = BuildService(scratch, Path.Combine(folder.Path, "*.evtx"), _ => HarmlessChannel);
            var bytes = service.BuildArchiveBytes(enrollmentSucceeded: false);

            var entries = ZipEntryNames(bytes);
            Assert.Contains(entries, e => e.EndsWith("/Setup.evtx"));
            Assert.DoesNotContain(entries, e => e.EndsWith("Security.evtx"));
            Assert.DoesNotContain(entries, e => e.Contains("PowerShell"));

            var manifest = ReadManifest(bytes);
            Assert.Contains("BLOCKED (event-log channel guard", manifest);
            Assert.Contains("Security.evtx", manifest);
            Assert.Equal(2, service.LastPackaging!.ProblemsByKind["path-guard"]);
        }

        [Fact]
        public void File_with_a_harmless_name_is_blocked_when_it_resolves_to_a_blocked_channel()
        {
            // A channel is free to write to any file name; and the resolver's naming fallback
            // maps a stray file onto a live channel. The export must never read that channel.
            using var scratch = new TempDirectory();
            using var folder = new EvtxFolder("Harmless.evtx");

            var service = BuildService(scratch, Path.Combine(folder.Path, "Harmless.evtx"), _ => "Security");
            var bytes = service.BuildArchiveBytes(enrollmentSucceeded: false);

            Assert.DoesNotContain(ZipEntryNames(bytes), e => e.EndsWith("Harmless.evtx"));
            Assert.Contains("BLOCKED (event-log channel guard, channel 'Security')", ReadManifest(bytes));
        }
    }
}
