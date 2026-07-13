using System;
using System.Collections.Generic;
using System.Globalization;
using AutopilotMonitor.DecisionCore.State;

namespace AutopilotMonitor.DecisionCore.Engine
{
    /// <summary>
    /// Single source of truth for "which terminal signals has the engine observed" — the
    /// list of signal-name strings, the ISO-8601 timestamp map, and the structured evidence
    /// map (ordinal + utc) used by both the on-the-wire <c>enrollment_*</c> audit trail and
    /// the on-disk <c>final-status.json</c> summary.
    /// <para>
    /// Lives in <see cref="DecisionCore"/> so the V2 agent's <c>FinalStatusBuilder</c> and
    /// the engine-internal <see cref="DecisionAuditTrailBuilder"/> consume identical naming
    /// rules. Adding a new milestone signal in one place automatically lights it up in the
    /// other; no more drift between Inspector audit and SummaryDialog post-mortem JSON.
    /// </para>
    /// </summary>
    public static class DecisionStateSignalCensus
    {
        /// <summary>
        /// Census the supplied <see cref="DecisionState"/>. Returns three parallel views over
        /// the same set of observed signals:
        /// <list type="bullet">
        ///   <item><c>SignalsSeen</c> — flat list of stable signal-name strings (KQL-friendly,
        ///         used by both <c>data["signalsSeen"]</c> and the dialog JSON).</item>
        ///   <item><c>SignalTimestamps</c> — camelCase key → ISO-8601 UTC. Only signals with a
        ///         <see cref="DateTime"/> fact are populated; engine-internal observations
        ///         (e.g. AAD-join / shellcore-success) carry no UTC and are absent here.</item>
        ///   <item><c>SignalEvidence</c> — camelCase key → <c>{ordinal, utc?}</c> dictionary
        ///         used by the Inspector to jump from an audit-trail row to the source signal
        ///         in the journal.</item>
        /// </list>
        /// All dictionaries use <see cref="StringComparer.Ordinal"/> so on-disk Snapshot tests
        /// produce stable ordering.
        /// </summary>
        public static SignalCensusResult Build(DecisionState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            var seen = new List<string>(8);
            var timestamps = new Dictionary<string, string>(capacity: 8, StringComparer.Ordinal);
            var evidence = new Dictionary<string, object>(capacity: 8, StringComparer.Ordinal);

            // Order matches the historical DecisionAuditTrailBuilder.BuildSignalCensus
            // ordering so existing snapshot-style assertions remain stable.

            if (state.HelloResolvedUtc != null)
            {
                seen.Add("hello_resolved");
                timestamps["helloResolved"] = FormatUtc(state.HelloResolvedUtc.Value);
                evidence["helloResolved"] = TimestampedEvidence(state.HelloResolvedUtc);
            }
            if (state.DesktopArrivedUtc != null)
            {
                seen.Add("desktop_arrived");
                timestamps["desktopArrived"] = FormatUtc(state.DesktopArrivedUtc.Value);
                evidence["desktopArrived"] = TimestampedEvidence(state.DesktopArrivedUtc);
            }
            if (state.EspFinalExitUtc != null)
            {
                seen.Add("esp_final_exit");
                timestamps["espFinalExit"] = FormatUtc(state.EspFinalExitUtc.Value);
                evidence["espFinalExit"] = TimestampedEvidence(state.EspFinalExitUtc);
            }
            if (state.ImeMatchedPatternId != null)
            {
                seen.Add("ime_pattern_matched");
                evidence["imePatternMatched"] = OrdinalEvidence(state.ImeMatchedPatternId.SourceSignalOrdinal);
            }
            if (state.SystemRebootUtc != null)
            {
                seen.Add("system_reboot");
                timestamps["systemReboot"] = FormatUtc(state.SystemRebootUtc.Value);
                evidence["systemReboot"] = TimestampedEvidence(state.SystemRebootUtc);
            }
            if (state.AccountSetupEnteredUtc != null)
            {
                seen.Add("account_setup_entered");
                timestamps["accountSetupEntered"] = FormatUtc(state.AccountSetupEnteredUtc.Value);
                evidence["accountSetupEntered"] = TimestampedEvidence(state.AccountSetupEnteredUtc);
            }
            // Session 330f73f3 fix — strong post-AccountSetup gate. Included in the census so
            // enrollment_complete audit + final-status.json show whether the genuine
            // AccountSetup-done observation was present when the session terminated.
            if (state.AccountSetupProvisioningSucceededUtc != null)
            {
                seen.Add("account_setup_provisioning_complete");
                timestamps["accountSetupProvisioningComplete"] = FormatUtc(state.AccountSetupProvisioningSucceededUtc.Value);
                evidence["accountSetupProvisioningComplete"] = TimestampedEvidence(state.AccountSetupProvisioningSucceededUtc);
            }
            // SelfDeploying-defang anchor — set when DeviceSetupProvisioningComplete signal
            // arrives. Surfaces in enrollment_complete audit so consumers can see whether the
            // DeviceSetup-done observation drove the SelfDeploying classification (via the new
            // 5-min DeviceOnlyEspDetection deadline path).
            if (state.DeviceSetupResolvedUtc != null)
            {
                seen.Add("device_setup_provisioning_complete");
                timestamps["deviceSetupResolved"] = FormatUtc(state.DeviceSetupResolvedUtc.Value);
                evidence["deviceSetupResolved"] = TimestampedEvidence(state.DeviceSetupResolvedUtc);
            }
            // Session 8bc1180f — IME user-session completion anchor. Consumed by the
            // AdvisoryCompletion deadline conjunction; surfaced in the census so terminal
            // audit trails show whether the IME evidence was present (and its timestamp,
            // for the >= AccountSetupEntered correlation check) when the session resolved.
            if (state.ImeUserSessionCompletedUtc != null)
            {
                seen.Add("ime_user_session_completed");
                timestamps["imeUserSessionCompleted"] = FormatUtc(state.ImeUserSessionCompletedUtc.Value);
                evidence["imeUserSessionCompleted"] = TimestampedEvidence(state.ImeUserSessionCompletedUtc);
            }
            // Session 772fe502 — genuine Hello-wizard launch (Shell-Core 62404, CXID AADHello/
            // NGC). Surfaced in the census so terminal audit trails show whether a wizard was
            // observed — the discriminant between "Hello skipped by policy" and "Hello skipped
            // while the wizard was actually running".
            if (state.HelloWizardStartedUtc != null)
            {
                seen.Add("hello_wizard_started");
                timestamps["helloWizardStarted"] = FormatUtc(state.HelloWizardStartedUtc.Value);
                evidence["helloWizardStarted"] = TimestampedEvidence(state.HelloWizardStartedUtc);
            }

            // WhiteGlove Part-1 sealing observations — engine-internal, no UTC fact.
            var obs = state.ScenarioObservations;
            if (obs.ShellCoreWhiteGloveSuccessSeen?.Value == true)
            {
                seen.Add("shellcore_whiteglove_success");
                evidence["shellcoreWhiteGloveSuccess"] = OrdinalEvidence(obs.ShellCoreWhiteGloveSuccessSeen.SourceSignalOrdinal);
            }
            if (obs.WhiteGloveSealingPatternSeen?.Value == true)
            {
                seen.Add("whiteglove_sealing_pattern");
                evidence["whiteGloveSealingPattern"] = OrdinalEvidence(obs.WhiteGloveSealingPatternSeen.SourceSignalOrdinal);
            }
            if (obs.AadUserJoinWithUserObserved != null)
            {
                seen.Add(obs.AadUserJoinWithUserObserved.Value
                    ? "aad_user_joined_with_user"
                    : "aad_user_joined_device_only");
                evidence["aadUserJoinObserved"] = new Dictionary<string, object>(capacity: 2, StringComparer.Ordinal)
                {
                    ["ordinal"] = obs.AadUserJoinWithUserObserved.SourceSignalOrdinal,
                    ["withUser"] = obs.AadUserJoinWithUserObserved.Value,
                };
            }

            return new SignalCensusResult(seen, timestamps, evidence);
        }

        private static Dictionary<string, object> TimestampedEvidence<T>(SignalFact<T>? fact)
        {
            var dict = new Dictionary<string, object>(capacity: 2, StringComparer.Ordinal)
            {
                ["ordinal"] = fact!.SourceSignalOrdinal,
            };
            if (fact.Value is DateTime dt)
            {
                dict["utc"] = FormatUtc(dt);
            }
            else if (fact.Value != null)
            {
                dict["value"] = fact.Value!;
            }
            return dict;
        }

        private static Dictionary<string, object> OrdinalEvidence(long ordinal) =>
            new Dictionary<string, object>(capacity: 1, StringComparer.Ordinal)
            {
                ["ordinal"] = ordinal,
            };

        private static string FormatUtc(DateTime utc) =>
            DateTime.SpecifyKind(utc, DateTimeKind.Utc)
                .ToString("o", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Result of <see cref="DecisionStateSignalCensus.Build"/>. Three parallel views over the
    /// same observed-signals set; consumers pick the one that fits their wire format.
    /// </summary>
    public sealed class SignalCensusResult
    {
        public SignalCensusResult(
            List<string> signalsSeen,
            Dictionary<string, string> signalTimestamps,
            Dictionary<string, object> signalEvidence)
        {
            SignalsSeen = signalsSeen ?? throw new ArgumentNullException(nameof(signalsSeen));
            SignalTimestamps = signalTimestamps ?? throw new ArgumentNullException(nameof(signalTimestamps));
            SignalEvidence = signalEvidence ?? throw new ArgumentNullException(nameof(signalEvidence));
        }

        public List<string> SignalsSeen { get; }
        public Dictionary<string, string> SignalTimestamps { get; }
        public Dictionary<string, object> SignalEvidence { get; }
    }
}
