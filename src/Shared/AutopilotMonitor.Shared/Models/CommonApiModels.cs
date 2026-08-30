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
