using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Gather.Collectors;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Gather
{
    /// <summary>
    /// Codex round 3: the registry collector returned a non-empty { exists=false } result for an
    /// absent key, which the executor emits as the rule's OutputEventType — so a positive-assertion
    /// event like windows_update_reboot_pending fired even when NO reboot was pending, and the
    /// analyze rules (matching on event existence) counted it as corroboration. The opt-in
    /// emitOnlyIfExists parameter suppresses that "not found" emission at the source.
    /// </summary>
    public sealed class RegistryCollectorEmitOnlyIfExistsTests : IDisposable
    {
        private readonly TempDirectory _tmp = new TempDirectory();
        private readonly GatherRuleContext _context;

        // A path that is guaranteed not to exist so the test is environment-independent.
        private const string AbsentPath =
            "HKLM\\SOFTWARE\\AutopilotMonitorTests\\DefinitelyAbsent_2f0c9e1a-0000-0000-0000-000000000000";

        public RegistryCollectorEmitOnlyIfExistsTests()
        {
            var logger = new AgentLogger(_tmp.Path, AgentLogLevel.Info);
            _context = new GatherRuleContext(
                logger: logger,
                sessionId: "sess",
                tenantId: "tenant",
                onEventCollected: _ => { },
                imeLogPathOverride: null,
                filePositionTracker: new LogFilePositionTracker())
            {
                // Bypass the registry allowlist guard for the synthetic test path.
                UnrestrictedMode = true
            };
        }

        public void Dispose() => _tmp.Dispose();

        private GatherRule Rule(bool emitOnlyIfExists) => new GatherRule
        {
            RuleId = "GATHER-TEST-001",
            CollectorType = "registry",
            Target = AbsentPath,
            Trigger = "startup",
            OutputEventType = "windows_update_reboot_pending",
            Parameters = emitOnlyIfExists
                ? new Dictionary<string, string> { ["emitOnlyIfExists"] = "true" }
                : new Dictionary<string, string>()
        };

        [Fact]
        public void AbsentKey_WithEmitOnlyIfExists_ReturnsEmpty_SoNothingIsEmitted()
        {
            var result = new RegistryCollector().Execute(Rule(emitOnlyIfExists: true), _context);

            // Empty result → GatherRuleExecutor emits no event (Count == 0 gate).
            Assert.Empty(result);
        }

        [Fact]
        public void AbsentKey_WithoutTheFlag_StillReportsExistsFalse()
        {
            // Back-compat: existing rules that legitimately want the negative signal are unaffected.
            var result = new RegistryCollector().Execute(Rule(emitOnlyIfExists: false), _context);

            Assert.True(result.ContainsKey("exists"));
            Assert.Equal(false, result["exists"]);
        }

        [Fact]
        public void ExistingHklmKey_IsFound_ViaRegistry64Base()
        {
            // Reads a key present on every Windows install through the (now default) 64-bit base view
            // — proves the OpenBaseKey(Registry64) read path resolves a real HKLM\SOFTWARE key rather
            // than being silently WOW6432Node-redirected on a 32-bit process.
            var rule = new GatherRule
            {
                RuleId = "GATHER-TEST-002",
                CollectorType = "registry",
                Target = "HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion",
                Trigger = "startup",
                OutputEventType = "gather_test",
                Parameters = new Dictionary<string, string> { ["valueName"] = "ProductName" }
            };

            var result = new RegistryCollector().Execute(rule, _context);

            Assert.Equal(true, result["exists"]);
            Assert.True(result.ContainsKey("ProductName"));
        }
    }
}
