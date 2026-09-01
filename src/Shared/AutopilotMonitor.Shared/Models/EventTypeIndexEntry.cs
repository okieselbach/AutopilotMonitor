using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// One <c>EventTypeIndex</c> row reduced to the pair the cross-session walkers need:
    /// which session, in which tenant. Both values come straight off the index row
    /// (<c>TenantId</c>/<c>SessionId</c> columns, PartitionKey suffix as fallback), so a
    /// consumer never has to resolve the tenant through a SessionsIndex scan.
    /// </summary>
    public sealed class EventTypeIndexEntry
    {
        public EventTypeIndexEntry(string tenantId, string sessionId)
        {
            TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            SessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
        }

        public string TenantId { get; }
        public string SessionId { get; }
    }
}
