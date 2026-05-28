#nullable enable
using System.Text.Json.Serialization;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Confidence level for an error-code mapping.
    /// </summary>
    public enum ErrorCodeConfidence
    {
        /// <summary>Inferred or rarely seen, may not be accurate in all contexts.</summary>
        Low,

        /// <summary>Community-confirmed (MVP blogs, Q&amp;A forums, consistent field reports).</summary>
        Medium,

        /// <summary>Documented by Microsoft (MS Learn, official docs).</summary>
        High,
    }

    /// <summary>
    /// One Windows / MSI / Intune error-code catalog entry. Mirrors the structure of
    /// <c>Resources/error-codes.json</c> and the TypeScript <c>ErrorCodeEntry</c> in the web app.
    /// </summary>
    public sealed class ErrorCodeEntry
    {
        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("confidence")]
        public ErrorCodeConfidence Confidence { get; set; }

        [JsonPropertyName("source")]
        public string Source { get; set; } = string.Empty;
    }
}
