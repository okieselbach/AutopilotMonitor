using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.SignalAdapters;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.DecisionCore.Signals;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.SignalAdapters
{
    /// <summary>
    /// Sessions 14690fc2/6cb01530 hardening (2026-07-10): IME writes its "Completed user
    /// session N" line at the end of EVERY user-session processing pass — the first
    /// post-reboot pass completed with 13–18 required apps still pending and arm C stamped
    /// the session Succeeded 5 minutes later mid-install. The adapter must defer the
    /// <see cref="DecisionSignalKind.ImeUserSessionCompleted"/> signal while required
    /// Install-intent user-ESP apps are pending, and post it on the first pass-completion
    /// after they settle. Error counts as terminal (GRS owns retries) so a failed app can
    /// never park the signal — and with it the AdvisoryCompletion conjunction — forever.
    /// </summary>
    public sealed class ImeLogTrackerAdapterUserSessionGateTests
    {
        private static AppPackageState AddApp(
            ImeLogTracker tracker,
            string id,
            AppIntent intent,
            AppInstallationState state)
        {
            var pkg = tracker.PackageStates.GetPackage(id, createIfNotFound: true);
            pkg.UpdateIntent(intent);
            if (state == AppInstallationState.Installed)
            {
                pkg.UpdateState(AppInstallationState.Installing);
            }
            if (state != AppInstallationState.Unknown)
            {
                pkg.UpdateState(state);
            }
            return pkg;
        }

        private static IReadOnlyList<FakeSignalIngressSink.PostedSignal> DeferralTraces(ImeLogTrackerAdapterFixture f) =>
            f.InfoEvents(SharedEventTypes.AgentTrace)
                .Where(p => p.Payload!.TryGetValue("decision", out var d)
                            && d == "ime_user_session_completion_deferred")
                .ToList();

        [Fact]
        public void Pending_required_install_app_defers_signal_and_emits_oneshot_deferral_trace()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            f.Tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(f.Tracker, "app-pending", AppIntent.Install, AppInstallationState.Unknown);

            adapter.TriggerUserSessionCompletedFromTest();
            adapter.TriggerUserSessionCompletedFromTest();  // second pass, still pending

            Assert.Empty(f.DecisionSignals(DecisionSignalKind.ImeUserSessionCompleted));
            Assert.Empty(f.InfoEvents(SharedEventTypes.ImeUserSessionCompleted));

            var trace = Assert.Single(DeferralTraces(f));
            Assert.Equal("1", trace.Payload!["pendingRequiredInstallApps"]);
            Assert.Contains("app-pending", trace.Payload["pendingAppNames"]);
        }

        [Fact]
        public void Signal_posts_on_next_pass_after_pending_app_settles()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            f.Tracker.SeedCurrentPhaseForTesting("AccountSetup");
            var pkg = AddApp(f.Tracker, "app-pending", AppIntent.Install, AppInstallationState.Unknown);

            adapter.TriggerUserSessionCompletedFromTest();
            Assert.Empty(f.DecisionSignals(DecisionSignalKind.ImeUserSessionCompleted));

            pkg.UpdateState(AppInstallationState.Installing);
            pkg.UpdateState(AppInstallationState.Installed);
            adapter.TriggerUserSessionCompletedFromTest();

            Assert.Single(f.DecisionSignals(DecisionSignalKind.ImeUserSessionCompleted));
            Assert.Single(f.InfoEvents(SharedEventTypes.ImeUserSessionCompleted));
        }

        [Fact]
        public void Error_state_app_counts_as_terminal_and_does_not_defer()
        {
            // A permanently failed app must not park the signal: the AdvisoryCompletion
            // conjunction needs the IME fact, and blocking it would flip an otherwise
            // completed enrollment into a 30-minute-backstop Failed.
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            f.Tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(f.Tracker, "app-failed", AppIntent.Install, AppInstallationState.Error);
            AddApp(f.Tracker, "app-installed", AppIntent.Install, AppInstallationState.Installed);

            adapter.TriggerUserSessionCompletedFromTest();

            Assert.Single(f.DecisionSignals(DecisionSignalKind.ImeUserSessionCompleted));
            Assert.Empty(DeferralTraces(f));
        }

        [Fact]
        public void Pending_uninstall_intent_app_does_not_defer()
        {
            // The ESP apps gate does not block on uninstalls (session a4537c36) — a pending
            // system-app removal must not delay the completion signal.
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            f.Tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(f.Tracker, "app-uninstall-pending", AppIntent.Uninstall, AppInstallationState.Unknown);

            adapter.TriggerUserSessionCompletedFromTest();

            Assert.Single(f.DecisionSignals(DecisionSignalKind.ImeUserSessionCompleted));
        }

        [Fact]
        public void Outside_AccountSetup_phase_posts_as_before()
        {
            // Pre-anchor completions (defaultuser0 / technician frame) keep their existing
            // semantics — DecisionCore's IsImeUserSessionGenuine guard already prevents them
            // from ever triggering completion, and the M2 recording rule needs the stamp.
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            f.Tracker.SeedCurrentPhaseForTesting("DeviceSetup");
            AddApp(f.Tracker, "app-pending", AppIntent.Install, AppInstallationState.Unknown);

            adapter.TriggerUserSessionCompletedFromTest();

            Assert.Single(f.DecisionSignals(DecisionSignalKind.ImeUserSessionCompleted));
            Assert.Empty(DeferralTraces(f));
        }

        [Fact]
        public void No_tracked_apps_posts_signal()
        {
            // Zero user-scope apps assigned is a legitimate immediate completion — an empty
            // catalog must not defer (fail-open to the pre-hardening behaviour).
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            f.Tracker.SeedCurrentPhaseForTesting("AccountSetup");

            adapter.TriggerUserSessionCompletedFromTest();

            Assert.Single(f.DecisionSignals(DecisionSignalKind.ImeUserSessionCompleted));
        }

        [Fact]
        public void Fire_once_preserved_after_signal_posts()
        {
            using var f = new ImeLogTrackerAdapterFixture();
            using var adapter = new ImeLogTrackerAdapter(f.Tracker, f.Ingress, f.Clock);
            f.Tracker.SeedCurrentPhaseForTesting("AccountSetup");
            AddApp(f.Tracker, "app-installed", AppIntent.Install, AppInstallationState.Installed);

            adapter.TriggerUserSessionCompletedFromTest();
            adapter.TriggerUserSessionCompletedFromTest();

            Assert.Single(f.DecisionSignals(DecisionSignalKind.ImeUserSessionCompleted));
            Assert.Single(f.InfoEvents(SharedEventTypes.ImeUserSessionCompleted));
        }
    }
}
