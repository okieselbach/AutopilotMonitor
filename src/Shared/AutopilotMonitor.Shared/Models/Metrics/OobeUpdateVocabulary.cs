namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// The wire vocabulary of the OOBE quality update (D-310): the update page's CXIDs, the
    /// <c>cxhEvent</c> values of <c>oobe_update_page</c>, the page names that tell an update and its
    /// outcome apart, and the CBS steps of <c>windows_update_servicing</c>. One definition for the
    /// agent that writes them, the time attribution that reads them and the portal's live hint
    /// (<c>shared-manifests.json</c> → <c>oobeUpdate</c>).
    /// </summary>
    public static class OobeUpdateVocabulary
    {
        /// <summary>CXID of the OOBE update page; <see cref="UpdateRestartPage"/> follows it and restarts for the update.</summary>
        public const string UpdatePage = "OobeNDUP";
        public const string UpdateRestartPage = "RebootNDUP";

        // cxhEvent of oobe_update_page
        public const string PageStarted = "page_started";
        public const string PageStopped = "page_stopped";
        public const string EventName = "event_name";

        /// <summary>
        /// Not a page record: the agent's marker that the names of its run started staying in
        /// agent.log — a name missing after it proves nothing.
        /// </summary>
        public const string NamesCapped = "names_capped";

        /// <summary>The result of a page stop that failed.</summary>
        public const string PageResultFail = "fail";

        /// <summary>
        /// Page names that only an update in progress writes (field data 2026-10-02). Every visit
        /// logs init names such as <c>initialize_NDUPInstallCanceledInOptOut</c> and
        /// <c>initialize_NDUPDownloadInstallPreviousFailureCount</c>, a scan that found nothing
        /// included, and <c>installSucceededNoReboot</c> follows the language check of visits whose
        /// update came later — so "Download"/"Install" in a name proves nothing. Matched as a
        /// case-insensitive substring.
        /// </summary>
        public static readonly string[] ActivityMarkers =
        {
            "commitExpeditionDownloadInstall", // the page commits the download and install
            "downloadSucceeded",
            "installSucceededRebootRequired",
            "DownloadPhase",                   // the progress bars of the download …
            "installPhase",                    // … and of the install
            "downloadInstallFailureHelper",    // a download or install failed
            "rebootCountdown",                 // the page restarts for the update
        };

        // Outcome evidence on the update page (field data 2026-10-03/05). Only the explicit
        // failures count: an update that installed fine can still log downloadInstallFailureHelper
        // and a start timeout after its "restart required" (session shape e4ecd6f8).
        public static readonly string[] SucceededMarkers =
        {
            "installSucceededRebootRequired",               // also the _lcu twin
            "commitExpeditionDownloadInstallAsyncSucceeded",
        };

        public static readonly string[] FailedMarkers =
        {
            "downloadFailedError",
            "installFailedError",
            "commitExpeditionDownloadInstallAsyncFailure",
        };

        public static readonly string[] SkippedMarkers =
        {
            "SkipDownloadInstallButtonClicked",             // someone selected Skip on the page
        };

        // step of windows_update_servicing
        public const string StepInitiating = "initiating";
        public const string StepStateReached = "state_reached";
        public const string StepRebootRequired = "reboot_required";
        public const string StepFailed = "failed";

        /// <summary>The CBS target state of an installed package.</summary>
        public const string InstalledState = "Installed";
    }
}
