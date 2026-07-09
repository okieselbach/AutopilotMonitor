using System;
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Platform scripts emit the same live <c>OnScriptStarted</c> signal health scripts always
    /// had, so the UI shows a running indicator for them too. The start line fires twice per
    /// execution (agentexecutor + ime source), so the signal is gated on pending-slot creation —
    /// exactly once per execution, re-armed after the execution completes.
    /// </summary>
    public sealed class ImeLogTrackerPlatformScriptStartedTests
    {
        private static readonly string[] RequiredPatternIds =
        {
            "PS-AGENT-INVOCATION", "PS-AGENT-SCRIPT-START", "PS-SCRIPT-RESULT",
        };

        // Same shipped-pattern loading approach as ImeLogTrackerPlatformScriptContaminationTests:
        // guard the real regex contract rather than a stale inline copy.
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
                    $"Shipped IME pattern '{id}' not found under {dir}.");
                result.Add(p!);
            }
            return result;
        }

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

        private const string PlatformResultLine =
            "[PowerShell] User Id = 00000000-0000-0000-0000-000000000000, " +
            "Policy id = " + PlatformId + ", policy result = Success";

        [Fact]
        public void Platform_start_emits_OnScriptStarted_once_per_execution()
        {
            using var tmp = new TempDirectory();
            var started = new List<ScriptStartedInfo>();
            var tracker = new ImeLogTracker(tmp.Path, ScriptPatterns(), new AgentLogger(tmp.Path, AgentLogLevel.Info));
            tracker.OnScriptStarted = info => started.Add(info);

            tracker.ProcessLogMessageForTest("ExecutorLog AgentExecutor gets invoked");
            tracker.ProcessLogMessageForTest(PlatformStartLine);
            // Duplicate start line for the same execution (the line fires from both the
            // agentexecutor and the ime source) — must NOT emit a second started signal.
            tracker.ProcessLogMessageForTest(PlatformStartLine);

            var info = Assert.Single(started);
            Assert.Equal(PlatformId, info.PolicyId);
            Assert.Equal("platform", info.ScriptType);
        }

        [Fact]
        public void Platform_rerun_after_completion_emits_OnScriptStarted_again()
        {
            using var tmp = new TempDirectory();
            var started = new List<ScriptStartedInfo>();
            var tracker = new ImeLogTracker(tmp.Path, ScriptPatterns(), new AgentLogger(tmp.Path, AgentLogLevel.Info));
            tracker.OnScriptStarted = info => started.Add(info);

            // Run 1: start → authoritative result (removes the pending slot).
            tracker.ProcessLogMessageForTest("ExecutorLog AgentExecutor gets invoked");
            tracker.ProcessLogMessageForTest(PlatformStartLine);
            tracker.ProcessLogMessageForTest(PlatformResultLine);

            // Run 2 (IME re-evaluation of the same policy) begins a new execution.
            tracker.ProcessLogMessageForTest("ExecutorLog AgentExecutor gets invoked");
            tracker.ProcessLogMessageForTest(PlatformStartLine);

            Assert.Equal(2, started.Count);
        }
    }
}
