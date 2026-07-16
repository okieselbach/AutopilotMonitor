using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.DeviceInfo;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Telemetry.DeviceInfo;

/// <summary>
/// Pins the ZTD verdict mapping (autopilot_profile_missing evidence). The event-ID semantics
/// come from the Microsoft Autopilot troubleshooting FAQ (see
/// docs/agent/autopilot-ztd-diagnostics.md): error IDs are authoritative and must win over
/// the Info-flow reconstruction, and the priority order within the errors is
/// registration (807) → identity mismatch (908) → deleted (809) → not assigned (815).
/// </summary>
public class ZtdEvidenceVerdictTests
{
    private static Dictionary<int, int> Counts(params int[] ids)
    {
        var counts = new Dictionary<int, int>();
        foreach (var id in ids)
        {
            counts.TryGetValue(id, out var c);
            counts[id] = c + 1;
        }
        return counts;
    }

    [Fact]
    public void NullOrEmpty_ReturnsNoEventsFound()
    {
        Assert.Equal("no_ztd_events_found", ZtdEvidence.ComputeZtdVerdict(null));
        Assert.Equal("no_ztd_events_found", ZtdEvidence.ComputeZtdVerdict(new Dictionary<int, int>()));
    }

    [Theory]
    [InlineData(807, "device_not_registered")]
    [InlineData(908, "serial_or_product_key_mismatch")]
    [InlineData(809, "assigned_profile_deleted")]
    [InlineData(815, "no_profile_assigned")]
    public void ErrorIds_AreAuthoritative(int eventId, string expectedVerdict)
    {
        // Error verdict wins even when the full Info download-flow is also present.
        Assert.Equal(expectedVerdict, ZtdEvidence.ComputeZtdVerdict(Counts(eventId, 100, 160, 164)));
    }

    [Fact]
    public void NotRegistered_WinsOverAllOtherErrors()
    {
        Assert.Equal("device_not_registered", ZtdEvidence.ComputeZtdVerdict(Counts(807, 809, 815, 908)));
    }

    [Fact]
    public void DownloadSucceeded_MapsToProfileDownloaded()
    {
        // 161 (retrieve settings succeeded) — a profile DID arrive; the missing cache read
        // was a timing artifact. 153 (ProfileState_Available) is equivalent evidence.
        Assert.Equal("profile_downloaded", ZtdEvidence.ComputeZtdVerdict(Counts(160, 161, 164)));
        Assert.Equal("profile_downloaded", ZtdEvidence.ComputeZtdVerdict(Counts(153)));
    }

    [Fact]
    public void AlreadyProvisioned_MapsTo163()
    {
        Assert.Equal("already_provisioned", ZtdEvidence.ComputeZtdVerdict(Counts(163)));
    }

    [Fact]
    public void InternetConfirmedButNoProfile_IsTheAssignmentGapSignature()
    {
        // 164 (internet available) without 161 (download succeeded): the ZTD round-trip ran and
        // came back empty — profile not assigned / assignment not propagated. This was the
        // session-423b5360 scenario.
        Assert.Equal("download_attempted_no_profile_returned", ZtdEvidence.ComputeZtdVerdict(Counts(100, 160, 164)));
    }

    [Fact]
    public void OnlyWaitingHeartbeat_MeansInternetNeverConfirmed()
    {
        Assert.Equal("waiting_for_profile_no_internet_confirmation", ZtdEvidence.ComputeZtdVerdict(Counts(100, 100, 100)));
    }

    [Fact]
    public void UnmappedIdsOnly_AreInconclusive()
    {
        // 171/172 (TPM attestation) are queried for context but don't decide the profile verdict.
        Assert.Equal("inconclusive", ZtdEvidence.ComputeZtdVerdict(Counts(171, 172)));
    }
}
