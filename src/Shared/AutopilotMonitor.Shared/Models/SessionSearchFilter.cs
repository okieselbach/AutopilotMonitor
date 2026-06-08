using System;
using System.Collections.Generic;

namespace AutopilotMonitor.Shared.Models
{
    public class SessionSearchFilter
    {
        // Basic (SessionSummary fields)
        public string? Status { get; set; }
        public string? SerialNumber { get; set; }
        public string? AgentVersion { get; set; }
        public string? ImeAgentVersion { get; set; }

        // Prefix-match variants. When set, narrow the candidate set to rows whose
        // AgentVersion / ImeAgentVersion *starts with* the given string. Mutually
        // exclusive with the exact-match variants above (the exact match wins).
        // Use case: "all V2 agents" → AgentVersionPrefix = "2.0." matches every
        // build in the 2.0.x line in a single call instead of one call per build.
        public string? AgentVersionPrefix { get; set; }
        public string? ImeAgentVersionPrefix { get; set; }
        public string? DeviceName { get; set; }
        public string? Manufacturer { get; set; }
        public string? Model { get; set; }
        public string? OsBuild { get; set; }
        public string? EnrollmentType { get; set; }
        public bool? IsPreProvisioned { get; set; }
        public bool? IsHybridJoin { get; set; }
        public string? GeoCountry { get; set; }
        public DateTime? StartedAfter { get; set; }
        public DateTime? StartedBefore { get; set; }

        // Reboot-count range. When set, narrow to sessions whose stored RebootCount is
        // within [RebootCountMin, RebootCountMax]. Use case: "machines with many reboots"
        // → RebootCountMin = 5. Sessions that predate the field lack the property and are
        // excluded by the >= bound (acceptable — they have no reboot data).
        public int? RebootCountMin { get; set; }
        public int? RebootCountMax { get; set; }

        public int Limit { get; set; } = 50;

        // Dynamic device property filters (key = "eventType.propertyName", value = filter expression)
        // Examples: "tpm_status.specVersion" = "2.0", "hardware_spec.ramTotalGB" = ">=8"
        public Dictionary<string, string>? DeviceProperties { get; set; }

        public bool HasDeviceSnapshotFilters =>
            DeviceProperties != null && DeviceProperties.Count > 0;
    }
}
