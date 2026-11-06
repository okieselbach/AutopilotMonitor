using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// One portal visit of a person, read from the UserActivity sign-in history (one row per auth/me call). The
    /// reader leaves out service principals (<c>app:</c> keys) and rows without any identity, so every entry is a
    /// person.
    /// </summary>
    public class PortalVisit
    {
        /// <summary>Tenant of the token, lower-cased: a delegated (MSP) admin counts in their home tenant.</summary>
        public string TenantId { get; set; } = string.Empty;

        /// <summary>The person: the Entra object id, else the lower-cased UPN.</summary>
        public string PersonKey { get; set; } = string.Empty;

        /// <summary>When the visit was recorded (UTC).</summary>
        public DateTime At { get; set; }
    }
}
