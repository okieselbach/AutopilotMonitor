using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Response of GET /api/progress/sessions/lookup — resolves at most ONE session from the
    /// serial/device-name search term (Progress Portal knowledge-proof lookup).
    /// </summary>
    // Declaration order == wire order.
    public class ProgressLookupSessionResponse : IApiResponse
    {
        public bool Success { get; set; }

        /// <summary>True when a session matched the search term.</summary>
        public bool Found { get; set; }

        /// <summary>The matched session; null (key omitted on the wire) when nothing matched.</summary>
        public SessionSummary? Session { get; set; }
    }

    /// <summary>
    /// Response of GET /api/progress/sessions/{sessionId}/events — the session's event stream
    /// after the serial knowledge proof passed.
    /// </summary>
    // Declaration order == wire order.
    public class ProgressGetSessionEventsResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string SessionId { get; set; } = default!;
        public int Count { get; set; }
        public IReadOnlyList<EnrollmentEvent> Events { get; set; } = default!;
    }
}
