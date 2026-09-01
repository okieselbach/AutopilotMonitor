using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Provider-agnostic alert rule for operational events.
    /// Stored as JSON array in AdminConfiguration.OpsAlertRulesJson.
    /// </summary>
    public class OpsAlertRule
    {
        /// <summary>Event type name, e.g. "ConsentFlowFailed".</summary>
        public string EventType { get; set; } = string.Empty;

        /// <summary>Minimum severity that triggers the alert: Info, Warning, Error, Critical.</summary>
        public string MinSeverity { get; set; } = "Error";

        /// <summary>Whether this rule is active.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Ids of the platform notification channels
        /// (<c>AdminConfiguration.GetOpsNotificationChannels</c>) this rule notifies.
        /// <para>
        /// Null or empty means EVERY enabled channel — the pre-routing broadcast behavior, so
        /// rules written before channels existed keep working untouched. An id that no longer
        /// resolves is ignored at dispatch (a deleted channel must not resurrect as "all").
        /// </para>
        /// </summary>
        public List<string>? NotifyChannelIds { get; set; }

        /// <summary>
        /// Send the event's structured payload (the ops event's Details) to this rule's channels,
        /// in addition to the category/event/severity/tenant baseline every alert carries.
        /// <para>
        /// Default FALSE, deliberately: an ops alert is a "something happened, go look" signal and
        /// most payloads are operational noise in a chat. Turn it on for the few events whose
        /// consumer needs the values themselves — e.g. a trial-conversion rule feeding a sales
        /// webhook that must not require a portal round-trip.
        /// </para>
        /// <para>
        /// SECURITY: a payload can carry data the baseline never does (a tenant's domain name, the
        /// administrator contact address). Enabling it on a rule widens what leaves the platform to
        /// that rule's destinations — check what the event records before switching it on.
        /// </para>
        /// </summary>
        public bool IncludePayload { get; set; }
    }
}
