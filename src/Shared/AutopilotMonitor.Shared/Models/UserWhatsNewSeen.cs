using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>The caller's per-channel What's-new seen marks from the UserPresence row.</summary>
    public sealed class UserWhatsNewSeen
    {
        public UserWhatsNewSeen(DateTime? platformUtc = null, DateTime? agentUtc = null)
        {
            PlatformUtc = platformUtc;
            AgentUtc = agentUtc;
        }

        public DateTime? PlatformUtc { get; }
        public DateTime? AgentUtc { get; }
    }
}
