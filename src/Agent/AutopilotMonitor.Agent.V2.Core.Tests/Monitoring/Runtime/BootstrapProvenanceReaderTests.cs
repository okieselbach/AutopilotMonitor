using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Runtime
{
    /// <summary>
    /// The bootstrap provenance in <c>agent_started</c>. Fixture lines are the scripts' real
    /// formats: installer <c>[ts] Bootstrap script version: vX.Y</c>
    /// (Install-AutopilotMonitor.ps1), loader <c>[ts] === Autopilot Monitor bootstrap loader vX.Y
    /// started (user=..., pid=...) ===</c> (Start-AutopilotMonitor.ps1).
    /// </summary>
    public sealed class BootstrapProvenanceReaderTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "apm-provenance-" + Guid.NewGuid().ToString("N"));

        public BootstrapProvenanceReaderTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        }

        private void Write(string name, params string[] lines) =>
            File.WriteAllText(Path.Combine(_dir, name), string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);

        private static string InstallerRun(string version) => $"[2026-10-13 09:00:00] Bootstrap script version: v{version}";

        private static string LoaderRun(string version) =>
            $"[2026-10-13 08:59:58] === Autopilot Monitor bootstrap loader v{version} started (user=SECRETUSER, pid=4242) ===";

        [Fact]
        public void Loader_path_reports_installer_and_loader_versions()
        {
            Write(BootstrapProvenanceReader.LoaderLogName, LoaderRun("1.2"), "[2026-10-13 08:59:59] Installer signature verified");
            Write(BootstrapProvenanceReader.InstallerLogName, "[2026-10-13 09:00:00] ===== Autopilot Monitor Bootstrap Started =====", InstallerRun("2.5"));

            var p = BootstrapProvenanceReader.Read(_dir);

            Assert.True(p.ScriptLogPresent);
            Assert.Equal("2.5", p.ScriptVersion);
            Assert.Equal("loader", p.LoaderLog);
            Assert.Equal("1.2", p.LoaderVersion);
            Assert.False(p.OobeLogPresent);
        }

        [Fact]
        public void The_last_run_wins_when_the_log_grew_over_several_runs()
        {
            Write(BootstrapProvenanceReader.InstallerLogName, InstallerRun("2.3"), "x", InstallerRun("2.4"), "y", InstallerRun("2.5"), "z");

            Assert.Equal("2.5", BootstrapProvenanceReader.Read(_dir).ScriptVersion);
        }

        [Fact]
        public void A_directly_assigned_installer_has_no_loader_log()
        {
            Write(BootstrapProvenanceReader.InstallerLogName, InstallerRun("2.4"));

            var p = BootstrapProvenanceReader.Read(_dir);

            Assert.Equal("2.4", p.ScriptVersion);
            Assert.Null(p.LoaderLog);
            Assert.Null(p.LoaderVersion);
        }

        [Fact]
        public void An_installer_from_before_the_version_line_is_present_but_without_version()
        {
            Write(BootstrapProvenanceReader.InstallerLogName, "[2026-03-01 09:00:00] ===== Autopilot Monitor Bootstrap Started =====");

            var p = BootstrapProvenanceReader.Read(_dir);

            Assert.True(p.ScriptLogPresent);
            Assert.Null(p.ScriptVersion);
        }

        [Fact]
        public void The_msi_loader_log_is_told_apart_from_the_intune_loader()
        {
            Write(BootstrapProvenanceReader.MsiLoaderLogName, LoaderRun("1.1"));

            var p = BootstrapProvenanceReader.Read(_dir);

            Assert.Equal("msi", p.LoaderLog);
            Assert.Equal("1.1", p.LoaderVersion);
        }

        [Fact]
        public void With_both_loader_logs_the_most_recently_written_one_counts()
        {
            Write(BootstrapProvenanceReader.LoaderLogName, LoaderRun("1.0"));
            Write(BootstrapProvenanceReader.MsiLoaderLogName, LoaderRun("1.2"));
            File.SetLastWriteTimeUtc(Path.Combine(_dir, BootstrapProvenanceReader.LoaderLogName), DateTime.UtcNow.AddDays(-3));
            File.SetLastWriteTimeUtc(Path.Combine(_dir, BootstrapProvenanceReader.MsiLoaderLogName), DateTime.UtcNow.AddMinutes(-1));

            var p = BootstrapProvenanceReader.Read(_dir);

            Assert.Equal("msi", p.LoaderLog);
            Assert.Equal("1.2", p.LoaderVersion);
        }

        [Fact]
        public void The_oobe_go_script_is_recognised_by_its_log()
        {
            Write(BootstrapProvenanceReader.OobeLogName, "[2026-10-13 09:00:00] ===== Autopilot Monitor OOBE Bootstrap =====");

            Assert.True(BootstrapProvenanceReader.Read(_dir).OobeLogPresent);
        }

        [Fact]
        public void Only_the_tail_of_a_large_log_is_read()
        {
            var filler = Enumerable.Repeat(new string('.', 200), 600).ToArray();   // ~120 KB
            var lines = new List<string> { InstallerRun("2.1") };
            lines.AddRange(filler);
            lines.Add(InstallerRun("2.5"));
            Write(BootstrapProvenanceReader.InstallerLogName, lines.ToArray());

            Assert.Equal("2.5", BootstrapProvenanceReader.Read(_dir).ScriptVersion);
        }

        [Fact]
        public void A_missing_directory_reads_as_unknown_and_never_throws()
        {
            var p = BootstrapProvenanceReader.Read(Path.Combine(_dir, "does-not-exist"));

            Assert.False(p.ScriptLogPresent);
            Assert.Null(p.ScriptVersion);
            Assert.Null(p.LoaderLog);
            Assert.False(p.OobeLogPresent);
        }

        [Fact]
        public void The_event_keys_carry_versions_only_never_the_account_name()
        {
            Write(BootstrapProvenanceReader.LoaderLogName, LoaderRun("1.2"));
            Write(BootstrapProvenanceReader.InstallerLogName, InstallerRun("2.5"));
            var data = new Dictionary<string, object>();

            BootstrapProvenanceReader.Read(_dir).AddTo(data);

            Assert.Equal(true, data["bootstrapScriptLogPresent"]);
            Assert.Equal("2.5", data["bootstrapScriptVersion"]);
            Assert.Equal("loader", data["bootstrapLoaderLog"]);
            Assert.Equal("1.2", data["bootstrapLoaderVersion"]);
            Assert.Equal(false, data["oobeBootstrapLogPresent"]);
            Assert.DoesNotContain(data.Values, v => v is string s && s.Contains("SECRETUSER"));
        }

        [Fact]
        public void Keys_that_were_not_found_are_left_out()
        {
            var data = new Dictionary<string, object>();

            BootstrapProvenanceReader.Read(_dir).AddTo(data);

            Assert.Equal(new[] { "bootstrapScriptLogPresent", "oobeBootstrapLogPresent" }, data.Keys.OrderBy(k => k).ToArray());
        }
    }
}
