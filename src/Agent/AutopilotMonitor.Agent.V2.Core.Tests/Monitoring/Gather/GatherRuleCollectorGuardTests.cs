using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// Allowlist enforcement for the two collectors that used to bypass it:
    /// <see cref="LogParserCollector"/> read any path the rule named — including
    /// C:\Users, whose hard block every other file collector honours — and
    /// <see cref="EventLogCollector"/> read any channel, including Security.
    /// A tenant admin can author both through the rules API, so the agent is the
    /// only place this can be enforced.
    /// </summary>
    public sealed class GatherRuleCollectorGuardTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly List<EnrollmentEvent> _events = new List<EnrollmentEvent>();

        // %TEMP% itself lives under C:\Users, which is hard-blocked — the positive
        // cases need a directory the guard can actually admit, so they run beside
        // the test assembly instead.
        private readonly string _outsideUsers = Path.Combine(
            AppContext.BaseDirectory, "guard-tests-" + Guid.NewGuid().ToString("N"));

        public GatherRuleCollectorGuardTests() => Directory.CreateDirectory(_outsideUsers);

        public void Dispose()
        {
            _tmp.Dispose();
            try { Directory.Delete(_outsideUsers, recursive: true); } catch { /* best effort */ }
        }

        private GatherRuleContext Context(bool unrestricted = false, string imeLogPathOverride = null)
            => new GatherRuleContext(
                new AgentLogger(_tmp.Path, AgentLogLevel.Info),
                "sess", "tenant",
                evt => _events.Add(evt),
                imeLogPathOverride,
                new LogFilePositionTracker())
            {
                UnrestrictedMode = unrestricted
            };

        private static GatherRule Rule(string collectorType, string target,
            Dictionary<string, string> parameters = null) => new GatherRule
            {
                RuleId = "GATHER-GUARD-TEST",
                Title = "guard test",
                CollectorType = collectorType,
                Target = target,
                Parameters = parameters,
                Trigger = "startup",
                OutputEventType = "guard_test",
            };

        private EnrollmentEvent SingleSecurityWarning()
            => Assert.Single(_events, e => e.EventType == Constants.EventTypes.SecurityWarning);

        // -------------------------------------------------------------------
        // Event log channel allowlist
        // -------------------------------------------------------------------

        [Theory]
        [InlineData("System")]
        [InlineData("Application")]
        [InlineData("Setup")]
        [InlineData("Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider/Admin")]
        [InlineData("Microsoft-Windows-AAD/Operational")]
        [InlineData("Microsoft-Windows-Shell-Core/Operational")]
        public void EventLogChannel_OnAllowlist_IsAllowed(string channel)
            => Assert.True(GatherRuleGuards.IsEventLogChannelAllowed(channel));

        [Theory]
        [InlineData("Security")]
        [InlineData("Microsoft-Windows-PowerShell/Operational")]
        [InlineData("Windows PowerShell")]
        [InlineData("Microsoft-Windows-Sysmon/Operational")]
        public void EventLogChannel_HardBlocked_StaysBlockedEvenUnrestricted(string channel)
        {
            Assert.False(GatherRuleGuards.IsEventLogChannelAllowed(channel));
            Assert.False(GatherRuleGuards.IsEventLogChannelAllowed(channel, unrestrictedMode: true));
        }

        [Theory]
        [InlineData("Microsoft-Windows-AADSomethingElse/Operational")] // prefix spoofing
        [InlineData("Microsoft-Windows-TerminalServices-LocalSessionManager/Operational")]
        [InlineData("")]
        public void EventLogChannel_OffAllowlist_IsBlocked(string channel)
            => Assert.False(GatherRuleGuards.IsEventLogChannelAllowed(channel));

        [Fact]
        public void EventLogChannel_Null_IsBlocked()
            => Assert.False(GatherRuleGuards.IsEventLogChannelAllowed(null));

        [Fact]
        public void EventLogChannel_OffAllowlist_IsAllowedInUnrestrictedMode()
            => Assert.True(GatherRuleGuards.IsEventLogChannelAllowed(
                "Microsoft-Windows-TerminalServices-LocalSessionManager/Operational",
                unrestrictedMode: true));

        [Fact]
        public void EventLogCollector_SecurityChannel_EmitsSecurityWarningAndReturnsNoData()
        {
            var result = new EventLogCollector().Execute(Rule("eventlog", "Security"), Context());

            Assert.Empty(result);
            var warning = SingleSecurityWarning();
            Assert.Equal(true, warning.Data["blocked"]);
            Assert.Equal("Security", warning.Data["target"]);
        }

        [Fact]
        public void EventLogCollector_SecurityChannel_StaysBlockedInUnrestrictedMode()
        {
            var result = new EventLogCollector().Execute(
                Rule("eventlog", "Security"), Context(unrestricted: true));

            Assert.Empty(result);
            SingleSecurityWarning();
        }

        // -------------------------------------------------------------------
        // Log parser file allowlist
        // -------------------------------------------------------------------

        private static Dictionary<string, string> TextPattern() => new Dictionary<string, string>
        {
            ["pattern"] = "(?<line>.+)",
            ["format"] = "text",
            ["trackPosition"] = "false",
        };

        [Fact]
        public void LogParser_UserProfilePath_IsBlockedAndEmitsSecurityWarning()
        {
            const string target = @"C:\Users\Public\Documents\secret.txt";

            var result = new LogParserCollector().Execute(
                Rule("logparser", target, TextPattern()), Context());

            Assert.Null(result);
            var warning = SingleSecurityWarning();
            Assert.Equal(target, warning.Data["target"]);
            Assert.DoesNotContain(_events, e => e.EventType == "guard_test");
        }

        [Fact]
        public void LogParser_UserProfilePath_StaysBlockedInUnrestrictedMode()
        {
            // C:\Users is a hard block — unrestricted mode must not lift it.
            new LogParserCollector().Execute(
                Rule("logparser", @"C:\Users\Public\Documents\secret.txt", TextPattern()),
                Context(unrestricted: true));

            SingleSecurityWarning();
        }

        [Fact]
        public void LogParser_OffAllowlistPath_IsBlockedEvenWhenFileExists()
        {
            var path = _tmp.File("evidence.log");
            File.WriteAllText(path, "line one\nline two\n");

            var result = new LogParserCollector().Execute(
                Rule("logparser", path, TextPattern()), Context());

            Assert.Null(result);
            SingleSecurityWarning();
            Assert.DoesNotContain(_events, e => e.EventType == "guard_test");
        }

        [Fact]
        public void LogParser_WildcardTarget_IsBlockedByItsDirectory()
        {
            // Path.GetFullPath rejects '*', so the guard has to check the directory.
            File.WriteAllText(_tmp.File("evidence.log"), "line one\n");

            new LogParserCollector().Execute(
                Rule("logparser", Path.Combine(_tmp.Path, "*.log"), TextPattern()), Context());

            SingleSecurityWarning();
            Assert.DoesNotContain(_events, e => e.EventType == "guard_test");
        }

        [Fact]
        public void LogParser_UnrestrictedMode_ReadsPathOffTheAllowlist()
        {
            var path = Path.Combine(_outsideUsers, "evidence.log");
            File.WriteAllText(path, "line one\nline two\n");

            new LogParserCollector().Execute(
                Rule("logparser", path, TextPattern()), Context(unrestricted: true));

            Assert.DoesNotContain(_events, e => e.EventType == Constants.EventTypes.SecurityWarning);
            Assert.Equal(2, _events.Count(e => e.EventType == "guard_test"));
        }

        [Fact]
        public void LogParser_LocalImeLogPathOverride_RelaxesTheAllowlist()
        {
            // --ime-log-path is a local CLI flag, never remote config: the operator
            // using it is already a local admin, so it relaxes the allowlist the way
            // unrestricted mode does.
            File.WriteAllText(Path.Combine(_outsideUsers, "AppWorkload.log"), "line one\n");

            new LogParserCollector().Execute(
                Rule("logparser", @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log",
                    TextPattern()),
                Context(imeLogPathOverride: _outsideUsers));

            Assert.DoesNotContain(_events, e => e.EventType == Constants.EventTypes.SecurityWarning);
            Assert.Single(_events, e => e.EventType == "guard_test");
        }

        // -------------------------------------------------------------------
        // Command execution
        // -------------------------------------------------------------------

        [Fact]
        public void Command_OffAllowlist_IsBlockedAndNeverRuns()
        {
            var result = new CommandCollector().Execute(
                Rule("command_allowlisted", "Get-Content C:\\Windows\\System32\\config\\SAM"), Context());

            Assert.Equal(true, result["blocked"]);
            Assert.False(result.ContainsKey("output"));
            SingleSecurityWarning();
        }

        [Fact]
        public void Command_HardBlockedPattern_StaysBlockedInUnrestrictedMode()
        {
            var result = new CommandCollector().Execute(
                Rule("command_allowlisted", "Invoke-WebRequest https://example.invalid/x"),
                Context(unrestricted: true));

            Assert.Equal(true, result["blocked"]);
            SingleSecurityWarning();
        }

        [Fact]
        public void Command_ThatHangs_IsKilledAtTheTimeoutRatherThanPinningTheWorker()
        {
            // Regression: reading stdout synchronously blocked until the pipe closed,
            // so WaitForExit's timeout was never reached and a hung command pinned the
            // worker indefinitely. Unrestricted mode is needed to run an ad-hoc command.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new CommandCollector().Execute(
                Rule("command_allowlisted", "Start-Sleep -Seconds 120"), Context(unrestricted: true));
            sw.Stop();

            Assert.Equal(true, result["timed_out"]);
            Assert.Equal(-1, result["exit_code"]);
            // The 30s timeout plus drain margin — nowhere near the command's 120s.
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60),
                $"expected the hung command to be killed at the timeout; took {sw.Elapsed}");
        }

        [Fact]
        public void LogParser_ImeLogPathOverride_StillHonoursTheUsersHardBlock()
        {
            new LogParserCollector().Execute(
                Rule("logparser", @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\AppWorkload.log",
                    TextPattern()),
                Context(imeLogPathOverride: @"C:\Users\Public\Documents"));

            SingleSecurityWarning();
        }
    }
}
