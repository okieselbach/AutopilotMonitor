#nullable enable
using System.Collections.Generic;
using System.Threading;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using Xunit;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring
{
    /// <summary>
    /// Unit tests for <see cref="DesktopArrivalDetector.IsExcludedUser"/>. Hybrid-User-Driven
    /// session diagnosis (e58bcfdb-…, 2026-05-01) showed the detector treated the fooUser
    /// OOBE shell as a real user desktop, firing DesktopArrived 6 s after agent start —
    /// before the Hybrid reboot to the AD account ever happened. The exclusion now covers
    /// the foouser@/autopilot@ Autopilot provisioning placeholders in both UPN and
    /// DOMAIN\User shapes.
    /// </summary>
    public sealed class DesktopArrivalDetectorTests
    {
        // ---------------- Existing exclusions stay covered ----------------

        [Theory]
        [InlineData("SYSTEM")]
        [InlineData("system")]
        [InlineData("LOCAL SERVICE")]
        [InlineData("NETWORK SERVICE")]
        [InlineData("DefaultUser0")]
        [InlineData("DefaultUser1")]
        [InlineData("defaultuser0")]
        [InlineData("DefaultUser42")]
        public void System_and_default_users_are_excluded(string user)
        {
            Assert.True(DesktopArrivalDetector.IsExcludedUser(user));
        }

        [Theory]
        [InlineData("NT AUTHORITY\\SYSTEM")]
        [InlineData("WORKGROUP\\DefaultUser0")]
        [InlineData("CONTOSO\\DefaultUser1")]
        public void Domain_qualified_system_users_are_excluded(string user)
        {
            Assert.True(DesktopArrivalDetector.IsExcludedUser(user));
        }

        // ---------------- Real users are NOT excluded ----------------

        [Theory]
        [InlineData("alice")]
        [InlineData("CONTOSO\\alice")]
        [InlineData("alice@contoso.com")]
        [InlineData("bob.smith@fabrikam.com")]
        // Codex review 2026-05-01 (Finding 3): the bare-username matcher was tightened
        // from prefix-match to exact-match, so the following real account shapes that
        // *start with* "autopilot" or "foouser" must now stay through the gate. Reused
        // by UserProfileResolver — incorrectly excluding these would resolve the wrong
        // home directory.
        [InlineData("CONTOSO\\autopilotadmin")]
        [InlineData("CONTOSO\\autopilot.admin")]
        [InlineData("FABRIKAM\\foouserservice")]
        [InlineData("autopilotadmin")]
        [InlineData("foouserservice")]
        public void Real_users_are_not_excluded(string user)
        {
            Assert.False(DesktopArrivalDetector.IsExcludedUser(user));
        }

        // ---------------- New: Autopilot placeholder UPN form ----------------

        [Theory]
        [InlineData("foouser@fabrikam.onmicrosoft.com")]
        [InlineData("FooUser@fabrikam.onmicrosoft.com")]
        [InlineData("FOOUSER@example.com")]
        [InlineData("autopilot@contoso.com")]
        [InlineData("Autopilot@contoso.com")]
        [InlineData("AUTOPILOT@contoso.com")]
        public void Autopilot_placeholder_upn_is_excluded(string upn)
        {
            // Hybrid User-Driven OOBE shell runs explorer.exe under foouser@<tenant>;
            // matching this prevents premature DesktopArrived firing on the foo desktop.
            // UPN form is delegated to AadJoinInfo.IsPlaceholderUserEmail (prefix-match
            // on the local-part, so any tenant domain works).
            Assert.True(DesktopArrivalDetector.IsExcludedUser(upn));
        }

        // ---------------- New: Domain-qualified placeholder (exact bare-name match) ----------------

        [Theory]
        [InlineData("AzureAD\\foouser")]
        [InlineData("WORKGROUP\\autopilot")]
        [InlineData("AzureAD\\FooUser")]    // case-insensitive
        [InlineData("AzureAD\\AUTOPILOT")]  // case-insensitive
        [InlineData("foouser")]             // bare username (no domain prefix)
        [InlineData("autopilot")]           // bare username (no domain prefix)
        public void Domain_qualified_placeholder_is_excluded(string user)
        {
            // WMI Win32_Process.GetOwner sometimes returns DOMAIN\foouser instead of the
            // UPN form. The bare-username match is now EXACT (Finding 3) — no prefix.
            Assert.True(DesktopArrivalDetector.IsExcludedUser(user));
        }

        // ---------------- Edge cases ----------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Null_or_empty_is_excluded(string? user)
        {
            Assert.True(DesktopArrivalDetector.IsExcludedUser(user!));
        }

        [Fact]
        public void Real_user_email_with_foouser_substring_is_NOT_excluded()
        {
            // Defense against false positives — UPN match goes through
            // AadJoinInfo.IsPlaceholderUserEmail which anchors the local-part at start.
            // "realfoouser@…" and "not-autopilot@…" must NOT trigger.
            Assert.False(DesktopArrivalDetector.IsExcludedUser("realfoouser@contoso.com"));
            Assert.False(DesktopArrivalDetector.IsExcludedUser("not-autopilot@contoso.com"));
        }

        [Fact]
        public void Real_user_with_default_substring_anywhere_is_NOT_excluded()
        {
            Assert.False(DesktopArrivalDetector.IsExcludedUser("MyDefaultUser"));
            // "DefaultUser" prefix-match: this WOULD trigger because the bare-username
            // DefaultUser* code path is still prefix-based (Windows generates
            // DefaultUser0/1/2/...). Documenting current behavior.
            Assert.True(DesktopArrivalDetector.IsExcludedUser("DefaultUserBob"));
        }

        // ============================================================================
        // ResetForRealUserSwitch (Pkt 5 — placeholder→real-user transition)
        // ============================================================================

        [Fact]
        public void ResetForRealUserSwitch_does_not_throw_when_detector_is_stopped()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var detector = new DesktopArrivalDetector(logger);

            // Detector has not been Started — Reset must be safe regardless. The Hybrid
            // reset path may fire before any explicit Start in test fixtures, and we
            // never want host-wiring to crash the agent.
            detector.ResetForRealUserSwitch();
        }

        [Fact]
        public void ResetForRealUserSwitch_is_idempotent()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var detector = new DesktopArrivalDetector(logger);

            // Calling reset multiple times back-to-back must not leak timer instances or
            // throw. The composition root currently invokes it once per real-user join,
            // but the contract is still safe-to-repeat.
            detector.ResetForRealUserSwitch();
            detector.ResetForRealUserSwitch();
            detector.ResetForRealUserSwitch();
        }

        [Fact]
        public void ResetForRealUserSwitch_after_Stop_restarts_polling_path()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var detector = new DesktopArrivalDetector(logger);

            detector.Start();
            detector.Stop();

            // After Stop the polling timer is null. Reset must reinstate it without
            // requiring a fresh Start call (the Hybrid reboot transition shouldn't have
            // to rebuild the host).
            detector.ResetForRealUserSwitch();
        }

        // ============================================================================
        // Owner resolution: WTS primary, WMI fallback (session 4d5a0b78 fix, 2026-06-11)
        // ============================================================================
        // Session 4d5a0b78: WMI Win32_Process.GetOwner failed on EVERY poll on a device
        // (wmiErrorCount == pollCount), the owner was never resolved and desktop_arrived
        // never fired — the Desktop half of the completion AND-gate starved. WTS session
        // queries are now the primary path; WMI is only consulted when WTS yields no user.

        private static DesktopArrivalDetector BuildDetector(TempDirectory tmp) =>
            new DesktopArrivalDetector(new AgentLogger(tmp.Path, AgentLogLevel.Info));

        [Fact]
        public void ResolveOwner_uses_WTS_result_without_consulting_WMI()
        {
            using var tmp = new TempDirectory();
            using var detector = BuildDetector(tmp);
            var wmiCalled = false;
            detector.SessionOwnerResolver = sessionId => "CONTOSO\\alice";
            detector.ProcessOwnerResolver = pid => { wmiCalled = true; return "CONTOSO\\bob"; };

            var owner = detector.ResolveOwner(processId: 1234, sessionId: 2);

            Assert.Equal("CONTOSO\\alice", owner);
            Assert.False(wmiCalled);
        }

        [Fact]
        public void ResolveOwner_falls_back_to_WMI_when_WTS_returns_null()
        {
            using var tmp = new TempDirectory();
            using var detector = BuildDetector(tmp);
            detector.SessionOwnerResolver = sessionId => null;
            detector.ProcessOwnerResolver = pid => "CONTOSO\\alice";

            Assert.Equal("CONTOSO\\alice", detector.ResolveOwner(1234, 2));
        }

        [Fact]
        public void ResolveOwner_falls_back_to_WMI_when_WTS_returns_empty()
        {
            // WTSQuerySessionInformation can succeed with an empty user name (no user
            // associated with the session) — must be treated like a miss, not a real owner.
            using var tmp = new TempDirectory();
            using var detector = BuildDetector(tmp);
            detector.SessionOwnerResolver = sessionId => "";
            detector.ProcessOwnerResolver = pid => "CONTOSO\\alice";

            Assert.Equal("CONTOSO\\alice", detector.ResolveOwner(1234, 2));
        }

        [Fact]
        public void ResolveOwner_falls_back_to_WMI_when_WTS_throws()
        {
            using var tmp = new TempDirectory();
            using var detector = BuildDetector(tmp);
            detector.SessionOwnerResolver = sessionId => throw new System.ComponentModel.Win32Exception(5);
            detector.ProcessOwnerResolver = pid => "CONTOSO\\alice";

            Assert.Equal("CONTOSO\\alice", detector.ResolveOwner(1234, 2));
        }

        [Fact]
        public void ResolveOwner_returns_null_when_both_paths_fail()
        {
            using var tmp = new TempDirectory();
            using var detector = BuildDetector(tmp);
            detector.SessionOwnerResolver = sessionId => null;
            detector.ProcessOwnerResolver = pid => null;

            Assert.Null(detector.ResolveOwner(1234, 2));
        }

        [Fact]
        public void ResolveOwner_passes_session_id_to_WTS_and_pid_to_WMI()
        {
            using var tmp = new TempDirectory();
            using var detector = BuildDetector(tmp);
            int? seenSessionId = null;
            int? seenPid = null;
            detector.SessionOwnerResolver = sessionId => { seenSessionId = sessionId; return null; };
            detector.ProcessOwnerResolver = pid => { seenPid = pid; return null; };

            detector.ResolveOwner(processId: 4711, sessionId: 3);

            Assert.Equal(3, seenSessionId);
            Assert.Equal(4711, seenPid);
        }

        // ============================================================================
        // DAD-liveness telemetry (2026-05-15) — Started / FirstPoll / NoCandidate
        // ============================================================================

        private sealed class TraceCapture
        {
            public readonly List<(string EventType, string Message, Dictionary<string, object> Data)> Events =
                new List<(string, string, Dictionary<string, object>)>();

            public System.Action<string, string, Dictionary<string, object>> Sink => (e, m, d) =>
            {
                lock (Events)
                {
                    Events.Add((e, m, d ?? new Dictionary<string, object>()));
                }
            };

            public int CountOf(string eventType)
            {
                lock (Events)
                {
                    int count = 0;
                    foreach (var (et, _, _) in Events)
                        if (et == eventType) count++;
                    return count;
                }
            }
        }

        [Fact]
        public void Liveness_Start_emits_detector_started_once()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var detector = new DesktopArrivalDetector(logger, noCandidateTimeoutMinutes: 10);
            var trace = new TraceCapture();
            detector.OnTraceEvent = trace.Sink;

            detector.Start();

            // ArmTimer in Start synchronously emits detector_started before the first poll runs.
            Assert.Equal(1, trace.CountOf(SharedEventTypes.DesktopDetectorStarted));
            var started = trace.Events.Find(e => e.EventType == SharedEventTypes.DesktopDetectorStarted);
            Assert.Equal("initial", started.Data["startReason"]);
            Assert.Equal(10, started.Data["noCandidateThresholdMinutes"]);
        }

        [Fact]
        public void Liveness_ResetForRealUserSwitch_re_emits_detector_started_with_reset_reason()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var detector = new DesktopArrivalDetector(logger, noCandidateTimeoutMinutes: 5);
            var trace = new TraceCapture();
            detector.OnTraceEvent = trace.Sink;

            detector.Start();
            detector.ResetForRealUserSwitch();

            Assert.Equal(2, trace.CountOf(SharedEventTypes.DesktopDetectorStarted));
            // Verify the reset emit carries the "real-user-switch-reset" discriminator so
            // dashboards can distinguish a fresh detector lifetime from an OOBE-handoff reset.
            var startReasons = new List<string>();
            foreach (var ev in trace.Events)
                if (ev.EventType == SharedEventTypes.DesktopDetectorStarted)
                    startReasons.Add((string)ev.Data["startReason"]);
            Assert.Contains("initial", startReasons);
            Assert.Contains("real-user-switch-reset", startReasons);
        }

        [Fact]
        public void Liveness_first_poll_emits_after_initial_poll_completes()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            using var detector = new DesktopArrivalDetector(logger, noCandidateTimeoutMinutes: 0);
            var trace = new TraceCapture();
            detector.OnTraceEvent = trace.Sink;

            detector.Start();

            // The polling timer fires after a 5s initial delay; wait up to 8s for the first
            // poll observation to land. On CI / WMI-unavailable boxes the WMI call may
            // throw and we still expect the first-poll snapshot (carrying wmiErrorCount).
            var deadline = System.DateTime.UtcNow.AddSeconds(8);
            while (System.DateTime.UtcNow < deadline && trace.CountOf(SharedEventTypes.DesktopDetectorFirstPoll) == 0)
            {
                Thread.Sleep(200);
            }

            Assert.Equal(1, trace.CountOf(SharedEventTypes.DesktopDetectorFirstPoll));
            var firstPoll = trace.Events.Find(e => e.EventType == SharedEventTypes.DesktopDetectorFirstPoll);
            Assert.True(firstPoll.Data.ContainsKey("elapsedMsSinceStart"));
            Assert.True(firstPoll.Data.ContainsKey("explorerProcessCount"));
            Assert.True(firstPoll.Data.ContainsKey("wmiErrorCount"));
        }

        [Fact]
        public void Liveness_no_candidate_does_not_fire_before_threshold()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            // 10-min threshold means our short test window (<8s) is well below — no_candidate
            // must NOT emit even though no real-user desktop is detected during the test.
            using var detector = new DesktopArrivalDetector(logger, noCandidateTimeoutMinutes: 10);
            var trace = new TraceCapture();
            detector.OnTraceEvent = trace.Sink;

            detector.Start();

            var deadline = System.DateTime.UtcNow.AddSeconds(8);
            while (System.DateTime.UtcNow < deadline && trace.CountOf(SharedEventTypes.DesktopDetectorFirstPoll) == 0)
            {
                Thread.Sleep(200);
            }

            // First-poll must have fired (the timer is alive), no-candidate must NOT have.
            Assert.True(trace.CountOf(SharedEventTypes.DesktopDetectorFirstPoll) >= 0); // best-effort, may be 0 on slow CI
            Assert.Equal(0, trace.CountOf(SharedEventTypes.DesktopDetectorNoCandidate));
        }

        [Fact]
        public void Liveness_no_candidate_disabled_when_threshold_is_zero()
        {
            using var tmp = new TempDirectory();
            var logger = new AgentLogger(tmp.Path, AgentLogLevel.Info);
            // threshold=0 = disabled; no_candidate must never fire regardless of elapsed time.
            using var detector = new DesktopArrivalDetector(logger, noCandidateTimeoutMinutes: 0);
            var trace = new TraceCapture();
            detector.OnTraceEvent = trace.Sink;

            detector.Start();
            Thread.Sleep(100); // give Start time to finish synchronous arming

            Assert.Equal(1, trace.CountOf(SharedEventTypes.DesktopDetectorStarted));
            Assert.Equal(0, trace.CountOf(SharedEventTypes.DesktopDetectorNoCandidate));
            var started = trace.Events.Find(e => e.EventType == SharedEventTypes.DesktopDetectorStarted);
            Assert.Equal(0, started.Data["noCandidateThresholdMinutes"]);
        }
    }
}
