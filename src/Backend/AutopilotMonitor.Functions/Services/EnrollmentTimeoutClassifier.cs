using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Pure classification of a session that the maintenance sweep is about to terminalize
    /// (docs/design/enrollment-status-reclassification.md). Instead of hard-coding every
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
        /// operators see the same evidence the classifier used.
        /// </summary>
        public readonly record struct EspProvisioningRollup(
            bool DeviceSetupAllSucceeded,
            int AccountSetupSucceededCount,
            int AccountSetupTotal,
            bool AccountSetupAllSucceeded,
            bool HasExplicitFailure,
            bool HasTerminalComplete,
            bool HasAgentEmergencyBreak);

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

            foreach (var evt in events)
            {
                var type = evt.EventType ?? string.Empty;

                if (Eq(type, "enrollment_failed") || Eq(type, "esp_failure"))
                    hasFailure = true;
                else if (Eq(type, "enrollment_complete") || Eq(type, "whiteglove_complete"))
                    hasComplete = true;
                else if (Eq(type, "agent_emergency_break"))
                    hasEmergencyBreak = true;

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
                    if (n > acctBestN) { acctBestN = n; acctBestM = total; }
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
                HasAgentEmergencyBreak: hasEmergencyBreak);
        }

        /// <summary>
        /// Decide the honest terminal (or non-terminal) state for a timed-out session. See the
        /// decision table in the design note. Returns the target status and a human-readable
        /// reason for the Sessions row / operator UI.
        /// </summary>
        /// <param name="rollup">Facts from <see cref="ExtractRollup"/>.</param>
        /// <param name="startedAtUtc">Session start (grace is measured from here).</param>
        /// <param name="nowUtc">Sweep timestamp.</param>
        /// <param name="graceHours">Grace window before AwaitingUser graduates to Incomplete.</param>
        public static (SessionStatus Status, string Reason) ClassifyTimedOutSession(
            EspProvisioningRollup rollup, DateTime startedAtUtc, DateTime nowUtc, int graceHours)
        {
            // 1. An explicit terminal failure event is a real failure (defensive — such a session
            //    would normally already be terminal via ingest and not reach the sweep).
            if (rollup.HasExplicitFailure)
                return (SessionStatus.Failed, "Enrollment reported an explicit failure before timeout");

            // 2. Account Setup fully succeeded (or a terminal completion event) → reconcile to success.
            if (rollup.AccountSetupAllSucceeded || rollup.HasTerminalComplete)
                return (SessionStatus.Succeeded,
                    "Reconciled at timeout: Account Setup completed (all subcategories succeeded / enrollment_complete observed)");

            // 3. The agent's absolute-age emergency break fired — the agent has cleaned up and exited, so
            //    nothing more will ever arrive for this session. Terminalize NOW (skip the AwaitingUser grace):
            //    it did not complete (2) and did not explicitly fail (1), so the honest verdict is Incomplete.
            if (rollup.HasAgentEmergencyBreak)
                return (SessionStatus.Incomplete,
                    "Agent emergency break fired (absolute session-age cap) — agent gone without completion");

            // 4. Device Setup fully provisioned (device is AADJ + MDM-enrolled), user phase pending.
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

                return (SessionStatus.Incomplete,
                    $"No completion signal within {graceHours}h grace after Device Setup completed " +
                    $"(last Account Setup {rollup.AccountSetupSucceededCount}/{rollup.AccountSetupTotal})");
            }

            // 5. Silent before Device Setup completed, with no explicit failure → unknown, not a failure.
            return (SessionStatus.Incomplete,
                "No Device Setup completion or explicit failure signal observed before timeout");
        }

        private static bool Eq(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
