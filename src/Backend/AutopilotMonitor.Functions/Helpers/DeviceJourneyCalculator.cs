using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// One journey (a single deployment intent for one device): consecutive terminal attempts of a
/// device key, ended by the first terminal success. Transient grouping result — never persisted.
/// </summary>
public sealed class DeviceJourney
{
    /// <summary>Terminal session refs of this journey, ordered by StartedAt ascending.</summary>
    public List<DeviceSessionRef> Attempts { get; } = new();

    /// <summary>True when the journey ended with a terminal success (its last attempt Succeeded).</summary>
    public bool Completed { get; set; }

    /// <summary>The success ref that completed the journey; null while open/abandoned.</summary>
    public DeviceSessionRef? CompletingRef => Completed ? Attempts[Attempts.Count - 1] : null;
}

/// <summary>
/// Deterministic, I/O-free core of the F2 device-history / First-Time-Right feature
/// (insights spec §F2 — JourneyVersion 1). Owns the device-key normalization (trim +
/// case-fold + junk-serial exclusion), the terminal-only chain maintenance (upsert by
/// sessionId, cap 20 most recent) and the journey grouping:
/// <list type="bullet">
/// <item>Terminal set = Succeeded / Failed / Incomplete (production-verified status catalogue).
/// A WhiteGlove <c>Pending</c> row is an OPEN session — WG part 1 + part 2 share ONE session
/// row (second-sweep finding), so journeys never stitch WG parts and a WG device still waiting
/// for its user session leaves the journey open, never failed.</item>
/// <item>A journey ends with the first terminal SUCCESS; the next session starts a new journey
/// (redeployment). A gap of &gt; 30 days since the previous terminal session also starts a new
/// journey (device shelved/repurposed) — production gaps: median 4.9 h, 92 % &lt; 7 d.</item>
/// <item>Attempt count = terminal sessions in the journey; Incomplete and Failed are
/// non-successful attempts.</item>
/// </list>
/// </summary>
public static class DeviceJourneyCalculator
{
    /// <summary>Bump on any grouping-semantics change so aggregates never mix definitions (truthfulness rule 8).</summary>
    public const int CurrentVersion = 1;

    /// <summary>Chain cap: the 20 most recent terminal sessions (spec §F2 — journey counts are chain-scoped).</summary>
    internal const int MaxChainLength = 20;

    /// <summary>New-journey boundary without a prior success. Constant in v1, not a setting (spec §F2).</summary>
    internal const int JourneyGapDays = 30;

    /// <summary>
    /// Placeholder serials that are not device identities (spec §F2 + audit Q5: "Unknown" is the
    /// agent's WMI-failure sentinel). Compared against the NORMALIZED (trimmed, lower-cased)
    /// serial; anything shorter than 4 characters is junk regardless.
    /// </summary>
    private static readonly HashSet<string> JunkSerials = new(StringComparer.Ordinal)
    {
        "system serial number",
        "to be filled by o.e.m.",
        "default string",
        "0",
        "none",
        "invalid",
        "unknown",
    };

    /// <summary>
    /// Normalizes a raw serial to the device-key form (trim + lower-case invariant) or returns
    /// null for junk/placeholder serials — excluded devices never enter FTR and never get a
    /// history chain (the exclusion is disclosed in the daily aggregate instead).
    /// </summary>
    public static string? NormalizeSerial(string? rawSerial)
    {
        if (string.IsNullOrWhiteSpace(rawSerial)) return null;
        var normalized = rawSerial.Trim().ToLowerInvariant();
        if (normalized.Length < 4) return null;
        if (JunkSerials.Contains(normalized)) return null;
        return normalized;
    }

    /// <summary>F2 terminal set — everything else (InProgress, Pending, Stalled, AwaitingUser, Unknown) is an open session.</summary>
    public static bool IsTerminal(SessionStatus status)
        => status == SessionStatus.Succeeded || status == SessionStatus.Failed || status == SessionStatus.Incomplete;

    /// <summary>
    /// Builds the chain ref for a terminal session; null for non-terminal statuses (defense in
    /// depth — callers gate on <see cref="IsTerminal"/> already). Duration is the session's
    /// authoritative <c>DurationSeconds</c> verbatim, never CompletedAt − StartedAt.
    /// </summary>
    public static DeviceSessionRef? BuildSessionRef(SessionSummary session)
    {
        if (!IsTerminal(session.Status)) return null;
        return new DeviceSessionRef
        {
            SessionId = session.SessionId,
            StartedAt = session.StartedAt,
            CompletedAt = session.CompletedAt,
            Status = session.Status.ToString(),
            EnrollmentType = string.IsNullOrEmpty(session.EnrollmentType) ? "v1" : session.EnrollmentType,
            IsPreProvisioned = session.IsPreProvisioned,
            DurationSeconds = session.DurationSeconds,
            AdminMarked = !string.IsNullOrEmpty(session.AdminMarkedAction),
        };
    }

    /// <summary>
    /// Merges refs into an existing chain: upsert by sessionId (a re-terminal reclassification,
    /// e.g. Incomplete → Succeeded, replaces the entry), non-terminal refs dropped defensively,
    /// ordered by StartedAt (tiebreak sessionId, ordinal) and capped to the
    /// <see cref="MaxChainLength"/> MOST RECENT entries. Pure — inputs are not mutated.
    /// </summary>
    public static List<DeviceSessionRef> MergeChain(
        IReadOnlyList<DeviceSessionRef>? existing, IEnumerable<DeviceSessionRef> updates)
    {
        var bySessionId = new Dictionary<string, DeviceSessionRef>(StringComparer.Ordinal);
        if (existing != null)
        {
            foreach (var reference in existing)
            {
                if (!string.IsNullOrEmpty(reference.SessionId))
                    bySessionId[reference.SessionId] = reference;
            }
        }
        foreach (var update in updates)
        {
            if (update != null && !string.IsNullOrEmpty(update.SessionId))
                bySessionId[update.SessionId] = update;
        }

        return SortAndCap(bySessionId.Values.Where(IsTerminalRef));
    }

    /// <summary>
    /// Drops refs whose sessions were deleted (tombstoned or cascaded away) and re-normalizes
    /// the chain. An empty result means the device has no observable history left — the caller
    /// deletes the row rather than keeping an empty claim.
    /// </summary>
    public static List<DeviceSessionRef> RemoveSessionRefs(
        IReadOnlyList<DeviceSessionRef> chain, ISet<string> deletedSessionIds)
        => SortAndCap(chain.Where(r => IsTerminalRef(r) && !deletedSessionIds.Contains(r.SessionId)));

    private static List<DeviceSessionRef> SortAndCap(IEnumerable<DeviceSessionRef> refs)
    {
        var sorted = refs
            .OrderBy(r => r.StartedAt)
            .ThenBy(r => r.SessionId, StringComparer.Ordinal)
            .ToList();
        if (sorted.Count > MaxChainLength)
            sorted.RemoveRange(0, sorted.Count - MaxChainLength);
        return sorted;
    }

    /// <summary>
    /// A chain entry parses as terminal. Chains only ever receive terminal refs, but the JSON
    /// column is data, not a proof — parse defensively (wire-type lesson: mirror the canonical
    /// reader, never assume).
    /// </summary>
    private static bool IsTerminalRef(DeviceSessionRef reference)
        => Enum.TryParse<SessionStatus>(reference.Status, ignoreCase: true, out var status) && IsTerminal(status);

    /// <summary>
    /// Groups an ordered terminal chain into journeys. The chain is re-sorted defensively;
    /// non-terminal refs are ignored. See the class doc for the grouping rules.
    /// </summary>
    public static List<DeviceJourney> GroupJourneys(IReadOnlyList<DeviceSessionRef> chain)
    {
        var ordered = SortAndCap(chain.Where(IsTerminalRef));
        var journeys = new List<DeviceJourney>();
        DeviceJourney? current = null;
        DeviceSessionRef? previous = null;

        foreach (var reference in ordered)
        {
            var startNew =
                current == null
                || current.Completed
                // Gap rule: > 30 days between the previous attempt's terminal moment and this
                // start. CompletedAt is the honest "last seen terminal" timestamp; StartedAt is
                // the defensive fallback (production: 0 terminal sessions without CompletedAt).
                || (previous != null && reference.StartedAt - (previous.CompletedAt ?? previous.StartedAt) > TimeSpan.FromDays(JourneyGapDays));

            if (startNew)
            {
                current = new DeviceJourney();
                journeys.Add(current);
            }

            current!.Attempts.Add(reference);
            if (string.Equals(reference.Status, nameof(SessionStatus.Succeeded), StringComparison.OrdinalIgnoreCase))
                current.Completed = true;

            previous = reference;
        }

        return journeys;
    }

    /// <summary>
    /// Derived counts for the history row: journeys in the retained chain and the attempt count
    /// of the LAST journey (open or completed) — the "Attempt N" the session banner renders.
    /// </summary>
    public static (int JourneyCount, int CurrentJourneyAttempts) Derive(IReadOnlyList<DeviceSessionRef> chain)
    {
        var journeys = GroupJourneys(chain);
        if (journeys.Count == 0) return (0, 0);
        return (journeys.Count, journeys[journeys.Count - 1].Attempts.Count);
    }

    /// <summary>
    /// Attempt number of one session within its journey — the "Attempt N for this device" the
    /// session-detail banner renders, computed server-side so no consumer re-derives journey
    /// semantics. Terminal sessions sit in the chain and take their real position. A LIVE
    /// (non-terminal) session is not a chain ref yet, so its position is computed by inserting
    /// a virtual non-successful attempt at the session's StartedAt — the redeploy rule (a prior
    /// completed journey starts a new one) and the 30-day gap rule then place it exactly like
    /// the real ref would land once terminal. Null when the chain gives no basis (empty).
    /// </summary>
    public static int? ComputeAttemptNumber(
        IReadOnlyList<DeviceSessionRef> chain, string sessionId, DateTime sessionStartedAt)
    {
        var effectiveChain = chain;
        if (!chain.Any(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal)))
        {
            if (chain.Count == 0) return null;
            // Virtual ref: status is a NON-successful terminal placeholder purely for position
            // math — it never persists and never marks the journey completed.
            var virtualRef = new DeviceSessionRef
            {
                SessionId = sessionId,
                StartedAt = sessionStartedAt,
                Status = nameof(SessionStatus.Failed),
            };
            effectiveChain = MergeChain(chain, new[] { virtualRef });
        }

        foreach (var journey in GroupJourneys(effectiveChain))
        {
            for (var i = 0; i < journey.Attempts.Count; i++)
            {
                if (string.Equals(journey.Attempts[i].SessionId, sessionId, StringComparison.Ordinal))
                    return i + 1;
            }
        }
        return null; // session fell off the 20-cap when merged — no honest position claim
    }
}
