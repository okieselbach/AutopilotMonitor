using System.Net;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// A tenant whose Graph access is confirmed lost is refused right after the hardware check, with
/// the envelope code <c>TenantGraphAccessLost</c> and without running a validator: every validator
/// would only spend the chain budget on token requests Entra keeps refusing. The gate harness of
/// <see cref="ConfigConfirmBeforeRefusalTests"/> wires no validator at all, so a chain that ran on
/// the Autopilot validation would throw.
/// </summary>
public sealed class GraphAccessLostGateTests
{
    internal const string Refusal = "Tenant access to Microsoft Graph is lost";

    private static readonly DateTime SuspectedAt = new(2026, 11, 2, 21, 4, 0, DateTimeKind.Utc);
    private static readonly DateTime ConfirmedAt = new(2026, 11, 2, 22, 4, 0, DateTimeKind.Utc);

    /// <summary>A validating tenant (Cloud PC validation, which the harness tolerates unwired) with a confirmed loss.</summary>
    internal static TenantConfiguration Lost(Action<TenantConfiguration>? change = null)
        => ConfigConfirmBeforeRefusalTests.Admitting(c =>
        {
            c.ValidateCloudPcDevice = true;
            c.GraphAccessRejectedSince = SuspectedAt;
            c.GraphAccessRejectionConfirmedAt = ConfirmedAt;
            c.GraphAccessRejectedCode = "AADSTS7000229";
            change?.Invoke(c);
        });

    private static string? DeviceValidationStamp(ConfigConfirmBeforeRefusalTests.Gate gate)
        => gate.Items.TryGetValue(RequestRowMarkers.DeviceValidationKey, out var value) ? value as string : null;

    [Fact]
    public async Task A_confirmed_loss_is_refused_before_the_chain_with_its_own_code()
    {
        // The Autopilot validation is on and its validator is not wired: reaching the chain throws.
        var lost = Lost(c => { c.AllowInsecureAgentRequests = false; c.ValidateAutopilotDevice = true; });
        var gate = ConfigConfirmBeforeRefusalTests.Build(lost, ConfigRepoMockExtensions.Clone(lost));

        var result = await ConfigConfirmBeforeRefusalTests.Validate(gate);

        Assert.False(result.IsValid);
        Assert.Equal(HttpStatusCode.Forbidden, result.StatusCode);
        Assert.Equal(Constants.ApiErrorCodes.TenantGraphAccessLost, result.Code);
        Assert.Equal(Refusal, result.ErrorMessage);
        Assert.Contains("AADSTS7000229", result.Details);
        Assert.Null(result.RetryAfterSeconds);
        Assert.Equal(RequestRowMarkers.DeviceValidation.AccessLost, DeviceValidationStamp(gate));
        Assert.Equal(RequestRowMarkers.ConfigConfirm.Confirmed, ConfigConfirmBeforeRefusalTests.Stamp(gate));
    }

    [Fact]
    public async Task A_suspicion_alone_runs_the_chain_as_before()
    {
        var suspected = Lost(c => { c.AllowInsecureAgentRequests = false; c.GraphAccessRejectionConfirmedAt = null; });
        var gate = ConfigConfirmBeforeRefusalTests.Build(suspected, ConfigRepoMockExtensions.Clone(suspected));

        var result = await ConfigConfirmBeforeRefusalTests.Validate(gate);

        // No validator admits in this harness: the chain ran and refused on its own terms.
        Assert.Equal("Device not registered", result.ErrorMessage);
        Assert.Null(result.Code);
        Assert.Equal(RequestRowMarkers.DeviceValidation.Rejected, DeviceValidationStamp(gate));
    }

    [Fact]
    public async Task Without_a_switched_on_validation_the_validation_gate_answers()
    {
        var unvalidated = Lost(c => { c.AllowInsecureAgentRequests = false; c.ValidateCloudPcDevice = false; });
        var gate = ConfigConfirmBeforeRefusalTests.Build(unvalidated, ConfigRepoMockExtensions.Clone(unvalidated));

        var result = await ConfigConfirmBeforeRefusalTests.Validate(gate);

        Assert.False(unvalidated.IsGraphAccessConfirmedLost());
        Assert.Equal("Device validation is required", result.ErrorMessage);
        Assert.Null(result.Code);
    }

    [Fact]
    public async Task Hardware_outside_the_whitelist_stays_a_hardware_refusal()
    {
        var lost = Lost(c => c.ManufacturerWhitelist = "Dell*");
        var gate = ConfigConfirmBeforeRefusalTests.Build(lost, ConfigRepoMockExtensions.Clone(lost));

        var result = await ConfigConfirmBeforeRefusalTests.Validate(gate);

        Assert.Equal("Hardware not authorized", result.ErrorMessage);
        Assert.Null(result.Code);
        Assert.Null(DeviceValidationStamp(gate));
    }

    [Fact]
    public void The_portal_notice_and_the_gate_share_one_predicate()
    {
        var lost = Lost();
        var payload = GetTenantFeatureFlagsFunction.BuildPayload(lost, ConfirmedAt.AddHours(1));
        Assert.True(payload.GraphAccessRejected);

        var suspected = Lost(c => c.GraphAccessRejectionConfirmedAt = null);
        Assert.False(suspected.IsGraphAccessConfirmedLost());
        Assert.False(GetTenantFeatureFlagsFunction.BuildPayload(suspected, ConfirmedAt.AddHours(1)).GraphAccessRejected);
    }
}
