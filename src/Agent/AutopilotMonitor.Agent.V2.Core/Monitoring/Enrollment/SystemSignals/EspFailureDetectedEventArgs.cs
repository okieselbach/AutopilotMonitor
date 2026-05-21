using System;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Event payload for <see cref="ProvisioningStatusTracker.EspFailureDetected"/> and the
    /// coordinator-forwarded <see cref="EspAndHelloTracker.EspFailureDetected"/>.
    /// <para>
    /// <see cref="FailureType"/> is the structured ID consumed by the DecisionEngine and the
    /// audit-trail (e.g. <c>Provisioning_DeviceSetup_Apps_Failed</c>). The remaining fields are
    /// populated from registry-derived ESP failures (<see cref="ProvisioningStatusTracker"/>);
    /// event-log-derived failures (<see cref="ShellCoreTracker"/>) only carry
    /// <see cref="FailureType"/> and leave the rest null.
    /// </para>
    /// </summary>
    public sealed class EspFailureDetectedEventArgs : EventArgs
    {
        /// <summary>Structured failure-type identifier (always set; never null/empty).</summary>
        public string FailureType { get; }

        /// <summary>
        /// Windows HRESULT extracted from the failed subcategory's <c>statusText</c>
        /// (e.g. <c>0x87d1041c</c>). Null when no HRESULT pattern matched or when the failure
        /// source is event-log-derived. Lower-cased hex with <c>0x</c> prefix.
        /// </summary>
        public string ErrorCode { get; }

        /// <summary>
        /// Name of the failed subcategory (e.g. <c>Apps</c>, <c>SecurityPolicies</c>,
        /// <c>Certificates</c>). Null when no subcategory could be identified
        /// (category-level failure).
        /// </summary>
        public string FailedSubcategory { get; }

        /// <summary>
        /// ESP category label (<c>DevicePreparation</c>, <c>DeviceSetup</c>,
        /// <c>AccountSetup</c>). Null when not applicable.
        /// </summary>
        public string Category { get; }

        public EspFailureDetectedEventArgs(
            string failureType,
            string errorCode = null,
            string failedSubcategory = null,
            string category = null)
        {
            FailureType = string.IsNullOrEmpty(failureType) ? "unknown" : failureType;
            ErrorCode = string.IsNullOrEmpty(errorCode) ? null : errorCode;
            FailedSubcategory = string.IsNullOrEmpty(failedSubcategory) ? null : failedSubcategory;
            Category = string.IsNullOrEmpty(category) ? null : category;
        }
    }
}
