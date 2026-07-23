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

        private GatherRuleContext Context(bool unrestricted = false, string? imeLogPathOverride = null)
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
            Dictionary<string, string>? parameters = null) => new GatherRule
            {
                RuleId = "GATHER-GUARD-TEST",
                Title = "guard test",
                CollectorType = collectorType,
                Target = target,
                // Match the model default (empty, never null) so the collectors see the
                // same shape a deserialized rule without parameters gives them.
                Parameters = parameters ?? new Dictionary<string, string>(),
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

        // -------------------------------------------------------------------
        // Every remaining collector actually calls its guard
        //
        // The guard functions have their own unit tests, but those prove nothing about
        // whether a collector consults them before touching the registry, WMI or disk.
        // These drive the collectors themselves: each blocked target must come back empty,
        // emit exactly one security_warning naming the target, and produce no rule output.
        // -------------------------------------------------------------------

        private void AssertBlocked(Dictionary<string, object> result, string target)
        {
            Assert.Empty(result);
            var warning = SingleSecurityWarning();
            Assert.Equal(true, warning.Data["blocked"]);
            Assert.Equal(target, warning.Data["target"]);
            Assert.DoesNotContain(_events, e => e.EventType == "guard_test");
        }

        [Theory]
        [InlineData(@"SAM\SAM\Domains\Account\Users")]
        [InlineData(@"SECURITY\Policy\Secrets")]
        [InlineData(@"HKLM\SOFTWARE\Microsoft\EnrollmentsSomethingElse")]  // prefix spoofing
        [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows\AutopilotSomethingElse")]  // sibling spoof of the Autopilot prefix
        public void RegistryCollector_OffAllowlistPath_IsBlockedBeforeTheHiveIsOpened(string target)
            => AssertBlocked(new RegistryCollector().Execute(Rule("registry", target), Context()), target);

        // Session 5d735290: the EnrollmentStatusTracking subtree (ESP policy providers / tracking
        // state) is allow-listed so tenants can build gather rules on the co-management stall
        // signature the esp_policy_provider_stalled detector reports (e.g. not_exists on the
        // Sidecar provider key).
        [Theory]
        [InlineData(@"SOFTWARE\Microsoft\Windows\Autopilot")]
        [InlineData(@"SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking\Device\Setup\Apps\PolicyProviders\Sidecar")]
        public void RegistryGuard_AutopilotEnrollmentTrackingSubtree_IsAllowed(string subPath)
            => Assert.True(GatherRuleGuards.IsRegistryPathAllowed(subPath));

        [Theory]
        [InlineData("SELECT * FROM Win32_UserAccount")]
        [InlineData("SELECT * FROM Win32_ShadowCopy")]
        [InlineData("SELECT * FROM Win32_OperatingSystemExtra")]          // prefix spoofing
        public void WmiCollector_OffAllowlistQuery_IsBlockedBeforeTheQueryRuns(string query)
            => AssertBlocked(new WmiCollector().Execute(Rule("wmi", query), Context()), query);

        [Fact]
        public void JsonCollector_UserProfilePath_IsBlockedBeforeTheFileIsOpened()
        {
            const string target = @"C:\Users\Public\Documents\secrets.json";
            AssertBlocked(
                new JsonCollector().Execute(
                    Rule("json", target, new Dictionary<string, string> { ["jsonpath"] = "$.token" }),
                    Context()),
                target);
        }

        [Fact]
        public void XmlCollector_UserProfilePath_IsBlockedBeforeTheFileIsOpened()
        {
            const string target = @"C:\Users\Public\Documents\secrets.xml";
            AssertBlocked(
                new XmlCollector().Execute(
                    Rule("xml", target, new Dictionary<string, string> { ["xpath"] = "//token" }),
                    Context()),
                target);
        }

        [Fact]
        public void FileCollector_UserProfilePath_IsBlockedBeforeTheFileIsOpened()
        {
            const string target = @"C:\Users\Public\Documents\secrets.txt";
            AssertBlocked(new FileCollector().Execute(Rule("file", target), Context()), target);
        }

        [Fact]
        public void JsonCollector_AllowedPath_ReadsTheFile()
        {
            // Counterpart to the blocked cases: proves the guard is a gate, not a wall —
            // the collector does reach the file once the path is admitted.
            var path = Path.Combine(_outsideUsers, "inventory.json");
            File.WriteAllText(path, "{ \"agent\": { \"version\": \"2.4.0\" } }");

            var result = new JsonCollector().Execute(
                Rule("json", path, new Dictionary<string, string> { ["jsonpath"] = "$.agent.version" }),
                Context(unrestricted: true));

            Assert.DoesNotContain(_events, e => e.EventType == Constants.EventTypes.SecurityWarning);
            Assert.Contains("2.4.0", string.Join(",", result.Values));
        }

        // -------------------------------------------------------------------
        // Path traversal
        // -------------------------------------------------------------------

        [Fact]
        public void FilePath_TraversalOutOfAnAllowedPrefix_IsBlocked()
        {
            // The raw string starts with an allowed prefix, so a naive StartsWith check would
            // admit it. Path.GetFullPath runs FIRST, so the allowlist sees C:\Secret.txt.
            const string traversal =
                @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\..\..\..\..\Secret.txt";

            Assert.Equal(@"C:\Secret.txt", Path.GetFullPath(traversal));
            Assert.False(GatherRuleGuards.IsFilePathAllowed(traversal));
        }

        [Fact]
        public void FilePath_TraversalIntoTheUsersHardBlock_StaysBlockedEvenUnrestricted()
        {
            // Unrestricted mode lifts the allowlist but never the C:\Users privacy block —
            // and it must not be reachable by spelling the path with '..' either.
            const string traversal =
                @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\..\..\..\..\Users\Public\ntuser.dat";

            Assert.StartsWith(@"C:\Users\", Path.GetFullPath(traversal), StringComparison.OrdinalIgnoreCase);
            Assert.False(GatherRuleGuards.IsFilePathAllowed(traversal));
            Assert.False(GatherRuleGuards.IsFilePathAllowed(traversal, unrestrictedMode: true));
        }

        [Fact]
        public void FilePath_TraversalIntoTheConfigHiveDirectory_StaysBlockedEvenUnrestricted()
        {
            const string traversal = @"C:\Windows\Logs\..\System32\config\SAM";

            Assert.False(GatherRuleGuards.IsFilePathAllowed(traversal));
            Assert.False(GatherRuleGuards.IsFilePathAllowed(traversal, unrestrictedMode: true));
        }

        [Fact]
        public void FileCollector_TraversalTarget_IsBlockedAndReported()
        {
            const string traversal =
                @"C:\ProgramData\Microsoft\IntuneManagementExtension\Logs\..\..\..\..\Users\Public\ntuser.dat";

            AssertBlocked(new FileCollector().Execute(Rule("file", traversal), Context()), traversal);
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
