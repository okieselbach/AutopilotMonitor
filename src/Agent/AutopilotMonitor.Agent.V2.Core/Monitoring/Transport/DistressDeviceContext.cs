using System;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Interop;
using AutopilotMonitor.Agent.V2.Core.Termination;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    /// <summary>
    /// Where the device stood when a failure was observed: Windows' OOBE flag, the Autopilot ESP
    /// phase and the last boot. Observational only. Sample it when the failing work starts — a
    /// failed registration retries for minutes, and the device moves on meanwhile.
    /// </summary>
    public sealed class DistressDeviceContext
    {
        public DistressDeviceContext(string oobeState, string espPhase, DateTime? deviceBootUtc)
        {
            OobeState = oobeState;
            EspPhase = espPhase;
            DeviceBootUtc = deviceBootUtc;
        }

        /// <summary><see cref="OobeStateReader"/>: flips to <c>completed</c> at the desktop, not ESP progress.</summary>
        public string OobeState { get; }

        /// <summary><see cref="EspPhaseSnapshot"/> token.</summary>
        public string EspPhase { get; }

        /// <summary>Last boot (UTC) from the monotonic uptime counter; null when it could not be read.</summary>
        public DateTime? DeviceBootUtc { get; }

        /// <summary>Samples all three now. Registry, WinRT and one kernel call; never throws.</summary>
        public static DistressDeviceContext Capture()
        {
            DateTime? boot;
            try { boot = ObservationCoverage.DeviceBootUtc(); }
            catch { boot = null; }
            return new DistressDeviceContext(OobeStateReader.Read(), EspPhaseSnapshot.Read(), boot);
        }
    }

    /// <summary>Optional parts of one distress report.</summary>
    public sealed class DistressDetail
    {
        /// <summary>TPM manufacturer id and firmware (TPM-PSS report only).</summary>
        public string TpmVersion { get; set; }

        /// <summary>The device context of the failure; null samples it when the report is sent.</summary>
        public DistressDeviceContext Context { get; set; }
    }
}
