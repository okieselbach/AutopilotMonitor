using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Operator-supplied replacement for one built-in transactional email template
    /// (kind = "welcome" | "farewell"). Stored in the PreviewConfig table, partition
    /// "EmailTemplates". The HTML may contain the <c>{{domainName}}</c> placeholder.
    /// </summary>
    public sealed class EmailTemplateOverride
    {
        public string Kind { get; set; } = string.Empty;
        public string Html { get; set; } = string.Empty;
        public string UpdatedBy { get; set; } = string.Empty;
        public DateTime UpdatedUtc { get; set; }
    }
}
