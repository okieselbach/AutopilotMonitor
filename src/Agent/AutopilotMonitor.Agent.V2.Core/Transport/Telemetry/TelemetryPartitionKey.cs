#nullable enable
namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// The one composition of a session's Azure-Table partition key. The three emitters stamp
    /// it on every <see cref="TelemetryItem"/>; the spool and the upload orchestrator compare
    /// against it to tell the current session's items from leftovers of a previous session id.
    /// A drift between producer and comparer would silently drop or retain the wrong items.
    /// </summary>
    public static class TelemetryPartitionKey
    {
        public static string ForSession(string tenantId, string sessionId) => $"{tenantId}_{sessionId}";
    }
}
