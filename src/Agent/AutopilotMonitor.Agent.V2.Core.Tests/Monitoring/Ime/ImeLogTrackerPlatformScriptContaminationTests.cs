using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Session 6b4993e5 regression: AgentExecutor.exe hosts BOTH platform scripts and
    /// proactive-remediation scripts and interleaves their lines in the shared AgentExecutor.log.
    /// The policyId-less <c>PS-AGENT-EXITCODE</c> / <c>PS-AGENT-OUTPUT</c> lines were routed to the
    /// last-started platform script via a sticky pointer; a remediation invocation (whose
    /// <c>Adding argument remediationScript …</c> start does NOT match <c>PS-AGENT-SCRIPT-START</c>)
    /// never moved the pointer, so its exit/output bled into the platform script. The field symptom:
    /// platform script <c>c3e0124c</c> emitted <c>result=Failed</c> but with <c>exit 0</c> and stdout
    /// <c>"[Compliant] No Classic Teams found"</c> — the Teams remediation's output.
    /// <para>
    /// Fix: <c>PS-AGENT-INVOCATION</c> ("ExecutorLog AgentExecutor gets invoked") resets the
    /// platform-script line-capture pointer at every invocation boundary, so a remediation
    /// invocation cannot capture into a platform slot, while a real platform invocation's following
    /// <c>Adding argument powershell …</c> re-establishes the pointer for its own exit/output.
    /// </para>
    /// </summary>
    public sealed class ImeLogTrackerPlatformScriptContaminationTests
    {
        // Codex P3 — load the ACTUAL shipped pattern JSON from rules/ime-log-patterns/ (the same
        // source combine.js embeds into the backend) so this regression guards the real contract:
        // if a pattern's regex drifts later (e.g. PS-AGENT-SCRIPT-START starts matching the
        // remediation line, or PS-AGENT-INVOCATION is removed/renamed) the test fails instead of
        // silently passing against a stale inline copy.
        private static readonly string[] RequiredPatternIds =
        {
            "PS-AGENT-INVOCATION", "PS-AGENT-ARG", "PS-AGENT-SCRIPT-START", "PS-AGENT-EXITCODE",
            "PS-AGENT-OUTPUT", "PS-AGENT-COMPLETED", "PS-SCRIPT-GENERATED", "PS-SCRIPT-CONTEXT",
            "PS-SCRIPT-RESULT", "HS-INVOCATION",
        };

        private static List<ImeLogPattern> ScriptPatterns()
        {
            var dir = FindRulesPatternDir();
            var byId = new Dictionary<string, ImeLogPattern>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                var pattern = JsonConvert.DeserializeObject<ImeLogPattern>(File.ReadAllText(file));
                if (pattern?.PatternId != null)
                    byId[pattern.PatternId] = pattern;
            }

            var result = new List<ImeLogPattern>();
            foreach (var id in RequiredPatternIds)
            {
                Assert.True(byId.TryGetValue(id, out var p),
                    $"Shipped IME pattern '{id}' not found under {dir} — the contamination fix relies on it.");
                result.Add(p!);
            }
            return result;
        }

        /// <summary>Walk up from the test assembly to the repo's rules/ime-log-patterns directory.</summary>
        private static string FindRulesPatternDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "rules", "ime-log-patterns");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException(
                $"Could not locate rules/ime-log-patterns walking up from {AppContext.BaseDirectory}");
        }

        private const string PlatformId = "c3e0124c-4936-4bfd-afcc-c7fe1d84d104";

        private const string PlatformStartLine =
            @"Adding argument powershell with value C:\Program Files (x86)\Microsoft Intune Management Extension\Policies\Scripts\00000000-0000-0000-0000-000000000000_c3e0124c-4936-4bfd-afcc-c7fe1d84d104.ps1 to the named argument list.";

        private const string RemediationStartLine =
            @"Adding argument remediationScript with value C:\Windows\IMECache\HealthScripts\446f0450-d0ee-404c-8dc0-a74123bde31f_1\detect.ps1 to the named argument list.";

        private const string ImeLog = "IntuneManagementExtension.log";
        private const string HealthLog = "HealthScripts.log";
        private const string ExecutorLog = "AgentExecutor.log";

        private const string PlatformGeneratedLine =
            @"Script file C:\Program Files (x86)\Microsoft Intune Management Extension\Policies\Scripts\00000000-0000-0000-0000-000000000000_c3e0124c-4936-4bfd-afcc-c7fe1d84d104.ps1 is generated";

        private static readonly string PlatformResultLine =
            $"[PowerShell] User Id = 00000000-0000-0000-0000-000000000000, Policy id = {PlatformId}, policy result = Success";

        // The health-script worker (IME AgentCommon ScriptWorker) writes its command line, the launch
        // line and the exit line to IntuneManagementExtension.log AND HealthScripts.log — session
        // 24dc69d1 (IME 1.105.152.0), where the exit line pre-filled a pending platform script's exit
        // code and the launch line its context.
        private const string HealthScriptCommandLine =
            @"""C:\Program Files (x86)\Microsoft Intune Management Extension\agentexecutor.exe""  -remediationScript  """"C:\WINDOWS\IMECache\HealthScripts\446f0450-d0ee-404c-8dc0-a74123bde31f_1\detect.ps1"""" ""C:\WINDOWS\IMECache\HealthScripts\446f0450-d0ee-404c-8dc0-a74123bde31f_1\detect.ps1.result"" 60";
        private const string HealthScriptExitLine = "Powershell execution is done, exitCode = 5";

        private static ImeLogTracker BuildTracker(TempDirectory tmp, out List<ScriptExecutionState> emitted)
        {
            var captured = new List<ScriptExecutionState>();
            emitted = captured;
            var tracker = new ImeLogTracker(tmp.Path, ScriptPatterns(), new AgentLogger(tmp.Path, AgentLogLevel.Info));
            tracker.OnScriptCompleted = s => captured.Add(s);
            return tracker;
        }

        [Fact]
        public void Remediation_exit_and_output_do_not_contaminate_interleaved_platform_script()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);

            // Platform invocation starts (banner + Adding argument powershell …c3e0124c…).
            tracker.ProcessLogMessageForTest("ExecutorLog AgentExecutor gets invoked");
            tracker.ProcessLogMessageForTest(PlatformStartLine);

            // A remediation AgentExecutor invocation interleaves before c3e0124c logs its own
            // exit/output. Its banner resets the pointer; its start line does NOT match
            // PS-AGENT-SCRIPT-START, so the pointer stays null and the remediation's exit/output
            // are dropped from the platform path (they reach the agent via HS-NEW-RESULT instead).
            tracker.ProcessLogMessageForTest("ExecutorLog AgentExecutor gets invoked");
            tracker.ProcessLogMessageForTest(RemediationStartLine);
            tracker.ProcessLogMessageForTest("Powershell exit code is 0");
            tracker.ProcessLogMessageForTest("write output done. output = [Compliant] No Classic Teams found, error = ");

            // Authoritative IME result for the PLATFORM script (keyed by policyId). Its own end
            // block never comes here, so the result is held until the shutdown flush — which must
            // not pick up the remediation's exit/output either.
            tracker.ProcessLogMessageForTest(
                $"[PowerShell] User Id = 00000000-0000-0000-0000-000000000000, Policy id = {PlatformId}, policy result = Failed");
            Assert.Empty(emitted);
            tracker.FlushPendingPlatformScriptResults(System.DateTime.UtcNow, force: true);

            var script = Assert.Single(emitted);
            Assert.Equal(PlatformId, script.PolicyId);
            Assert.Equal("Failed", script.Result);
            Assert.Equal("ime_policy_result", script.ResultSource);
            // The contaminants must NOT be attached.
            Assert.Null(script.Stdout);
            Assert.Null(script.ExitCode);
        }

        [Fact]
        public void Platform_script_without_interleave_still_captures_its_own_exit_and_output()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);

            // Clean, non-interleaved platform invocation: banner → start → own exit → own output →
            // authoritative result. The invocation-boundary reset must not strip a script's own data.
            tracker.ProcessLogMessageForTest("ExecutorLog AgentExecutor gets invoked");
            tracker.ProcessLogMessageForTest(PlatformStartLine);
            tracker.ProcessLogMessageForTest("Powershell exit code is 0");
            tracker.ProcessLogMessageForTest("write output done. output = Hello from c3e0124c, error = ");
            tracker.ProcessLogMessageForTest(
                $"[PowerShell] User Id = 00000000-0000-0000-0000-000000000000, Policy id = {PlatformId}, policy result = Success");

            var script = Assert.Single(emitted);
            Assert.Equal(PlatformId, script.PolicyId);
            Assert.Equal("Success", script.Result);
            Assert.Equal(0, script.ExitCode);
            Assert.Equal("Hello from c3e0124c", script.Stdout);
        }

        // -----------------------------------------------------------------------
        // Health-script lines never feed a platform slot (session 24dc69d1)
        // -----------------------------------------------------------------------

        [Fact]
        public void No_shipped_pattern_reads_the_health_script_workers_exit_line()
        {
            // "Powershell execution is done, exitCode = N" is the health-script worker's line only;
            // as PS-SCRIPT-EXITCODE (no scriptType) it pre-filled the exit code of whichever platform
            // script was pending, which then emitted without waiting for its own end block.
            foreach (var file in Directory.GetFiles(FindRulesPatternDir(), "*.json"))
            {
                var pattern = JsonConvert.DeserializeObject<ImeLogPattern>(File.ReadAllText(file))!;
                Assert.False(System.Text.RegularExpressions.Regex.IsMatch(HealthScriptExitLine, pattern.Pattern),
                    $"{pattern.PatternId} matches the health-script worker's exit line");
            }
        }

        [Fact]
        public void Health_script_exit_line_in_the_ime_log_never_pre_fills_the_platform_slot()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);

            tracker.ProcessLogMessageForTest(PlatformGeneratedLine, sourceFileName: ImeLog);
            // A health script that ran in parallel finishes: its exit line lands inside the platform
            // run's window (c81b8053 had "exit code 0" before its executor even started).
            tracker.ProcessLogMessageForTest(HealthScriptExitLine, sourceFileName: ImeLog);
            tracker.ProcessLogMessageForTest(PlatformResultLine, sourceFileName: ImeLog);

            // No exit code of its own → held for the end block, never emitted with the health script's 5.
            Assert.Empty(emitted);
            tracker.FlushPendingPlatformScriptResults(DateTime.UtcNow, force: true);
            var script = Assert.Single(emitted);
            Assert.Null(script.ExitCode);
            Assert.Equal("Success", script.Result);
        }

        [Fact]
        public void Health_script_launch_line_in_the_ime_log_does_not_flip_the_platform_context()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);

            tracker.ProcessLogMessageForTest(PlatformGeneratedLine, sourceFileName: ImeLog);
            tracker.ProcessLogMessageForTest("Launch powershell executor in user session", sourceFileName: ImeLog);
            // The health-script worker launches its executor while the platform script runs: its
            // command line opens an invocation that owns nothing, so its launch line stays there.
            tracker.ProcessLogMessageForTest(HealthScriptCommandLine, sourceFileName: ImeLog);
            tracker.ProcessLogMessageForTest("Launch powershell executor in machine session", sourceFileName: ImeLog);
            tracker.ProcessLogMessageForTest(PlatformResultLine, sourceFileName: ImeLog);
            tracker.FlushPendingPlatformScriptResults(DateTime.UtcNow, force: true);

            var script = Assert.Single(emitted);
            Assert.Equal("User", script.RunContext);
        }

        [Fact]
        public void Lines_from_the_health_scripts_log_never_resolve_to_a_platform_script()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);

            tracker.ProcessLogMessageForTest("ExecutorLog AgentExecutor gets invoked", sourceFileName: ExecutorLog);
            tracker.ProcessLogMessageForTest(PlatformStartLine, sourceFileName: ExecutorLog);
            // HealthScripts.log never carries a platform marker — the run in flight is not the
            // fallback owner of the lines there.
            tracker.ProcessLogMessageForTest("Launch powershell executor in machine session", sourceFileName: HealthLog);
            tracker.ProcessLogMessageForTest(PlatformResultLine, sourceFileName: ImeLog);
            tracker.FlushPendingPlatformScriptResults(DateTime.UtcNow, force: true);

            var script = Assert.Single(emitted);
            Assert.Null(script.RunContext);
        }

        [Fact]
        public void Context_line_after_an_ime_log_rollover_still_finds_the_run_in_flight()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp, out var emitted);

            tracker.ProcessLogMessageForTest(PlatformGeneratedLine, sourceFileName: ImeLog);
            tracker.ProcessLogMessageForTest("ExecutorLog AgentExecutor gets invoked", sourceFileName: ExecutorLog);
            tracker.ProcessLogMessageForTest(PlatformStartLine, sourceFileName: ExecutorLog);
            // IME rolls the log over between its two lines: the fresh file has no markers, but
            // platform runs open there — the run in flight still owns the context line.
            tracker.ClearInvocationMarkersForTest(ImeLog);
            tracker.ProcessLogMessageForTest("Launch powershell executor in user session", sourceFileName: ImeLog);
            tracker.ProcessLogMessageForTest(PlatformResultLine, sourceFileName: ImeLog);
            tracker.FlushPendingPlatformScriptResults(DateTime.UtcNow, force: true);

            var script = Assert.Single(emitted);
            Assert.Equal("User", script.RunContext);
        }
    }
}
