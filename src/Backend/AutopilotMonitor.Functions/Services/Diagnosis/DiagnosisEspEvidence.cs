using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services.Diagnosis;

/// <summary>The terminal failure of a session as the agent reported it, plus what the ESP named alongside it.</summary>
internal sealed class FailureEvidence
{
    public FailureEvidence(DiagnosisFailedStep step, IReadOnlyList<string> likelyCulpritNames, IReadOnlyList<IReadOnlyDictionary<string, object>> trackedAppsNotCompleted)
    {
        Step = step;
        LikelyCulpritNames = likelyCulpritNames;
        TrackedAppsNotCompleted = trackedAppsNotCompleted;
    }

    public DiagnosisFailedStep Step { get; }

    /// <summary>The ESP's own culprit names (<c>likelyCulpritApps</c>, never-started apps first, at most three).</summary>
    public IReadOnlyList<string> LikelyCulpritNames { get; }

    /// <summary><c>trackedAppsNotCompleted</c> of the settle window: the apps the ESP still tracked as unfinished.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object>> TrackedAppsNotCompleted { get; }

    /// <summary>The failure names an ESP phase, step or failure type — an ESP failure, not an agent-level one.</summary>
    public bool IsEspFailure =>
        !string.IsNullOrEmpty(Step.Category) || !string.IsNullOrEmpty(Step.Subcategory) || !string.IsNullOrEmpty(Step.FailureType);

    /// <summary>The Apps step failed (by name, or by the registry failure type <c>Provisioning_{Category}_Apps_Failed</c>).</summary>
    public bool IsAppsStep =>
        string.Equals(Step.Subcategory, "Apps", StringComparison.OrdinalIgnoreCase)
        || (Step.FailureType?.IndexOf("_Apps_", StringComparison.OrdinalIgnoreCase) ?? -1) >= 0;
}

/// <summary>
/// Reads the ESP side of a session from its events: the terminal failure with its step, the last
/// reported state of each ESP phase, and the ESP profile settings.
/// </summary>
internal static class DiagnosisEspEvidence
{
    private static readonly string[] PhaseOrder = { "DevicePreparation", "DeviceSetup", "AccountSetup" };

    /// <summary>
    /// The terminal failure: the last <c>enrollment_failed</c> that is not the agent's own timeout,
    /// else the last Shell-Core <c>esp_failure</c> (the ingest fallback path). Missing step details are
    /// filled from the latest <c>esp_failure_advisory</c>, then <c>esp_failure_settle_started</c>, at
    /// or before it — the expired-observation variant of <c>enrollment_failed</c> carries none.
    /// Null when the session has no failure event.
    /// </summary>
    internal static FailureEvidence? FindFailure(IReadOnlyList<EnrollmentEvent> events)
    {
        var terminal = LastOf(events, e => e.EventType == Constants.EventTypes.EnrollmentFailed
                                           && !string.Equals(EventDataReader.String(e, "failureType"), "agent_timeout", StringComparison.OrdinalIgnoreCase))
                       ?? LastOf(events, e => e.EventType == Constants.EventTypes.EspFailure);
        if (terminal == null) return null;

        var advisory = LastOf(events, e => e.EventType == Constants.EventTypes.EspFailureAdvisory && e.Sequence <= terminal.Sequence);
        var settle = LastOf(events, e => e.EventType == Constants.EventTypes.EspFailureSettleStarted && e.Sequence <= terminal.Sequence);

        string? Field(string key) =>
            NonEmpty(EventDataReader.String(terminal, key))
            ?? NonEmpty(EventDataReader.String(advisory, key))
            ?? NonEmpty(EventDataReader.String(settle, key));

        var step = new DiagnosisFailedStep
        {
            Category = Field("category"),
            Subcategory = Field("failedSubcategory"),
            ErrorCode = Field("errorCode")?.ToLowerInvariant(),
            FailureType = Field("failureType"),
            Reason = NonEmpty(EventDataReader.String(terminal, "reason")),
            At = terminal.Timestamp,
            EventId = terminal.EventId,
        };
        // The settle window reports a category-level failure as "category-level"; that is no step name.
        if (string.Equals(step.Subcategory, "category-level", StringComparison.OrdinalIgnoreCase))
            step.Subcategory = null;
        if (string.Equals(step.Category, "unknown", StringComparison.OrdinalIgnoreCase))
            step.Category = null;

        var names = SplitList(Field("likelyCulpritApps"));
        var tracked = EventDataReader.Maps(settle?.Data, "trackedAppsNotCompleted");
        return new FailureEvidence(step, names, tracked);
    }

    /// <summary>
    /// The soft failure the user continued past: the latest <c>esp_failure_advisory</c>, else null.
    /// </summary>
    internal static FailureEvidence? FindAdvisory(IReadOnlyList<EnrollmentEvent> events)
    {
        var advisory = LastOf(events, e => e.EventType == Constants.EventTypes.EspFailureAdvisory);
        if (advisory == null) return null;
        var step = new DiagnosisFailedStep
        {
            Category = NonEmpty(EventDataReader.String(advisory, "category")),
            Subcategory = NonEmpty(EventDataReader.String(advisory, "failedSubcategory")),
            ErrorCode = NonEmpty(EventDataReader.String(advisory, "errorCode"))?.ToLowerInvariant(),
            FailureType = NonEmpty(EventDataReader.String(advisory, "failureType")),
            Reason = NonEmpty(EventDataReader.String(advisory, "reason")),
            At = advisory.Timestamp,
            EventId = advisory.EventId,
        };
        return new FailureEvidence(step, SplitList(EventDataReader.String(advisory, "likelyCulpritApps")), Array.Empty<IReadOnlyDictionary<string, object>>());
    }

    /// <summary>
    /// The last reported state of each ESP phase from <c>esp_provisioning_status</c>, phases in ESP
    /// order. A failed step carries the HRESULT the agent reported for it. Null without any report.
    /// </summary>
    internal static List<DiagnosisEspCategory>? Phases(IReadOnlyList<EnrollmentEvent> events)
    {
        var latest = new Dictionary<string, EnrollmentEvent>(StringComparer.OrdinalIgnoreCase);
        var failedCodes = new Dictionary<string, (HashSet<string> Steps, string Code)>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in events)
        {
            if (evt.EventType != Constants.EventTypes.EspProvisioningStatus) continue;
            var category = NonEmpty(EventDataReader.String(evt, "category"));
            if (category == null) continue;
            latest[category] = evt;

            var code = NonEmpty(EventDataReader.String(evt, "failedSubcategoryErrorCode"));
            var failed = SplitList(EventDataReader.String(evt, "failedSubcategories"));
            if (code != null && failed.Count > 0)
                failedCodes[category] = (new HashSet<string>(failed, StringComparer.OrdinalIgnoreCase), code.ToLowerInvariant());
        }
        if (latest.Count == 0) return null;

        var result = new List<DiagnosisEspCategory>();
        foreach (var category in latest.Keys.OrderBy(PhaseRank).ThenBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            var data = latest[category].Data;
            var phase = new DiagnosisEspCategory { Category = category, State = PhaseState(EventDataReader.String(data, "categorySucceeded")) };
            var subcategories = EventDataReader.Map(data, "subcategories");
            if (subcategories != null)
            {
                foreach (var entry in subcategories)
                {
                    var detail = EventDataReader.AsMap(entry.Value);
                    var state = NonEmpty(EventDataReader.String(detail, "state")) ?? "unknown";
                    string? errorCode = null;
                    if (string.Equals(state, "failed", StringComparison.OrdinalIgnoreCase)
                        && failedCodes.TryGetValue(category, out var codes) && codes.Steps.Contains(entry.Key))
                        errorCode = codes.Code;
                    phase.Subcategories.Add(new DiagnosisEspSubcategory
                    {
                        Name = entry.Key,
                        State = state,
                        StatusText = NonEmpty(EventDataReader.String(detail, "statusText")),
                        ErrorCode = errorCode,
                    });
                }
            }
            result.Add(phase);
        }
        return result;
    }

    /// <summary>
    /// The ESP profile from <c>esp_config_detected</c>: each setting from the latest emission that
    /// reports it (the agent suppresses unchanged repeats). Null when the device never reported it.
    /// </summary>
    internal static DiagnosisEspConfig? Config(IReadOnlyList<EnrollmentEvent> events, EspBlockingSets? blocking)
    {
        DiagnosisEspConfig? config = null;
        foreach (var evt in events)
        {
            if (evt.EventType != Constants.EventTypes.EspConfigDetected) continue;
            config ??= new DiagnosisEspConfig();
            config.ContinueAnyway = EventDataReader.Bool(evt, "allowContinueAnyway") ?? config.ContinueAnyway;
            config.TryAgain = EventDataReader.Bool(evt, "allowTryAgain") ?? config.TryAgain;
            config.Reset = EventDataReader.Bool(evt, "allowReset") ?? config.Reset;
            config.SkipUserEsp = EventDataReader.Bool(evt, "skipUserStatusPage") ?? config.SkipUserEsp;
            config.SkipDeviceEsp = EventDataReader.Bool(evt, "skipDeviceStatusPage") ?? config.SkipDeviceEsp;
            var timeout = EventDataReader.Long(evt.Data, "syncFailureTimeoutMinutes");
            if (timeout is > 0 and <= int.MaxValue) config.TimeoutMinutes = (int)timeout.Value;
        }
        if (config != null && blocking != null)
            config.BlockingAppCount = blocking.ListedCount;
        return config;
    }

    internal static EnrollmentEvent? LastOf(IReadOnlyList<EnrollmentEvent> events, Func<EnrollmentEvent, bool> match)
    {
        for (var i = events.Count - 1; i >= 0; i--)
            if (match(events[i])) return events[i];
        return null;
    }

    /// <summary>A comma list as trimmed, non-empty items (the agent joins with "," or ", ").</summary>
    internal static List<string> SplitList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value!.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    internal static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    private static int PhaseRank(string category)
    {
        var i = Array.FindIndex(PhaseOrder, p => string.Equals(p, category, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? PhaseOrder.Length : i;
    }

    // categorySucceeded is C# bool.ToString on the agent ("True"/"False") or "in_progress".
    private static string PhaseState(string? categorySucceeded) => categorySucceeded?.Trim().ToLowerInvariant() switch
    {
        "true" => "succeeded",
        "false" => "failed",
        _ => "in_progress",
    };
}
