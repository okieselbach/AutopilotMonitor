using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    // Declaration order == wire order.

    /// <summary>Response of GET feedback/eligibility: whether the caller should be shown the feedback prompt.</summary>
    public class FeedbackEligibilityResponse : IApiResponse
    {
        public bool Eligible { get; set; }
    }

    /// <summary>Response of GET feedback/all (Global Admin dashboard): every stored feedback entry.</summary>
    public class FeedbackListResponse : IApiResponse
    {
        public IReadOnlyList<FeedbackEntryWire> Feedback { get; set; } = default!;
    }

    /// <summary>One feedback interaction as rendered on the Global Admin dashboard.</summary>
    public class FeedbackEntryWire
    {
        public string? Type { get; set; }
        public string? Upn { get; set; }
        public string? TenantId { get; set; }
        public string? DisplayName { get; set; }
        /// <summary>Absent on dismissals.</summary>
        public int? Rating { get; set; }
        /// <summary>Absent on dismissals.</summary>
        public string? Comment { get; set; }
        public bool Dismissed { get; set; }
        public bool Submitted { get; set; }
        /// <summary>ISO-8601 round-trip string; absent when never stamped.</summary>
        public string? InteractedAt { get; set; }
        public string? HistoryRowKey { get; set; }
        public string? DomainName { get; set; }
    }
}
