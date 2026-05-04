using System.Xml;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Pins the hardened Task Scheduler XML produced by the V2 install mode. The CLI
    /// equivalent (<c>schtasks /Create /SC ONSTART /RU SYSTEM /RL HIGHEST</c>) inherits
    /// XML schema defaults that queue the BootTrigger run indefinitely on a laptop
    /// booting on battery in OOBE / WinPE (Event 325 'Launch request queued',
    /// observed 2026-05-04). Disabling these settings silently in a future refactor
    /// would re-introduce that failure mode — these tests fail loudly if that happens.
    /// </summary>
    public sealed class InstallModeTaskXmlTests
    {
        private const string SampleExePath = @"C:\ProgramData\AutopilotMonitor\Agent\AutopilotMonitor.Agent.V2.exe";

        [Fact]
        public void Xml_is_well_formed_and_uses_task_scheduler_1_2_namespace()
        {
            var xml = AutopilotMonitor.Agent.V2.Program.BuildScheduledTaskXml(SampleExePath);

            var doc = new XmlDocument();
            doc.LoadXml(xml);

            Assert.NotNull(doc.DocumentElement);
            Assert.Equal("Task", doc.DocumentElement!.LocalName);
            Assert.Equal("http://schemas.microsoft.com/windows/2004/02/mit/task", doc.DocumentElement.NamespaceURI);
            Assert.Equal("1.2", doc.DocumentElement.GetAttribute("version"));
        }

        [Fact]
        public void Xml_disables_battery_conditions_so_oobe_boot_trigger_does_not_queue()
        {
            var xml = AutopilotMonitor.Agent.V2.Program.BuildScheduledTaskXml(SampleExePath);

            // The two settings whose schema-defaults caused the WinPE 'Launch request queued' regression.
            Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
            Assert.Contains("<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>", xml);
        }

        [Fact]
        public void Xml_runs_as_system_with_highest_privileges_and_boot_trigger()
        {
            var xml = AutopilotMonitor.Agent.V2.Program.BuildScheduledTaskXml(SampleExePath);

            // S-1-5-18 == LocalSystem; HighestAvailable matches /RL HIGHEST in the CLI form.
            Assert.Contains("<UserId>S-1-5-18</UserId>", xml);
            Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
            Assert.Contains("<BootTrigger>", xml);
        }

        [Fact]
        public void Xml_disables_execution_time_limit_so_long_running_agent_is_not_killed_at_72h()
        {
            var xml = AutopilotMonitor.Agent.V2.Program.BuildScheduledTaskXml(SampleExePath);

            // schtasks CLI default is PT72H; agent is meant to live for the entire enrollment session.
            Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
        }

        [Fact]
        public void Xml_keeps_start_when_available_and_allow_start_on_demand()
        {
            var xml = AutopilotMonitor.Agent.V2.Program.BuildScheduledTaskXml(SampleExePath);

            // StartWhenAvailable: covers a missed BootTrigger after a deferred boot.
            // AllowStartOnDemand: keeps manual `schtasks /Run` available for support / dev.
            Assert.Contains("<StartWhenAvailable>true</StartWhenAvailable>", xml);
            Assert.Contains("<AllowStartOnDemand>true</AllowStartOnDemand>", xml);
        }

        [Fact]
        public void Xml_embeds_exec_command_with_xml_special_chars_escaped()
        {
            // ProgramData is admin-set; defensive against `&` or `<` in custom paths.
            const string trickyExe = @"C:\Program Files & Co\Auto<pilot>Monitor\Agent.V2.exe";

            var xml = AutopilotMonitor.Agent.V2.Program.BuildScheduledTaskXml(trickyExe);

            Assert.DoesNotContain(trickyExe, xml);
            Assert.Contains("Program Files &amp; Co", xml);
            Assert.Contains("Auto&lt;pilot&gt;Monitor", xml);

            // Still parses as well-formed XML after escape.
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var commandNode = doc.GetElementsByTagName("Command")[0];
            Assert.NotNull(commandNode);
            Assert.Equal(trickyExe, commandNode!.InnerText);
        }

        [Fact]
        public void Empty_exe_path_is_rejected()
        {
            Assert.Throws<System.ArgumentException>(
                () => AutopilotMonitor.Agent.V2.Program.BuildScheduledTaskXml(string.Empty));
            Assert.Throws<System.ArgumentException>(
                () => AutopilotMonitor.Agent.V2.Program.BuildScheduledTaskXml(null!));
        }
    }
}
