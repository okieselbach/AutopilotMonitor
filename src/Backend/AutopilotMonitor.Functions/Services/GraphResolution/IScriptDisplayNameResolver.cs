using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Shared.Models.Graph;

namespace AutopilotMonitor.Functions.Services.GraphResolution;

/// <summary>
/// Resolves Intune script (Platform + Remediation) display names for a tenant. Encapsulates
/// the gate (does the SP have the required Graph permission?), the cache (have we seen this
/// script recently?), the Graph fetch (list-full-pull or per-ID fallback), and the negative-
/// cache write for 404s.
/// <para>
/// The value for any unresolved ref is <c>null</c>. Never throws to the caller — Graph
/// timeouts / 429s / 5xx degrade to "no name" so the UI can still render IDs. Cache failures
/// behave the same way.
/// </para>
/// </summary>
public interface IScriptDisplayNameResolver
{
    Task<ScriptDisplayNameLookup> ResolveAsync(
        string tenantId,
        IReadOnlyCollection<ScriptRef> refs,
        CancellationToken ct = default);
}

/// <summary>
/// One lookup's outcome. <see cref="TimedOut"/> is true when a time limit of the resolver ended
/// the lookup before every ref had an answer, so asking again can return more names. A failed
/// Graph call leaves it false: asking again at once would only add to a throttle or an outage.
/// </summary>
public sealed record ScriptDisplayNameLookup(IReadOnlyDictionary<ScriptRef, string?> Names, bool TimedOut);
