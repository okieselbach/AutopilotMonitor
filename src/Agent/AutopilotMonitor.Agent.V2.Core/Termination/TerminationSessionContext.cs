#nullable enable
using System;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Security;

namespace AutopilotMonitor.Agent.V2.Core.Termination
{
    /// <summary>
    /// Immutable session facts the <see cref="EnrollmentTerminationHandler"/> needs at
    /// terminal time (ARCH-F2 grouping — replaces seven loose constructor parameters).
    /// A concrete class, not an interface: it is pure data with no behavioral seam, so
    /// production and tests construct the same type and there is no fake-vs-prod divergence.
    /// </summary>
    public sealed class TerminationSessionContext
    {
        private readonly Func<bool>? _isWhiteGlovePart2;

        public AgentConfiguration Configuration { get; }

        /// <summary>State directory for final-status.json + enrollment-complete.marker.</summary>
        public string StateDirectory { get; }

        /// <summary>Used for the uptimeMinutes field on <c>agent_shutting_down</c>.</summary>
        public DateTime AgentStartTimeUtc { get; }

        /// <summary>Normalized to <see cref="string.Empty"/> when unknown.</summary>
        public string AgentVersion { get; }

        /// <summary>
        /// Writes the <c>whiteglove.complete</c> marker on WG Part-1 exit so Part-2 resume is
        /// detected on the next boot. Nullable — the handler warns and skips when absent.
        /// </summary>
        public SessionIdPersistence? SessionPersistence { get; }

        /// <summary>Test override for the SummaryDialog exe path; null in production.</summary>
        public string? DialogExePathOverride { get; }

        /// <summary>
        /// V1-symmetric Part-2 hint (plan §11). A Part-2 resume runs as a fresh Classic
        /// enrollment after Archive-and-Reset, so the terminal Stage is
        /// <c>Completed</c>/<c>Failed</c> like any first-run completion. The shutdown analyzer
        /// pipeline still needs to know it was a Part-2 run so SoftwareInventoryAnalyzer can
        /// tag findings with phase=2 and the backend's vulnerability-correlation pipeline can
        /// filter Part-2 inventory out of the Part-1 set. Evaluated lazily at terminal time
        /// (production wires <c>() =&gt; orchestrator.IsWhiteGlovePart2</c>); defaults to
        /// <c>false</c> when no accessor was supplied.
        /// </summary>
        public bool IsWhiteGlovePart2 => _isWhiteGlovePart2?.Invoke() ?? false;

        public TerminationSessionContext(
            AgentConfiguration configuration,
            string stateDirectory,
            DateTime agentStartTimeUtc,
            string? agentVersion = null,
            SessionIdPersistence? sessionPersistence = null,
            string? dialogExePathOverride = null,
            Func<bool>? isWhiteGlovePart2 = null)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            StateDirectory = string.IsNullOrEmpty(stateDirectory)
                ? throw new ArgumentNullException(nameof(stateDirectory))
                : stateDirectory;
            AgentStartTimeUtc = agentStartTimeUtc;
            AgentVersion = agentVersion ?? string.Empty;
            SessionPersistence = sessionPersistence;
            DialogExePathOverride = dialogExePathOverride;
            _isWhiteGlovePart2 = isWhiteGlovePart2;
        }
    }
}
