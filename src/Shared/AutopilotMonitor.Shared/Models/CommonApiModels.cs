using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Marker for every typed HTTP success-response body. The generic
    /// <c>ResponseHelper.OkAsync&lt;T&gt;/CreatedAsync&lt;T&gt;/JsonAsync&lt;T&gt;</c> overloads constrain on it,
    /// which blocks anonymous objects at compile time (anonymous types cannot implement an
    /// interface). Implementers are wire contracts: property declaration order IS the JSON key
    /// order (System.Text.Json serializes in declaration order), so implementers stay flat —
    /// no base classes (derived properties would serialize before base properties) — and are
    /// exported to TypeScript by SharedManifestParityTests.
    /// </summary>
    public interface IApiResponse
    {
    }

    /// <summary>
    /// Canonical mutation acknowledgement: <c>{ "success": ..., "message": ... }</c>.
    /// No property defaults on purpose — every call site sets both, and a default would
    /// add a key the anonymous site never wrote.
    /// </summary>
    public class SuccessMessageResponse : IApiResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = default!;
    }

    /// <summary>Canonical minimal acknowledgement: <c>{ "success": ... }</c>.</summary>
    public class SuccessOnlyResponse : IApiResponse
    {
        public bool Success { get; set; }
    }

    /// <summary>
    /// Marker for every typed HTTP ERROR body (any non-2xx response). The wire prefix is fixed:
    /// the first three keys are <c>error</c> (human-readable), <c>code</c> (machine-readable,
    /// <c>Constants.ApiErrorCodes</c> or a domain code class) and <c>correlationId</c> (the
    /// request's X-Correlation-ID, stamped by the writer — never by the call site). Specialised
    /// implementers may append domain fields after the prefix; TypedResponseGuardTests pins the
    /// prefix order by reflection. <see cref="ApiErrorResponse"/> is the generic implementer.
    /// </summary>
    public interface IApiErrorResponse : IApiResponse
    {
        string Error { get; }
        string Code { get; }
        string CorrelationId { get; set; }
    }

    /// <summary>
    /// The one generic error body: <c>{ error, code, correlationId, hint?, retryAfterSeconds?, operation? }</c>.
    /// Written by <c>ResponseHelper.ErrorAsync</c> (functions) and <c>ApiErrorWriter</c> (middleware).
    /// </summary>
    public class ApiErrorResponse : IApiErrorResponse
    {
        /// <summary>Human-readable message. Never carries stack traces or infrastructure detail.</summary>
        public string Error { get; set; } = default!;
        /// <summary>Machine-readable code — <c>Constants.ApiErrorCodes</c> unless a domain class owns it.</summary>
        public string Code { get; set; } = default!;
        /// <summary>The request's correlation id (also the X-Correlation-ID response header); the handle for backend log lookup.</summary>
        public string CorrelationId { get; set; } = string.Empty;
        /// <summary>Recovery hint for the caller (MCP clients read it into the tool error text); absent when none applies.</summary>
        public string? Hint { get; set; }
        /// <summary>Mirrors the Retry-After header on 429/503; absent otherwise.</summary>
        public int? RetryAfterSeconds { get; set; }
        /// <summary>The failing operation (function name) — 500 bodies for MCP clients only; absent otherwise.</summary>
        public string? Operation { get; set; }
    }

    /// <summary>
    /// Marks a list property whose items are runtime projections of <see cref="ItemType"/>
    /// (a <c>fields=</c> query-parameter projection builds dictionaries with a subset of the
    /// item's wire keys). The property stays <c>IReadOnlyList&lt;object&gt;</c> in C#; the manifest
    /// exporter emits it as <c>Partial&lt;ItemType&gt;[]</c> for TypeScript.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class ProjectedItemsAttribute : Attribute
    {
        public ProjectedItemsAttribute(Type itemType)
        {
            ItemType = itemType;
        }

        public Type ItemType { get; }
    }

    /// <summary>
    /// Opt-in marker for payload types that are part of the HTTP wire contract but are not
    /// themselves response envelopes (e.g. SessionSummary, EnrollmentEvent, RuleResult).
    /// SharedManifestParityTests exports every <see cref="IApiResponse"/> implementer plus
    /// every [WireContract] type (and their transitive closure) to TypeScript.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Enum)]
    public sealed class WireContractAttribute : Attribute
    {
    }
}
