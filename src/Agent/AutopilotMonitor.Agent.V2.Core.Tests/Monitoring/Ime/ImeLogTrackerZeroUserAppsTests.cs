using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Ime
{
    /// <summary>
    /// Sessions 81daa77f / 75d6ae8e (2026-08-25) — zero user-targeted apps starved every
    /// user-phase completion probe. The IME zero-app branch returns before it writes the
    /// "Completed user session" line, so a device whose user has no targeted Win32 apps never
    /// produced the evidence <c>IsImeUserSessionGenuine</c> requires, and the AdvisoryCompletion
    /// window failed the enrollment 30 minutes later — with the user at the desktop, Hello
    /// provisioned and RealmJoin still installing.
    /// <para>
    /// IME-USER-SESSION-ZERO-APPS reads the line IME does write in that branch and routes it
    /// through the same <see cref="ImeLogTracker.OnUserSessionCompleted"/> callback.
    /// </para>
    /// </summary>
    public sealed class ImeLogTrackerZeroUserAppsTests
    {
        // Mirrors rules/ime-log-patterns/IME-USER-SESSION-ZERO-APPS.json. The JSON itself is
        // pinned against a real IME line by BuiltInRulesTests in the backend suite.
        private const string ZeroAppsPattern =
            @"\[Win32App\] Get 0 apps for user session (?<sessionId>\d+), user id = (?<userId>[a-f0-9\-]+)";

        private const string EspPhasePattern =
            @"\[Win32App\] (?:In|The) EspPhase: (?<espPhase>\w+)";

        private const string ZeroAppsLine =
            "[Win32App] Get 0 apps for user session 1, user id = 4a1ba5db-44ed-4e69-8477-47ed88e76020";

        private static ImeLogTracker BuildTracker(TempDirectory tmp) =>
            new ImeLogTracker(
                logFolder: tmp.Path,
                patterns: new List<ImeLogPattern>
                {
                    new ImeLogPattern
                    {
                        PatternId = "IME-USER-SESSION-ZERO-APPS",
                        Category = "always",
                        Pattern = ZeroAppsPattern,
                        Action = "userSessionZeroApps",
                    },
                    new ImeLogPattern
                    {
                        PatternId = "IME-ESP-PHASE",
                        Category = "always",
                        Pattern = EspPhasePattern,
                        Action = "espPhaseDetected",
                    },
                },
                logger: new AgentLogger(tmp.Path, AgentLogLevel.Info));

        [Fact]
        public void ZeroApps_line_during_user_phase_fires_user_session_completed()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            var fired = 0;
            tracker.OnUserSessionCompleted = () => fired++;

            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: AccountSetup");
            tracker.ProcessLogMessageForTest(ZeroAppsLine);

            Assert.Equal(1, fired);
            Assert.True(tracker.UserSessionZeroAppsObserved);
        }

        [Fact]
        public void ZeroApps_line_before_user_phase_does_not_fire()
        {
            // Firing here would burn the adapter fire-once flag on a pre-sign-in timestamp that
            // IsImeUserSessionGenuine (>= AccountSetupEnteredUtc) can never accept.
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            var fired = 0;
            tracker.OnUserSessionCompleted = () => fired++;

            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: DeviceSetup");
            tracker.ProcessLogMessageForTest(ZeroAppsLine);

            Assert.Equal(0, fired);
            Assert.True(tracker.UserSessionZeroAppsObserved);
        }

        [Fact]
        public void ZeroApps_observed_early_is_replayed_on_the_user_phase_transition()
        {
            // IME writes its check-in lines independently of when the agent first parses the ESP
            // phase marker, so the evidence can legitimately arrive first. Dropping it would
            // leave a zero-app device with no completion evidence at all.
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            var fired = 0;
            tracker.OnUserSessionCompleted = () => fired++;

            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: DeviceSetup");
            tracker.ProcessLogMessageForTest(ZeroAppsLine);
            Assert.Equal(0, fired);

            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: AccountSetup");

            Assert.Equal(1, fired);
        }

        [Fact]
        public void Repeated_user_phase_matches_do_not_replay_again()
        {
            // IME re-emits the phase string as it re-evaluates app sets. Only a real transition
            // replays; the adapter fire-once flag is the outer guard, but the tracker must not
            // spam the callback either.
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            var fired = 0;
            tracker.OnUserSessionCompleted = () => fired++;

            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: DeviceSetup");
            tracker.ProcessLogMessageForTest(ZeroAppsLine);
            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: AccountSetup");
            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: AccountSetup");
            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: AccountSetup");

            Assert.Equal(1, fired);
        }

        [Fact]
        public void Nonzero_app_count_lines_do_not_match_the_pattern()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);
            var fired = 0;
            tracker.OnUserSessionCompleted = () => fired++;
            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: AccountSetup");

            tracker.ProcessLogMessageForTest(
                "[Win32App] Get 3 apps for user session 1, user id = 4a1ba5db-44ed-4e69-8477-47ed88e76020");
            tracker.ProcessLogMessageForTest(
                "[Win32App] Get 10 apps for user session 1, user id = 4a1ba5db-44ed-4e69-8477-47ed88e76020");

            Assert.Equal(0, fired);
            Assert.False(tracker.UserSessionZeroAppsObserved);
        }

        [Fact]
        public void AreUserEspAppsSettled_true_after_zero_apps_observation()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);

            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: AccountSetup");
            tracker.ProcessLogMessageForTest(ZeroAppsLine);

            Assert.True(tracker.AreUserEspAppsSettled());
        }

        [Fact]
        public void AreUserEspAppsSettled_stays_false_on_an_empty_list_without_the_observation()
        {
            // Mutation proof for the branch above: an empty live list on its own still means
            // "phase just cleared / apps not surfaced yet", never vacuously settled.
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);

            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: AccountSetup");

            Assert.False(tracker.AreUserEspAppsSettled());
        }

        [Fact]
        public void AreUserEspAppsSettled_false_outside_the_user_phase_even_after_the_observation()
        {
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);

            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: DeviceSetup");
            tracker.ProcessLogMessageForTest(ZeroAppsLine);

            Assert.True(tracker.UserSessionZeroAppsObserved);
            Assert.False(tracker.AreUserEspAppsSettled());
        }

        [Fact]
        public void GetPendingRequiredUserEspInstallApps_is_empty_for_a_zero_app_user_session()
        {
            // The adapter deferral probe must not park the signal when there is nothing to wait
            // for — otherwise the zero-app path would starve exactly like the old one.
            using var tmp = new TempDirectory();
            var tracker = BuildTracker(tmp);

            tracker.ProcessLogMessageForTest("[Win32App] In EspPhase: AccountSetup");
            tracker.ProcessLogMessageForTest(ZeroAppsLine);

            Assert.Empty(tracker.GetPendingRequiredUserEspInstallApps());
        }
    }
}
