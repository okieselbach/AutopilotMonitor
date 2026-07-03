#nullable enable
using System.Collections.Generic;
using System.IO;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Persistence;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Persistence
{
    /// <summary>
    /// StartupEventGate — the cross-cutting restart dedup for one-shot startup checks.
    /// Covers both policies (emit-on-change, retry-until-success), cross-instance persistence
    /// (a new instance over the same state directory models an agent restart), fingerprint
    /// stability and fail-soft behavior on corrupt state.
    /// </summary>
    public sealed class StartupEventGateTests
    {
        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        // ---------------------------------------------------------------- emit-on-change

        [Fact]
        public void ShouldEmit_true_first_time_false_for_same_fingerprint_true_on_change()
        {
            using var tmp = new TempDirectory();
            var gate = new StartupEventGate(tmp.Path, NewLogger(tmp.Path));

            Assert.True(gate.ShouldEmit("os_info", "fp-1"));
            Assert.False(gate.ShouldEmit("os_info", "fp-1")); // identical repeat suppressed
            Assert.True(gate.ShouldEmit("os_info", "fp-2"));  // real change re-emits
            Assert.False(gate.ShouldEmit("os_info", "fp-2"));
        }

        [Fact]
        public void ShouldEmit_state_survives_an_agent_restart_once_committed()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            var firstRun = new StartupEventGate(tmp.Path, logger);
            Assert.True(firstRun.ShouldEmit("aad_join_status", "not-joined"));
            firstRun.MarkEmitted("aad_join_status"); // event went out → commit

            // New instance over the same state directory = agent restarted after a reboot.
            var secondRun = new StartupEventGate(tmp.Path, logger);
            Assert.False(secondRun.ShouldEmit("aad_join_status", "not-joined")); // unchanged → suppressed
            Assert.True(secondRun.ShouldEmit("aad_join_status", "joined"));      // late join → emits
        }

        [Fact]
        public void Uncommitted_claim_does_not_survive_a_restart()
        {
            // M4 (delta review 2026-07-02): a crash between ShouldEmit and the actual emission
            // must NOT suppress the event for the rest of the enrollment — only MarkEmitted
            // (called after the emission) persists the claim.
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            var firstRun = new StartupEventGate(tmp.Path, logger);
            Assert.True(firstRun.ShouldEmit("tpm_info", "fp-static"));
            // process dies here — no MarkEmitted

            var secondRun = new StartupEventGate(tmp.Path, logger);
            Assert.True(secondRun.ShouldEmit("tpm_info", "fp-static")); // re-emits after the crash
        }

        [Fact]
        public void MarkEmitted_commits_only_its_own_key()
        {
            // Two events claim; only A's emission goes out before the crash. B must re-emit.
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            var firstRun = new StartupEventGate(tmp.Path, logger);
            Assert.True(firstRun.ShouldEmit("event_a", "fp-a"));
            Assert.True(firstRun.ShouldEmit("event_b", "fp-b"));
            firstRun.MarkEmitted("event_a"); // only A emitted before the crash

            var secondRun = new StartupEventGate(tmp.Path, logger);
            Assert.False(secondRun.ShouldEmit("event_a", "fp-a"));
            Assert.True(secondRun.ShouldEmit("event_b", "fp-b"));
        }

        [Fact]
        public void MarkEmitted_without_prior_claim_is_a_noop()
        {
            using var tmp = new TempDirectory();
            var gate = new StartupEventGate(tmp.Path, NewLogger(tmp.Path));

            gate.MarkEmitted("gate_exempt_type"); // never claimed → must not throw or persist
            Assert.True(gate.ShouldEmit("gate_exempt_type", "fp"));
        }

        [Fact]
        public void HasFingerprint_peeks_without_claiming()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            var firstRun = new StartupEventGate(tmp.Path, logger);
            Assert.False(firstRun.HasFingerprint("disk_space_low", "low"));
            Assert.True(firstRun.ShouldEmit("disk_space_low", "low"));
            firstRun.MarkEmitted("disk_space_low");

            var secondRun = new StartupEventGate(tmp.Path, logger);
            Assert.True(secondRun.HasFingerprint("disk_space_low", "low"));
            Assert.False(secondRun.HasFingerprint("disk_space_low", "rearmed"));
            // The peek made no claim: an actual state change still goes through normally.
            Assert.True(secondRun.ShouldEmit("disk_space_low", "rearmed"));
        }

        [Fact]
        public void Keys_are_independent()
        {
            using var tmp = new TempDirectory();
            var gate = new StartupEventGate(tmp.Path, NewLogger(tmp.Path));

            Assert.True(gate.ShouldEmit("hardware_spec", "fp"));
            Assert.True(gate.ShouldEmit("tpm_status", "fp")); // same fingerprint, different key
        }

        // ---------------------------------------------------------------- retry-until-success

        [Fact]
        public void MarkSucceeded_latches_across_restarts()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);

            var firstRun = new StartupEventGate(tmp.Path, logger);
            Assert.False(firstRun.AlreadySucceeded("device_location"));
            firstRun.MarkSucceeded("device_location");
            Assert.True(firstRun.AlreadySucceeded("device_location"));

            var secondRun = new StartupEventGate(tmp.Path, logger);
            Assert.True(secondRun.AlreadySucceeded("device_location"));
            Assert.False(secondRun.AlreadySucceeded("ntp_time_check")); // never marked → retries
        }

        [Fact]
        public void ShouldEmit_preserves_a_previous_success_latch_and_vice_versa()
        {
            using var tmp = new TempDirectory();
            var gate = new StartupEventGate(tmp.Path, NewLogger(tmp.Path));

            gate.MarkSucceeded("k");
            Assert.True(gate.ShouldEmit("k", "fp-1")); // fingerprint update...
            Assert.True(gate.AlreadySucceeded("k"));   // ...must not clear the success latch

            gate.MarkSucceeded("k");                   // success latch update...
            Assert.False(gate.ShouldEmit("k", "fp-1")); // ...must not clear the fingerprint
        }

        // ---------------------------------------------------------------- fail-soft

        [Theory]
        [InlineData("not json {{{")]
        [InlineData("null")]
        public void Corrupt_state_file_loads_fresh_and_everything_emits(string content)
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var gate = new StartupEventGate(tmp.Path, logger);
            File.WriteAllText(gate.StateFilePath, content);

            var reloaded = new StartupEventGate(tmp.Path, logger);
            Assert.True(reloaded.ShouldEmit("os_info", "fp"));
            Assert.False(reloaded.AlreadySucceeded("device_location"));
        }

        // ---------------------------------------------------------------- fingerprint

        [Fact]
        public void ComputeFingerprint_is_stable_across_toplevel_insertion_order()
        {
            var a = new Dictionary<string, object> { { "x", 1 }, { "y", "two" } };
            var b = new Dictionary<string, object> { { "y", "two" }, { "x", 1 } };

            Assert.Equal(StartupEventGate.ComputeFingerprint(a), StartupEventGate.ComputeFingerprint(b));
        }

        [Fact]
        public void ComputeFingerprint_changes_when_a_value_changes()
        {
            var a = new Dictionary<string, object> { { "joinType", "Not Joined" } };
            var b = new Dictionary<string, object> { { "joinType", "Azure AD Joined" } };

            Assert.NotEqual(StartupEventGate.ComputeFingerprint(a), StartupEventGate.ComputeFingerprint(b));
        }

        [Fact]
        public void ComputeFingerprint_ignores_excluded_volatile_fields()
        {
            var a = new Dictionary<string, object> { { "adapterName", "WiFi" }, { "linkSpeedMbps", 433L } };
            var b = new Dictionary<string, object> { { "adapterName", "WiFi" }, { "linkSpeedMbps", 866L } };
            var excluded = new[] { "linkSpeedMbps" };

            Assert.Equal(
                StartupEventGate.ComputeFingerprint(a, excluded),
                StartupEventGate.ComputeFingerprint(b, excluded));
            Assert.NotEqual(
                StartupEventGate.ComputeFingerprint(a),
                StartupEventGate.ComputeFingerprint(b));
        }

        [Fact]
        public void ComputeFingerprint_handles_nested_structures()
        {
            var a = new Dictionary<string, object>
            {
                { "adapters", new List<Dictionary<string, object>> { new Dictionary<string, object> { { "mac", "AA" } } } },
            };
            var b = new Dictionary<string, object>
            {
                { "adapters", new List<Dictionary<string, object>> { new Dictionary<string, object> { { "mac", "BB" } } } },
            };

            Assert.NotEqual(StartupEventGate.ComputeFingerprint(a), StartupEventGate.ComputeFingerprint(b));
        }
    }
}
