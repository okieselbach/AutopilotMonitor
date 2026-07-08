using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Services
{
    /// <summary>
    /// Builds a compact JSON snapshot of "what we last knew about the session" at the
    /// moment the maintenance watchdog times it out. Hybrid User-Driven completion-gap
    /// fix (2026-05-01 trigger: session e58bcfdb-3e68-4f23-a3c2-437429ca9e78). Today's
    /// 5h-timeout failure reason is just the literal string "Session timed out after 5
    /// hours" — that gives operators no insight into where the session was stuck. This
    /// builder walks the session's events and extracts the canonical lifecycle anchors
    /// (last ESP phase, desktop arrival, Hello policy, AAD-join state, network state,
    /// reboot observation) plus a list of MISSING signals so the snapshot answers
    /// "why did this not complete?" at a glance.
    /// <para>
    /// The shape is intentionally flat and small (≤ ~1 KB JSON typical) so it fits in a
    /// single Sessions-table column without strangling Storage costs. Schema is versioned
    /// so the Web UI can render forward-compatibly.
    /// </para>
    /// </summary>
    public static class FailureSnapshotBuilder
    {
        // v2 (2026-07-08): added the ESP DeviceSetup/AccountSetup subcategory rollup fields
        // (deviceSetupAllSucceeded / accountSetup*) that the timeout reclassification uses —
        // see docs/design/enrollment-status-reclassification.md.
        public const int CurrentSchemaVersion = 2;

        // Canonical signals we expect to have seen by the time a healthy session completes.
        // Missing entries are surfaced in the snapshot under "missingSignals". Some entries
        // are conceptual slots that admit multiple concrete event types (see special-case
        // branches in Build): "hello_terminal" is satisfied by any Hello terminal event,
        // and "aad_user_join" by either the Part 1 (aad_user_joined_observed — was
        // aad_user_joined_late before the 2026-05-04 rename, still emitted by V1 agents and
        // present in historical data) or Part 2 (user_aad_signin_complete) real-user event.
        private static readonly string[] CanonicalCompletionSignals =
        {
            "esp_phase_changed",
            "esp_exiting",
            "desktop_arrived",
            "hello_policy_detected",
            "hello_provisioning_completed", // OR _failed / _blocked / _skipped — emitted as "hello_terminal" slot
            "aad_user_joined_observed",     // OR aad_user_joined_late (legacy) / user_aad_signin_complete — "aad_user_join" slot
            "enrollment_complete",
        };

        /// <summary>
        /// Build a snapshot from a session's chronological event list. Returns
        /// <c>null</c> when the input is empty (caller should write nothing rather than
        /// the literal "{}"). All inputs are tolerated — missing/malformed event data is
        /// recorded as "unknown" rather than causing the maintenance pass to throw.
        /// </summary>
        public static string? Build(IReadOnlyList<EnrollmentEvent>? events, DateTime nowUtc)
        {
            if (events == null || events.Count == 0)
                return null;

            // Events arrive sorted by Sequence (ascending) per ISessionRepository contract.
            // Defensive copy + secondary sort by Timestamp keeps the snapshot stable even
            // if the caller passes an unsorted list.
            var ordered = events.OrderBy(e => e.Timestamp).ToList();

            var seenTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var evt in ordered) seenTypes.Add(evt.EventType ?? "");

            // ---- Last ESP phase (from esp_phase_changed events) ------------------------
            string? lastEspPhase = null;
            DateTime? lastEspPhaseAt = null;
            foreach (var evt in ordered)
            {
                if (string.Equals(evt.EventType, "esp_phase_changed", StringComparison.OrdinalIgnoreCase))
                {
                    lastEspPhase = TryGetDataString(evt, "espPhase") ?? lastEspPhase;
                    lastEspPhaseAt = evt.Timestamp;
                }
            }

            // ---- Desktop arrival -------------------------------------------------------
            var desktopArrivedEvt = ordered.FirstOrDefault(e =>
                string.Equals(e.EventType, "desktop_arrived", StringComparison.OrdinalIgnoreCase));
            var desktopArrived = desktopArrivedEvt != null;
            DateTime? desktopArrivedAt = desktopArrivedEvt?.Timestamp;

            // ---- Hello policy ----------------------------------------------------------
            var helloPolicyEvt = ordered.FirstOrDefault(e =>
                string.Equals(e.EventType, "hello_policy_detected", StringComparison.OrdinalIgnoreCase));
            bool? helloPolicyEnabled = null;
            if (helloPolicyEvt != null)
            {
                var raw = TryGetDataString(helloPolicyEvt, "helloEnabled");
                if (bool.TryParse(raw, out var parsed)) helloPolicyEnabled = parsed;
            }

            // ---- AAD join state — placeholder vs real user vs unknown ------------------
            // Sources (newest wins):
            //   - aad_placeholder_user_detected → placeholder
            //   - aad_user_joined_observed      → real_user (Part 1 path, V2 since 2026-05-04)
            //   - aad_user_joined_late          → real_user (Part 1 path, V1 + historical data)
            //   - user_aad_signin_complete      → real_user (Part 2 path; future)
            //   - aad_join_status               → fallback heuristic via isFooUser flag
            string aadJoinState = "unknown";
            DateTime? aadJoinStateAt = null;
            foreach (var evt in ordered)
            {
                var et = evt.EventType ?? "";
                if (string.Equals(et, "aad_placeholder_user_detected", StringComparison.OrdinalIgnoreCase))
                {
                    aadJoinState = "placeholder";
                    aadJoinStateAt = evt.Timestamp;
                }
                else if (string.Equals(et, "aad_user_joined_observed", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(et, "aad_user_joined_late", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(et, "user_aad_signin_complete", StringComparison.OrdinalIgnoreCase))
                {
                    aadJoinState = "real_user";
                    aadJoinStateAt = evt.Timestamp;
                }
                else if (aadJoinState == "unknown"
                         && string.Equals(et, "aad_join_status", StringComparison.OrdinalIgnoreCase))
                {
                    var isFoo = TryGetDataString(evt, "isFooUser");
                    if (bool.TryParse(isFoo, out var foo))
                    {
                        aadJoinState = foo ? "placeholder" : "real_user";
                        aadJoinStateAt = evt.Timestamp;
                    }
                }
            }

            // ---- Reboot observed -------------------------------------------------------
            var rebootEvt = ordered.FirstOrDefault(e =>
                string.Equals(e.EventType, "system_reboot_detected", StringComparison.OrdinalIgnoreCase));
            var rebootObserved = rebootEvt != null;

            // ---- Hybrid-join hint from autopilot_profile / enrollment_type_detected ----
            bool? isHybridJoin = null;
            string? enrollmentType = null;
            foreach (var evt in ordered)
            {
                var et = evt.EventType ?? "";
                if (string.Equals(et, "autopilot_profile", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(et, "enrollment_facts_observed", StringComparison.OrdinalIgnoreCase))
                {
                    var hybridRaw = TryGetDataString(evt, "isHybridJoin");
                    if (bool.TryParse(hybridRaw, out var h)) isHybridJoin = h;
                    enrollmentType ??= TryGetDataString(evt, "enrollmentType");
                }
                else if (enrollmentType == null
                         && string.Equals(et, "enrollment_type_detected", StringComparison.OrdinalIgnoreCase))
                {
                    enrollmentType = TryGetDataString(evt, "enrollmentType");
                }
            }

            // ---- Last network state ----------------------------------------------------
            string? lastNetworkState = null;
            for (var i = ordered.Count - 1; i >= 0; i--)
            {
                if (string.Equals(ordered[i].EventType, "network_state_change", StringComparison.OrdinalIgnoreCase))
                {
                    lastNetworkState = ordered[i].Message;
                    break;
                }
            }

            // ---- Timing facts ----------------------------------------------------------
            var lastEventAt = ordered[ordered.Count - 1].Timestamp;
            var silenceMinutes = (int)Math.Max(0, (nowUtc - lastEventAt).TotalMinutes);

            // ---- Missing canonical signals --------------------------------------------
            var missing = new List<string>();
            foreach (var sig in CanonicalCompletionSignals)
            {
                if (sig == "hello_provisioning_completed")
                {
                    // Treat any Hello terminal as satisfying this slot.
                    if (!seenTypes.Contains("hello_provisioning_completed")
                        && !seenTypes.Contains("hello_provisioning_failed")
                        && !seenTypes.Contains("hello_provisioning_blocked")
                        && !seenTypes.Contains("hello_skipped"))
                    {
                        missing.Add("hello_terminal");
                    }
                }
                else if (sig == "aad_user_joined_observed")
                {
                    // Codex review 2026-05-01: Part 1 (Classic / WhiteGlove) emits
                    // aad_user_joined_observed (V2 since 2026-05-04, was aad_user_joined_late
                    // in V1 + historical data); Part 2 (post-reboot user sign-in) emits
                    // user_aad_signin_complete. Any of the three satisfies the same conceptual
                    // slot — without this branch, every healthy Part-2 session would falsely
                    // list aad_user_joined_observed as missing even though aadJoinState is
                    // correctly classified as real_user above.
                    if (!seenTypes.Contains("aad_user_joined_observed")
                        && !seenTypes.Contains("aad_user_joined_late")
                        && !seenTypes.Contains("user_aad_signin_complete"))
                    {
                        missing.Add("aad_user_join");
                    }
                }
                else if (!seenTypes.Contains(sig))
                {
                    missing.Add(sig);
                }
            }

            // ---- ESP subcategory rollup (drives timeout reclassification) --------------
            // Same extraction the maintenance sweep's classifier uses, embedded here so the
            // snapshot records the exact evidence behind the AwaitingUser/Incomplete/Succeeded
            // verdict. categorySucceeded is intentionally ignored (Windows never sets it).
            var espRollup = EnrollmentTimeoutClassifier.ExtractRollup(ordered);

            var snapshot = new
            {
                schemaVersion = CurrentSchemaVersion,
                generatedAtUtc = nowUtc.ToString("o"),
                eventCount = ordered.Count,
                lastEventAtUtc = lastEventAt.ToString("o"),
                silenceMinutes,
                lastEspPhase,
                lastEspPhaseAtUtc = lastEspPhaseAt?.ToString("o"),
                desktopArrived,
                desktopArrivedAtUtc = desktopArrivedAt?.ToString("o"),
                helloPolicyDetected = helloPolicyEvt != null,
                helloPolicyEnabled,
                aadJoinState,
                aadJoinStateAtUtc = aadJoinStateAt?.ToString("o"),
                rebootObserved,
                isHybridJoin,
                enrollmentType,
                lastNetworkState,
                deviceSetupAllSucceeded = espRollup.DeviceSetupAllSucceeded,
                accountSetupSucceededCount = espRollup.AccountSetupSucceededCount,
                accountSetupTotal = espRollup.AccountSetupTotal,
                accountSetupAllSucceeded = espRollup.AccountSetupAllSucceeded,
                missingSignals = missing,
            };

            return JsonConvert.SerializeObject(snapshot, Formatting.None);
        }

        private static string? TryGetDataString(EnrollmentEvent evt, string key)
        {
            if (evt?.Data == null) return null;
            if (!evt.Data.TryGetValue(key, out var raw) || raw == null) return null;
            return raw.ToString();
        }
    }
}
