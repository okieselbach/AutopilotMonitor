using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Represents a single event during enrollment
    /// </summary>
    public class EnrollmentEvent
    {
        /// <summary>
        /// Unique identifier for this event
        /// </summary>
        public string EventId { get; set; }

        /// <summary>
        /// Session identifier this event belongs to
        /// </summary>
        public string? SessionId { get; set; }

        /// <summary>
        /// Tenant identifier
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Timestamp when the agent detected/created this event on the device (UTC).
        /// Set at construction time (DateTime.UtcNow) or from the source's native timestamp
        /// (e.g., Windows EventLog TimeCreated). The backend stores this as-is — it is the
        /// authoritative "agent-side" timestamp, not a server-side receive time.
        /// </summary>
        public DateTime Timestamp { get; set; }

        /// <summary>
        /// Server-side UTC timestamp set when the backend receives and stores this event.
        /// Null for events that pre-date this feature. Never set by the agent.
        /// </summary>
        public DateTime? ReceivedAt { get; set; }

        /// <summary>
        /// Device-clock UTC timestamp of the moment the agent SENT the upload batch carrying
        /// this event (X-Send-Time-Utc request header, one value per ingest request — never
        /// set by the agent per event). Same clock frame as <see cref="Timestamp"/>, so
        /// SentAt − Timestamp is pure spool delay and ReceivedAt − SentAt is network latency
        /// plus device-vs-server clock offset — the two are indistinguishable without this
        /// field. Null for events from agents that pre-date the header.
        /// </summary>
        public DateTime? SentAt { get; set; }

        /// <summary>
        /// Type of event (e.g., "phase_transition", "app_install_start", "error")
        /// </summary>
        public string EventType { get; set; } = default!;

        /// <summary>
        /// Severity level of the event (internal property, not serialized)
        /// </summary>
        [JsonIgnore]
        public EventSeverity Severity { get; set; }

        /// <summary>
        /// Severity as string for JSON serialization
        /// </summary>
        [JsonPropertyName("severity")]
        public string SeverityString => Severity.ToString();

        /// <summary>
        /// Source of the event — typically the producing class name (e.g.
        /// "ImeLogTracker", "EnrollmentTracker", "DecisionEngine") or a
        /// stable lifecycle label ("Agent").
        /// </summary>
        public string Source { get; set; } = default!;

        /// <summary>
        /// Enrollment phase associated with this event.
        /// IMPORTANT: Only set this to a concrete phase (e.g. DeviceSetup, AccountSetup) when the event
        /// represents an actual phase transition/declaration. The UI timeline relies on phase values to
        /// render phase boundaries — setting a phase on a non-transition event will break phase grouping.
        /// Leave as Unknown (default) for informational events that occur within a phase but do not
        /// initiate a phase change. Unknown events are chronologically sorted into the active phase.
        /// </summary>
        [JsonIgnore]
        public EnrollmentPhase Phase { get; set; } = EnrollmentPhase.Unknown;

        /// <summary>
        /// Phase as number for JSON serialization (frontend expects number)
        /// </summary>
        [JsonPropertyName("phase")]
        public int PhaseNumber => (int)Phase;

        /// <summary>
        /// Phase name as string for JSON serialization
        /// </summary>
        [JsonPropertyName("phaseName")]
        public string PhaseName => GetPhaseName(Phase);

        /// <summary>
        /// When true, the event bypasses the debounce timer and triggers an immediate upload.
        /// Set by the emitter when real-time delivery is required (phase changes, app installs,
        /// terminal events). Not serialized — upload transport only.
        /// </summary>
        [JsonIgnore]
        public bool ImmediateUpload { get; set; }

        private static string GetPhaseName(EnrollmentPhase phase)
        {
            return phase switch
            {
                EnrollmentPhase.Start => "Start",
                EnrollmentPhase.DevicePreparation => "Device Preparation",
                EnrollmentPhase.DeviceSetup => "Device Setup",
                EnrollmentPhase.AppsDevice => "Apps (Device)",
                EnrollmentPhase.AccountSetup => "Account Setup",
                EnrollmentPhase.AppsUser => "Apps (User)",
                EnrollmentPhase.FinalizingSetup => "Finalizing Setup",
                EnrollmentPhase.Complete => "Complete",
                EnrollmentPhase.Failed => "Failed",
                _ => "Unknown"
            };
        }

        /// <summary>
        /// Human-readable message
        /// </summary>
        public string Message { get; set; } = default!;

        /// <summary>
        /// Additional structured data
        /// </summary>
        public Dictionary<string, object> Data { get; set; }

        /// <summary>
        /// Sequence number for ordering events with same timestamp
        /// </summary>
        public long Sequence { get; set; }

        /// <summary>
        /// Original agent-side timestamp preserved when the backend clamps an out-of-range value.
        /// Null when the timestamp was within the valid range and no correction was needed.
        /// Use this for troubleshooting and root-cause analysis of clock issues on devices.
        /// </summary>
        public DateTime? OriginalTimestamp { get; set; }

        /// <summary>
        /// True when the backend had to clamp the agent-side Timestamp to a valid range.
        /// When set, OriginalTimestamp contains the raw value the agent sent.
        /// AI analysis and UI should treat clamped timestamps with caution.
        /// </summary>
        public bool TimestampClamped { get; set; }

        /// <summary>
        /// Azure Table Storage RowKey — format: {Timestamp:yyyyMMddHHmmssfff}_{Sequence:D10}
        /// Represents the exact sort key used in storage.
        /// </summary>
        [JsonPropertyName("rowKey")]
        public string RowKey { get; set; } = default!;

        /// <summary>
        /// Reducer <c>StepIndex</c> of the <c>DecisionTransition</c> whose
        /// <c>EmitEventTimelineEntry</c> effect produced this event. Nullable for
        /// forward-compat (events that pre-date Codex follow-up #3 or that are emitted
        /// outside the reducer pipeline). Together with <see cref="CausedBySignalOrdinal"/>
        /// this replaces the always-empty <c>DecisionTransition.EmittedEventSequences</c>
        /// — the forward link lives on the event side because the event's <c>Sequence</c>
        /// is assigned after the journal record is already on disk.
        /// </summary>
        public long? CausedByTransitionStepIndex { get; set; }

        /// <summary>
        /// <c>SessionSignalOrdinal</c> of the signal that drove the transition which emitted
        /// this event. Nullable for the same reason as
        /// <see cref="CausedByTransitionStepIndex"/>. Lets the Inspector jump
        /// "event → causing signal" without joining through the transitions table.
        /// </summary>
        public long? CausedBySignalOrdinal { get; set; }

        public EnrollmentEvent()
        {
            EventId = Guid.NewGuid().ToString();
            Timestamp = DateTime.UtcNow;
            Data = new Dictionary<string, object>();
        }
    }

    /// <summary>
    /// Severity levels for events
    /// </summary>
    public enum EventSeverity
    {
        Trace = -1,
        Debug = 0,
        Info = 1,
        Warning = 2,
        Error = 3,
        Critical = 4
    }
}
