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
        /// <summary>Row id of a general-feedback entry; absent on the other kinds.</summary>
        public string? FeedbackId { get; set; }
        /// <summary>Reply address a general-feedback sender entered; absent when none was given.</summary>
        public string? ContactEmail { get; set; }
    }

    /// <summary>Body of POST feedback.</summary>
    public class FeedbackRequest : IApiRequest
    {
        /// <summary>1..5 for a submission; omitted on a dismissal.</summary>
        public int? Rating { get; set; }
        public string? Comment { get; set; }
        public bool Dismissed { get; set; }
    }

    /// <summary>Body of POST feedback/general: free-text feedback from a member of a tenant to the Autopilot Monitor team.</summary>
    public class GeneralFeedbackRequest : IApiRequest
    {
        /// <summary>The feedback text, 1 to 4096 characters after trimming.</summary>
        public string Message { get; set; } = default!;
        /// <summary>Optional reply address, for senders whose sign-in name is not a mailbox.</summary>
        public string? ContactEmail { get; set; }
    }
}
