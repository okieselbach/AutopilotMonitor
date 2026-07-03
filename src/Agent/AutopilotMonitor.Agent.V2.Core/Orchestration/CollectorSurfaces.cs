#nullable enable
using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Orchestration
{
    /// <summary>
    /// Result of <see cref="IComponentFactory.CreateCollectorHosts"/> — the collector hosts
    /// plus the typed read-model surfaces peripheral consumers need after startup
    /// (ARCH-F4, V2 agent review 2026-06-10).
    /// <para>
    /// Before this type existed, <c>DefaultComponentFactory</c> stashed host references in
    /// mutable fields and exposed them through lazy properties, which turned the factory into
    /// a service locator + state bag and forced <c>EnrollmentOrchestrator</c> to downcast the
    /// <see cref="IComponentFactory"/> seam. Now the factory returns everything in one
    /// immutable bundle; the orchestrator stores it and exposes it via
    /// <c>EnrollmentOrchestrator.CollectorSurfaces</c> for the runtime host.
    /// </para>
    /// <para>
    /// The read-model properties are <b>live delegating views</b> over the underlying hosts —
    /// not snapshots. They return <c>null</c> (or an empty fallback) when the corresponding
    /// host was not created, e.g. in test fakes that only supply <see cref="Hosts"/>.
    /// </para>
    /// </summary>
    public sealed class CollectorSurfaces
    {
        private readonly ImeLogHost? _imeLogHost;

        /// <summary>
        /// All collector hosts in creation order. The orchestrator starts them in this
        /// order and stops/disposes them in the same order on shutdown.
        /// </summary>
        public IReadOnlyList<ICollectorHost> Hosts { get; }

        /// <summary>
        /// ESP/Hello coordinator host — the WhiteGlove-success + DeviceSetup-complete event
        /// surface consumed by <c>WhiteGloveInventoryTrigger</c> / <c>AutoLogonDeviceSetupTrigger</c>.
        /// <c>internal</c> because <see cref="EspAndHelloHost"/> itself is <c>internal sealed</c>;
        /// AutopilotMonitor.Agent.V2 (the runtime entry-point project) sees it via the
        /// <c>InternalsVisibleTo</c> declared on this project's csproj.
        /// </summary>
        internal EspAndHelloHost? EspAndHelloHost { get; }

        /// <summary>
        /// AAD-join watcher host — used by the runtime host to arm the Hybrid User-Driven
        /// login-pending detector after observing a reboot-kill on a Hybrid-AAD device
        /// (2026-05-01 completion-gap fix).
        /// </summary>
        internal AadJoinHost? AadJoinHost { get; }

        /// <summary>
        /// Surfaces with hosts only — for test fakes that don't build the production
        /// read-model hosts. All read-model properties return their empty fallbacks.
        /// </summary>
        public CollectorSurfaces(IReadOnlyList<ICollectorHost> hosts)
            : this(hosts, imeLogHost: null, espAndHelloHost: null, aadJoinHost: null)
        {
        }

        internal CollectorSurfaces(
            IReadOnlyList<ICollectorHost> hosts,
            ImeLogHost? imeLogHost,
            EspAndHelloHost? espAndHelloHost,
            AadJoinHost? aadJoinHost)
        {
            Hosts = hosts ?? throw new ArgumentNullException(nameof(hosts));
            _imeLogHost = imeLogHost;
            EspAndHelloHost = espAndHelloHost;
            AadJoinHost = aadJoinHost;
        }

        /// <summary>
        /// F5 (debrief 7dd4e593) — deduped union of phase-snapshotted apps + the live
        /// package-state list. Used by the termination summary path so DeviceSetup apps
        /// cleared from the live list on the AccountSetup transition still reach the
        /// SummaryDialog and <c>app_tracking_summary</c> event.
        /// </summary>
        public IReadOnlyList<AppPackageState>? AllKnownPackageStates =>
            _imeLogHost?.AllKnownPackageStates;

        /// <summary>
        /// Plan §5 Fix 4c — per-app install-lifecycle timings (StartedAt / CompletedAt /
        /// DurationSeconds) captured by <c>ImeLogTrackerAdapter</c>.
        /// </summary>
        public IReadOnlyDictionary<string, AppInstallTiming>? ImeAppTimings => _imeLogHost?.AppTimings;

        /// <summary>
        /// V1-parity field: count of IME apps in the tracker's ignore list (e.g. uninstall
        /// intents that don't surface in the install pipeline). Lives on the live
        /// <see cref="AppPackageStateList"/> only — phase snapshots don't carry it.
        /// </summary>
        public int ImeIgnoredCount => _imeLogHost?.PackageStates?.IgnoreList?.Count ?? 0;

        /// <summary>
        /// c117946b debrief (2026-05-12) — bridge for the V2 EnrollmentTerminationHandler
        /// pre-hook. Delegates to <c>ImeLogHost.PromoteActiveInstallsToStuck</c> which calls
        /// the tracker directly so the standard <c>OnAppStateChanged</c> path fires and the
        /// adapter emits regular <c>app_install_failed</c> events for every promoted app.
        /// Returns an empty list when no IME host exists.
        /// </summary>
        public IReadOnlyList<string> PromoteActiveInstallsToStuck(string failureType, string message, string? errorCode = null) =>
            _imeLogHost?.PromoteActiveInstallsToStuck(failureType, message, errorCode) ?? Array.Empty<string>();

        /// <summary>
        /// Session 080edee9 follow-up + Codex review (P2/P3, 2026-05-28) — last observed
        /// ESP failure context (HRESULT + failedSubcategory + category). Read by
        /// <c>EnrollmentTerminationHandler.MaybePromoteActiveInstallsAsStuck</c> so the
        /// promotion can: (a) classify via HRESULT (detection-failure / install-failure),
        /// AND (b) refuse to attach app-level classifications when the failure originated
        /// outside the Apps subcategory. Returns null when no registry-derived ESP failure
        /// was observed.
        /// </summary>
        public Termination.EspTerminalFailureSnapshot? LastEspTerminalFailure => EspAndHelloHost?.LastEspTerminalFailure;

        /// <summary>
        /// Liveness plan PR3 — required user-ESP apps that never started installing (current
        /// AccountSetup phase). Empty when no IME host exists.
        /// </summary>
        public IReadOnlyList<AppPackageState> GetStarvedUserEspApps() =>
            _imeLogHost?.GetStarvedUserEspApps() ?? Array.Empty<AppPackageState>();

        /// <summary>
        /// Liveness plan PR3 / L6 — atomically claims the <c>app_install_starved</c> report for
        /// an app against the same dedupe set the live (esp_exited) path uses, so the terminal
        /// sweep and a racing live emission can never double-report. Returns true when the
        /// caller owns the report. Without an ESP/Hello host there is no live path to race —
        /// the claim always succeeds.
        /// </summary>
        public bool TryClaimStarvedUserEspAppReport(string appId) =>
            EspAndHelloHost?.TryClaimStarvedAppReport(appId) ?? true;
    }
}
