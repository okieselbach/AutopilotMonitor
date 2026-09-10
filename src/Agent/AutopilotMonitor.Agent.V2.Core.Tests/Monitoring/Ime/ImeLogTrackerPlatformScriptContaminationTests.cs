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
            "PS-AGENT-INVOCATION", "PS-AGENT-SCRIPT-START", "PS-AGENT-EXITCODE",
            "PS-AGENT-OUTPUT", "PS-SCRIPT-RESULT",
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
    }
}
