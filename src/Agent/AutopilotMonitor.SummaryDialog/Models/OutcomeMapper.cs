using System;

namespace AutopilotMonitor.SummaryDialog.Models
{
    /// <summary>
    /// Five visual states the dialog can render. The mapping from the wire-format
    /// <see cref="FinalStatus.Outcome"/> string depends on the schema version: V1 only
    /// produces "completed"/"failed", V2 uses "succeeded"/"failed"/"timed_out"/
    /// "whiteglove_part1"/"unknown". The renderer chooses header text, icon and banner
    /// colour from this enum, not from the raw string.
    /// </summary>
    public enum OutcomeKind
    {
        /// <summary>Standard success — green check, no banner.</summary>
        Success,

        /// <summary>WhiteGlove Part 1 sealed — blue check + "Pre-provisioning complete" banner.</summary>
        PreProvisioningComplete,

        /// <summary>Terminal failure — red X + failure reason banner.</summary>
        Failure,

        /// <summary>Max-lifetime / explicit timeout — orange warning + reason banner.</summary>
        TimedOut,

        /// <summary>Outcome string is missing or unrecognised — neutral gray + soft warning.</summary>
        Unknown
    }

    /// <summary>
    /// Pure-logic helper extracted so the schema-version dispatch and outcome-string
    /// mapping are unit-testable without spinning up WPF. Used by <c>MainWindow</c>'s
    /// rendering path; unchanged behaviour is intentional for V1 strings (V1 written
    /// JSON deserialises with <c>SchemaVersion = 0</c> and the V1 mapping kicks in).
    /// </summary>
    public static class OutcomeMapper
    {
        /// <summary>Returns true when the dialog should run the V2 (schema 2) renderer.</summary>
        public static bool IsV2Schema(FinalStatus status)
        {
            return status != null && status.SchemaVersion >= 2;
        }

        /// <summary>
        /// Maps the wire-format <c>outcome</c> string to a visual state.
        /// <para>
        /// V1 path (schemaVersion &lt; 2): only "completed" → Success, anything else → Failure.
        /// </para>
        /// <para>
        /// V2 path (schemaVersion ≥ 2): granular mapping for all six terminal kinds.
        /// </para>
        /// </summary>
        public static OutcomeKind Map(FinalStatus status)
        {
            if (status == null) return OutcomeKind.Unknown;

            var outcome = status.Outcome ?? string.Empty;

            // V1 backward compat — keep the existing "completed" check intact so V1 agents
            // continue to render Success unchanged. Anything else collapsed to Failure
            // matches the V1 dialog's prior binary-state UI.
            if (status.SchemaVersion < 2)
            {
                return string.Equals(outcome, "completed", StringComparison.OrdinalIgnoreCase)
                    ? OutcomeKind.Success
                    : OutcomeKind.Failure;
            }

            // V2 — five explicit states. Accept "completed" too, defensively, in case a
            // mixed environment ever surfaces (V1-style outcome string with V2 schema flag).
            switch (outcome.ToLowerInvariant())
            {
                case "succeeded":
                case "completed":
                    return OutcomeKind.Success;

                case "whiteglove_part1":
                    return OutcomeKind.PreProvisioningComplete;

                case "timed_out":
                    return OutcomeKind.TimedOut;

                case "failed":
                    return OutcomeKind.Failure;

                default:
                    return OutcomeKind.Unknown;
            }
        }

        /// <summary>
        /// Header text shown in large type beneath the icon.
        /// </summary>
        public static string HeaderText(OutcomeKind kind)
        {
            switch (kind)
            {
                case OutcomeKind.Success: return "Enrollment Completed Successfully";
                case OutcomeKind.PreProvisioningComplete: return "Pre-Provisioning Complete";
                case OutcomeKind.TimedOut: return "Enrollment Timed Out";
                case OutcomeKind.Failure: return "Enrollment Failed";
                case OutcomeKind.Unknown:
                default: return "Enrollment Status Unknown";
            }
        }

        /// <summary>
        /// Sub-header / banner default text. The dialog overrides this with
        /// <see cref="FinalStatus.FailureReason"/> when the agent supplied a specific
        /// explanation; otherwise the generic banner is shown.
        /// </summary>
        public static string DefaultBannerText(OutcomeKind kind)
        {
            switch (kind)
            {
                case OutcomeKind.PreProvisioningComplete:
                    return "Device sealed. Hand the device to the end user to complete enrollment.";
                case OutcomeKind.TimedOut:
                    return "Enrollment did not finish within the allowed runtime.";
                case OutcomeKind.Failure:
                    return "Enrollment did not complete successfully.";
                case OutcomeKind.Unknown:
                    return "The enrollment outcome could not be determined.";
                case OutcomeKind.Success:
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Compact duration formatting for the right-column app label (Schema 2 V2 render).
        /// 90 → "1m 30s", 7200 → "2h 0m", 45 → "45s", &lt;= 0 → "".
        /// </summary>
        public static string FormatDuration(double seconds)
        {
            if (seconds <= 0) return string.Empty;
            var ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            return $"{ts.Seconds}s";
        }
    }
}
