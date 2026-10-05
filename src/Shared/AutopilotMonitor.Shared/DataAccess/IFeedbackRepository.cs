using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AutopilotMonitor.Shared.DataAccess
{
    /// <summary>
    /// User-feedback storage. Backed by the dedicated <c>Feedback</c> Azure-Table which is
    /// intentionally NOT in any tenant-offboarding wipe list — feedback (especially from
    /// offboarded tenants) is exactly the data we want to keep for product learning.
    /// <para>
    /// Three partition layouts share the table:
    /// </para>
    /// <list type="bullet">
    ///   <item><c>PK="InApp"</c>, <c>RK=upn</c> — in-app star rating + comment from the
    ///   feedback bubble. One row per user: the latest answer plus the dismissal count; a rating
    ///   is final and never replaced.</item>
    ///   <item><c>PK="Offboarding"</c>, <c>RK=historyRowKey</c> — free-form "what could we
    ///   improve" comment captured during the offboarding drain-barrier countdown. One row
    ///   per offboarding attempt (matches the <c>OffboardingHistory</c> row).</item>
    ///   <item><c>PK="General"</c>, <c>RK={invertedTicks}_{id}</c> — free-text feedback sent
    ///   from the portal's help menu. One row per submission, newest first; never replaced.</item>
    /// </list>
    /// </summary>
    public interface IFeedbackRepository
    {
        // ── In-App feedback (star rating + comment) ─────────────────────────────

        /// <summary>Returns the in-app feedback entry for the given UPN, or null if the user has not interacted.</summary>
        Task<FeedbackEntry?> GetInAppFeedbackAsync(string upn);

        /// <summary>
        /// Applies one answer to the person's row: reads it, lets <paramref name="decide"/> compute the new row
        /// (null = nothing changes) and writes it only if nobody wrote in between; a conflict decides again on what
        /// was stored. Returns the written row, or null when nothing was written.
        /// </summary>
        Task<FeedbackEntry?> UpdateInAppFeedbackAsync(string upn, Func<FeedbackEntry?, FeedbackEntry?> decide);

        // ── Offboarding feedback (one per offboarding history row) ──────────────

        /// <summary>Returns the offboarding feedback for the given history-row-key, or null if none submitted.</summary>
        Task<FeedbackEntry?> GetOffboardingFeedbackAsync(string historyRowKey);

        /// <summary>Upserts the offboarding feedback entry. Sets <see cref="FeedbackEntry.Type"/> to <c>"Offboarding"</c>.</summary>
        Task SaveOffboardingFeedbackAsync(FeedbackEntry entry);

        // ── General feedback (free text, one row per submission) ────────────────

        /// <summary>
        /// Inserts one general-feedback submission. Sets <see cref="FeedbackEntry.Type"/> to <c>"General"</c>
        /// and stamps <see cref="FeedbackEntry.FeedbackId"/> (the RowKey) from <see cref="FeedbackEntry.InteractedAt"/>.
        /// </summary>
        Task SaveGeneralFeedbackAsync(FeedbackEntry entry);

        /// <summary>Counts the general-feedback submissions of one user (case-insensitive UPN) at or after <paramref name="sinceUtc"/>.</summary>
        Task<int> CountGeneralFeedbackSinceAsync(string upn, DateTime sinceUtc);

        // ── Reports / dashboard ─────────────────────────────────────────────────

        /// <summary>Returns ALL feedback entries (every partition). Used by the Global-Admin reports page.</summary>
        Task<List<FeedbackEntry>> GetAllAsync();
    }

    /// <summary>
    /// Single shape for all partitions; nullable fields disambiguate the kinds.
    /// </summary>
    public class FeedbackEntry
    {
        /// <summary><c>"InApp"</c>, <c>"Offboarding"</c> or <c>"General"</c>. Matches the storage PartitionKey.</summary>
        public string Type { get; set; } = FeedbackEntryType.InApp;

        public string Upn { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? Comment { get; set; }
        public DateTime? InteractedAt { get; set; }

        // ── In-App-only ─────────────────────────────────────────────────────────
        public int? Rating { get; set; }
        public bool Dismissed { get; set; }
        public bool Submitted { get; set; }

        /// <summary>How often the person dismissed the rating prompt (0 to 2). Rows written before the count existed read as 1 when dismissed.</summary>
        public int DismissCount { get; set; }

        // ── Offboarding-only ────────────────────────────────────────────────────

        /// <summary>RowKey of the matching <c>OffboardingHistory</c> entry. Only set for <c>Type="Offboarding"</c>.</summary>
        public string? HistoryRowKey { get; set; }

        /// <summary>Snapshot of the tenant's domain at submit time — captured once so the row keeps a readable label after the tenant's configuration is gone. Set for <c>Type="Offboarding"</c> and <c>Type="General"</c>.</summary>
        public string? DomainName { get; set; }

        // ── General-only ────────────────────────────────────────────────────────

        /// <summary>RowKey of a general-feedback row (<c>{invertedTicks}_{id}</c>). Only set for <c>Type="General"</c>.</summary>
        public string? FeedbackId { get; set; }

        /// <summary>Optional reply address the sender entered. Only set for <c>Type="General"</c>.</summary>
        public string? ContactEmail { get; set; }
    }

    /// <summary>Discriminator values for <see cref="FeedbackEntry.Type"/> + storage PartitionKey.</summary>
    public static class FeedbackEntryType
    {
        public const string InApp = "InApp";
        public const string Offboarding = "Offboarding";
        public const string General = "General";
    }
}
