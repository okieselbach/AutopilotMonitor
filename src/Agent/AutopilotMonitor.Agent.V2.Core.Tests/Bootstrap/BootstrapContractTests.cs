using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Runtime;
using AutopilotMonitor.Agent.V2.Core.Security;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Bootstrap
{
    /// <summary>
    /// What the bootstrap scripts and the agent must agree on: the names by which the installer
    /// recognizes an installed agent, the folders and log files, and the log lines the agent reads
    /// back as its provenance. Every script value is read from the script's own text, so a rename
    /// on either side fails here. The bootstrap script gates run this class on every change to
    /// the scripts.
    /// </summary>
    [Trait("Category", "BootstrapContract")]
    public sealed class BootstrapContractTests : IDisposable
    {
        private static readonly string BootstrapDir = Path.Combine(RepoPaths.Root(), "scripts", "Bootstrap");
        private static readonly string Installer = File.ReadAllText(Path.Combine(BootstrapDir, "Install-AutopilotMonitor.ps1"));
        private static readonly string Loader = File.ReadAllText(Path.Combine(BootstrapDir, "Start-AutopilotMonitor.ps1"));
        private static readonly string OobeScript = File.ReadAllText(Path.Combine(
            RepoPaths.Root(), "src", "Backend", "AutopilotMonitor.Functions.Tests", "TestData", "oobe-bootstrap-golden.ps1"));

        private readonly string _logs = Path.Combine(Path.GetTempPath(), "apm-bootstrap-contract-" + Guid.NewGuid().ToString("N"));

        public BootstrapContractTests() => Directory.CreateDirectory(_logs);

        public void Dispose()
        {
            try { Directory.Delete(_logs, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>The value of the one top-level <c>$Name = "value"</c> line of a script.</summary>
        private static string Assignment(string script, string name)
        {
            var matches = Regex.Matches(script, @"(?m)^\$" + name + @"[ \t]*=[ \t]*""([^""]*)""[ \t]*\r?$");
            Assert.True(matches.Count == 1, $"expected exactly one top-level ${name} line, found {matches.Count}");
            return matches[0].Groups[1].Value;
        }

        private static string ScriptPath(string agentPath) => agentPath.Replace("%ProgramData%", "$env:ProgramData");

        private static string AgentSubdirectory() =>
            (string)typeof(AutopilotMonitor.Agent.V2.Program)
                .GetField("DefaultAgentSubdirectory", BindingFlags.NonPublic | BindingFlags.Static)
                .GetRawConstantValue();

        private void WriteLog(string name, string line) =>
            File.WriteAllText(Path.Combine(_logs, name), line + "\r\n", Encoding.ASCII);

        [Fact]
        public void The_installer_recognizes_an_agent_by_the_names_the_agent_uses()
        {
            Assert.Equal(DeploymentMarker.RegistryKeyPath, Assignment(Installer, "DeploymentMarkerKey"));
            Assert.Equal(DeploymentMarker.RegistryValueName, Assignment(Installer, "DeploymentMarkerValue"));
            Assert.Equal(Constants.ScheduledTaskName, Assignment(Installer, "AgentTaskName"));
            Assert.Equal(typeof(AutopilotMonitor.Agent.V2.Program).Assembly.GetName().Name + ".exe", Assignment(Installer, "AgentExeName"));
        }

        [Fact]
        public void The_installer_works_in_the_folders_the_agent_uses()
        {
            var basePath = Assignment(Installer, "AgentBasePath");

            Assert.Equal(ScriptPath(Constants.AgentDataDirectory), basePath);
            Assert.Equal(ScriptPath(Constants.LogDirectory), Assignment(Installer, "AgentLogPath").Replace("$AgentBasePath", basePath));
            Assert.Equal(basePath + "\\" + AgentSubdirectory(), Assignment(Installer, "AgentBinPath").Replace("$AgentBasePath", basePath));
        }

        [Fact]
        public void Every_bootstrap_logs_under_the_name_the_agent_reads()
        {
            Assert.Equal("$AgentLogPath\\" + BootstrapProvenanceReader.InstallerLogName, Assignment(Installer, "LogFile"));
            Assert.Matches(@"\[string\]\$LogFileName = """ + Regex.Escape(BootstrapProvenanceReader.LoaderLogName) + @"""", Loader);
            Assert.Contains("$LogFile          = \"$AgentLogPath\\" + BootstrapProvenanceReader.OobeLogName + "\"", OobeScript);

            var wxs = File.ReadAllText(Path.Combine(RepoPaths.Root(), "src", "Agent", "AutopilotMonitor.BootstrapMsi", "Package.wxs"));
            Assert.Contains("-LogFileName " + BootstrapProvenanceReader.MsiLoaderLogName, wxs);
        }

        [Fact]
        public void The_agent_reads_the_versions_the_current_scripts_write()
        {
            var installerVersion = Assignment(Installer, "ScriptVersion");
            var loaderVersion = Assignment(Loader, "ScriptVersion");
            Assert.Contains("Write-Log \"Bootstrap script version: v$ScriptVersion\"", Installer);
            Assert.Contains("Write-Log \"=== Autopilot Monitor bootstrap loader v$ScriptVersion started (user=$env:USERNAME, pid=$PID) ===\"", Loader);

            WriteLog(BootstrapProvenanceReader.InstallerLogName, $"[2026-10-22 10:00:01] Bootstrap script version: v{installerVersion}");
            WriteLog(BootstrapProvenanceReader.LoaderLogName, $"[2026-10-22 10:00:00] === Autopilot Monitor bootstrap loader v{loaderVersion} started (user=SYSTEM, pid=4242) ===");

            var provenance = BootstrapProvenanceReader.Read(_logs);

            Assert.Equal(installerVersion, provenance.ScriptVersion);
            Assert.Equal("loader", provenance.LoaderLog);
            Assert.Equal(loaderVersion, provenance.LoaderVersion);
        }

        // The installer carries only the start line over from loaders before 1.3, rebuilt from a
        // pattern; the agent has to read that line as the loader's version.
        [Fact]
        public void The_agent_reads_the_start_line_the_installer_carries_over_from_an_older_loader()
        {
            const string suffix = " (carried over by the bootstrapper) ===`r`n";
            Assert.Contains("$starts[$starts.Count - 1].Value + \"" + suffix + "\"", Installer);

            WriteLog(BootstrapProvenanceReader.MsiLoaderLogName, "[2026-10-22 10:00:00] === Autopilot Monitor bootstrap loader v1.2 started (carried over by the bootstrapper) ===");

            var provenance = BootstrapProvenanceReader.Read(_logs);

            Assert.Equal("msi", provenance.LoaderLog);
            Assert.Equal("1.2", provenance.LoaderVersion);
        }
    }
}
