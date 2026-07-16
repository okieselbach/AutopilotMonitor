using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Pure classification of a session that the maintenance sweep is about to terminalize
    /// (tasks/enrollment-status-reclassification.md). Instead of hard-coding every
    /// silent session to <see cref="SessionStatus.Failed"/>, we read the ESP subcategory
    /// rollup the agent already emits and decide the honest target state.
    ///
    /// Why parse the <c>esp_provisioning_status</c> <b>Message</b> rather than the Data
    /// object: Windows never sets the category-level <c>categorySucceeded</c> boolean (it is
    /// perpetually <c>"in_progress"</c> even at DeviceSetup 4/4 and AccountSetup 5/5), so the
    /// authoritative rollup is the per-category "N of M subcategories completed" line — which
    /// the agent authors in one place (ProvisioningStatusTracker) and which was validated at
    /// scale against real crcins.com data. The 30s "all subcategories succeeded but
    /// categorySucceeded was not confirmed … treating as complete" fallback line is treated
    /// as all-succeeded for its category.
    /// </summary>
    public static class EnrollmentTimeoutClassifier
    {
        /// <summary>ESP category names as they appear in the provisioning-status message.</summary>
        private const string DeviceSetup = "DeviceSetup";
        private const string AccountSetup = "AccountSetup";

        /// <summary>Fallback agent absolute session-age cap (AgentConfiguration.AbsoluteMaxSessionHours) when a tenant hasn't overridden it.</summary>
        public const int DefaultAbsoluteMaxSessionHours = 48;
        /// <summary>
        /// Small margin added on top of the agent's absolute cap before a silent session graduates to
        /// Incomplete. A completion can only ever arrive ≤ the cap (the agent self-destructs at the cap and
        /// sends nothing after), so this does NOT need to wait hours for stragglers — it only has to (a) span
        /// one 2h maintenance-sweep cycle so a boundary session is reliably picked up on the next tick, and
        /// (b) let a single in-flight event land / absorb minor session.created↔StartedAt clock skew. Kept
        /// small on purpose: the reconcile path heals any prematurely-set Incomplete back to Succeeded, so a
        /// tight buffer costs at most a brief flicker, never a wrong terminal state.
        /// </summary>
        public const int DefaultGraceBufferHours = 3;

        /// <summary>
        /// Resolve the effective backend grace window. The grace MUST never be shorter than the agent's
        /// absolute session-age cap (<c>AbsoluteMaxSessionHours</c>, default 48h) — until that cap fires the
        /// agent may legitimately still be enrolling, so terminalizing to Incomplete earlier would race a
        /// real completion. And because the agent's 48h emergency break is SILENT to the backend (it writes a
        /// local marker, cleans up and exits without a terminal event), anything still silent past cap+buffer
        /// is provably dead. So: effective grace = max(agentAbsoluteCap + buffer, tenant override).
        /// A tenant override of 0/null means "auto-derive".
        /// </summary>
        public static int ResolveGraceHours(int? configuredGraceHours, int? absoluteMaxSessionHours, int bufferHours = DefaultGraceBufferHours)
        {
            var absMax = absoluteMaxSessionHours.GetValueOrDefault(DefaultAbsoluteMaxSessionHours);
            // The agent does NOT yet honor a per-tenant AbsoluteMaxSessionHours override (wiring it
            // down to the agent config response is a follow-up — see TenantConfiguration.AbsoluteMaxSessionHours),
            // so the agent always runs to at least the real runtime default (48h). The silence-based
            // grace floor is only safe if it assumes the agent could still be alive up to that real cap:
            // a lower/unset override must therefore only ever RAISE the assumed cap, never drag the floor
            // below the agent's actual runtime — which would terminalize a still-running session as
            // Incomplete before its emergency break even fires. Clamp accordingly (also covers 0/negative).
            absMax = Math.Max(absMax, DefaultAbsoluteMaxSessionHours);
            var floor = absMax + Math.Max(0, bufferHours);
            var configured = configuredGraceHours.GetValueOrDefault(0);
            return Math.Max(floor, configured);
        }

        // "ESP provisioning status: AccountSetup — 5 of 5 subcategories completed"
        private static readonly Regex RollupRegex = new(
            @"ESP provisioning status:\s*(?<cat>DeviceSetup|AccountSetup)\s*[—-]\s*(?<n>\d+)\s+of\s+(?<m>\d+)\s+subcategories completed",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // "ESP provisioning status: DeviceSetup — all 4 subcategories succeeded but
        //  categorySucceeded was not confirmed by Windows — treating as complete (fallback after 30s)"
        private static readonly Regex FallbackCompleteRegex = new(
            @"ESP provisioning status:\s*(?<cat>DeviceSetup|AccountSetup)\s*[—-]\s*all\s+(?<m>\d+)\s+subcategories succeeded.*treating as complete",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Distilled ESP lifecycle facts extracted from a session's event stream — the inputs
        /// to <see cref="ClassifyTimedOutSession"/>. Also embedded into the failure snapshot so
        /// operators see the same evidence the classifier used. Besides the ESP registry rollup
        /// this carries the user-presence evidence (desktop arrival + positive Hello terminal —
        /// the agent's Classic completion prerequisites) and the RealmJoin gate state, so the
        /// classifier can tell "user was provably there" apart from "awaiting user" (session
        /// 294ab5b4).
        /// </summary>
        public readonly record struct EspProvisioningRollup(
            bool DeviceSetupAllSucceeded,
            int AccountSetupSucceededCount,
            int AccountSetupTotal,
            bool AccountSetupAllSucceeded,
            bool HasExplicitFailure,
            bool HasTerminalComplete,
            bool HasAgentEmergencyBreak,
            bool DesktopArrived,
            bool HelloResolved,
            bool HelloPolicyDisabled,
            bool SkipUserEsp,
            bool RealmJoinDetected,
            bool RealmJoinResolved);

        /// <summary>
        /// Walk a session's events and distill the ESP rollup. Tolerant of missing/empty input
        /// (returns an all-false rollup). Order-independent — takes the strongest observation
        /// per category (highest AccountSetup completion count, any DeviceSetup all-succeeded).
        /// </summary>
        public static EspProvisioningRollup ExtractRollup(IReadOnlyList<EnrollmentEvent>? events)
        {
            if (events == null || events.Count == 0)
                return default;

            bool deviceAll = false;
            int acctBestN = 0, acctBestM = 0;
            bool acctFallbackAll = false;
            bool hasFailure = false, hasComplete = false, hasEmergencyBreak = false;
            bool desktopArrived = false, helloResolved = false;
            bool realmJoinDetected = false, realmJoinResolved = false;
            bool sawHelloPolicyEnabled = false, sawHelloPolicyDisabled = false;
            bool sawSkipUserTrue = false, sawSkipUserFalse = false;

            foreach (var evt in events)
            {
                var type = evt.EventType ?? string.Empty;

                if (Eq(type, "enrollment_failed") || Eq(type, "esp_failure"))
                {
                    // The agent's max-lifetime watchdog emits enrollment_failed with
                    // failureType=agent_timeout — that is "the agent gave up waiting", not an
                    // enrollment failure verdict. Counting it as an explicit failure would send
                    // every timed-out-but-actually-provisioned session through rule 1 straight to
                    // Failed, defeating the honest classification this class exists for
                    // (misclassification audit 2026-07-16, tenant a53e67ec cluster).
                    if (!Eq(TryGetDataString(evt, "failureType") ?? string.Empty, "agent_timeout"))
                        hasFailure = true;
                }
                else if (Eq(type, "enrollment_complete") || Eq(type, "whiteglove_complete"))
                    hasComplete = true;
                else if (Eq(type, "agent_emergency_break"))
                    hasEmergencyBreak = true;
                else if (Eq(type, "desktop_arrived"))
                    desktopArrived = true;
                // Positive Hello terminals only (provisioned / skipped) — these are what raises
                // the agent's HelloResolved completion fact. _failed / _blocked / _timeout leave
                // the agent still waiting and must not count as "user finished setup".
                else if (Eq(type, "hello_provisioning_completed") || Eq(type, "hello_skipped"))
                    helloResolved = true;
                else if (Eq(type, "realmjoin_detected"))
                    realmJoinDetected = true;
                // Either terminal opens the agent-side RealmJoin gate: phase 110 or the 60-min
                // hard timeout (both dual-emitted to the timeline by the agent/engine).
                else if (Eq(type, "realmjoin_resolved") || Eq(type, "realmjoin_timeout"))
                    realmJoinResolved = true;
                // Policy facts for the Hello-disabled / User-ESP-skipped completion mirror.
                // Contradicting observations are resolved pessimistically below (both-seen →
                // treat as enabled/required, i.e. keep demanding the Hello terminal).
                else if (Eq(type, "hello_policy_detected"))
                {
                    var raw = TryGetDataString(evt, "helloEnabled");
                    if (bool.TryParse(raw, out var enabled))
                    {
                        if (enabled) sawHelloPolicyEnabled = true;
                        else sawHelloPolicyDisabled = true;
                    }
                }
                else if (Eq(type, "esp_config_detected"))
                {
                    var raw = TryGetDataString(evt, "skipUserStatusPage");
                    if (bool.TryParse(raw, out var skip))
                    {
                        if (skip) sawSkipUserTrue = true;
                        else sawSkipUserFalse = true;
                    }
                }

                var msg = evt.Message;
                if (string.IsNullOrEmpty(msg)) continue;

                var fb = FallbackCompleteRegex.Match(msg);
                if (fb.Success)
                {
                    if (Eq(fb.Groups["cat"].Value, DeviceSetup)) deviceAll = true;
                    else if (Eq(fb.Groups["cat"].Value, AccountSetup)) acctFallbackAll = true;
                    continue;
                }

                var m = RollupRegex.Match(msg);
                if (!m.Success) continue;

                var cat = m.Groups["cat"].Value;
                var n = int.Parse(m.Groups["n"].Value);
                var total = int.Parse(m.Groups["m"].Value);

                if (Eq(cat, DeviceSetup))
                {
                    if (total > 0 && n >= total) deviceAll = true;
                }
                else if (Eq(cat, AccountSetup))
                {
                    // Keep the strongest AccountSetup observation (highest completion count).
                    // A "0 of M" observation must still record the total — dropping it entirely
                    // made the Incomplete reason read "0/0" although a rollup WAS observed
                    // (misclassification audit 2026-07-16, session 08ddbeec).
                    if (n > acctBestN) { acctBestN = n; acctBestM = total; }
                    else if (acctBestM == 0 && total > 0) { acctBestM = total; }
                }
            }

            var acctAll = acctFallbackAll || (acctBestM > 0 && acctBestN >= acctBestM);
            return new EspProvisioningRollup(
                DeviceSetupAllSucceeded: deviceAll,
                AccountSetupSucceededCount: acctBestN,
                AccountSetupTotal: acctBestM,
                AccountSetupAllSucceeded: acctAll,
                HasExplicitFailure: hasFailure,
                HasTerminalComplete: hasComplete,
                HasAgentEmergencyBreak: hasEmergencyBreak,
                DesktopArrived: desktopArrived,
                HelloResolved: helloResolved,
                HelloPolicyDisabled: sawHelloPolicyDisabled && !sawHelloPolicyEnabled,
                SkipUserEsp: sawSkipUserTrue && !sawSkipUserFalse,
                RealmJoinDetected: realmJoinDetected,
                RealmJoinResolved: realmJoinResolved);
        }

        /// <summary>
        /// Decide the honest terminal (or non-terminal) state for a timed-out session. See the
        /// decision table in the design note. Returns the target status and a human-readable
        /// reason for the Sessions row / operator UI.
        /// </summary>
        /// <param name="rollup">Facts from <see cref="ExtractRollup"/>.</param>
        /// <param name="startedAtUtc">Session start (grace is measured from here).</param>
        /// <param name="nowUtc">Sweep timestamp — also the moment the platform declares the verdict.</param>
        /// <param name="graceHours">Grace window before AwaitingUser graduates to Incomplete.</param>
        /// <param name="lastEventAtUtc">
        /// Last event the agent reported (last backend contact). Used only to make the two
        /// Succeeded-reconcile reasons transparent about the silence: without it operators (and
        /// customers) cannot tell "user finished and powered the device off" apart from "the
        /// mechanism declared success too early" — session efbc17ff. Falls back to
        /// <paramref name="startedAtUtc"/> when unknown (same fallback the stalled-marker uses).
        /// </param>
        public static (SessionStatus Status, string Reason) ClassifyTimedOutSession(
            EspProvisioningRollup rollup, DateTime startedAtUtc, DateTime nowUtc, int graceHours,
            DateTime? lastEventAtUtc = null)
        {
            // 1. An explicit terminal failure event is a real failure (defensive — such a session
            //    would normally already be terminal via ingest and not reach the sweep).
            if (rollup.HasExplicitFailure)
                return (SessionStatus.Failed, "Enrollment reported an explicit failure before timeout");

            // 2. Account Setup fully succeeded (or a terminal completion event) → reconcile to success.
            if (rollup.AccountSetupAllSucceeded || rollup.HasTerminalComplete)
                return (SessionStatus.Succeeded, AppendReconcileTiming(
                    "Reconciled at timeout: Account Setup completed (all subcategories succeeded / enrollment_complete observed)",
                    startedAtUtc, nowUtc, lastEventAtUtc));

            // 3. The agent's absolute-age emergency break fired — the agent has cleaned up and exited, so
            //    nothing more will ever arrive for this session. Terminalize NOW (skip the AwaitingUser grace):
            //    it did not complete (2) and did not explicitly fail (1), so the honest verdict is Incomplete.
            if (rollup.HasAgentEmergencyBreak)
                return (SessionStatus.Incomplete,
                    "Agent emergency break fired (absolute session-age cap) — agent gone without completion");

            // 4. The user demonstrably finished setup: a real-user desktop was observed AND
            //    Windows Hello reached a positive terminal (provisioned or skipped). Those are
            //    exactly the agent's Classic completion prerequisites — the only thing that can
            //    still block enrollment_complete on the device is the RealmJoin completion gate,
            //    and that self-releases via a 60-min hard timeout. A session still silent by the
            //    time this sweep runs is therefore past every agent-side wait: the enrollment
            //    itself succeeded and only the final completion report never left the device
            //    (shutdown / process kill / egress cut — session 294ab5b4). Labeling this
            //    "AwaitingUser" would be factually wrong (the user was provably there), so
            //    reconcile to Succeeded with the honest reason. Note desktop arrival ALONE
            //    remains explicitly rejected as a completion signal (design doc) — it fires
            //    while the user phase is still running; the Hello terminal is what proves the
            //    user finished.
            //
            //    Hello-disabled mirror: when the tenant runs with Windows Hello disabled AND
            //    the User ESP skipped, no Hello terminal can ever exist — the agent's own
            //    Hello-disabled fast-path (HandleDesktopArrivedV1) completes on desktop arrival
            //    in exactly that configuration (HelloPolicyEnabled==false + SkipUserEsp==true).
            //    Hello disabled WITHOUT SkipUserEsp intentionally does NOT qualify: there the
            //    agent's strong post-AccountSetup gate (session 08c99638) requires the
            //    AccountSetup rollup, which rule 2 already covers when it reached all-succeeded.
            var helloSatisfied = rollup.HelloResolved
                || (rollup.HelloPolicyDisabled && rollup.SkipUserEsp);
            if (rollup.DesktopArrived && helloSatisfied)
            {
                var evidence = rollup.HelloResolved
                    ? "desktop + Windows Hello"
                    : "desktop; User ESP skipped, Windows Hello disabled";
                var detail = rollup.RealmJoinDetected && !rollup.RealmJoinResolved
                    ? "RealmJoin deployment never reported completion before the agent went silent"
                    : "agent went silent before reporting completion";
                return (SessionStatus.Succeeded, AppendReconcileTiming(
                    $"Reconciled at timeout: user completed setup ({evidence}) — {detail}",
                    startedAtUtc, nowUtc, lastEventAtUtc));
            }

            // 5. Device Setup fully provisioned (device is AADJ + MDM-enrolled), user phase pending.
            if (rollup.DeviceSetupAllSucceeded)
            {
                var elapsedHours = (nowUtc - startedAtUtc).TotalHours;
                if (elapsedHours < graceHours)
                {
                    var acct = rollup.AccountSetupTotal > 0
                        ? $" (Account Setup {rollup.AccountSetupSucceededCount}/{rollup.AccountSetupTotal})"
                        : " (Account Setup not yet started)";
                    return (SessionStatus.AwaitingUser,
                        $"Device Setup completed; awaiting user / Account Setup phase — agent silent, within {graceHours}h grace{acct}");
                }

                // "0/0" would suggest a parsed rollup that never existed — when no Account Setup
                // rollup was ever observed, say so instead (misclassification audit 2026-07-16).
                var acctDetail = rollup.AccountSetupTotal > 0
                    ? $"last Account Setup {rollup.AccountSetupSucceededCount}/{rollup.AccountSetupTotal}"
                    : "Account Setup progress never observed";
                return (SessionStatus.Incomplete,
                    $"No completion signal within {graceHours}h grace after Device Setup completed ({acctDetail})");
            }

            // 6. Silent before Device Setup completed, with no explicit failure → unknown, not a failure.
            return (SessionStatus.Incomplete,
                "No Device Setup completion or explicit failure signal observed before timeout");
        }

        /// <summary>
        /// Append the silence-transparency clause to a Succeeded-reconcile reason. Names the last
        /// agent contact, how long the session was silent, and the exact moment the platform
        /// declared the verdict (<paramref name="nowUtc"/> = the sweep timestamp). This is the one
        /// field that survives the reconcile hygiene (which wipes FailureReason on a Succeeded
        /// write), so the numbers must live here to reach the badge / MCP / exports — see
        /// session efbc17ff. Anchors to <paramref name="lastEventAtUtc"/>, falling back to
        /// <paramref name="startedAtUtc"/> when the last-contact time is unknown.
        /// </summary>
        private static string AppendReconcileTiming(
            string reason, DateTime startedAtUtc, DateTime nowUtc, DateTime? lastEventAtUtc)
        {
            var lastSeen = lastEventAtUtc ?? startedAtUtc;
            var silence = HumanizeDuration(nowUtc - lastSeen);
            return $"{reason}. Agent last reported {lastSeen.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC; " +
                   $"silent ~{silence} before the platform declared this success at " +
                   $"{nowUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC.";
        }

        /// <summary>Compact human duration ("5h 6m", "45m", "2d 3h"). Negative/zero → "0m".</summary>
        private static string HumanizeDuration(TimeSpan d)
        {
            if (d <= TimeSpan.Zero) return "0m";
            int days = d.Days, hours = d.Hours, minutes = d.Minutes;
            if (days > 0) return $"{days}d {hours}h";
            if (hours > 0) return $"{hours}h {minutes}m";
            return $"{minutes}m";
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static string? TryGetDataString(EnrollmentEvent evt, string key)
        {
            if (evt?.Data == null) return null;
            if (!evt.Data.TryGetValue(key, out var raw) || raw == null) return null;
            return raw.ToString();
        }
    }
}
