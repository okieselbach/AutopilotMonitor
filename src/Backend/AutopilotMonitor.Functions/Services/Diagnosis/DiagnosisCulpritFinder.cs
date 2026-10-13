using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services.Diagnosis;

/// <summary>
/// Finds what a failed ESP Apps step names as its cause, strongest evidence first: apps that
/// failed (their own error, then the error the agent assigned to an app still installing when the
/// ESP failed), then apps that had not finished (in flight, never started, still tracked, named by
/// the ESP only), plus the AppX log's package candidate and stuck ESP policy providers. An app that
/// later reports installed is no culprit, and only required apps count. App facts come from the
/// session's own app events — the same source as the session page's install list.
/// </summary>
internal static class DiagnosisCulpritFinder
{
    internal const int MaxCulprits = 5;

    private static readonly IReadOnlyList<string> EvidenceRank = DiagnosisCulpritEvidence.All;

    internal static List<DiagnosisCulprit> Find(
        IReadOnlyList<EnrollmentEvent> events, FailureEvidence failure, EspBlockingSets? blocking)
    {
        var apps = FoldApps(events);
        var culprits = new List<DiagnosisCulprit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(DiagnosisCulprit c)
        {
            var key = c.Key ?? c.Name;
            if (seen.Add(c.Kind + "|" + key)) culprits.Add(c);
        }

        if (failure.IsAppsStep)
        {
            foreach (var app in apps.Values
                         .Where(a => a.Failure != null && !a.InstalledAfterFailure && IsRequired(a.Intent))
                         .OrderByDescending(a => MatchesEspError(a, failure.Step.ErrorCode))
                         .ThenBy(a => a.Presumed)
                         .ThenByDescending(a => blocking?.Contains(a.AppId) == true)
                         .ThenByDescending(a => a.Failure!.Sequence))
            {
                var evidence = !app.Presumed
                    ? DiagnosisCulpritEvidence.InstallFailed
                    : string.Equals(app.FailureType, Constants.AppFailureTypes.EspAppsTimeout, StringComparison.OrdinalIgnoreCase)
                        ? DiagnosisCulpritEvidence.PresumedStuck
                        : DiagnosisCulpritEvidence.PresumedFailed;
                Add(AppCulprit(app, evidence, blocking, app.Failure!.EventId));
            }

            var correlation = DiagnosisEspEvidence.LastOf(events, e => e.EventType == Constants.EventTypes.EspAppsFailureCorrelation);
            foreach (var item in EventDataReader.Maps(correlation?.Data, "likelyCauseApps"))
                AddUnfinished(item, DiagnosisCulpritEvidence.InFlight, correlation!.EventId);

            foreach (var starved in events.Where(e => e.EventType == Constants.EventTypes.AppInstallStarved))
                AddUnfinished(starved.Data, DiagnosisCulpritEvidence.NeverStarted, starved.EventId);

            foreach (var item in failure.TrackedAppsNotCompleted)
            {
                var neverStarted = EventDataReader.Bool(item, "everStartedInstalling") == false;
                AddUnfinished(item, neverStarted ? DiagnosisCulpritEvidence.NeverStarted : DiagnosisCulpritEvidence.NotCompleted, failure.Step.EventId);
            }

            foreach (var name in failure.LikelyCulpritNames)
            {
                var app = apps.Values.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
                if (app?.Installed == true) continue;
                Add(app != null
                    ? AppCulprit(app, DiagnosisCulpritEvidence.NamedByEsp, blocking, failure.Step.EventId)
                    : new DiagnosisCulprit { Kind = DiagnosisCulpritKinds.App, Evidence = DiagnosisCulpritEvidence.NamedByEsp, Name = name, EventId = failure.Step.EventId });
            }

            var appx = DiagnosisEspEvidence.LastOf(events, e => e.EventType == Constants.EventTypes.EspAppxFailureAnalysis);
            if (appx != null && string.Equals(EventDataReader.String(appx, "verdict"), "appx_candidate_identified", StringComparison.OrdinalIgnoreCase))
            {
                var package = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(appx, "topCandidatePackage"));
                var packageName = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(appx, "topCandidatePackageName")) ?? package;
                if (packageName != null)
                    Add(new DiagnosisCulprit
                    {
                        Kind = DiagnosisCulpritKinds.AppxPackage,
                        Evidence = DiagnosisCulpritEvidence.AppxLog,
                        Key = package,
                        Name = packageName,
                        ErrorCode = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(appx, "topCandidateHresult")),
                        Confidence = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(appx, "confidence")),
                        EventId = appx.EventId,
                    });
            }
        }

        foreach (var provider in StalledProviders(events))
            Add(provider);

        return culprits
            .Select((c, i) => (c, i))
            .OrderBy(x => Rank(x.c.Evidence))
            .ThenBy(x => x.i)
            .Take(MaxCulprits)
            .Select(x => x.c)
            .ToList();

        void AddUnfinished(IReadOnlyDictionary<string, object>? item, string evidence, string? eventId)
        {
            var appId = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(item, "appId"));
            var name = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(item, "appName")) ?? appId;
            if (name == null) return;
            var app = Lookup(apps, appId, name);
            if (app?.Installed == true) return;
            var culprit = app != null
                ? AppCulprit(app, evidence, blocking, eventId)
                : new DiagnosisCulprit
                {
                    Kind = DiagnosisCulpritKinds.App, Evidence = evidence, Key = appId, Name = name,
                    Intent = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(item, "intent")),
                    Targeted = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(item, "targeted")),
                    EspBlocking = blocking?.Contains(appId) == true ? true : null,
                    EventId = eventId,
                };
            culprit.State = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(item, "state")) ?? culprit.State;
            var progress = EventDataReader.Long(item, "progressPercent");
            if (progress is >= 0 and <= 100) culprit.ProgressPercent = (int)progress.Value;
            if (!IsRequired(culprit.Intent)) return;
            Add(culprit);
        }
    }

    /// <summary>Stuck ESP policy providers from the latest <c>esp_policy_provider_stalled</c>.</summary>
    internal static IEnumerable<DiagnosisCulprit> StalledProviders(IReadOnlyList<EnrollmentEvent> events)
    {
        var stalled = DiagnosisEspEvidence.LastOf(events, e => e.EventType == Constants.EventTypes.EspPolicyProviderStalled);
        foreach (var item in EventDataReader.Maps(stalled?.Data, "stalledProviders"))
        {
            var name = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(item, "name"));
            if (name == null) continue;
            yield return new DiagnosisCulprit
            {
                Kind = DiagnosisCulpritKinds.PolicyProvider,
                Evidence = DiagnosisCulpritEvidence.ProviderStalled,
                Key = name,
                Name = name,
                Scope = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(item, "scope")),
                StalledForMinutes = EventDataReader.Double(item, "stalledForMinutes"),
                EventId = stalled!.EventId,
            };
        }
    }

    private static DiagnosisCulprit AppCulprit(AppTrack app, string evidence, EspBlockingSets? blocking, string? eventId) => new()
    {
        Kind = DiagnosisCulpritKinds.App,
        Evidence = evidence,
        Key = app.AppId,
        Name = app.Name,
        AppType = app.AppType,
        Intent = app.Intent,
        Targeted = app.Targeted,
        State = app.State,
        ErrorCode = app.Failure != null ? app.ErrorCode : null,
        ExitCode = app.Failure != null ? app.ExitCode : null,
        DetectionResult = app.DetectionResult,
        InstallStarts = app.Starts > 0 ? app.Starts : null,
        LastStartedAt = app.LastStartedAt,
        FailedAt = app.Failure?.Timestamp,
        EspBlocking = blocking?.Contains(app.AppId) == true ? true : null,
        EventId = eventId,
    };

    private static int Rank(string evidence)
    {
        for (var i = 0; i < EvidenceRank.Count; i++)
            if (EvidenceRank[i] == evidence) return i;
        return EvidenceRank.Count;
    }

    /// <summary>Required apps only: an Available, uninstall or untargeted assignment never blocks the ESP.</summary>
    private static bool IsRequired(string? intent) =>
        string.IsNullOrEmpty(intent)
        || string.Equals(intent, "Install", StringComparison.OrdinalIgnoreCase)
        || string.Equals(intent, "Unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The app's failure is the one the ESP reports: the same code (hex HRESULT on both sides, or the
    /// same number in hex and decimal), or a detection failure for the ESP's "not detected" HRESULT —
    /// IME reports those without a code of its own.
    /// </summary>
    private static bool MatchesEspError(AppTrack app, string? espCode) =>
        SameCode(app.ErrorCode, espCode)
        || (string.Equals(espCode, Constants.AppFailureTypes.DetectionFailureHResult, StringComparison.OrdinalIgnoreCase)
            && string.Equals(app.DetectionResult, "NotDetected", StringComparison.OrdinalIgnoreCase));

    private static bool SameCode(string? appCode, string? espCode)
    {
        if (string.IsNullOrEmpty(appCode) || string.IsNullOrEmpty(espCode)) return false;
        if (string.Equals(appCode, espCode, StringComparison.OrdinalIgnoreCase)) return true;
        return TryParseCode(appCode!, out var a) && TryParseCode(espCode!, out var b) && a == b;
    }

    private static bool TryParseCode(string code, out uint value)
    {
        var s = code.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out value);
        if (long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var signed)
            && signed >= int.MinValue && signed <= uint.MaxValue)
        {
            value = unchecked((uint)signed);
            return true;
        }
        value = 0;
        return false;
    }

    private static AppTrack? Lookup(Dictionary<string, AppTrack> apps, string? appId, string name) =>
        (appId != null && apps.TryGetValue(appId, out var byId) ? byId : null)
        ?? apps.Values.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>One app's install history in this session, folded from its events in canonical order.</summary>
    private sealed class AppTrack
    {
        public AppTrack(string? appId, string name) { AppId = appId; Name = name; }
        public string? AppId { get; }
        public string Name { get; set; }
        public string? AppType, Intent, Targeted, State, DetectionResult, ErrorCode, ExitCode, FailureType;
        public int Starts;
        public DateTime? LastStartedAt;
        public EnrollmentEvent? Failure;
        public bool Presumed;
        public bool Installed;
        public bool InstalledAfterFailure;
    }

    private static Dictionary<string, AppTrack> FoldApps(IReadOnlyList<EnrollmentEvent> events)
    {
        var apps = new Dictionary<string, AppTrack>(StringComparer.OrdinalIgnoreCase);
        foreach (var evt in events)
        {
            var type = evt.EventType;
            if (type != Constants.EventTypes.AppInstallStart && type != Constants.EventTypes.AppInstallComplete
                && type != Constants.EventTypes.AppInstallFailed && type != Constants.EventTypes.AppStateReconciliation)
                continue;

            var appId = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(evt, "appId"));
            var name = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(evt, "appName"));
            var key = appId ?? name;
            if (key == null) continue;
            if (!apps.TryGetValue(key, out var app))
                apps[key] = app = new AppTrack(appId, name ?? key);
            else if (name != null)
                app.Name = name;

            app.AppType = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(evt, "appType")) ?? app.AppType;
            app.Intent = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(evt, "intent")) ?? app.Intent;
            app.Targeted = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(evt, "targeted")) ?? app.Targeted;
            app.DetectionResult = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(evt, "detectionResult")) ?? app.DetectionResult;

            if (type == Constants.EventTypes.AppInstallStart)
            {
                app.Starts++;
                app.LastStartedAt = evt.Timestamp;
                app.State = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(evt, "state")) ?? "Installing";
            }
            else if (type == Constants.EventTypes.AppInstallComplete)
            {
                app.State = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(evt, "state")) ?? "Installed";
                if (string.Equals(app.State, "Installed", StringComparison.OrdinalIgnoreCase))
                {
                    app.Installed = true;
                    if (app.Failure != null) app.InstalledAfterFailure = true;
                }
            }
            else if (type == Constants.EventTypes.AppInstallFailed)
            {
                var priorState = app.State;
                RecordFailure(app, evt,
                    ErrorCode(EventDataReader.String(evt, "errorCode")) ?? ErrorCode(EventDataReader.String(evt, "hresultFromWin32")),
                    DiagnosisEspEvidence.NonEmpty(EventDataReader.String(evt, "exitCode")));
                app.Presumed = string.Equals(EventDataReader.String(evt, "confidence"), "presumed", StringComparison.OrdinalIgnoreCase);
                app.FailureType = DiagnosisEspEvidence.NonEmpty(EventDataReader.String(evt, "failureType"));
                // The agent promotes an app that was still installing when the ESP failed: it keeps that state.
                if (app.Presumed && priorState != null) app.State = priorState;
            }
            else
            {
                // app_state_reconciliation: the IME registry settled on an outcome the log tracking missed.
                var outcome = EventDataReader.String(evt, "registryOutcome");
                if (string.Equals(outcome, "error", StringComparison.OrdinalIgnoreCase))
                {
                    RecordFailure(app, evt, ErrorCode(EventDataReader.String(evt, "errorCode")), app.ExitCode);
                    app.Presumed = false;
                    app.FailureType = null;
                }
                else if (string.Equals(outcome, "success", StringComparison.OrdinalIgnoreCase))
                {
                    app.Installed = true;
                    app.State = "Installed";
                    if (app.Failure != null) app.InstalledAfterFailure = true;
                }
            }
        }
        return apps;
    }

    /// <summary>An error code worth showing: a zero code (IME sends hresultFromWin32 "0" with detection failures) is none.</summary>
    private static string? ErrorCode(string? raw)
    {
        var code = DiagnosisEspEvidence.NonEmpty(raw);
        return code == null || (TryParseCode(code, out var value) && value == 0) ? null : code;
    }

    private static void RecordFailure(AppTrack app, EnrollmentEvent evt, string? errorCode, string? exitCode)
    {
        app.Failure = evt;
        app.State = "Error";
        app.ErrorCode = errorCode;
        app.ExitCode = exitCode;
        app.Installed = false;
        app.InstalledAfterFailure = false;
    }
}
