using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Session 8e4cc4ae (2026-07-24): two on-demand ("Collect Logs") uploads landed in hosted
/// storage, but the portal never flipped to Download — the agent's ServerActionDispatcher
/// confirms the upload via <c>server_action_executed(actionType=request_diagnostics,
/// blobName=…)</c> and emits NO <c>diagnostics_uploaded</c> (only the terminal path does),
/// so the Sessions row was never stamped. These tests pin the classification predicate and
/// the destination inference that close that gap backend-side (no agent release needed).
/// </summary>
public class IngestOnDemandDiagnosticsTests
{
    private const string TenantId = "b54dc1af-5320-4f60-b5d4-821e0cf2a359";
    private const string HostedBlob =
        TenantId + "/AgentDiagnostics-8e4cc8ae-1111-2222-3333-444455556666-20260724170902-server-requested.zip";

    private static EnrollmentEvent Evt(
        string eventType = "server_action_executed",
        string? actionType = "request_diagnostics",
        string? blobName = HostedBlob)
    {
        var data = new Dictionary<string, object>();
        if (actionType != null) data["actionType"] = actionType;
        if (blobName != null) data["blobName"] = blobName;
        return new EnrollmentEvent { EventType = eventType, Data = data };
    }

    // -------- IsOnDemandDiagnosticsUploadConfirmation --------

    [Fact]
    public void ExecutedRequestDiagnosticsWithBlobName_IsConfirmation()
    {
        Assert.True(EventIngestProcessor.IsOnDemandDiagnosticsUploadConfirmation(Evt()));
    }

    [Fact]
    public void ActionTypeIsCaseInsensitive()
    {
        Assert.True(EventIngestProcessor.IsOnDemandDiagnosticsUploadConfirmation(
            Evt(actionType: "Request_Diagnostics")));
    }

    [Theory]
    [InlineData("server_action_received")]
    [InlineData("server_action_failed")]
    [InlineData("diagnostics_uploaded")]
    public void OtherEventTypes_AreNotConfirmations(string eventType)
    {
        Assert.False(EventIngestProcessor.IsOnDemandDiagnosticsUploadConfirmation(Evt(eventType: eventType)));
    }

    [Theory]
    [InlineData("rotate_config")]
    [InlineData("terminate_session")]
    [InlineData(null)]
    public void OtherActionTypes_AreNotConfirmations(string? actionType)
    {
        Assert.False(EventIngestProcessor.IsOnDemandDiagnosticsUploadConfirmation(Evt(actionType: actionType)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingOrEmptyBlobName_IsNotAConfirmation(string? blobName)
    {
        // A failed upload never reaches server_action_executed, but an executed event without
        // blobName (defensive) must not wipe/stamp the row either.
        Assert.False(EventIngestProcessor.IsOnDemandDiagnosticsUploadConfirmation(Evt(blobName: blobName)));
    }

    [Fact]
    public void NullEventOrData_IsNotAConfirmation()
    {
        Assert.False(EventIngestProcessor.IsOnDemandDiagnosticsUploadConfirmation(null));
        Assert.False(EventIngestProcessor.IsOnDemandDiagnosticsUploadConfirmation(
            new EnrollmentEvent { EventType = "server_action_executed", Data = null }));
    }

    // -------- InferDiagnosticsDestination --------

    [Fact]
    public void ExplicitDestination_AlwaysWins()
    {
        Assert.Equal("CustomerSas",
            EventIngestProcessor.InferDiagnosticsDestination("CustomerSas", HostedBlob, TenantId));
    }

    [Fact]
    public void TenantPrefixedBlobName_InfersHosted()
    {
        Assert.Equal("Hosted",
            EventIngestProcessor.InferDiagnosticsDestination(null, HostedBlob, TenantId));
    }

    [Fact]
    public void TenantPrefixMatch_IsCaseInsensitive()
    {
        Assert.Equal("Hosted",
            EventIngestProcessor.InferDiagnosticsDestination(null, HostedBlob.ToUpperInvariant(), TenantId));
    }

    [Fact]
    public void BareFilename_InfersNothing()
    {
        // CustomerSas uploads persist the bare zip filename — repo must leave the
        // destination column unchanged (read-time default CustomerSas).
        Assert.Null(EventIngestProcessor.InferDiagnosticsDestination(
            null, "AgentDiagnostics-x-20260724.zip", TenantId));
    }

    [Fact]
    public void ForeignTenantPrefix_InfersNothing()
    {
        Assert.Null(EventIngestProcessor.InferDiagnosticsDestination(
            null, "99999999-9999-9999-9999-999999999999/x.zip", TenantId));
    }

    [Fact]
    public void NullBlobName_InfersNothing()
    {
        Assert.Null(EventIngestProcessor.InferDiagnosticsDestination(null, null, TenantId));
    }
}
