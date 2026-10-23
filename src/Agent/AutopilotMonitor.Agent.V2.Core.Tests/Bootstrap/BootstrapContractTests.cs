using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
        private static readonly string Wxs = File.ReadAllText(Path.Combine(
            RepoPaths.Root(), "src", "Agent", "AutopilotMonitor.BootstrapMsi", "Package.wxs"));

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

            Assert.Contains("-LogFileName " + BootstrapProvenanceReader.MsiLoaderLogName, Wxs);
        }

        // The bootstrap MSI installs its loader copy where the installer removes it, starts
        // PowerShell in System32, and runs only bytes it has checked against the publisher pin
        // the loader applies to the installer. Windows Installer formats the command, so every
        // bracket and brace is escaped and the only property references are the two paths.
        [Fact]
        public void The_msi_puts_the_loader_copy_where_the_installer_removes_it_and_runs_only_what_it_checked()
        {
            XNamespace ns = "http://wixtoolset.org/schemas/v4/wxs";
            var package = XDocument.Parse(Wxs).Root.Element(ns + "Package");
            Assert.Equal("1fbe5515-d346-445c-b8b8-14ee79f97522", (string)package.Attribute("UpgradeCode"));

            var root = package.Elements(ns + "StandardDirectory").Single();
            Assert.Equal("CommonAppDataFolder", (string)root.Attribute("Id"));
            var chain = root.Descendants(ns + "Directory").Select(d => (string)d.Attribute("Name")).ToArray();
            Assert.Equal("INSTALLFOLDER", (string)root.Descendants(ns + "Directory").Last().Attribute("Id"));
            var transport = Assignment(Installer, "MsiTransportPath").Replace("$AgentBasePath", Assignment(Installer, "AgentBasePath"));
            Assert.Equal("$env:ProgramData\\" + string.Join("\\", chain), transport);

            var action = package.Elements(ns + "CustomAction").Single(a => (string)a.Attribute("Id") == "RunBootstrap");
            Assert.Equal("System64Folder", (string)action.Attribute("Directory"));
            Assert.Equal("deferred", (string)action.Attribute("Execute"));
            Assert.Equal("no", (string)action.Attribute("Impersonate"));
            Assert.Equal("ignore", (string)action.Attribute("Return"));

            var exeCommand = (string)action.Attribute("ExeCommand");
            var command = exeCommand.Substring(exeCommand.IndexOf(" -Command ", StringComparison.Ordinal));
            Assert.Contains("-NonInteractive", exeCommand);
            Assert.Contains("[\\[]System.IO.File[\\]]::ReadAllBytes('[INSTALLFOLDER]Start-AutopilotMonitor.ps1')", command);
            Assert.Contains("Get-AuthenticodeSignature -Content $b -SourcePathOrExtension '.ps1'", command);
            Assert.Contains("$h -eq '$(var.LoaderSha256)' -and $s.Status -eq 'Valid' -and $s.SignerCertificate.Subject -like '" + Assignment(Loader, "ExpectedPublisher") + "'", command);
            // The hash is baked in by the build from the loader it embeds; a package built without it must not exist.
            Assert.Contains("<?ifndef var.LoaderSha256 ?>", Wxs);
            var workflow = File.ReadAllText(Path.Combine(RepoPaths.Root(), ".github", "workflows", "build-msi.yml"));
            Assert.Contains("wix build Package.wxs -arch x64 -d LoaderSha256=$loaderSha256", workflow);
            Assert.Contains("Get-FileHash -LiteralPath $env:RUNNER_SCRIPT -Algorithm SHA256", workflow);
            Assert.Contains("[\\[]scriptblock[\\]]::Create([\\[]System.Text.Encoding[\\]]::UTF8.GetString($b))", command);
            Assert.DoesNotContain("-File", command);
            // Without the [\x] escapes, the only bracket left is the one property reference.
            var unescaped = Regex.Replace(command, @"\[\\.\]", "");
            Assert.DoesNotMatch(@"[{}\[\]]", unescaped.Replace("[INSTALLFOLDER]", ""));
            Assert.Equal(new[] { "[INSTALLFOLDER]" }, Regex.Matches(command, @"\[[A-Za-z0-9_.]+\]").Cast<Match>().Select(m => m.Value).Distinct().ToArray());

            var custom = package.Element(ns + "InstallExecuteSequence").Elements(ns + "Custom").Single();
            Assert.Equal("RunBootstrap", (string)custom.Attribute("Action"));
            Assert.Equal("NOT Installed", (string)custom.Attribute("Condition"));
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
