#nullable enable
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Read-only view over the enrollment + app-tracking state the
    /// <see cref="EnrollmentTerminationHandler"/> needs at terminal time (ARCH-F2 grouping —
    /// replaces six loose <c>Func&lt;&gt;</c> constructor parameters). Production implementation:
    /// <see cref="OrchestratorAppTrackingReadModel"/>; tests supply a small fake over their rig
    /// state. All members are read lazily inside <c>Handle</c> — never cached at construction —
    /// because the collector surfaces only exist after <c>EnrollmentOrchestrator.Start</c>.
    /// </summary>
    public interface IAppTrackingReadModel
    {
        /// <summary>
        /// Current <see cref="DecisionState"/> snapshot. May throw before the orchestrator has
        /// started — the handler guards every read with try/catch.
        /// </summary>
        DecisionState CurrentState { get; }

        /// <summary>
        /// F5 (debrief 7dd4e593) — deduped union of phase-snapshotted apps + live package
        /// states, so the termination summary can iterate the union of phase-snapshotted apps
        /// + live <c>_packageStates</c> (V2's clear-on-phase-transition would otherwise drop
        /// the DeviceSetup apps from app_tracking_summary and the SummaryDialog). May return
        /// null when no IME surface exists.
        /// </summary>
        IReadOnlyList<AppPackageState>? PackageStates { get; }

        /// <summary>
        /// Plan §5 Fix 4 — per-app install-lifecycle timings for FinalStatusBuilder +
        /// app_tracking_summary emission. May return null (handler substitutes an empty map).
        /// </summary>
        IReadOnlyDictionary<string, AppInstallTiming>? AppTimings { get; }

        /// <summary>
        /// V1-parity field: count of IME apps the tracker has marked as "ignored" (e.g.
        /// uninstall intents that don't surface in the install pipeline). Lives on the live
        /// <c>AppPackageStateList</c> only — the deduped phase-snapshot union in
        /// <see cref="PackageStates"/> doesn't carry it.
        /// </summary>
        int IgnoredCount { get; }

        /// <summary>
        /// c117946b debrief (2026-05-12): on terminal ESP-Apps failure, promote any apps the
        /// agent observed in <c>Installing</c> to Error so the user sees a name (not just an
        /// opaque "installing: 1" counter) and the app_install_failed event carries the
        /// canonical failureType. Invoked ONLY when the discriminator in
        /// <c>ShouldPromoteActiveInstallsAsStuck</c> matches — every other terminal path
        /// leaves app states untouched. Returns the list of promoted appIds for logging.
        /// <para>
        /// Session 080edee9 follow-up (2026-05-28): <paramref name="errorCode"/> carries the
        /// HRESULT from the failed Apps subcategory (e.g. <c>0x87D1041C</c>) when available,
        /// so the promotion can stamp <c>AppPackageState.ErrorCode</c> + emit an enriched
        /// <c>app_install_failed</c> event. Null = no HRESULT observed → fallback to the
        /// legacy "esp_apps_timeout" classification.
        /// </para>
        /// </summary>
        IReadOnlyList<string> PromoteActiveInstallsToStuck(string failureType, string message, string? errorCode);

        /// <summary>
        /// Session 080edee9 follow-up + Codex review (P2/P3, 2026-05-28): latest ESP failure
        /// context (HRESULT + failedSubcategory + category) observed by
        /// ProvisioningStatusTracker. Read once during
        /// <c>MaybePromoteActiveInstallsAsStuck</c>. The classifier first checks
        /// <c>snapshot.IsAppsSubcategory</c> — only then does the HRESULT drive the
        /// failureType. A non-Apps ESP failure (DevicePreparation/*,
        /// DeviceSetup/SecurityPolicies, AccountSetup/CertificatesAccountSetup) falls through
        /// to the generic <c>esp_apps_timeout</c> wording so a non-app HRESULT cannot
        /// mis-classify in-flight installs. Null = no HRESULT context → fallback.
        /// </summary>
        EspTerminalFailureSnapshot? LastEspTerminalFailure { get; }
    }
}
