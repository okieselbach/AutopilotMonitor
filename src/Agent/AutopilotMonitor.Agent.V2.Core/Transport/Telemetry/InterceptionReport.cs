#nullable enable
using System;

namespace AutopilotMonitor.Agent.V2.Core.Transport.Telemetry
{
    /// <summary>
    /// The first intercepted upload of the process, carried by
    /// <see cref="TelemetryUploadOrchestrator.UploadIntercepted"/> so the orchestrator can surface a
    /// <c>telemetry_upload_blocked</c> event with the evidence.
    /// </summary>
    public sealed class InterceptionReport
    {
        public InterceptionReport(ResponseClassification interception, DateTime firstBlockedAtUtc)
        {
            Interception = interception ?? throw new ArgumentNullException(nameof(interception));
            FirstBlockedAtUtc = firstBlockedAtUtc;
        }

        /// <summary>Status, sub-reason, page title and server-certificate issuer of the intercepted upload.</summary>
        public ResponseClassification Interception { get; }

        /// <summary>When the first upload was intercepted (agent clock, UTC).</summary>
        public DateTime FirstBlockedAtUtc { get; }
    }
}
