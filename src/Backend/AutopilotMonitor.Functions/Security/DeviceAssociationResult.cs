using System;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Result of looking up a device in the DevPrep "Device association" Graph API
    /// (<c>/beta/deviceManagement/tenantAssociatedDevices</c>).
    ///
    /// Mirrors the resilience contract of <see cref="AutopilotDeviceValidationResult"/>:
    /// transient failures (Graph 5xx, token issues, network) set <see cref="IsTransient"/>=true
    /// and are NOT cached; definitive results (found / not-found) ARE cached.
    /// </summary>
    public class DeviceAssociationResult
    {
        public bool IsValid { get; set; }

        /// <summary>
        /// True when the failure is transient and should be retried (Graph API error, token issue, network).
        /// Transient failures are NOT cached. The caller surfaces 503 Retry-After to the agent.
        /// </summary>
        public bool IsTransient { get; set; }

        public string? SerialNumber { get; set; }

        // ---- Fields populated when a matching tenantAssociatedDevices entry is found ----

        /// <summary>
        /// Association state from the Graph response.
        /// Observed values: "preassociated"; expected to also include "associated", "enrolled".
        /// </summary>
        public string? AssociationState { get; set; }

        /// <summary>
        /// GUID of the DevPrep policy that the device is associated to (or all-zero GUID if not yet assigned).
        /// </summary>
        public string? DevicePreparationPolicyId { get; set; }

        /// <summary>
        /// UPN of the user who pre-associated the device (or null when none).
        /// </summary>
        public string? PreAssociatedByUserPrincipalName { get; set; }

        /// <summary>
        /// UPN the device is assigned to (set later in the association lifecycle, may be null).
        /// </summary>
        public string? AssignedToUserPrincipalName { get; set; }

        public DateTime? PreAssociationDateTime { get; set; }
        public DateTime? AssociationDateTime { get; set; }

        /// <summary>
        /// Intune managed device ID once enrollment has occurred (all-zero GUID otherwise).
        /// </summary>
        public string? ManagedDeviceId { get; set; }

        public string? ErrorMessage { get; set; }

        /// <summary>
        /// True when this result came from the in-memory cache rather than a fresh Graph lookup
        /// (kept for callers that want to log once per real lookup instead of once per request).
        /// </summary>
        public bool ServedFromCache { get; set; }

        /// <summary>
        /// Copy of this result marked as a cache hit, so the shared cached instance is never
        /// mutated by a caller.
        /// </summary>
        internal DeviceAssociationResult AsCacheHit() => new()
        {
            IsValid = IsValid,
            IsTransient = IsTransient,
            SerialNumber = SerialNumber,
            AssociationState = AssociationState,
            DevicePreparationPolicyId = DevicePreparationPolicyId,
            PreAssociatedByUserPrincipalName = PreAssociatedByUserPrincipalName,
            AssignedToUserPrincipalName = AssignedToUserPrincipalName,
            PreAssociationDateTime = PreAssociationDateTime,
            AssociationDateTime = AssociationDateTime,
            ManagedDeviceId = ManagedDeviceId,
            ErrorMessage = ErrorMessage,
            ServedFromCache = true
        };
    }
}
