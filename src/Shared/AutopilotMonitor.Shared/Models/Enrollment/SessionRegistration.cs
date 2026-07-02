using System;

namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Registration payload for a new enrollment session
    /// </summary>
    public class SessionRegistration
    {
        /// <summary>
        /// Unique session identifier (GUID)
        /// </summary>
        public string SessionId { get; set; }

        /// <summary>
        /// Tenant identifier
        /// </summary>
        public string TenantId { get; set; } = default!;

        /// <summary>
        /// Device serial number
        /// </summary>
        public string SerialNumber { get; set; } = default!;

        /// <summary>
        /// Device manufacturer
        /// </summary>
        public string Manufacturer { get; set; } = default!;

        /// <summary>
        /// Device model
        /// </summary>
        public string Model { get; set; } = default!;

        /// <summary>
        /// Device name
        /// </summary>
        public string DeviceName { get; set; } = default!;

        /// <summary>
        /// OS product name, e.g. "Microsoft Windows 11 Pro"
        /// </summary>
        public string OsName { get; set; } = default!;

        /// <summary>
        /// Real OS build number, e.g. "26220.7934" (CurrentBuild.UBR from registry)
        /// </summary>
        public string OsBuild { get; set; } = default!;

        /// <summary>
        /// OS display version, e.g. "25H2", "24H2"
        /// </summary>
        public string OsDisplayVersion { get; set; } = default!;

        /// <summary>
        /// OS edition (e.g., "Pro", "Enterprise")
        /// </summary>
        public string OsEdition { get; set; } = default!;

        /// <summary>
        /// OS language
        /// </summary>
        public string OsLanguage { get; set; } = default!;

        /// <summary>
        /// Whether this is user-driven enrollment
        /// </summary>
        public bool IsUserDriven { get; set; }

        /// <summary>
        /// Whether this is pre-provisioned
        /// </summary>
        public bool IsPreProvisioned { get; set; }

        /// <summary>
        /// Timestamp when enrollment started (UTC)
        /// </summary>
        public DateTime StartedAt { get; set; }

        /// <summary>
        /// Agent version
        /// </summary>
        public string AgentVersion { get; set; } = default!;

        /// <summary>
        /// Enrollment type detected by the agent: "v1" (Autopilot Classic/ESP) or "v2" (Windows Device Preparation)
        /// </summary>
        public string EnrollmentType { get; set; } = "v1";

        /// <summary>
        /// Whether the Autopilot profile indicates Hybrid Azure AD Join (CloudAssignedDomainJoinMethod == 1).
        /// Derived from the profile at registration time.
        /// </summary>
        public bool IsHybridJoin { get; set; }

        /// <summary>
        /// Whether the Autopilot profile carries the self-deploying/kiosk OOBE marker
        /// (CloudAssignedOobeConfig bits 0x20|0x40; validated platform-wide 2026-07-02 as
        /// exclusive to self-deploying profiles). Display/filter metadata only — the
        /// DecisionCore behavioural gate consumes the EnrollmentFactsObserved signal, not
        /// this flag.
        /// </summary>
        public bool IsSelfDeployingProfile { get; set; }

        public SessionRegistration()
        {
            SessionId = Guid.NewGuid().ToString();
            StartedAt = DateTime.UtcNow;
        }
    }
}
