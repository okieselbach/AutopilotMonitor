using System.Text.RegularExpressions;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services.Diagnosis;

/// <summary>
/// The verdict of one session (<see cref="DiagnosisHeadline"/>) from its row and events. The outcome
/// is the status; the cause comes from the verdict path (stamped, or derived for older rows), and
/// for an explicit failure from the ESP evidence in the events. Rule findings are no input: the
/// verdict never follows a rule's confidence. Pure — the diagnosis endpoint and its scenario tests
/// call it directly.
/// </summary>
internal static class DiagnosisHeadlineBuilder
{
    private const int MaxWaitingApps = 10;

    // "Superseded by session {id}: …" (RegisterSessionFunction, LegacyReclassificationService).
    private static readonly Regex SupersededBy = new(
        @"^Superseded by session ([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\b",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    private const string AdminPreemptionReason = "Session failed by administrator";

    /// <param name="session">The session row.</param>
    /// <param name="events">The session's events in canonical (Sequence) order.</param>
    internal static DiagnosisHeadline Build(SessionSummary session, IReadOnlyList<EnrollmentEvent> events)
    {
        var (path, derived) = VerdictPathDerivation.Derive(session);
        var outcome = OutcomeOf(session.Status);
        var blocking = EspBlockingSets.FromLatest(events);

        var headline = new DiagnosisHeadline
        {
            Outcome = outcome,
            VerdictPath = path,
            ReasonText = DiagnosisEspEvidence.NonEmpty(session.Status == SessionStatus.Succeeded ? session.ReconcileReason : session.FailureReason),
            At = VerdictTime(session),
            Esp = DiagnosisEspEvidence.Phases(events),
            EspConfig = DiagnosisEspEvidence.Config(events, blocking),
        };

        var cause = CauseFromPath(path, session);
        if (cause == null)
        {
            // Explicit failure: the ESP evidence decides between an app, a step and the agent itself.
            var failure = DiagnosisEspEvidence.FindFailure(events);
            cause = ExplicitFailureCause(failure, events, blocking, headline);
        }
        else if (cause == DiagnosisCauses.SoftFailure)
        {
            var advisory = DiagnosisEspEvidence.FindAdvisory(events);
            if (advisory != null)
            {
                headline.FailedStep = advisory.Step;
                headline.Culprits = NullIfEmpty(DiagnosisCulpritFinder.Find(events, advisory, blocking));
            }
        }
        headline.Cause = cause == DiagnosisCauses.Unknown ? StatusFallback(session.Status) : cause;

        var origin = VerdictPaths.Origin(path);
        if (origin == VerdictPaths.OriginRule)
            headline.RuleId = DiagnosisEspEvidence.NonEmpty(Detail(path, origin));
        if (headline.Cause == DiagnosisCauses.Superseded)
        {
            var match = SupersededBy.Match(session.FailureReason ?? string.Empty);
            if (match.Success) headline.SupersededBySessionId = match.Groups[1].Value.ToLowerInvariant();
        }

        if (outcome is DiagnosisOutcomes.Stalled or DiagnosisOutcomes.AwaitingUser or DiagnosisOutcomes.Incomplete
            or DiagnosisOutcomes.InProgress or DiagnosisOutcomes.Pending)
            headline.Waiting = NullIfEmpty(Waiting(events));

        headline.Caveats = NullIfEmpty(Caveats(events, derived, blocking));
        return headline;
    }

    internal static string OutcomeOf(SessionStatus status) => status switch
    {
        SessionStatus.Failed => DiagnosisOutcomes.Failed,
        SessionStatus.Stalled => DiagnosisOutcomes.Stalled,
        SessionStatus.AwaitingUser => DiagnosisOutcomes.AwaitingUser,
        SessionStatus.Incomplete => DiagnosisOutcomes.Incomplete,
        SessionStatus.Succeeded => DiagnosisOutcomes.Succeeded,
        SessionStatus.InProgress => DiagnosisOutcomes.InProgress,
        SessionStatus.Pending => DiagnosisOutcomes.Pending,
        _ => DiagnosisOutcomes.Unknown,
    };

    /// <summary>
    /// The cause a verdict path states on its own; null for an explicit failure, whose cause the
    /// ESP evidence decides (<c>agent:failed</c>, the ESP fallback, classifier rule r1 of any origin).
    /// </summary>
    internal static string? CauseFromPath(string path, SessionSummary session)
    {
        switch (path)
        {
            case VerdictPaths.AgentComplete:
            case VerdictPaths.AgentGatherComplete:
                return session.EspSoftFailure ? DiagnosisCauses.SoftFailure : DiagnosisCauses.Completed;
            case VerdictPaths.AgentCompleteSoft:
                return DiagnosisCauses.SoftFailure;
            case VerdictPaths.AgentFailed:
            case VerdictPaths.AgentEspFailureFallback:
                return null;
            case VerdictPaths.AgentWhiteGlovePending:
                return DiagnosisCauses.WhiteGloveSealed;
            case VerdictPaths.AgentStallProbe:
                return DiagnosisCauses.NoProgress;
            case VerdictPaths.SweepStalled:
                return DiagnosisCauses.AgentSilent;
            case VerdictPaths.ManualFailed:
            case VerdictPaths.ManualSucceeded:
                return DiagnosisCauses.AdminMarked;
            case VerdictPaths.RegisterNew:
            case VerdictPaths.RegisterWhiteGloveResume:
            case VerdictPaths.AgentWhiteGloveResumed:
            case VerdictPaths.AgentStallHeal:
                return DiagnosisCauses.None;
        }

        var origin = VerdictPaths.Origin(path);
        if (origin == VerdictPaths.OriginRule)
            return DiagnosisCauses.RuleKo;

        // The detail half is shared by the stamped paths and their legacy derivations
        // (register:/retro:/legacy:superseded, ingest:/sweep:/legacy:wg_awaiting, sweep:/retro:sd_reconcile).
        switch (Detail(path, origin))
        {
            case "superseded":
                return DiagnosisCauses.Superseded;
            case "wg_awaiting":
            case ClassifierRules.R1bWhiteGloveAwaiting:
            case ClassifierRules.R5DeviceSetupAwaiting:
                return DiagnosisCauses.AwaitingUser;
            case "sd_reconcile":
            case ClassifierRules.R1bWhiteGloveSucceeded:
            case ClassifierRules.R1cSelfDeploying:
            case ClassifierRules.R2AccountSetupComplete:
            case ClassifierRules.R4DesktopHello:
            case ClassifierRules.R5DesktopAssumed:
                return DiagnosisCauses.Reconciled;
            case ClassifierRules.R1ExplicitFailure:
                return null;
            case ClassifierRules.R3EmergencyBreak:
                return DiagnosisCauses.AgentGaveUp;
            case ClassifierRules.R5DeviceSetupIncomplete:
            case ClassifierRules.R6Fallthrough:
                return origin == VerdictPaths.OriginMaxLifetime || origin == VerdictPaths.OriginStoppedWaiting
                    ? DiagnosisCauses.AgentGaveUp
                    : DiagnosisCauses.SweepTimeout;
            default:
                return DiagnosisCauses.Unknown;
        }
    }

    private static string ExplicitFailureCause(
        FailureEvidence? failure, IReadOnlyList<EnrollmentEvent> events, EspBlockingSets? blocking, DiagnosisHeadline headline)
    {
        if (failure == null)
            return DiagnosisCauses.Unknown;
        if (failure.Step.Reason?.StartsWith(AdminPreemptionReason, StringComparison.OrdinalIgnoreCase) == true)
            return DiagnosisCauses.AdminMarked;

        headline.FailedStep = failure.Step;
        if (!failure.IsEspFailure)
            return DiagnosisCauses.AgentFailure;

        var culprits = DiagnosisCulpritFinder.Find(events, failure, blocking);
        headline.Culprits = NullIfEmpty(culprits);

        if (failure.Step.FailureType?.StartsWith("WhiteGlove_", StringComparison.OrdinalIgnoreCase) == true)
            return DiagnosisCauses.WhiteGloveFailure;
        if (!failure.IsAppsStep)
            return DiagnosisCauses.EspStepFailure;
        if (culprits.Any(c => c.Evidence is DiagnosisCulpritEvidence.InstallFailed or DiagnosisCulpritEvidence.PresumedFailed))
            return DiagnosisCauses.EspAppFailure;
        return culprits.Any(c => c.Kind == DiagnosisCulpritKinds.App)
            ? DiagnosisCauses.EspAppTimeout
            : DiagnosisCauses.EspStepFailure;
    }

    /// <summary>A path the cause map does not know still says something through the status.</summary>
    private static string StatusFallback(SessionStatus status) => status switch
    {
        SessionStatus.InProgress => DiagnosisCauses.None,
        SessionStatus.Pending => DiagnosisCauses.WhiteGloveSealed,
        _ => DiagnosisCauses.Unknown,
    };

    private static DateTime? VerdictTime(SessionSummary session) => session.Status switch
    {
        SessionStatus.Succeeded or SessionStatus.Failed or SessionStatus.Incomplete => session.CompletedAt ?? session.LastEventAt,
        SessionStatus.Stalled => session.StalledAt ?? session.LastEventAt,
        SessionStatus.AwaitingUser or SessionStatus.Pending => session.CompletedAt ?? session.LastEventAt,
        _ => null,
    };

    /// <summary>
    /// What an unfinished enrollment still waited for: the agent's own missing completion prerequisites
    /// (it knows the scenario, so a self-deploying device never "waits for Hello"), the apps not yet
    /// installed per the latest app summary, and stuck ESP policy providers.
    /// </summary>
    private static List<DiagnosisWaitItem> Waiting(IReadOnlyList<EnrollmentEvent> events)
    {
        var items = new List<DiagnosisWaitItem>();
        var waiting = DiagnosisEspEvidence.LastOf(events, e => e.EventType == Constants.EventTypes.CompletionWaiting);
        foreach (var name in DiagnosisEspEvidence.SplitList(EventDataReader.String(waiting, "missingPrerequisites")))
            items.Add(new DiagnosisWaitItem { Kind = DiagnosisWaitKinds.Prerequisite, Name = name });

        var summary = DiagnosisEspEvidence.LastOf(events, e => e.EventType == Constants.EventTypes.AppTrackingSummary);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, state) in new[]
                 {
                     ("installingNames", "installing"), ("downloadingNames", "downloading"),
                     ("likelyStuckNames", "likely_stuck"), ("pendingNames", "pending"),
                 })
        {
            foreach (var name in EventDataReader.StringList(summary?.Data, key) ?? new List<string>())
            {
                if (seen.Count >= MaxWaitingApps) break;
                if (seen.Add(name))
                    items.Add(new DiagnosisWaitItem { Kind = DiagnosisWaitKinds.App, Name = name, State = state });
            }
        }

        foreach (var provider in DiagnosisCulpritFinder.StalledProviders(events))
            items.Add(new DiagnosisWaitItem { Kind = DiagnosisWaitKinds.PolicyProvider, Name = provider.Name });
        return items;
    }

    private static List<string> Caveats(IReadOnlyList<EnrollmentEvent> events, bool derived, EspBlockingSets? blocking)
    {
        var caveats = new List<string>();
        if (events.Any(e => e.EventType == Constants.EventTypes.AgentLateStart))
            caveats.Add(DiagnosisCaveats.AgentStartedLate);
        if (events.Any(e => e.EventType == Constants.EventTypes.PreviousCrashDetected
                            || (e.EventType == Constants.EventTypes.AgentStarted
                                && EventDataReader.String(e, "previousExitType") is "exception_crash" or "hard_kill")))
            caveats.Add(DiagnosisCaveats.AgentCrashed);
        if (derived)
            caveats.Add(DiagnosisCaveats.VerdictDerived);
        if (blocking?.IsTruncated == true)
            caveats.Add(DiagnosisCaveats.BlockingListTruncated);
        return caveats;
    }

    private static string Detail(string path, string origin) =>
        path.Length > origin.Length + 1 ? path.Substring(origin.Length + 1) : string.Empty;

    private static List<T>? NullIfEmpty<T>(List<T> list) => list.Count == 0 ? null : list;
}
