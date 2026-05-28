namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Read-only snapshot of the last ESP terminal failure observed by the agent,
    /// captured from <c>ProvisioningStatusTracker.EspFailureDetected</c> args and
    /// forwarded up to <see cref="EnrollmentTerminationHandler"/> via the host
    /// stack. All three fields share the same source event so the consumer can
    /// safely correlate them (e.g. only treat an HRESULT as an app-detection or
    /// app-install failure when <see cref="FailedSubcategory"/> is also "Apps").
    /// <para>
    /// Session 080edee9 follow-up + Codex review (P2/P3): an earlier shape carried
    /// only the HRESULT, which let a non-Apps ESP failure (e.g.
    /// DevicePreparation/* or DeviceSetup/SecurityPolicies) classify still-installing
    /// apps as <c>esp_apps_install_failure</c>. The subcategory + category fields
    /// are now part of the snapshot so the classifier can refuse that misattribution.
    /// </para>
    /// </summary>
    public sealed class EspTerminalFailureSnapshot
    {
        /// <summary>
        /// HRESULT extracted from the failed subcategory's <c>statusText</c>
        /// (lower-case, e.g. <c>0x87d1041c</c>). Null when no parseable HRESULT
        /// was present.
        /// </summary>
        public string ErrorCode { get; }

        /// <summary>
        /// Failed ESP subcategory short name (e.g. <c>Apps</c>,
        /// <c>SecurityPolicies</c>, <c>CertificatesAccountSetup</c>). Null when the
        /// failure is recorded at category level (no specific subcategory).
        /// </summary>
        public string FailedSubcategory { get; }

        /// <summary>
        /// ESP category short name (<c>DevicePreparation</c> | <c>DeviceSetup</c>
        /// | <c>AccountSetup</c>). Null when not available.
        /// </summary>
        public string Category { get; }

        public EspTerminalFailureSnapshot(string errorCode, string failedSubcategory, string category)
        {
            ErrorCode = string.IsNullOrEmpty(errorCode) ? null : errorCode;
            FailedSubcategory = string.IsNullOrEmpty(failedSubcategory) ? null : failedSubcategory;
            Category = string.IsNullOrEmpty(category) ? null : category;
        }

        /// <summary>
        /// True when the failure was emitted against the ESP <c>Apps</c>
        /// subcategory. This is the only failure shape where promoting still-
        /// installing apps with a HRESULT-based classification is correct;
        /// non-Apps failures (security policies, certificate enrolment, etc.)
        /// must fall back to the generic <c>esp_apps_timeout</c> wording because
        /// the HRESULT does not describe per-app outcome.
        /// </summary>
        public bool IsAppsSubcategory =>
            string.Equals(FailedSubcategory, "Apps", System.StringComparison.OrdinalIgnoreCase);
    }
}
